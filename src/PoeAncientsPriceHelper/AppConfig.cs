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
    public List<string> AvailableLeagues { get; set; } =
        ["Runes of Aldur", "HC Runes of Aldur", "Forbidden Rites", "HC Forbidden Rites"];
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
    // PoE 2 client language (#29). The locale code whose name map is loaded to translate OCR'd item
    // names into the English keys poe.ninja prices by. "en" (default) loads no map — English client,
    // identity pass-through. Other codes (de/pt/ru/sp, …) match a locales\<code>.json file.
    public string GameLanguage { get; set; } = "en";
    // UI theme preset name. Default "Toxic" (dark green gradient).
    public string Theme { get; set; } = "Toxic";
    // When true (and a region is calibrated), launching the app auto-starts scanning and minimizes to
    // the tray — "just open it and it runs". Missing in older configs → true (the new default behaviour).
    // Skipped under --debug so a troubleshooting session keeps the window and console visible.
    public bool AutoStart { get; set; } = true;

    // Foreground gate (#49). When true (default) the price scan pauses while the game isn't the active
    // window — no capture/OCR, overlay hidden — and resumes on return. Turn off to keep pricing running
    // regardless of what's in front (costs background CPU, but survives odd focus setups where the gate
    // wrongly reads the game as not-foreground). Missing in older configs → the default true, unchanged.
    public bool PauseWhenGameNotFocused { get; set; } = true;

    // Island Rumour helper (#36). Enabled by default; when off, the WORLD-gated auto-detect loop is
    // fully idle (no gate check, no OCR). Missing in older configs → the initializer keeps these
    // defaults (Newtonsoft only overwrites keys present in the file), exactly like AutoStart above.
    public bool RumourHelperEnabled { get; set; } = true;
    // How often the full-screen rumour detect runs WHILE on the Atlas map (ms). Clamped on use.
    public int RumourScanIntervalMs { get; set; } = 1800;

    // Atlas gate (#45). When true (default) the loop finds the "WORLD" label automatically at the
    // top-centre of the game window. On windowed / custom-resolution setups where the stylised label
    // can't be read there, the user can turn this off and drag a box over the WORLD label themselves;
    // the region is then stored in absolute screen pixels and OCR'd verbatim as the gate.
    public bool RumourWorldAutoDetect { get; set; } = true;
    public int RumourWorldX { get; set; }
    public int RumourWorldY { get; set; }
    public int RumourWorldWidth { get; set; }
    public int RumourWorldHeight { get; set; }

    public Rectangle RegionRect
    {
        get => new(RegionX, RegionY, RegionWidth, RegionHeight);
        set { RegionX = value.X; RegionY = value.Y; RegionWidth = value.Width; RegionHeight = value.Height; }
    }

    public bool IsCalibrated => RegionWidth > 0 && RegionHeight > 0;

    public Rectangle RumourWorldRect
    {
        get => new(RumourWorldX, RumourWorldY, RumourWorldWidth, RumourWorldHeight);
        set { RumourWorldX = value.X; RumourWorldY = value.Y; RumourWorldWidth = value.Width; RumourWorldHeight = value.Height; }
    }

    [JsonIgnore]
    public bool HasManualWorldRegion => RumourWorldWidth > 0 && RumourWorldHeight > 0;
}
