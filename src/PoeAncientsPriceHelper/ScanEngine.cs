using System.Diagnostics;
using System.Drawing;

namespace PoeAncientsPriceHelper;

internal sealed class ScanEngine : IDisposable
{
    private readonly AppConfig _config;
    private readonly PriceRepository _prices;
    private readonly IconCache _icons;
    private readonly IScreenCaptureBackend _capture;
    private readonly NameTranslator _translator;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Dictionary<string, int> _lastPositions = new();
    private string _logPath = null!;   // assigned in RunLoopAsync before any Log() call

    // Resolution cache for the exact → prefix → fuzzy chain in BuildPriceRows. The same OCR'd
    // names recur on every pass while a panel is open, so caching the resolved price key (or a
    // recorded miss) skips the dictionary scan + Levenshtein work on all but the first pass.
    // Invalidated wholesale when the price snapshot changes (tracked via PriceGeneration).
    private int _cachedPriceGeneration = -1;
    private readonly Dictionary<string, (string? Key, bool Exact)> _resolutionCache = new();

    // Shared with the global hotkey hook (App). The loop owns the detection state, so the hook
    // only sets a "dismissed" latch; the loop reads it and keeps the overlay hidden.
    private static volatile bool _dismissed;
    private static volatile bool _showing;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    // True while the overlay is actually showing a confirmed panel. Written only by the scan loop (never
    // the hotkey hook, which just latches _dismissed); the setter starts/stops the Ctrl+click watcher on
    // each transition so it only polls while the overlay is up (#52).
    public static bool IsShowing
    {
        get => _showing;
        private set
        {
            if (_showing == value) return;
            _showing = value;
            App.UpdateClickWatcher();
        }
    }

    // ESC / Left-Ctrl+click: hide the overlay and keep it hidden until the panel actually closes
    // (ESC closes the panel, so it clears fast; Ctrl+click leaves the panel open, so it stays
    // dismissed without flickering until the user closes the panel themselves).
    public static void RequestDismiss() => _dismissed = true;

    public ScanEngine(AppConfig config, PriceRepository prices, IconCache icons, IScreenCaptureBackend capture,
        NameTranslator? translator = null)
    {
        _config = config;
        _prices = prices;
        _icons = icons;
        _capture = capture;
        // Localized-client support (#29): maps OCR'd non-English names → English price keys for the
        // language picked in Settings (config.GameLanguage). "en" (default) loads nothing → no-op.
        _translator = translator ?? NameTranslator.ForLanguage(config.GameLanguage);
    }

    public void Start()
    {
        if (IsRunning) return;
        // Reset shared static flags so a stale loop (e.g. one that timed out in StopAndWait)
        // can't clobber the new instance's dismiss/show state.
        _dismissed = false;
        IsShowing = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void StopAndWait(TimeSpan timeout)
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(timeout); } catch { }
    }

    private void Log(string msg)
    {
        // File logging is debug-only: in normal use the loop fires ~10×/s and would otherwise
        // churn the log file continuously (a real cost on the hot path).
        if (!App.DebugMode) return;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(_logPath, line + "\n"); } catch { }
        Console.WriteLine(line);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Keep _logPath assigned even when not debugging so Log() never throws on a null/empty
        // path; only truncate the file when debug logging is actually enabled.
        _logPath = Path.Combine(AppPaths.DataDir, "scan_log.txt");
        if (App.DebugMode)
            File.WriteAllText(_logPath, "");

        Log($"START prices={_prices.ItemCount} icons={_icons.IsAvailable} region={_config.RegionRect}");

        var scanner = new OcrScanner(Log, App.DebugMode, _config.GameLanguage);
        var detector = new ListDetector();
        var sw = Stopwatch.StartNew();
        var slots = new List<RowSlot>();             // per-row accumulator: priced rows lock, misses keep retrying
        IReadOnlyList<PriceRow> lastRows = [];       // what the overlay shows
        // Last state actually pushed to the overlay. The loop ticks ~10×/s but the displayed rows
        // only change when OCR resolves something new, so skipping UpdateState when nothing changed
        // avoids needless cross-thread marshalling / repaints on the hot path.
        IReadOnlyList<PriceRow> lastPushedRows = [];
        bool lastPushedConfirmed = false;
        bool lastPushedReading = false;
        // Stack memory is now per-row, held on each RowSlot (see MergeReads), so a dropped "Nx" marker
        // on one row can't bleed its multiplier onto another row of the same item.
        int topmostCounter = 0;
        const int TopmostEveryN = 10;
        bool isOpen = false;          // brightness gate: bright enough to attempt OCR
        bool confirmedOpen = false;   // OCR actually found a list — only then show the overlay
        // After a dismiss (ESC / Ctrl+click) the brightness gate can re-trip on ambient light that
        // grazes the threshold (the game world after the panel closes reads almost as bright as a real
        // panel — measured 105 vs a real panel's 101). That re-show is the post-ESC flicker. While this
        // is set, the brightness-only "reading…" hint is suppressed: nothing shows until OCR actually
        // confirms a priced row again. Cleared on the next real confirm.
        bool suppressHintUntilConfirm = false;
        int brightStreak = 0;
        int darkStreak = 0;
        // Dismiss (ESC / Ctrl+click) is released on CONTENT, not brightness: brightness can't tell the
        // dismissed panel still being open from a different panel now being open (both read bright), so
        // a close into a bright scene — or a switch to another panel — used to leave the latch stuck.
        // While dismissed we keep scanning and: release quietly once the dismissed panel's items are
        // gone, or release and show once a DIFFERENT priced item appears. dismissedNames is the set of
        // priced items on screen at the moment of dismissal (the "same panel" signature).
        bool wasDismissed = false;    // edge-detect entry into the dismissed branch (to capture the signature once)
        HashSet<string> dismissedNames = new(StringComparer.Ordinal);
        int dismissNoPrice = 0;       // consecutive dismissed passes with no priced row (or dark) — panel gone
        int dismissDiffStreak = 0;    // consecutive dismissed passes showing an item not in dismissedNames — new panel
        DateTime dismissStartedAt = DateTime.UtcNow;   // when the current dismiss latch began — absolute-ceiling clock
        DateTime dismissPanelSeenAt = DateTime.UtcNow; // last pass that still saw the dismissed panel — stall clock
        int staleCount = 0;           // consecutive 0-row OCR passes — clears stale overlay on loading screens
        const int StaleLimit = 10;    // consecutive 0-row OCR passes before clearing (~800ms at 80ms interval)
        // Consecutive OCR passes (whether 0-row or rows that didn't resolve) with NO priced row while
        // the panel was already confirmed. The brightness gate misses a close into a bright/white scene
        // (brightness never drops below CloseBrightness), so a confirmed panel can linger with its
        // locked rows showing. This streak is the brightness-independent "the panel is gone" signal.
        int noPriceStreak = 0;
        const int NoPriceCloseLimit = 3;   // ~450ms at the OCR floor before a confirmed panel is dropped
        int cycleCount = 0;
        bool paused = false;          // true while the game isn't the foreground window (no capture/OCR)
        // Debounce for the foreground gate (#49). The game momentarily losing focus — a click on the
        // helper's own windows, a transient activation blip — must not instantly pause and hide the
        // overlay. We keep scanning until the game has been continuously not-foreground for this long;
        // any foreground cycle in between resets the timer. -1 means "currently foreground".
        long notForegroundSinceMs = -1;
        const int ForegroundGraceMs = 1000;
        var lastOcrAt = DateTime.MinValue;
        const int MinOcrIntervalMs = 150;            // OCR floor while panel is open — Windows OCR is fast enough that 6.7/s gives sub-200ms turnaround
        const int OpenCycleMs = 120;                 // tight loop while scanning
        const int ClosedCycleMs = 300;               // polling while watching for the panel — halves idle capture cost
        const int DarkToRelease = 3;                 // dark frames before a dismiss latch releases
        // Two backstops so a dismiss latch can never stick forever, WITHOUT cutting a dismiss short while
        // the user is still working in the panel they dismissed (#52 follow-up). Ctrl+click deliberately
        // keeps the overlay hidden for as long as that panel is up, so the stall clock is reset on every
        // pass that still sees it — it only runs down once the panel stops being recognised. The ceiling
        // never resets and exists purely so no latch can outlive the session.
        const int DismissStallMs = 5000;             // no sighting of the dismissed panel for this long ⇒ release
        const int DismissMaxHoldMs = 60000;          // absolute ceiling on a single dismiss latch
        // Asymmetric brightness hysteresis. A frame counts toward OPENING only above OpenBrightness and
        // toward CLOSING only below CloseBrightness; readings in the [80,100] dead zone hold the current
        // state so brightness hovering at the boundary can't flicker the overlay. OpenBrightness stays
        // at the detector's old threshold (100) on purpose — real panels read as low as 101, so raising
        // it would miss dim ones; the confirm-gate (above) is what rejects bright-but-fake frames.
        const int OpenBrightness = 100;
        const int CloseBrightness = 80;

        PriceOverlayManager.EnsureVisible(_config.RegionRect, _config.OverlayXOffset, _icons);
        Log("overlay ready");

        while (!ct.IsCancellationRequested)
        {
            var cycleStart = sw.ElapsedMilliseconds;
            cycleCount++;
            try
            {
                // Pause while the game isn't the foreground window (alt-tabbed to another app): no
                // capture, no OCR, overlay hidden. Skipped entirely when the user disables the gate
                // (config) so pricing runs regardless of what's in front. Fail-open — when the game
                // window can't be located we scan exactly as before, so a detection miss can never
                // silently stop pricing. A short grace period (#49) rides out brief focus blips: we only
                // pause after the game has been continuously not-foreground for ForegroundGraceMs.
                if (_config.PauseWhenGameNotFocused && GameWindow.TryGet(out var game) && !game.IsForeground)
                {
                    if (notForegroundSinceMs < 0) notForegroundSinceMs = cycleStart;
                    if (cycleStart - notForegroundSinceMs >= ForegroundGraceMs)
                    {
                        if (!paused)
                        {
                            paused = true;
                            // Reset transient detection state so the panel re-confirms cleanly on return.
                            slots.Clear(); lastRows = [];
                            isOpen = false; confirmedOpen = false;
                            brightStreak = 0; darkStreak = 0; staleCount = 0; noPriceStreak = 0;
                            lastPushedRows = []; lastPushedConfirmed = false; lastPushedReading = false;
                            IsShowing = false;
                            PriceOverlayManager.HideNow();
                            Log($"paused (game not foreground) — {GameWindow.DescribeForeground()}");
                        }
                        try { await Task.Delay(ClosedCycleMs, ct); } catch (OperationCanceledException) { break; }
                        continue;
                    }
                    // Within the grace window: fall through and keep scanning as normal for now.
                }
                else
                {
                    notForegroundSinceMs = -1;
                }
                if (paused) { paused = false; Log("resumed (game foreground)"); }

                using var bmp = _capture.CaptureRegion(_config.RegionRect);
                var sampledPixel = detector.SampleAverage(bmp);
                int brightness = (sampledPixel.R + sampledPixel.G + sampledPixel.B) / 3;
                bool brightFrame = brightness > OpenBrightness;   // strong enough to count toward opening
                bool darkFrame = brightness < CloseBrightness;    // dim enough to count toward closing

                // Dismissed (ESC / Left-Ctrl+click): stay hidden, but keep scanning so we can tell the
                // dismissed panel still being open (keep hidden) from it closing / a different panel
                // taking over (release). Release triggers: the region goes dark, the dismissed items
                // disappear for a few passes (panel closed — covers a close into a bright scene), or a
                // priced item not in the dismissed set shows up for 2 passes (a different panel).
                if (_dismissed)
                {
                    // On entry, snapshot what was priced on screen as the "same panel" signature before
                    // we clear anything. lastRows still holds the dismissed panel's rows here.
                    if (!wasDismissed)
                    {
                        dismissedNames = new HashSet<string>(
                            lastRows.Where(r => r.HasPrice).Select(r => r.Name), StringComparer.Ordinal);
                        dismissNoPrice = 0;
                        dismissDiffStreak = 0;
                        dismissStartedAt = DateTime.UtcNow;
                        dismissPanelSeenAt = dismissStartedAt;
                    }
                    wasDismissed = true;

                    // Backstops (see the constants). The stall clock is reset below on every pass that
                    // still recognises the dismissed panel, so holding that panel open keeps the overlay
                    // hidden exactly as intended; these only fire once it stops being seen (or, for the
                    // ceiling, after an implausibly long single dismiss).
                    var sinceSeen = (DateTime.UtcNow - dismissPanelSeenAt).TotalMilliseconds;
                    var sinceStart = (DateTime.UtcNow - dismissStartedAt).TotalMilliseconds;
                    if (sinceSeen >= DismissStallMs || sinceStart >= DismissMaxHoldMs)
                    {
                        _dismissed = false; wasDismissed = false;
                        suppressHintUntilConfirm = true;
                        Log(sinceSeen >= DismissStallMs
                            ? $"dismiss released (panel not seen for {sinceSeen:F0}ms)"
                            : $"dismiss released (max hold {sinceStart:F0}ms elapsed)");
                    }

                    isOpen = false; confirmedOpen = false; brightStreak = 0; darkStreak = 0;
                    slots.Clear(); lastRows = [];
                    staleCount = 0;
                    noPriceStreak = 0;
                    IsShowing = false;
                    // Always push when dismissed to clear the overlay, and reset the change-tracker
                    // so the next real state is treated as new.
                    PriceOverlayManager.UpdateState([], false, false);
                    lastPushedRows = []; lastPushedConfirmed = false; lastPushedReading = false;

                    if (darkFrame)
                    {
                        // Dark region — the panel is gone (panels read bright). Fast path, no OCR.
                        dismissDiffStreak = 0;
                        if (++dismissNoPrice >= DarkToRelease)
                        {
                            _dismissed = false; wasDismissed = false;
                            suppressHintUntilConfirm = true;
                            Log("dismiss released (region went dark)");
                        }
                    }
                    else
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastOcrAt).TotalMilliseconds >= MinOcrIntervalMs)
                        {
                            lastOcrAt = now;
                            var ocrRows = scanner.Scan(bmp);
                            var pricedNames = ocrRows.Count == 0
                                ? new List<string>()
                                : BuildPriceRows(ocrRows).Where(r => r.HasPrice).Select(r => r.Name).ToList();

                            if (pricedNames.Count == 0)
                            {
                                // Nothing priced on screen — the dismissed panel has closed.
                                dismissDiffStreak = 0;
                                if (++dismissNoPrice >= DarkToRelease)
                                {
                                    _dismissed = false; wasDismissed = false;
                                    suppressHintUntilConfirm = true;
                                    Log("dismiss released (panel closed)");
                                }
                            }
                            else if (pricedNames.Any(n => !dismissedNames.Contains(n)))
                            {
                                // An item that wasn't on the dismissed panel — a different panel is up.
                                // Require two consecutive passes so a single OCR misread can't re-show
                                // the same panel we just dismissed.
                                dismissNoPrice = 0;
                                if (++dismissDiffStreak >= 2)
                                {
                                    _dismissed = false; wasDismissed = false;
                                    // A genuinely new panel — let the normal flow confirm and show it,
                                    // so no hint suppression here.
                                    Log("dismiss released (different panel detected)");
                                }
                            }
                            else
                            {
                                // Same items as when dismissed — still that panel; keep it hidden. This is
                                // the Ctrl+click "I'm buying, stay out of the way" case, so hold the stall
                                // clock off for as long as the panel keeps being recognised.
                                dismissNoPrice = 0;
                                dismissDiffStreak = 0;
                                dismissPanelSeenAt = DateTime.UtcNow;
                            }
                        }
                    }
                }
                else
                {
                    wasDismissed = false;
                    dismissNoPrice = 0;
                    dismissDiffStreak = 0;

                    // Hysteresis: 2 consecutive bright frames to open, 3 dark frames to close; readings
                    // in the [CloseBrightness, OpenBrightness] dead zone hold the current state.
                    if (brightFrame) { brightStreak++; darkStreak = 0; }
                    else if (darkFrame) { darkStreak++; brightStreak = 0; }
                    else { brightStreak = 0; darkStreak = 0; }
                    bool prevIsOpen = isOpen;
                    if (!isOpen && brightStreak >= 2) isOpen = true;
                    else if (isOpen && darkStreak >= 3) isOpen = false;

                    // Heartbeat every ~5s so we know the loop is alive
                    if (cycleCount % 12 == 0)
                    {
                        Log($"heartbeat cycle={cycleCount} panelOpen={isOpen} confirmed={confirmedOpen} region={_config.RegionRect} rows={lastRows.Count} " +
                            $"avgPixel=#{sampledPixel.R:X2}{sampledPixel.G:X2}{sampledPixel.B:X2} brightness={brightness}");
                    }

                    if (isOpen != prevIsOpen)
                    {
                        Log($"panel {(isOpen ? "OPEN" : "CLOSED")} brightness={brightness} " +
                            $"avgPixel=#{sampledPixel.R:X2}{sampledPixel.G:X2}{sampledPixel.B:X2}");

                        // Panel just detected — show the "reading…" hint right away, before the first
                        // (200–400ms) OCR runs, so the wait isn't a blank screen. But right after a
                        // dismiss, suppress it: a brightness blip that isn't a real panel never
                        // confirms, so showing the hint here is exactly the post-ESC flicker.
                        if (isOpen && !suppressHintUntilConfirm)
                        {
                            IsShowing = false;
                            PriceOverlayManager.UpdateState([], false, true);
                        }
                    }

                    if (isOpen)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastOcrAt).TotalMilliseconds >= MinOcrIntervalMs)
                        {
                            lastOcrAt = now;
                            var ocrRows = scanner.Scan(bmp);
                            if (ocrRows.Count == 0)
                            {
                                staleCount++;
                                noPriceStreak++;
                                // Hide stale prices quickly (after 2 passes ≈ 160ms) so they don't
                                // linger on loading screens, but keep slots alive until StaleLimit
                                // for fast recovery when the panel reappears.
                                if (staleCount >= 2)
                                    lastRows = [];
                                if (staleCount >= StaleLimit)
                                {
                                    Log($"OCR 0 rows for {staleCount} passes — clearing slots");
                                    slots.Clear();
                                    confirmedOpen = false;
                                }
                                else
                                {
                                    Log($"OCR 0 rows ({staleCount}/{StaleLimit})");
                                }
                            }
                            else
                            {
                                staleCount = 0;
                                var reads = BuildPriceRows(ocrRows);
                                Log($"OCR {ocrRows.Count} rows → " +
                                    string.Join(" | ", reads.Select(r =>
                                        $"raw='{r.OcrText.Trim()}' y={r.CenterY} " +
                                        $"{(r.HasPrice ? $"HIT→'{r.Name}'" : "MISS")}")));

                                // "Any priced row this pass" both confirms a real exchange panel
                                // (combat effects / stray windows never resolve to a price) and feeds
                                // the lost-panel reset below.
                                bool hasPriced = reads.Any(r => r.HasPrice);
                                if (hasPriced)
                                {
                                    noPriceStreak = 0;
                                    if (!confirmedOpen)
                                    {
                                        confirmedOpen = true;
                                        suppressHintUntilConfirm = false;
                                        Log("panel CONFIRMED (priced row found)");
                                    }
                                }
                                else
                                {
                                    noPriceStreak++;
                                }

                                lastRows = MergeReads(slots, reads, now, Log);
                            }

                            // Lost-panel reset (brightness-independent): a panel we'd CONFIRMED that
                            // then yields no priced row for NoPriceCloseLimit passes has gone away —
                            // even when the brightness gate still reads "open" because the panel
                            // closed into a bright/white scene. Locked rows are otherwise sticky (a
                            // miss never unlocks them), so without this they'd keep showing the
                            // previous panel's prices on a false-positive bright frame. Clear them and
                            // re-arm hint suppression so nothing shows until OCR confirms a real
                            // priced row again.
                            if (confirmedOpen && noPriceStreak >= NoPriceCloseLimit)
                            {
                                Log($"confirmed panel lost priced rows for {noPriceStreak} passes — clearing slots");
                                slots.Clear();
                                lastRows = [];
                                confirmedOpen = false;
                                suppressHintUntilConfirm = true;
                                noPriceStreak = 0;
                            }
                        }
                    }
                    else
                    {
                        slots.Clear();
                        lastRows = [];
                        confirmedOpen = false;
                        staleCount = 0;
                        noPriceStreak = 0;
                    }

                    // "reading" = brightness says a panel is up but OCR hasn't confirmed prices yet.
                    // Suppressed straight after a dismiss until a real confirm (anti-flicker, see above).
                    bool reading = isOpen && !confirmedOpen && !suppressHintUntilConfirm;

                    // Show prices only once OCR has confirmed a real list, not on brightness alone.
                    IsShowing = confirmedOpen;
                    // Skip the cross-thread UpdateState when nothing actually changed since the last
                    // push — the loop ticks far faster than the displayed rows move. The HUD is derived
                    // from lastRows, so it can't change without the rows changing — no extra push gate.
                    if (!lastRows.SequenceEqual(lastPushedRows) || confirmedOpen != lastPushedConfirmed || reading != lastPushedReading)
                    {
                        PriceOverlayManager.UpdateState(lastRows, confirmedOpen, reading, BuildDebugHud(lastRows));
                        lastPushedRows = lastRows.ToArray();
                        lastPushedConfirmed = confirmedOpen;
                        lastPushedReading = reading;
                    }

                    topmostCounter++;
                    if (topmostCounter >= TopmostEveryN)
                    {
                        PriceOverlayManager.ForceTopmost();
                        topmostCounter = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR {ex.GetType().Name}: {ex.Message}");
            }

            var cycleMs = sw.ElapsedMilliseconds - cycleStart;
            var wait = (int)Math.Max(0, (isOpen ? OpenCycleMs : ClosedCycleMs) - cycleMs);
            if (wait > 0)
            {
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        IsShowing = false;
        PriceOverlayManager.Hide();
        Log("loop exited");
    }

    private IReadOnlyList<PriceRow> BuildPriceRows(IReadOnlyList<OcrRow> ocrRows)
    {
        var snap = _prices.Current;
        var snapshot = snap.Prices;
        var rows = new List<PriceRow>(ocrRows.Count);
        var newPositions = new Dictionary<string, int>(ocrRows.Count);

        // Invalidate the resolution cache when the price snapshot changed since the last build.
        // (Gem rows below are resolved independently and are NOT cached.)
        if (_prices.PriceGeneration != _cachedPriceGeneration)
        {
            _cachedPriceGeneration = _prices.PriceGeneration;
            _resolutionCache.Clear();
        }

        foreach (var row in ocrRows)
        {
            if (row.NormalizedName.Contains("runeshape"))
                continue;

            // Localized clients (#29): map the OCR'd name to its English price key up front, then run
            // the entire matcher (gem / easter-egg / exact / prefix / fuzzy) on the English `name`.
            // On an English client (or any item with no translation) this returns the input unchanged,
            // so everything below is byte-for-byte the original behaviour. Using the translated name as
            // the row identity also stabilises positions when the localized OCR spelling jitters.
            var name = _translator.Translate(row.NormalizedName);

            int stableY = row.CenterY;
            if (_lastPositions.TryGetValue(name, out int prevY) &&
                Math.Abs(prevY - row.CenterY) < 5)
                stableY = prevY;
            newPositions[name] = stableY;

            // Uncut gems (skill / spirit / support) are priced PER LEVEL, and adjacent levels differ
            // several-fold (e.g. spirit gem L18 ≈ 0.027 div vs L19 ≈ 0.143 div). The only things that
            // distinguish one gem line from another are the TYPE word and the LEVEL number, so we pin
            // both EXACTLY and deliberately skip the prefix/fuzzy fallbacks here: a single-character OCR
            // slip on the digit (or skill↔spirit) would otherwise lock a confidently-wrong, multiples-off
            // price. If the type or level can't be read cleanly, the row shows '?' until a clean read
            // arrives — better than guessing a neighbouring level.
            if (TryResolveGemKey(name, out var gemKey))
            {
                if (gemKey is not null && snapshot.TryGetValue(gemKey, out var gemEntry))
                {
                    if (gemEntry.HasMarketData)
                        rows.Add(new PriceRow(stableY, row.RawText, gemEntry.DivineValue, gemEntry.ExaltedValue,
                            true, row.Multiplier, gemKey, true, MultiplierExplicit: row.MultiplierExplicit));
                    else
                        rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true, row.Multiplier, gemKey, true, MemeKind.NoInfo, row.MultiplierExplicit));
                }
                else
                    // Recognised as an uncut gem but type+level didn't pin to a known price → '?', never fuzzy.
                    rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, false, row.Multiplier, name, MultiplierExplicit: row.MultiplierExplicit));
                continue;
            }

            // Easter eggs: certain OCR'd names render as a gag icon + caption instead of a price.
            // ExactMatch=true so they lock on the first read like a real priced row.
            //   "5x random currency" (the "5x" is stripped into the multiplier, leaving "random
            //    currency") → Mirror of Kalandra. "unique belt" → Headhunter.
            if (name.Contains("random") && name.Contains("currency"))
            {
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true, row.Multiplier, "random currency", true, MemeKind.Mirror, row.MultiplierExplicit));
                continue;
            }
            if (name.Contains("unique") && name.Contains("belt"))
            {
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true, row.Multiplier, "unique belt", true, MemeKind.Headhunter, row.MultiplierExplicit));
                continue;
            }

            // Resolve the OCR'd name to a price key: exact → prefix → fuzzy (edit distance).
            // The fuzzy step rescues single-character misreads ("viswn" → "vision"). The matched
            // key (not the noisy OCR text) is stored as the row Name so the same item locks even
            // when OCR jitters between passes.
            PriceEntry? entry;
            string matchedKey = name;
            bool exact = false;
            if (_resolutionCache.TryGetValue(name, out var cached))
            {
                // Reuse a previously resolved key (or a recorded miss). The same OCR'd names recur
                // on every pass while a panel is open, so this skips the dict scan + Levenshtein
                // work on all but the first pass. The Exact flag is preserved from the original
                // resolution so fuzzy high-confidence matches (score ≥ 0.92) still lock in 1 read
                // on subsequent passes — recalculating it as cachedKey == NormalizedName would
                // wrongly degrade them to needing 2 reads.
                if (cached.Key is not null && snapshot.TryGetValue(cached.Key, out entry))
                {
                    matchedKey = cached.Key;
                    exact = cached.Exact;
                }
                else
                {
                    entry = null;   // cached miss
                }
            }
            else
            {
                // OCR often misreads a name's letters as visually identical digits ("Olroth's" →
                // "01roth's", #43). Fold those digits back to letters and use the folded form for the
                // fallback lookups; keys hold no digits here, so the fold is safe. Skipped (lookup ==
                // name) when the name has no digit — the common case.
                string lookup = name.Any(char.IsDigit) ? NameNormalizer.DigitFold(name) : name;

                if (snapshot.TryGetValue(name, out entry))
                {
                    exact = true;
                }
                else if (lookup != name && snapshot.TryGetValue(lookup, out entry))
                {
                    // Digit-folded exact hit — as trustworthy as a plain exact match.
                    matchedKey = lookup;
                    exact = true;
                }
                else if (lookup.Length >= 10 &&
                         snapshot.Keys.Where(k => k.StartsWith(lookup, StringComparison.Ordinal))
                                      .MinBy(k => k.Length) is { } prefixKey)
                {
                    entry = snapshot[prefixKey];
                    matchedKey = prefixKey;
                }
                else if (lookup.Length >= 6 &&
                         BestFuzzy(snapshot, snap.KeysByLength, lookup) is { } fuzzy &&
                         snapshot.TryGetValue(fuzzy.Key, out entry))
                {
                    matchedKey = fuzzy.Key;
                    exact = fuzzy.Score >= HighConfidenceThreshold;
                }
                else
                {
                    entry = null;
                }
                // Cache the resolution: the matched key, or null to record a miss.
                _resolutionCache[name] = entry != null ? (matchedKey, exact) : (null, false);
            }

            if (entry != null)
            {
                if (entry.HasMarketData)
                    rows.Add(new PriceRow(stableY, row.RawText, entry.DivineValue, entry.ExaltedValue, true, row.Multiplier, matchedKey, exact, MultiplierExplicit: row.MultiplierExplicit));
                else
                    // Known item (matched in poe.ninja) but no trading data — show "no info".
                    rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true, row.Multiplier, matchedKey, true, MemeKind.NoInfo, row.MultiplierExplicit));
            }
            else
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, false, row.Multiplier, row.NormalizedName, MultiplierExplicit: row.MultiplierExplicit));
        }
        _lastPositions = newPositions;
        return rows;
    }

    // Pre-compiled regexes for gem detection (TryResolveGemKey runs on every OCR'd line).
    private static readonly Regex GemTypePattern = new(@"\b(skill|spirit|support)\b", RegexOptions.Compiled);
    private static readonly Regex GemLevelPattern = new(@"\blevel\s+(\d+)\b", RegexOptions.Compiled);

    // Minimum character-similarity (1 - editDistance/maxLen) for a fuzzy price match.
    // 0.84 lets ~2 wrong characters through on a 12+ char name, 1 on a ~6 char name —
    // enough to absorb typical OCR slips without matching an unrelated item.
    private const double FuzzyThreshold = 0.84;
    // Fuzzy matches at or above this score are trusted as much as exact matches (lock in 1 read
    // instead of 2). At 0.92 the edit distance is ≤1 char on a 12+ char name — a false positive
    // at this level is virtually impossible.
    private const double HighConfidenceThreshold = 0.92;

    // Closest price key to an OCR'd name by Levenshtein similarity, or null if nothing clears
    // FuzzyThreshold. Only candidates within ±3 of the name's length are considered (cheaper,
    // and a large length gap is never a near-match). The length-bucketed index avoids iterating
    // every key in the snapshot — we walk only the buckets near the name's length.
    // Returns the matched key AND its similarity score so the caller can trust high-confidence
    // matches (≥ HighConfidenceThreshold) as if they were exact.
    private static (string Key, double Score)? BestFuzzy(
        IReadOnlyDictionary<string, PriceEntry> snapshot,
        IReadOnlyDictionary<int, List<string>> keysByLength,
        string name)
    {
        string? best = null;
        double bestScore = FuzzyThreshold;   // must strictly exceed the threshold to win
        // Only check keys within ±3 of the name's length, using the pre-built index.
        for (int len = Math.Max(0, name.Length - 3); len <= name.Length + 3; len++)
        {
            if (!keysByLength.TryGetValue(len, out var keys)) continue;
            foreach (var key in keys)
            {
                int dist = Levenshtein(name, key);
                double score = 1.0 - (double)dist / Math.Max(name.Length, key.Length);
                if (score > bestScore) { bestScore = score; best = key; }
            }
        }
        return best is not null ? (best, bestScore) : null;
    }

    // Detect an uncut gem and pin its identity. Returns true when the name is an uncut gem (a type
    // word skill/spirit/support together with "gem"); the discriminating type word and "gem" are what
    // mark it, so a slip in the boilerplate words ("uncot", "levei") doesn't hide a gem. When a level
    // number is also present, `key` is the canonical price key with the type and level pinned exactly
    // (no fuzzy) — caller looks it up as-is. When the level can't be read, `key` is null so the caller
    // shows '?' rather than guessing an adjacent level (which can be several-fold off).
    internal static bool TryResolveGemKey(string normalizedName, out string? key)
    {
        key = null;
        if (!normalizedName.Contains("gem")) return false;
        var type = GemTypePattern.Match(normalizedName);
        if (!type.Success) return false;
        var lvl = GemLevelPattern.Match(normalizedName);
        if (lvl.Success) key = $"uncut {type.Groups[1].Value} gem level {lvl.Groups[1].Value}";
        return true;
    }

    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    // One display row per screen position. A slot locks onto a price once the same item name
    // is read on two consecutive passes, then stays fixed (noise can't dislodge it). Rows that
    // are still unpriced keep showing the latest attempt and get re-read every pass, so an early
    // misread no longer freezes a row — a later correct read upgrades it.
    internal sealed class RowSlot
    {
        public int Y;                    // stable display position (first-seen)
        public PriceRow Latest = null!;  // most recent read (shown, as unpriced, until locked)
        public bool Locked;              // a confirmed price is pinned
        public PriceRow LockedRow = null!;
        public string? PendingName;      // candidate price name awaiting a second confirming read
        public int PendingCount;
        public int Unseen;               // consecutive passes this slot wasn't matched
        // Per-row stack memory: the last stack size seen on THIS slot, kept briefly so a frame where
        // OCR drops the "Nx" marker doesn't flicker the price back to unit-only. Scoped to the slot
        // (a screen position), NOT the item name — so two rows of the SAME item at different stack
        // sizes (e.g. "2x" and "1x" of the same currency) can't cross-contaminate each other.
        public int RememberedMultiplier = 1;
        public DateTime RememberedExpiresUtc;
    }

    internal static IReadOnlyList<PriceRow> MergeReads(
        List<RowSlot> slots,
        IReadOnlyList<PriceRow> reads,
        DateTime nowUtc,
        Action<string>? log = null)
    {
        const int Tolerance = 20;   // px: how far a read can move and still be the same row
        const int Confirm = 2;      // matching fuzzy/prefix reads before a row locks (exact: 1)
        const int EvictAfter = 3;   // passes a slot can go unmatched before it's dropped
        const int QuantityMemoryMs = 1500;  // how long a seen stack multiplier survives an Nx dropout

        // Panel-switch detection: the user opened a different panel without the overlay closing.
        // Locked rows are otherwise sticky (a miss never unlocks them), so they'd keep showing the
        // previous panel's prices. If two or more locked positions now read a *different* priced
        // item, the content changed — drop only the changed slots so the new panel takes over.
        // (Previously this cleared ALL slots, which was too aggressive: OCR jitter on 2 fuzzy
        // matches could trigger a false panel-switch and wipe all locking progress.)
        var changedSlots = new List<RowSlot>();
        foreach (var read in reads)
        {
            if (!read.HasPrice) continue;
            var locked = slots.FirstOrDefault(s => s.Locked && Math.Abs(s.Y - read.CenterY) <= Tolerance);
            if (locked is not null && locked.LockedRow.Name != read.Name)
                changedSlots.Add(locked);
        }
        if (changedSlots.Count >= 2)
        {
            foreach (var s in changedSlots)
                slots.Remove(s);
            log?.Invoke($"panel switch detected ({changedSlots.Count} rows changed) — resetting changed slots only");
        }

        var matched = new HashSet<RowSlot>();
        foreach (var read in reads)
        {
            RowSlot? slot = null;
            int best = int.MaxValue;
            foreach (var s in slots)
            {
                if (matched.Contains(s)) continue;
                int d = Math.Abs(s.Y - read.CenterY);
                if (d <= Tolerance && d < best) { best = d; slot = s; }
            }
            if (slot is null)
            {
                slot = new RowSlot { Y = read.CenterY };
                slots.Add(slot);
            }
            matched.Add(slot);
            slot.Unseen = 0;
            slot.Latest = read;

            if (read.HasPrice)
            {
                if (slot.PendingName == read.Name) slot.PendingCount++;
                else { slot.PendingName = read.Name; slot.PendingCount = 1; }

                // Exact dictionary matches are trustworthy enough to lock immediately; only the
                // uncertain fuzzy/prefix matches need a second confirming read.
                int needed = read.ExactMatch ? 1 : Confirm;
                if (slot.PendingCount >= needed)
                {
                    if (!slot.Locked || slot.LockedRow.Name != read.Name)
                        log?.Invoke($"locked y={slot.Y} '{read.Name}'");

                    // Stack stickiness: an explicitly-read Nx always wins; otherwise a row that already
                    // locked onto a stack, or saw one within the memory window on THIS slot, keeps that
                    // multiplier through a pass where OCR drops the marker and reads a bare 1x.
                    int remembered = slot.RememberedExpiresUtc > nowUtc ? slot.RememberedMultiplier : 1;
                    int priorLocked = slot.Locked && slot.LockedRow.Name == read.Name ? slot.LockedRow.Multiplier : 1;
                    int effectiveMultiplier = ResolveMultiplierForDisplay(
                        read.Multiplier, read.MultiplierExplicit, priorLocked, remembered);

                    bool effectiveExplicit = read.MultiplierExplicit;
                    if (effectiveMultiplier > 1 && slot.Locked && slot.LockedRow.Name == read.Name)
                        effectiveExplicit = slot.LockedRow.MultiplierExplicit || read.MultiplierExplicit;

                    // Refresh THIS slot's memory whenever we believe the row is a stack, so a one-pass
                    // dropout keeps showing the stack total instead of flickering back to the unit price.
                    if (effectiveMultiplier > 1)
                    {
                        slot.RememberedMultiplier = effectiveMultiplier;
                        slot.RememberedExpiresUtc = nowUtc.AddMilliseconds(QuantityMemoryMs);
                    }

                    slot.Locked = true;
                    slot.LockedRow = read with
                    {
                        CenterY = slot.Y,
                        Multiplier = effectiveMultiplier,
                        MultiplierExplicit = effectiveExplicit,
                    };
                }
            }
            // A miss (read.HasPrice == false) does NOT reset the pending streak. A miss means
            // OCR couldn't resolve the name this pass — it's "no information", not "different item".
            // The streak resets only when a DIFFERENT priced name arrives (handled in the if-branch
            // above via PendingName comparison). Resetting on misses made fuzzy/prefix items un-
            // lockable whenever OCR alternated between a correct read and a fragmented read.
        }

        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (matched.Contains(slots[i])) continue;
            if (++slots[i].Unseen > EvictAfter) slots.RemoveAt(i);
        }

        var display = new List<PriceRow>(slots.Count);
        foreach (var s in slots.OrderBy(s => s.Y))
        {
            display.Add(s.Locked
                ? s.LockedRow
                : s.Latest with { CenterY = s.Y, HasPrice = false, DivineValue = 0m, ExaltedValue = 0m });
        }
        return display;
    }

    // Decide which stack multiplier to display for a row. An explicit "Nx" read this pass always
    // wins. Failing that, a row that already locked onto a stack keeps it; failing that, a stack
    // seen recently (memory window, non-explicit read only) is reused. Otherwise it's a plain 1x.
    internal static int ResolveMultiplierForDisplay(
        int readMultiplier,
        bool readMultiplierExplicit,
        int priorLockedMultiplier,
        int rememberedMultiplier)
    {
        if (readMultiplier > 1) return readMultiplier;
        if (priorLockedMultiplier > 1) return priorLockedMultiplier;
        if (!readMultiplierExplicit && rememberedMultiplier > 1) return rememberedMultiplier;
        return readMultiplier;
    }

    // F3-only diagnostic line: how many rows OCR produced, how many are priced, and how the priced
    // stacks split between an explicit Nx read and one carried by quantity memory.
    private static string BuildDebugHud(IReadOnlyList<PriceRow> rows)
    {
        int priced = rows.Count(r => r.HasPrice);
        int explicitQty = rows.Count(r => r.HasPrice && r.Multiplier > 1 && r.MultiplierExplicit);
        int memoryQty = rows.Count(r => r.HasPrice && r.Multiplier > 1 && !r.MultiplierExplicit);
        return $"rows={rows.Count} priced={priced} qty-exp={explicitQty} qty-mem={memoryQty}";
    }

    public void Dispose()
    {
        StopAndWait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
        _capture.Dispose();
    }
}
