using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using SharpHook;
using SharpHook.Data;

namespace PoeAncientsPriceHelper;

public partial class App : System.Windows.Application
{
    internal static bool DebugMode { get; private set; }

    // Set by MainWindow once an update is downloaded/staged. Held statically (not read off MainWindow)
    // because under ShutdownMode=OnMainWindowClose the main window is already closed when OnExit runs,
    // so Application.MainWindow is null there — reading it always missed the staged update and the
    // silent on-exit apply never fired (#14).
    internal static Velopack.UpdateManager? PendingUpdateManager;
    internal static Velopack.UpdateInfo? PendingUpdate;
    // A single keyboard-only global hook, live for the whole session. It is deliberately NOT a
    // keyboard+mouse hook: a global low-level mouse hook routes every mouse-move through us and stutters
    // the cursor on high-refresh / high-polling setups (#52), and libuiohook can't reliably swap a
    // hook's scope or restart mid-session (dispose→recreate goes deaf nondeterministically). So no mouse
    // hook exists at all; the one thing it was used for — the Ctrl+left-click dismiss gesture — is
    // handled by _clickWatcher instead.
    private static TaskPoolGlobalHook? _hook;

    // Poll-based Ctrl+left-click detector, active only while an overlay is on screen (the only time the
    // dismiss gesture applies). Replaces the global mouse hook — GetAsyncKeyState is cheap and touches no
    // hook, so there's zero cursor cost. See UpdateClickWatcher / PollClick. (#52)
    private static readonly object _clickGate = new();
    private static System.Threading.Timer? _clickWatcher;
    private static bool _prevLButtonDown;
    private const int ClickPollMs = 25;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01;
    private const int VK_LCONTROL = 0xA2;

    // The currently-bound hotkeys, matched on every key event. MainWindow pushes the persisted values
    // once config is loaded and again on each rebind; until then the historical defaults keep working.
    // Held as one immutable record behind a single volatile reference so the hook thread always reads a
    // consistent set (a Chord is a two-field struct — it can't be `volatile` and a plain field could tear
    // across threads). Writers are UI-thread only, so the read-modify-write in the setters can't race.
    private sealed record HotkeyBindings(Chord StartStop, Chord Debug, Chord Calibrate);
    private static volatile HotkeyBindings _bindings = new(
        HotkeyBinding.DefaultStartStop, HotkeyBinding.DefaultDebug, HotkeyBinding.DefaultCalibrate);
    internal static void SetStartStopChord(Chord chord) => _bindings = _bindings with { StartStop = chord };
    internal static void SetDebugChord(Chord chord) => _bindings = _bindings with { Debug = chord };
    internal static void SetCalibrateChord(Chord chord) => _bindings = _bindings with { Calibrate = chord };

    // Live modifier state, updated as modifier keys go down/up so a key release can be matched as a full
    // chord (#46). Enum with an int base ⇒ safe to mark volatile for the hook thread.
    private static volatile Modifiers _mods = Modifiers.None;

    // One-shot rebind capture. While active, the hook swallows keys from their normal actions and the
    // next available key becomes the binding. Outcomes are reported via the callback, marshalled to the
    // UI thread. Reserved keys (or a key already bound to another action) report back but keep
    // listening; Esc cancels. _captureAction is the binding being replaced, so its own current key
    // doesn't count as a collision.
    internal enum CaptureOutcome { Captured, Cancelled, Reserved }
    private static volatile bool _capturing;
    private static volatile HotkeyBinding.Action _captureAction;
    private static Action<CaptureOutcome, Chord>? _captureCallback;

    internal static void BeginHotkeyCapture(HotkeyBinding.Action action, Action<CaptureOutcome, Chord> onEvent)
    {
        _captureAction = action;
        _captureCallback = onEvent;
        _capturing = true;
    }

    // Single-instance guard. Held for the lifetime of the process; a second launch fails to
    // create it, focuses the already-running window, and exits. Without this, every extra launch
    // is a full second app that also receives the global F3 hook and paints its own overlay —
    // which is how testers ended up seeing two or three calibration boxes at once.
    private static Mutex? _instanceMutex;
    private const string InstanceMutexName = @"Global\PoeAncientsPriceHelper.SingleInstance";

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    // Hide the overlay immediately (if it's up because a panel was detected) and pause detection
    // briefly so the closing panel's fading brightness can't re-trigger it.
    private static void DismissOverlay()
    {
        if (!ScanEngine.IsShowing) return;
        PriceOverlayManager.HideNow();   // instant, off the scan loop
        ScanEngine.RequestDismiss();     // keep it hidden until the panel actually closes
    }

    // ESC / Left-Ctrl+click also dismiss the rumour overlay: hide it now and latch it dismissed so the
    // WORLD-gated loop keeps it hidden until the rumour panel actually closes (then it re-arms).
    private static void DismissRumour()
    {
        RumourOverlayManager.HideNow();
        RumourScanEngine.RequestDismiss();
    }

    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Headless OCR repro: run the real OCR pipeline on a screenshot and print what it sees.
        //   PoeAncientsPriceHelper.exe --ocr-test <imagePath>
        if (e.Args.Length >= 2 && e.Args[0] == "--ocr-test")
        {
            RunOcrTest(e.Args[1]);
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);

        // Refuse to start a second copy: only one instance owns the global hook + overlay.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            FocusExistingInstance();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--debug"))
        {
            DebugMode = true;
            if (!AttachConsole(-1)) AllocConsole(); // attach to parent terminal, else open new window
            Console.WriteLine("[Debug] PoeAncientsPriceHelper starting");
        }

        // One keyboard-only hook for the whole session — see the field comment for why it's never a
        // mouse hook and never swapped. TaskPoolGlobalHook (not SimpleGlobalHook): the simple hook runs
        // handlers on the low-level-hook thread, and a handler slower than LowLevelHooksTimeout (~300ms)
        // makes Windows silently drop the hook, which killed all hotkeys in the first #52 test build.
        _hook = new TaskPoolGlobalHook(parallelismLevel: 1, GlobalHookType.Keyboard);
        _hook.KeyPressed += (_, ev) =>
        {
            var code = ev.Data.KeyCode;
            var mod = HotkeyBinding.ModifierOf(code);
            if (mod != Modifiers.None) _mods |= mod;   // track modifier state even during a rebind capture
            if (_capturing) return;   // rebind in progress: swallow keys from their normal actions
            // ESC closes the in-game panel — hide the overlay the instant the key goes down.
            if (code == KeyCode.VcEscape) { DismissOverlay(); DismissRumour(); }
        };
        _hook.KeyReleased += (_, ev) =>
        {
            var code = ev.Data.KeyCode;
            var mod = HotkeyBinding.ModifierOf(code);
            var heldMods = _mods;                       // modifiers held at the instant of release
            if (mod != Modifiers.None) _mods &= ~mod;   // clear the released modifier
            // A modifier alone never triggers an action, and never completes a rebind — a chord must end
            // on a non-modifier key. `heldMods` still carries the other modifiers held with this key.
            if (mod != Modifiers.None) return;

            var chord = new Chord(code, heldMods);
            if (_capturing) { HandleCapture(chord); return; }   // swallow + consume for rebind
            // Act on release (not press) so holding a key can't auto-repeat-fire many times. Exact-chord
            // match: `Q` and `Ctrl+Q` are distinct and never fire each other (#46).
            var b = _bindings;
            if (chord == b.Debug) FireHotkey(chord, PriceOverlayManager.ToggleDebug);
            else if (chord == b.Calibrate) FireHotkey(chord, InvokeCalibrate);
            else if (chord == b.StartStop) FireHotkey(chord, InvokeStartStopToggle);
            // Debug-only one-shot rumour read (#34 spine). F8 triggers a full-screen detect + overlay;
            // the WORLD-gated auto-detect loop (#35) supersedes this manual trigger.
            else if (DebugMode && chord == new Chord(KeyCode.VcF8)) InvokeRumourScan();
        };
        _ = _hook.RunAsync();
    }

    // Swallows a duplicate of the SAME hotkey within a short window. Some keyboards / overlay utilities
    // emit a key twice in quick succession, which on Start/Stop reads as an instant stop-then-restart
    // (#52 follow-up — a tester's 360 Hz setup double-fired the default F5). Different hotkeys are
    // unaffected. Runs on the single sequential hook thread (parallelismLevel 1), so no locking needed.
    private const int HotkeyRepeatGuardMs = 400;
    private static Chord _lastFiredChord;
    private static long _lastFiredTick;

    private static void FireHotkey(Chord chord, System.Action action)
    {
        long now = Environment.TickCount64;
        if (chord == _lastFiredChord && now - _lastFiredTick < HotkeyRepeatGuardMs) return;
        _lastFiredChord = chord;
        _lastFiredTick = now;
        action();
    }

    // Starts/stops the Ctrl+left-click watcher as overlays show/hide. Called from the scan loops on real
    // visibility transitions (they own _showing; the hotkey hook only latches _dismissed). Runs the poll
    // only while an overlay is on screen — the only window in which the "purchase-gesture" dismiss can
    // apply — so normal play pays nothing. (#52)
    internal static void UpdateClickWatcher()
    {
        bool wantWatch = ScanEngine.IsShowing || RumourScanEngine.IsShowing;
        lock (_clickGate)
        {
            if (wantWatch && _clickWatcher is null)
            {
                // Seed with the current button state so a click already in progress when the overlay
                // appears isn't counted as a fresh press.
                _prevLButtonDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                _clickWatcher = new System.Threading.Timer(_ => PollClick(), null, ClickPollMs, ClickPollMs);
            }
            else if (!wantWatch && _clickWatcher is not null)
            {
                _clickWatcher.Dispose();
                _clickWatcher = null;
            }
        }
    }

    // Left-Ctrl + left click (the in-game "purchase" gesture) also dismisses the overlay. Edge-detected
    // off GetAsyncKeyState so we fire once per press, not every poll. Cheap enough to run at ~40 Hz while
    // an overlay is up; DismissOverlay/DismissRumour no-op when nothing is showing.
    private static void PollClick()
    {
        bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool ctrl = (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
        if (down && !_prevLButtonDown && ctrl) { DismissOverlay(); DismissRumour(); }
        _prevLButtonDown = down;
    }

    // Runs on a hook thread-pool thread. Esc cancels; a reserved key or one already bound to another
    // action reports back but keeps listening; anything else is the new binding. The callback is
    // marshalled to the UI thread.
    private static void HandleCapture(Chord chord)
    {
        if (chord.Key == KeyCode.VcEscape) { FinishCapture(CaptureOutcome.Cancelled, chord); return; }
        if (HotkeyBinding.IsReserved(chord.Key) || CollidesWithOtherAction(chord, _captureAction))
        {
            ReportCapture(CaptureOutcome.Reserved, chord);
            return;
        }
        FinishCapture(CaptureOutcome.Captured, chord);
    }

    // True if the chord is already bound to one of the two actions that isn't the one being rebound —
    // binding it would make a single press fire two actions. The action being rebound is skipped so
    // re-confirming its own current chord is allowed. Compares the whole chord, so `Q` and `Ctrl+Q`
    // are treated as different bindings (#46).
    private static bool CollidesWithOtherAction(Chord chord, HotkeyBinding.Action target)
    {
        var b = _bindings;
        if (target != HotkeyBinding.Action.StartStop && chord == b.StartStop) return true;
        if (target != HotkeyBinding.Action.Debug && chord == b.Debug) return true;
        if (target != HotkeyBinding.Action.Calibrate && chord == b.Calibrate) return true;
        return false;
    }

    private static void FinishCapture(CaptureOutcome outcome, Chord chord)
    {
        _capturing = false;
        var cb = _captureCallback;
        _captureCallback = null;
        ReportTo(cb, outcome, chord);
    }

    private static void ReportCapture(CaptureOutcome outcome, Chord chord) =>
        ReportTo(_captureCallback, outcome, chord);

    private static void ReportTo(Action<CaptureOutcome, Chord>? cb, CaptureOutcome outcome, Chord chord)
    {
        if (cb is null) return;
        Current?.Dispatcher.BeginInvoke(() => cb(outcome, chord));
    }

    private static void InvokeStartStopToggle() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.ToggleStartStop());

    private static void InvokeCalibrate() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.RunCalibration());

    private static void InvokeRumourScan() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.RunRumourScanOnce());

    protected override void OnExit(ExitEventArgs e)
    {
        // If an update was staged this session but the user didn't click "Update now", apply it
        // silently as we close so the next launch is already on the new version. Update.exe waits for
        // this process to exit, swaps current\, and does NOT relaunch (the user chose to quit).
        // Best-effort: if nothing is staged or the updater isn't available, just exit normally.
        try
        {
            if (PendingUpdateManager is { } mgr && PendingUpdate is { } update)
            {
                AppPaths.LogUpdate($"OnExit: applying staged v{update.TargetFullRelease.Version} (WaitExitThenApplyUpdates)");
                mgr.WaitExitThenApplyUpdates(update, silent: true, restart: false);
                AppPaths.LogUpdate("OnExit: WaitExitThenApplyUpdates returned (Update.exe will swap after exit)");
            }
            else
            {
                AppPaths.LogUpdate("OnExit: nothing staged to apply");
            }
        }
        catch (Exception ex) { AppPaths.LogUpdate($"OnExit: apply failed: {ex.GetType().Name}: {ex.Message}"); }

        _hook?.Dispose();
        lock (_clickGate) { _clickWatcher?.Dispose(); _clickWatcher = null; }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // Bring the already-running instance's window to the foreground so the user gets feedback that
    // the app is up (instead of "nothing happened, click again" — which spawned the extra copies).
    private static void FocusExistingInstance()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
                break;
            }
        }
        catch { /* best-effort focus; the guard still prevents the second instance */ }
    }

    // Relaunch with --debug so a user can capture diagnostics (scan_log.txt / debug_ocr.png) for a bug
    // report — the old debug.cmd no longer ships with the Velopack build, so this is the only in-app way
    // to turn logging on. The single-instance mutex is released first, on the UI thread that created it
    // (the same thread as the caller), so the relaunched copy can take it; otherwise it would treat us as
    // the running instance, focus our window, and exit — leaving nothing running. If the spawn fails we
    // re-acquire the mutex so the guard is restored and the still-running app stays single-instance.
    // Returns false if the exe path is unknown or the process failed to start.
    internal static bool RelaunchWithDebug()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try { _instanceMutex?.ReleaseMutex(); } catch { /* not owned / already released */ }
        _instanceMutex?.Dispose();
        _instanceMutex = null;

        try
        {
            Process.Start(new ProcessStartInfo(exe, "--debug") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Diagnostics] relaunch with --debug failed: {ex.Message}");
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _);   // restore the guard
            return false;
        }
    }

    private static void RunOcrTest(string imagePath)
    {
        var outPath = System.IO.Path.Combine(AppPaths.DataDir, "ocr_test.txt");
        var lines = new List<string>();
        void Out(string s) => lines.Add(s);
        try
        {
            var config = ConfigStore.Load();
            var r = config.RegionRect;
            Out($"[ocr-test] image='{imagePath}' region={r}");
            using var full = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(imagePath);
            Out($"[ocr-test] image size {full.Width}x{full.Height}");

            // Crop the calibrated region (or use the whole image if it's already the region).
            var rect = System.Drawing.Rectangle.Intersect(
                new System.Drawing.Rectangle(0, 0, full.Width, full.Height), r);
            if (rect.Width <= 0 || rect.Height <= 0) rect = new System.Drawing.Rectangle(0, 0, full.Width, full.Height);
            using var region = new System.Drawing.Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(region))
                g.DrawImage(full, new System.Drawing.Rectangle(0, 0, rect.Width, rect.Height), rect, System.Drawing.GraphicsUnit.Pixel);

            var scanner = new OcrScanner(Out, debug: true, config.GameLanguage);   // --ocr-test wants the dump + the game-language recognizer
            var rows = scanner.Scan(region);
            Out($"[ocr-test] merged {rows.Count} rows:");
            foreach (var row in rows)
                Out($"    y={row.CenterY} mult={row.Multiplier} norm='{row.NormalizedName}' raw='{row.RawText}'");
        }
        catch (Exception ex)
        {
            Out($"[ocr-test] ERROR {ex}");
        }
        try { System.IO.File.WriteAllLines(outPath, lines); } catch { }
    }
}
