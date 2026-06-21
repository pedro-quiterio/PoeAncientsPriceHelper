namespace PoeAncientsPriceHelper;

// Single source of truth for where the app keeps state that must survive updates. Under the Velopack
// installed model the binaries live in %LocalAppData%\PoeAncientsPriceHelper\current\, and that
// `current\` folder is replaced wholesale on every update — so anything written next to the exe would
// be wiped. DataDir resolves to %LocalAppData%\PoeAncientsPriceHelper (the *parent* of `current\`,
// which the updater never touches), so config, the icon cache and user overrides persist across
// updates. Local (not Roaming): the calibration region is monitor-specific and must not roam between
// machines. Tests bypass this entirely by passing an explicit dir to ConfigStore/IconCache.
internal static class AppPaths
{
    private static readonly Lazy<string> LazyDataDir = new(() =>
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PoeAncientsPriceHelper");
        Directory.CreateDirectory(dir);
        return dir;
    });

    public static string DataDir => LazyDataDir.Value;

    // Append-only breadcrumb log for the auto-updater. The update path is otherwise silent (failures
    // are swallowed so a flaky GitHub never bothers the user), which makes field issues impossible to
    // diagnose — this gives a minimal trail in %LocalAppData%\...\update.log without any UI noise.
    public static void LogUpdate(string message)
    {
        try { File.AppendAllText(Path.Combine(DataDir, "update.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}"); }
        catch { /* logging must never throw */ }
    }
}
