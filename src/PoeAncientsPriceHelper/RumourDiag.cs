namespace PoeAncientsPriceHelper;

// Debug-only rumour-scan tracing. Writes to "rumour_scan.txt" in the data dir (next to scan_log.txt),
// only when the app is launched with --debug. Records window detection, the gate region + WORLD read,
// and detect results — the trail that pinned down the windowed-mode gate failure in #45. Off the debug
// path this is a no-op, so it stays out of the hot loop's cost.
internal static class RumourDiag
{
    private static readonly object Gate = new();
    private static readonly string LogPath = System.IO.Path.Combine(AppPaths.DataDir, "rumour_scan.txt");
    private static bool _started;

    public static void Log(string msg)
    {
        if (!App.DebugMode) return;
        lock (Gate)
        {
            try
            {
                if (!_started) { File.WriteAllText(LogPath, ""); _started = true; }
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { /* logging must never break the app */ }
        }
    }
}
