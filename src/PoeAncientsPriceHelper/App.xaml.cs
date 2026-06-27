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
    private TaskPoolGlobalHook? _hook;
    private bool _leftCtrlDown;

    // The currently-bound hotkeys, matched on every key event. MainWindow pushes the persisted values
    // once config is loaded and again on each rebind; until then the historical defaults keep working.
    private static volatile KeyCode _startStopKey = HotkeyBinding.DefaultStartStop;
    private static volatile KeyCode _debugKey = HotkeyBinding.DefaultDebug;
    private static volatile KeyCode _calibrateKey = HotkeyBinding.DefaultCalibrate;
    internal static void SetStartStopKey(KeyCode key) => _startStopKey = key;
    internal static void SetDebugKey(KeyCode key) => _debugKey = key;
    internal static void SetCalibrateKey(KeyCode key) => _calibrateKey = key;

    // One-shot rebind capture. While active, the hook swallows keys from their normal actions and the
    // next available key becomes the binding. Outcomes are reported via the callback, marshalled to the
    // UI thread. Reserved keys (or a key already bound to another action) report back but keep
    // listening; Esc cancels. _captureAction is the binding being replaced, so its own current key
    // doesn't count as a collision.
    internal enum CaptureOutcome { Captured, Cancelled, Reserved }
    private static volatile bool _capturing;
    private static volatile HotkeyBinding.Action _captureAction;
    private static Action<CaptureOutcome, KeyCode>? _captureCallback;

    internal static void BeginHotkeyCapture(HotkeyBinding.Action action, Action<CaptureOutcome, KeyCode> onEvent)
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

        _hook = new TaskPoolGlobalHook();
        _hook.KeyPressed += (_, ev) =>
        {
            if (_capturing) return;   // rebind in progress: swallow keys from their normal actions
            // ESC closes the in-game panel — hide the overlay the instant the key goes down.
            if (ev.Data.KeyCode == KeyCode.VcEscape) DismissOverlay();
            else if (ev.Data.KeyCode is KeyCode.VcLeftControl) _leftCtrlDown = true;
        };
        _hook.KeyReleased += (_, ev) =>
        {
            var code = ev.Data.KeyCode;
            if (_capturing) { HandleCapture(code); return; }   // swallow + consume for rebind
            // Act on release (not press) so holding a key can't auto-repeat-fire many times.
            if (code == _debugKey) PriceOverlayManager.ToggleDebug();
            else if (code == _calibrateKey) InvokeCalibrate();
            else if (code == _startStopKey) InvokeStartStopToggle();
            else if (code is KeyCode.VcLeftControl) _leftCtrlDown = false;
        };
        // Left-Ctrl + left click (the in-game "purchase" gesture) also dismisses the overlay.
        _hook.MousePressed += (_, ev) =>
        {
            if (_capturing) return;
            if (ev.Data.Button == MouseButton.Button1 && _leftCtrlDown) DismissOverlay();
        };
        _ = _hook.RunAsync();
    }

    // Runs on a hook thread-pool thread. Esc cancels; a reserved key or one already bound to another
    // action reports back but keeps listening; anything else is the new binding. The callback is
    // marshalled to the UI thread.
    private static void HandleCapture(KeyCode code)
    {
        if (code == KeyCode.VcEscape) { FinishCapture(CaptureOutcome.Cancelled, code); return; }
        if (HotkeyBinding.IsReserved(code) || CollidesWithOtherAction(code, _captureAction))
        {
            ReportCapture(CaptureOutcome.Reserved, code);
            return;
        }
        FinishCapture(CaptureOutcome.Captured, code);
    }

    // True if the key is already bound to one of the two actions that isn't the one being rebound —
    // binding it would make a single press fire two actions. The action being rebound is skipped so
    // re-confirming its own current key is allowed.
    private static bool CollidesWithOtherAction(KeyCode code, HotkeyBinding.Action target)
    {
        if (target != HotkeyBinding.Action.StartStop && code == _startStopKey) return true;
        if (target != HotkeyBinding.Action.Debug && code == _debugKey) return true;
        if (target != HotkeyBinding.Action.Calibrate && code == _calibrateKey) return true;
        return false;
    }

    private static void FinishCapture(CaptureOutcome outcome, KeyCode code)
    {
        _capturing = false;
        var cb = _captureCallback;
        _captureCallback = null;
        ReportTo(cb, outcome, code);
    }

    private static void ReportCapture(CaptureOutcome outcome, KeyCode code) =>
        ReportTo(_captureCallback, outcome, code);

    private static void ReportTo(Action<CaptureOutcome, KeyCode>? cb, CaptureOutcome outcome, KeyCode code)
    {
        if (cb is null) return;
        Current?.Dispatcher.BeginInvoke(() => cb(outcome, code));
    }

    private static void InvokeStartStopToggle() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.ToggleStartStop());

    private static void InvokeCalibrate() =>
        Current?.Dispatcher.BeginInvoke(() => (Current.MainWindow as MainWindow)?.RunCalibration());

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

            var scanner = new OcrScanner(Out, debug: true);   // --ocr-test wants the dump
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
