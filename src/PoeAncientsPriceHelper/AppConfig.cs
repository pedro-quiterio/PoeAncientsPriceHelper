using System.Drawing;
using Newtonsoft.Json;

namespace PoeAncientsPriceHelper;

internal sealed class AppConfig
{
    public string LeagueName { get; set; } = "Runes of Aldur";
    // Leagues offered in the settings dropdown. Add more here as new leagues launch.
    // The string is used verbatim as poe.ninja's API league param, so "HC Runes of Aldur" is the
    // Hardcore variant of "Runes of Aldur". [JsonIgnore]: this is an app-defined constant, not user
    // data — persisting it makes Newtonsoft APPEND the saved list onto this default on load
    // (ObjectCreationHandling.Auto), duplicating every entry. Keep it code-only.
    [JsonIgnore]
    public List<string> AvailableLeagues { get; set; } = ["Runes of Aldur", "HC Runes of Aldur"];
    public int RegionX { get; set; } = 0;
    public int RegionY { get; set; } = 0;
    public int RegionWidth { get; set; } = 0;
    public int RegionHeight { get; set; } = 0;
    public int OverlayXOffset { get; set; } = 8;
    // Global hotkeys, each stored as a SharpHook KeyCode name (e.g. "VcF5"). Missing in older configs
    // → fall back to the historical defaults (F5 start/stop, F3 debug, F4 calibrate), preserving prior
    // behaviour. All three live on the same SharpHook hook now. See HotkeyBinding for parse/display.
    public string StartStopHotkey { get; set; } = "VcF5";
    public string DebugHotkey { get; set; } = "VcF3";
    public string CalibrateHotkey { get; set; } = "VcF4";
    public string CustomPricesPath { get; set; } = "custom_prices.json";
    // "GDI" forces legacy BitBlt; any other value uses WGC (GPU capture) with GDI fallback.
    public string CaptureBackend { get; set; } = "Auto";
    // UI theme preset name. Default "Toxic" (dark green gradient).
    public string Theme { get; set; } = "Toxic";
    // When true (and a region is calibrated), launching the app auto-starts scanning and minimizes to
    // the tray — "just open it and it runs". Missing in older configs → true (the new default behaviour).
    // Skipped under --debug so a troubleshooting session keeps the window and console visible.
    public bool AutoStart { get; set; } = true;

    public Rectangle RegionRect
    {
        get => new(RegionX, RegionY, RegionWidth, RegionHeight);
        set { RegionX = value.X; RegionY = value.Y; RegionWidth = value.Width; RegionHeight = value.Height; }
    }

    public bool IsCalibrated => RegionWidth > 0 && RegionHeight > 0;
}
