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

        info = new GameWindowInfo(handle, client, handle == GetForegroundWindow(), source);
        return true;
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
}
