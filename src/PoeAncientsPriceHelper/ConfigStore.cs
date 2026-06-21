using Newtonsoft.Json;

namespace PoeAncientsPriceHelper;

internal static class ConfigStore
{
    private const string FileName = "config.json";

    // dir defaults to the folder next to the exe. It's overridable purely so tests can point at a
    // temp directory and exercise the *real* Load/Save logic instead of reimplementing it.
    public static AppConfig Load(string? dir = null)
    {
        var path = PathFor(dir);
        try
        {
            if (!File.Exists(path)) return new AppConfig();
            return JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
        }
        catch { return new AppConfig(); }
    }

    public static void Save(AppConfig config, string? dir = null)
    {
        var path = PathFor(dir);
        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        // Write to a sibling temp file, then rename over the target. A crash mid-write can then only
        // corrupt the throwaway .tmp, never config.json — otherwise a truncated file makes Load fall
        // back to defaults and the user silently loses their calibration. File.Replace is the atomic
        // swap but needs an existing target, so the very first save just moves the temp into place.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    private static string PathFor(string? dir) =>
        Path.Combine(dir ?? AppPaths.DataDir, FileName);
}
