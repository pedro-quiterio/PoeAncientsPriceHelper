namespace PoeAncientsPriceHelper;

// Debug-only ritual-scan tracing. Writes to "ritual_scan.txt" in the data dir (next to scan_log.txt and
// rumour_scan.txt), only under --debug. Records the region, the raw OCR read, the parsed N/M, and the
// armed/fire state each pass — the trail for tuning the upscale factor and separator regex against a
// real client's font. A no-op off the debug path, so it stays out of the loop's cost.
internal static class RitualDiag
{
    private static readonly object Gate = new();
    private static readonly string LogPath = System.IO.Path.Combine(AppPaths.DataDir, "ritual_scan.txt");
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
