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
// detection miss must never silently stop the overlay.
internal readonly record struct GameWindowInfo(IntPtr Handle, Rectangle ClientBounds, bool IsForeground);

internal static class GameWindow
{
    // The client's own top-level window title. A browser tab about PoE reads like "… - Chrome", so an
    // EXACT (case-insensitive) match keeps us from latching onto anything but the game itself.
    private static readonly string[] Titles = ["path of exile 2", "path of exile"];

    // Locate the game window. Returns its client (viewport) rect in screen coordinates and whether it
    // is the foreground window. False when no visible, non-minimised PoE window exists.
    public static bool TryGet(out GameWindowInfo info)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;   // skip hidden / minimised
            if (!TitleMatches(h)) return true;
            found = h;
            return false;   // stop at the first match
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) { info = default; return false; }

        // Client area, not the window rect: the WORLD label sits at the top-centre of the viewport,
        // BELOW any title bar, so the gate has to be relative to the client rect to catch it.
        if (!GetClientRect(found, out var rc)) { info = default; return false; }
        var origin = new POINT { X = rc.Left, Y = rc.Top };
        ClientToScreen(found, ref origin);
        var client = new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
        if (client.Width <= 0 || client.Height <= 0) { info = default; return false; }

        info = new GameWindowInfo(found, client, found == GetForegroundWindow());
        return true;
    }

    private static bool TitleMatches(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len == 0) return false;
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        var title = sb.ToString().Trim();
        foreach (var t in Titles)
            if (title.Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
}
