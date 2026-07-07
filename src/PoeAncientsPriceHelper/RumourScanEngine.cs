using System.Drawing;

namespace PoeAncientsPriceHelper;

// Background auto-detect loop for the rumour helper (#35). Gated cheaply by an OCR check for the
// "WORLD" label at the top-centre of the Atlas screen: off the map the loop only does that tiny check
// (~1 Hz, near-zero cost); on the map it runs the full-screen rumour detection on a throttle and
// shows/hides the overlay as the panel appears and closes. ESC / Left-Ctrl+click force-dismiss via a
// latch that re-arms once the panel is gone.
internal sealed class RumourScanEngine : IDisposable
{
    private readonly RumourScanner _scanner;
    private readonly Func<Rectangle> _screen;
    private readonly Func<bool> _enabled;
    private readonly Func<int> _intervalMs;
    // Manual atlas gate (#45): returns the user-drawn WORLD region (absolute screen px) when auto-detect
    // is off, else null → the loop computes the gate from the game viewport as usual.
    private readonly Func<Rectangle?> _worldRegion;
    // Holds the best resolution per rumour row while a panel is open so the ratings stop flickering
    // resolved↔"unknown" as OCR jitters between passes (#45 follow-up). Reset when the panel closes.
    private readonly RumourStabilizer _stabilizer = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Shared with the App hotkey hook: ESC / Ctrl+click set the dismiss latch; the loop keeps the
    // overlay hidden until the panel is gone, then clears it so a later panel shows again.
    private static volatile bool _dismissed;
    private static volatile bool _showing;
    public static bool IsShowing => _showing;
    public static void RequestDismiss() => _dismissed = true;

    private const int GateIntervalMs = 1000;   // ~1 Hz WORLD-gate check (cheap, small region)
    private const int TickMs = 150;            // loop granularity; the work is timestamp-throttled
    private const int HideAfterMisses = 2;     // signature-gone passes before hiding the overlay
    // Sane bounds for the user-configurable full-screen scan interval (#36).
    private const int MinIntervalMs = 500;
    private const int MaxIntervalMs = 10000;

    public RumourScanEngine(RumourScanner scanner, Func<Rectangle> screen, Func<bool> enabled, Func<int> intervalMs,
        Func<Rectangle?>? worldRegion = null)
    {
        _scanner = scanner;
        _screen = screen;
        _enabled = enabled;
        _intervalMs = intervalMs;
        _worldRegion = worldRegion ?? (() => null);
    }

    public static int ClampInterval(int ms) => Math.Clamp(ms, MinIntervalMs, MaxIntervalMs);

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _dismissed = false;
        _showing = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        RumourDiag.Log($"=== rumour engine start ===\n{GameWindow.Describe()}");
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void StopAndWait(TimeSpan timeout)
    {
        _cts?.Cancel();
        try { _loop?.Wait(timeout); } catch { }
        HideOverlay();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        bool onMap = false;
        int missStreak = 0;
        var lastGate = DateTime.MinValue;
        var lastScan = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_enabled())
                {
                    // Feature off (#36): fully idle — no gate, no OCR. Reset so re-enabling starts fresh.
                    HideOverlay();
                    onMap = false;
                    missStreak = 0;
                    try { await Task.Delay(TickMs, ct); } catch (OperationCanceledException) { break; }
                    continue;
                }

                var now = DateTime.UtcNow;
                var screen = _screen();

                // Locate the game window: gate the WORLD check to its viewport (windowed mode parks the
                // panel anywhere on the monitor, #45), and pause entirely while the game isn't in front.
                // Fail-open — no game window found ⇒ monitor-relative gate and keep scanning, as before.
                bool poeFound = GameWindow.TryGet(out var game);
                if (poeFound && !game.IsForeground)
                {
                    // Alt-tabbed / minimised: drop the overlay and idle until the game is back in front.
                    HideOverlay();
                    onMap = false;
                    missStreak = 0;
                    try { await Task.Delay(TickMs, ct); } catch (OperationCanceledException) { break; }
                    continue;
                }
                var gateArea = poeFound ? game.ClientBounds : screen;

                // Cheap WORLD gate (~1 Hz). Off the map this is the only work the loop does. The user
                // can override the auto-computed band with a hand-drawn region (#45) for windowed /
                // custom-resolution setups where the label isn't at the viewport's top-centre.
                if ((now - lastGate).TotalMilliseconds >= GateIntervalMs)
                {
                    lastGate = now;
                    var overrideRegion = _worldRegion();
                    var gateRegion = overrideRegion ?? WorldGateRegion(gateArea);
                    // The stylised "WORLD" banner is small (~20px) at low/windowed resolutions and OCRs
                    // as nothing at native size, so upscale the tiny gate region before reading it (#45).
                    int upscale = GateUpscale(gateRegion.Height);
                    var gateLines = _scanner.CaptureLines(gateRegion, upscale);
                    bool nowOnMap = ContainsWorldToken(gateLines);
                    RumourDiag.Log(
                        $"gate poeFound={poeFound} src={(poeFound ? game.Source : "-")} " +
                        $"gateRegion={gateRegion} x{upscale} manual={overrideRegion is not null} WORLD={nowOnMap} " +
                        $"raw=[{string.Join(" | ", gateLines.Select(l => l.Text.Trim()))}]");
                    if (!nowOnMap && onMap)
                    {
                        // Left the map — drop the overlay and clear any dismiss latch.
                        HideOverlay();
                        _dismissed = false;
                        missStreak = 0;
                    }
                    onMap = nowOnMap;
                }

                // Full-screen detect (throttled) only while on the map.
                if (onMap && (now - lastScan).TotalMilliseconds >= ClampInterval(_intervalMs()))
                {
                    lastScan = now;
                    var result = _scanner.ReadOnce(screen);
                    bool present = result is { Rows.Count: > 0 };
                    RumourDiag.Log(present
                        ? $"detect present=true rows={result!.Rows.Count} " +
                          $"names=[{string.Join(" | ", result.Rows.Select(r => $"{r.OcrName}{(r.Matched ? "→" + r.Entry!.Rumor : "→?")}"))}]"
                        : "detect present=false (no panel)");

                    if (_dismissed)
                    {
                        // Held dismissed: stay hidden; once the panel is gone for a couple passes,
                        // clear the latch so a reopened / different panel shows again.
                        HideOverlay();
                        if (!present)
                        {
                            if (++missStreak >= HideAfterMisses) { _dismissed = false; missStreak = 0; }
                        }
                        else missStreak = 0;
                    }
                    else if (present)
                    {
                        // Hold the best resolution seen so far stable — the panel's rumours don't change
                        // while it's open, so a row that OCR reads differently this pass mustn't flip its
                        // rating under the user (#45 follow-up).
                        var stableRows = _stabilizer.Stabilize(result!.Rows);
                        RumourOverlayManager.Show(stableRows, result.PanelBounds);
                        _showing = true;
                        missStreak = 0;
                    }
                    else if (++missStreak >= HideAfterMisses)
                    {
                        HideOverlay();
                        missStreak = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RumourScanEngine] {ex.GetType().Name}: {ex.Message}");
            }

            try { await Task.Delay(TickMs, ct); }
            catch (OperationCanceledException) { break; }
        }

        HideOverlay();
    }

    private void HideOverlay()
    {
        // Drop the accumulated per-row locks so a reopened panel starts fresh (mirrors the price loop
        // clearing its slots when the panel closes).
        _stabilizer.Reset();
        if (_showing) { RumourOverlayManager.HideNow(); _showing = false; }
    }

    // The small top-centre band where the Atlas shows the "WORLD" label, relative to the passed area —
    // the game's client viewport when it can be located, else the monitor (#45). A fifth of the width
    // and a fifteenth of the height, centred horizontally at the very top — big enough to catch the
    // label across resolutions / UI scales, small enough that OCR'ing it ~1 Hz is negligible. Using the
    // viewport (not the monitor) keeps the band over the label when a windowed client sits offset from
    // the monitor's top-left.
    public static Rectangle WorldGateRegion(Rectangle area)
    {
        int w = Math.Max(1, area.Width / 5);
        int h = Math.Max(1, area.Height / 15);
        int x = area.Left + (area.Width - w) / 2;
        return new Rectangle(x, area.Top, w, h);
    }

    // Upscale factor for the WORLD-gate region. The stylised banner text is only ~20-30px tall at
    // windowed / default / custom resolutions and the OCR engine reads it only when enlarged ~4× — a
    // FLAT factor, not inverse-height: a wider/taller gate band still holds the SAME small text, so it
    // needs the same magnification, not less. (An earlier inverse-height formula gave a 54px band only
    // 3×, which read garbage; 4× reads "WORLD" cleanly — measured on real 34px and 54px captures, where
    // 6× starts to over-blur.) Native 4K bands are already large enough, so they're left at 1×.
    internal static int GateUpscale(int regionHeight) => regionHeight is > 0 and < 200 ? 4 : 1;

    // True if a gate line reads "world". Exact whole-word match first (so "underworld" etc. can't trip
    // it); then a tolerant fallback — the stylised banner can OCR a glyph off even after upscaling
    // ("worid", "vvorld"), so a close ~5-letter token still counts as being on the Atlas map.
    internal static bool ContainsWorldToken(IEnumerable<OcrTextLine> lines)
    {
        foreach (var line in lines)
        foreach (var tok in NameNormalizer.Normalize(line.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok == "world") return true;
            if (tok.Length is >= 4 and <= 7 &&
                1.0 - (double)ScanEngine.Levenshtein(tok, "world") / 5.0 >= 0.6)   // ≤2 edits from "world"
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        StopAndWait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
    }
}
