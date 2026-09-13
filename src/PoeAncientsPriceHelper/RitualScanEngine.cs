using System.Drawing;

namespace PoeAncientsPriceHelper;

// Background loop for the ritual chime helper (test branch). While enabled and the ritual region is
// calibrated, it OCRs that small region every ~500ms, parses the "N/M" tribute counter, and plays a
// one-shot chime the moment it fills. Foreground-gated like the other loops (no OCR while the game is
// alt-tabbed). Independent of the F5 price scan — it's "always watching" whenever the feature is on.
//
// Headless in normal use: no overlay, just the chime. Under --debug it traces every pass to
// ritual_scan.txt (see RitualDiag) for tuning the OCR against a real client.
internal sealed class RitualScanEngine : IDisposable
{
    private readonly RitualScanner _scanner;
    private readonly RitualChime _chime;
    private readonly Func<bool> _enabled;
    private readonly Func<Rectangle?> _region;   // null when not calibrated
    private readonly RitualChimeState _state = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private const int IntervalMs = 500;   // confirmed with the owner — fast enough to feel instant on 4/4
    // The counter badge is small (~20-30px), so enlarge it before OCR like the WORLD gate does. A first-
    // pass guess; tune against the --debug ritual_scan.txt read on a real client.
    internal const int UpscaleFactor = 3;

    public RitualScanEngine(RitualScanner scanner, RitualChime chime, Func<bool> enabled, Func<Rectangle?> region)
    {
        _scanner = scanner;
        _chime = chime;
        _enabled = enabled;
        _region = region;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _state.Reset();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void StopAndWait(TimeSpan timeout)
    {
        _cts?.Cancel();
        try { _loop?.Wait(timeout); } catch { }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_enabled())
                {
                    // Feature off: fully idle. Reset so re-enabling starts armed and fresh.
                    _state.Reset();
                    await DelayAsync(ct);
                    continue;
                }

                var region = _region();
                if (region is null || region.Value.Width <= 0 || region.Value.Height <= 0)
                {
                    // Not calibrated yet — nothing to read. Don't reset (no state to protect either).
                    await DelayAsync(ct);
                    continue;
                }

                // Foreground gate (fail-open, like the rumour loop): pause OCR while the game is
                // alt-tabbed. Leave the arming state untouched so returning mid-ritual doesn't re-arm.
                if (GameWindow.TryGet(out var game) && !game.IsForeground)
                {
                    await DelayAsync(ct);
                    continue;
                }

                var text = _scanner.ReadText(region.Value, UpscaleFactor);
                var reading = RitualCounter.TryParse(text, out int n, out int m)
                    ? RitualReading.Counter(n, m)
                    : RitualReading.Blank;
                bool fire = _state.Observe(reading);
                RitualDiag.Log(
                    $"region={region.Value} raw='{text.Trim()}' " +
                    $"parsed={(reading.HasCounter ? $"{n}/{m}" : "-")} armed={_state.Armed} fire={fire}");
                if (fire) _chime.Play();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RitualScanEngine] {ex.GetType().Name}: {ex.Message}");
            }

            await DelayAsync(ct);
        }
    }

    private static async Task DelayAsync(CancellationToken ct)
    {
        try { await Task.Delay(IntervalMs, ct); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        StopAndWait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
    }
}
