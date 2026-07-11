using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace PoeAncientsPriceHelper;

// Where the game window is, and whether it's in front. The scan loops use this to (a) gate the rumour
// "WORLD" check to the game's viewport instead of the monitor's top edge — windowed mode can park the
// window anywhere on the monitor, which pushed the label out of a monitor-relative band (#45) — and
// (b) pause OCR entirely while the game is alt-tabbed or minimised.
//
// Everything here is FAIL-OPEN: TryGet returns false when no game window is found, and every caller
// treats "not found" as "carry on exactly as before" (monitor-relative gate, keep scanning). A
// detection miss must never silently stop the overlay. `Source` records how the window was found (for
// the diagnostic log).
internal readonly record struct GameWindowInfo(IntPtr Handle, Rectangle ClientBounds, bool IsForeground, string Source);

internal static class GameWindow
{
    // The client's own top-level window title. A browser tab about PoE reads like "… - Chrome", so an
    // EXACT (case-insensitive) match keeps us from latching onto anything but the game itself.
    private static readonly string[] Titles = ["path of exile 2", "path of exile"];
    // The client executable is PathOfExile.exe / PathOfExileSteam.exe (PoE1 & PoE2, standalone/Steam/EGS).
    private const string ProcessPrefix = "PathOfExile";

    // Locate the game window. Returns its client (viewport) rect in screen coordinates and whether it
    // is the foreground window. False when no visible, non-minimised PoE window exists. Process-name
    // lookup is tried first (robust to a localised/decorated window title), then an exact title match.
    public static bool TryGet(out GameWindowInfo info)
    {
        var hit = FindByProcess() ?? FindByTitle();
        if (hit is not var (handle, source) || handle == IntPtr.Zero) { info = default; return false; }

        // Client area, not the window rect: the WORLD label sits at the top-centre of the viewport,
        // BELOW any title bar, so the gate has to be relative to the client rect to catch it.
        if (!GetClientRect(handle, out var rc)) { info = default; return false; }
        var origin = new POINT { X = rc.Left, Y = rc.Top };
        ClientToScreen(handle, ref origin);
        var client = new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
        if (client.Width <= 0 || client.Height <= 0) { info = default; return false; }

        info = new GameWindowInfo(handle, client, IsGameForeground(handle), source);
        return true;
    }

    // Is the game in front? NOT an exact-handle test: some clients (multiple top-level windows, the
    // borderless/overlay child that actually takes focus) make GetForegroundWindow() return a window
    // that ISN'T the process's MainWindowHandle, so `fg == handle` reads as "not foreground" forever and
    // the overlay never resumes (#47, regression from the 3.5.8 focus gate). Instead we treat the game as
    // foreground when the focused window belongs to the SAME PROCESS as the detected game window. FAIL-OPEN:
    // if the foreground window (or either PID) can't be resolved, assume foreground so we keep scanning.
    private static bool IsGameForeground(IntPtr gameHandle)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == gameHandle) return true;
        GetWindowThreadProcessId(gameHandle, out uint gamePid);
        GetWindowThreadProcessId(fg, out uint fgPid);
        if (gamePid == 0 || fgPid == 0) return true;
        // Our OWN windows (settings, the click-through overlay, the debug console) count as foreground:
        // the user is interacting with the helper, not tabbing away from the game, so pausing there is
        // wrong — that's the resume→pause flap in #49. Keep scanning when we hold focus.
        if (fgPid == (uint)Environment.ProcessId) return true;
        return gamePid == fgPid;
    }

    // The foreground window's identity (hwnd/pid/process/title), for the pause diagnostic log. The
    // plain "game not foreground" line never said WHAT stole focus, which is exactly what a false pause
    // needs to be understood from a user's log alone (#49).
    public static string DescribeForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "foreground=<none>";
        GetWindowThreadProcessId(fg, out uint pid);
        string proc = "?";
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; }
        catch { /* process gone / access denied — leave as '?' */ }
        return $"foreground hwnd={fg} pid={pid} proc={proc} title='{GetTitle(fg)}'";
    }

    // A visible, non-minimised top-level window belonging to a PathOfExile* process.
    private static (IntPtr Handle, string Source)? FindByProcess()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!p.ProcessName.StartsWith(ProcessPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var h = p.MainWindowHandle;
                    if (h != IntPtr.Zero && IsWindowVisible(h) && !IsIconic(h))
                        return (h, $"process:{p.ProcessName}");
                }
                catch { /* access denied on a foreign process — skip it */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* GetProcesses can throw under heavy churn — treat as "not found" */ }
        return null;
    }

    // A visible, non-minimised top-level window whose title exactly matches the client.
    private static (IntPtr Handle, string Source)? FindByTitle()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;   // skip hidden / minimised
            if (!TitleMatches(GetTitle(h))) return true;
            found = h;
            return false;   // stop at the first match
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : (found, "title");
    }

    private static bool TitleMatches(string title)
    {
        foreach (var t in Titles)
            if (title.Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string GetTitle(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    // One-off snapshot of everything the detector had to work with: PathOfExile* processes, every
    // visible top-level window (title + rect), and the monitor layout. Dumped to the diagnostic log at
    // startup so a failure to find the window can be understood without a live machine (#45).
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("processes (PathOfExile*):");
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!p.ProcessName.StartsWith(ProcessPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    sb.AppendLine($"  {p.ProcessName} pid={p.Id} mainHwnd={p.MainWindowHandle} title='{p.MainWindowTitle}'");
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        sb.AppendLine("visible top-level windows:");
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            var title = GetTitle(h);
            if (title.Length == 0) return true;
            GetWindowRect(h, out var wr);
            sb.AppendLine($"  hwnd={h} rect=({wr.Left},{wr.Top},{wr.Right - wr.Left}x{wr.Bottom - wr.Top}) title='{title}'");
            return true;
        }, IntPtr.Zero);

        sb.AppendLine("screens:");
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
            sb.AppendLine($"  {(s.Primary ? "*" : " ")} bounds={s.Bounds} name='{s.DeviceName}'");
        return sb.ToString().TrimEnd();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
}
