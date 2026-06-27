using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using SharpHook.Data;
using Velopack;
using Velopack.Sources;

namespace PoeAncientsPriceHelper;

public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private PriceRepository? _repo;
    private IconCache? _icons;
    private ScanEngine? _engine;
    // 15s cap so a stalled poe.ninja/poecdn connection can't hang a whole fetch cycle for the
    // default 100s. Per-fetch cancellation (shutdown) is handled inside PriceRepository.
    // HTTP/2 + compression enabled for faster parallel fetches (5 concurrent requests multiplexed
    // over a single connection instead of 5 separate TCP handshakes).
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
    };
    private bool _loading;
    // Reentrancy guard for LeagueBox_SelectionChanged → StartupAsync (rapid league changes could
    // otherwise overlap and dispose repo/icons mid-fetch). Also remembers whether the scanner was
    // running before a league change so StartupAsync can restart it against the new repo/icons.
    private bool _startingUp;
    private bool _engineWasRunning;

    // Minimize-to-tray (#2). The window hides to a tray icon on minimize and restores from it; the X
    // button still fully exits. Scanning is independent of this window, so it keeps running in the tray.
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _trayBalloonShown;

    // All three hotkeys (Start/Stop, Debug, Calibrate) now live on the App-level SharpHook hook and are
    // user-configurable — no Win32 RegisterHotKey here anymore.

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v is not null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _config = ConfigStore.Load();
        PopulateFields();
        // When already running in debug mode the relaunch is pointless — repurpose the link to open the
        // folder the logs land in. App.DebugMode is settled by now (it's set in App.OnStartup, before the
        // window's Loaded fires).
        if (App.DebugMode)
        {
            DiagnosticsLink.Text = "Open logs";
            DiagnosticsLink.ToolTip = "Open the folder with scan_log.txt and debug_ocr.png";
        }
        await StartupAsync();
        // Auto-start QoL: with a calibrated region and the option enabled, begin scanning and drop
        // straight to the tray, so the user just opens the app and it runs (saving the Start + minimize
        // clicks). Skipped under --debug (keep the window and console visible for troubleshooting) and
        // when not calibrated (the user has to calibrate first, so the window must stay up for input).
        // _engine is null here on a fresh load (StartupAsync only restarts it across a league change).
        if (_config.AutoStart && _config.IsCalibrated && !App.DebugMode && _engine is null)
        {
            ToggleStartStop();                     // start the engine; flips the button to Stop
            WindowState = WindowState.Minimized;   // OnStateChanged hides to tray + shows the balloon once
        }
        // Fire-and-forget, once per launch (not inside StartupAsync, which re-runs on league change).
        // A slow/hung GitHub response must never delay the price fetch or the Start button.
        _ = CheckForUpdatesAsync();
    }

    // Check GitHub Releases for a newer build via Velopack on startup AND on every 30-min price
    // refresh (see OnPricesUpdated), so a release published while the app is left running gets picked
    // up without a restart. If one is found, eagerly download/stage it in the background so both
    // "Update now" (the link below) and the silent apply-on-exit (#14) are instant. Only works from an
    // installed build — in a dev/unpacked run UpdateManager.IsInstalled is false and this no-ops. Any
    // failure (offline, rate-limited, not installed) is swallowed: the link just stays hidden, exactly
    // as the old check did. Stable only (prerelease: false). The manager + staged UpdateInfo are
    // retained for the on-exit apply (#14).
    private UpdateManager? _updateManager;
    private UpdateInfo? _stagedUpdate;
    // 0/1 reentrancy flag (via Interlocked): the startup check can overlap the first timer tick, and a
    // slow GitHub response can outlast the 30-min interval — either way only one check runs at a time.
    private int _updateCheckInFlight;

    // Update feed: GitHub Releases in production. If POEPRICE_UPDATE_FEED names a local folder (a
    // `vpk pack` output dir), read from it via SimpleFileSource instead — that lets the full
    // check → download → apply → restart cycle (and the on-exit apply) be tested from disk on one
    // machine, with no public release. Run the *installed* app (Setup.exe) with the var set and a
    // newer-versioned package in the folder. See .claude/issues/15.
    private const string GithubRepoUrl = "https://github.com/pedro-quiterio/PoeAncientsPriceHelper";

    private static IUpdateSource ResolveUpdateSource()
    {
        var localFeed = Environment.GetEnvironmentVariable("POEPRICE_UPDATE_FEED");
        if (!string.IsNullOrWhiteSpace(localFeed))
            return new SimpleFileSource(new DirectoryInfo(localFeed));
        return new GithubSource(GithubRepoUrl, null, prerelease: false);
    }

    private async Task CheckForUpdatesAsync()
    {
        // A build is already staged and waiting to apply (on "Update now" or on exit) — re-checking
        // every 30 min would just re-download the same release. Stop once we've found one.
        if (_stagedUpdate is not null) return;
        // Drop this check if one is already running (startup overlapping the first tick, or a slow
        // check spanning a tick). Exchange returns the prior value: 1 means a check is already in flight.
        if (Interlocked.Exchange(ref _updateCheckInFlight, 1) == 1) return;
        try
        {
            // Disable deltas (MaximumDeltasBeforeFallback < 0 → download the full package). Reconstructing
            // a full release from a delta during the otherwise-silent background DownloadUpdatesAsync
            // spawns Velopack's Update.exe, which flashes a visible window — seen on the first delta
            // update (3.0.0 → 3.1.0). The full package is a larger download but keeps the background
            // stage truly silent; the user only ever sees the "Update now" link.
            var mgr = new UpdateManager(ResolveUpdateSource(),
                new UpdateOptions { MaximumDeltasBeforeFallback = -1 });
            AppPaths.LogUpdate($"check start; IsInstalled={mgr.IsInstalled}");
            if (!mgr.IsInstalled) return;   // dev / unpacked run — nothing to update

            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) { AppPaths.LogUpdate("no update available"); return; }

            AppPaths.LogUpdate($"found v{info.TargetFullRelease.Version}; downloading…");
            await mgr.DownloadUpdatesAsync(info);   // stage now so applying is instant
            _updateManager = mgr;
            _stagedUpdate = info;
            App.PendingUpdateManager = mgr;   // App.OnExit reads these — MainWindow is already gone by then
            App.PendingUpdate = info;
            AppPaths.LogUpdate($"staged v{info.TargetFullRelease.Version} — ready to apply");

            var version = info.TargetFullRelease.Version;
            _ = Dispatcher.BeginInvoke(() =>
            {
                UpdateLink.Text = $"⬆ Update now: v{version} — click to install & restart";
                UpdateLink.Visibility = Visibility.Visible;
                // Test seam (parallels POEPRICE_UPDATE_FEED): drive the real "Update now" relaunch path
                // without a synthetic mouse click. Never set in production.
                if (Environment.GetEnvironmentVariable("POEPRICE_TEST_UPDATE_NOW") == "1")
                {
                    AppPaths.LogUpdate("test seam: auto-invoking Update now");
                    ApplyUpdateNow();
                }
            });
        }
        catch (Exception ex) { AppPaths.LogUpdate($"check failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _updateCheckInFlight, 0); }
    }

    private void CreditsLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        new CreditsWindow { Owner = this }.ShowDialog();
    }

    private void UpdateLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ApplyUpdateNow();

    // "Diagnostics" link. In a normal run it offers to restart with --debug so the user can capture
    // scan_log.txt / debug_ocr.png for a bug report (the old debug.cmd no longer ships with the Velopack
    // build). When already running in debug mode it instead opens the folder those files land in.
    private void DiagnosticsLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (App.DebugMode) { OpenDataFolder(); return; }

        var choice = System.Windows.MessageBox.Show(this,
            "Restart with diagnostics logging enabled?\n\n" +
            "A console window will open and detailed logs (scan_log.txt and debug_ocr.png) will be " +
            "written to your data folder so you can attach them to a bug report.\n\n" +
            "The app will close and reopen now.",
            "Diagnostics", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (choice != System.Windows.MessageBoxResult.OK) return;

        if (App.RelaunchWithDebug())
            Close();   // routes through Window_Closing for a clean shutdown; the new --debug copy takes over
        else
            System.Windows.MessageBox.Show(this,
                "Couldn't restart in diagnostics mode. You can launch the app from a terminal with the " +
                "--debug switch instead.",
                "Diagnostics", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Diagnostics] open data folder failed: {ex.Message}");
        }
    }

    // Apply the already-staged update and relaunch into the new version. Invoked by the "Update now"
    // link, and by the POEPRICE_TEST_UPDATE_NOW test seam so the relaunch path is exercised directly.
    internal void ApplyUpdateNow()
    {
        if (_updateManager is null || _stagedUpdate is null) return;
        try
        {
            AppPaths.LogUpdate($"applying v{_stagedUpdate.TargetFullRelease.Version} + restart (Update now)");
            _updateManager.ApplyUpdatesAndRestart(_stagedUpdate);   // instant — already staged
        }
        catch (Exception ex)
        {
            AppPaths.LogUpdate($"apply-now failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"[Update] apply failed: {ex.Message}");
        }
    }

    private void PopulateFields()
    {
        _loading = true;
        LeagueBox.ItemsSource = _config.AvailableLeagues;
        LeagueBox.SelectedItem = _config.AvailableLeagues.Contains(_config.LeagueName)
            ? _config.LeagueName
            : _config.AvailableLeagues.FirstOrDefault();
        // Arm the global hook with all three persisted bindings. The keybind UI lives in the Settings
        // window now, but the hook must be armed at startup so the hotkeys work before it's ever opened.
        App.SetStartStopKey(HotkeyBinding.Parse(_config.StartStopHotkey));
        App.SetDebugKey(HotkeyBinding.Parse(_config.DebugHotkey));
        App.SetCalibrateKey(HotkeyBinding.Parse(_config.CalibrateHotkey));
        UpdateRegionLabel();
        ThemePresets.Apply(ThemePresets.Resolve(_config.Theme));
        _loading = false;
    }

    // Opens the modal Settings window. It edits the same _config instance and persists each change, so
    // nothing needs syncing back here: theme is applied app-wide live, hotkey rebinds re-arm the hook,
    // and capture/auto-start are read straight from _config when next needed.
    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_config) { Owner = this }.ShowDialog();

    private void UpdateRegionLabel()
    {
        RegionLabel.Text = _config.IsCalibrated
            ? $"x={_config.RegionX} y={_config.RegionY} {_config.RegionWidth}×{_config.RegionHeight}"
            : "Not calibrated";
    }

    private async Task StartupAsync()
    {
        StatusLabel.Text = "Fetching prices from poe.ninja…";
        StartStopButton.IsEnabled = false;

        // Stop the scanner before disposing the repo/icons it depends on (league change, initial load).
        // Otherwise a running ScanEngine keeps referencing the OLD repo (stale prices) and icons
        // (disposed IconCache → crashes/stale icons).
        _engineWasRunning = _engine is { IsRunning: true };
        if (_engine is not null)
        {
            _engine.StopAndWait(TimeSpan.FromSeconds(2));
            _engine.Dispose();
            _engine = null;
        }

        _repo?.Dispose();
        _icons?.Dispose();

        _repo = new PriceRepository(_http);
        _repo.PricesUpdated += OnPricesUpdated;   // keep the "last fetch" label live on each refresh
        _icons = new IconCache(_http);

        await Task.WhenAll(
            _repo.InitialFetchAsync(_config),
            _icons.LoadAsync());

        _repo.StartAutoRefresh(_config);

        UpdateStatusLabel();
        StartStopButton.IsEnabled = _config.IsCalibrated;

        // If the scanner was running before the league change, restart it with the new repo/icons.
        if (_engineWasRunning && _config.IsCalibrated)
        {
            _engine = new ScanEngine(_config, _repo, _icons, CreateCaptureBackend());
            _engine.Start();
            StartStopButton.Content = "Stop";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkRed;
        }
        else
        {
            StartStopButton.Content = "Start";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkGreen;
        }
        _engineWasRunning = false;
    }

    // The 30-min background refresh fires on a thread-pool thread — marshal to the UI thread
    // before touching the label. (Previously the label was set once at startup and never updated,
    // so it stayed frozen at the launch-time fetch even though prices kept refreshing.)
    private void OnPricesUpdated()
    {
        Dispatcher.BeginInvoke(UpdateStatusLabel);
        // Piggyback the update check on the 30-min price refresh: a build released while the app is
        // left running (common, since it lives in the tray during a play session) is now picked up
        // without a restart. No-ops once something is staged, and is reentrancy-guarded. (#27 follow-up)
        _ = CheckForUpdatesAsync();
    }

    private void UpdateStatusLabel()
    {
        if (_repo is null) return;
        string fetched = _repo.LastFetchedAt is { } t ? t.ToString("MMM d HH:mm") : "never";
        StatusLabel.Text = $"{_repo.ItemCount} items loaded  ·  last fetch {fetched}";
    }

    private void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://www.paypal.com/donate/?business=pedro.levi.magic%40gmail.com&currency_code=USD&item_name=PoeAncientsPriceHelper";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Donate] failed to open browser: {ex.Message}");
        }
    }

    // internal so the App-level hook (configurable Calibrate key) can trigger it too.
    internal void RunCalibration()
    {
        var rect = CalibrationOverlay.RunOnStaThread();
        if (rect is null) return;
        _config.RegionRect = rect.Value;
        ConfigStore.Save(_config);
        Dispatcher.Invoke(() =>
        {
            UpdateRegionLabel();
            StartStopButton.IsEnabled = _config.IsCalibrated;
        });
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e) => RunCalibration();

    private void StartStopButton_Click(object sender, RoutedEventArgs e) => ToggleStartStop();

    // Selects the screen-capture backend based on config. "GDI" forces legacy BitBlt;
    // "Auto"/"WGC" use Windows Graphics Capture (GPU) with built-in GDI fallback per call.
    private IScreenCaptureBackend CreateCaptureBackend() =>
        _config.CaptureBackend == "GDI"
            ? new GdiScreenCaptureBackend()
            : new WgcScreenCaptureBackend();

    // Shared by the Start/Stop button and the configurable global hotkey (invoked via App, marshalled
    // to the UI thread). internal so the App-level hook can reach it.
    internal void ToggleStartStop()
    {
        if (_engine is null)
        {
            // The hotkey can fire even when the button is disabled — don't start until we're ready.
            if (!_config.IsCalibrated || _repo is null || _icons is null) return;
            _engine = new ScanEngine(_config, _repo, _icons, CreateCaptureBackend());
            _engine.Start();
            StartStopButton.Content = "Stop";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkRed;
        }
        else
        {
            _engine.StopAndWait(TimeSpan.FromSeconds(2));
            _engine.Dispose();
            _engine = null;
            StartStopButton.Content = "Start";
            StartStopButton.Background = System.Windows.Media.Brushes.DarkGreen;
        }
    }

    // Minimize → hide the window and drop to the tray (scanning keeps running). Restore/Exit live on
    // the tray icon. The X button is unaffected and still quits via Window_Closing.
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        EnsureTrayIcon();
        _trayIcon!.Visible = true;
        Hide();   // remove the taskbar button; the tray icon is now the way back
        if (!_trayBalloonShown)
        {
            _trayIcon.ShowBalloonTip(3000, "Poe Ancients Price Helper",
                "Still running — double-click the tray icon to restore.",
                System.Windows.Forms.ToolTipIcon.Info);
            _trayBalloonShown = true;
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null) return;
        var exe = Environment.ProcessPath;
        var icon = exe is not null
            ? System.Drawing.Icon.ExtractAssociatedIcon(exe)
            : System.Drawing.SystemIcons.Application;
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Poe Ancients Price Helper",
            Visible = false,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => ExitFromTray());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void ExitFromTray()
    {
        if (_trayIcon is not null) _trayIcon.Visible = false;
        Close();   // routes through Window_Closing for the normal shutdown/cleanup
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _trayIcon?.Dispose();
        _engine?.StopAndWait(TimeSpan.FromSeconds(2));
        _engine?.Dispose();
        _repo?.Dispose();
        _icons?.Dispose();
        _http.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async void LeagueBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading || LeagueBox.SelectedItem is not string league || league == _config.LeagueName) return;
        if (_startingUp) return;  // prevent overlapping StartupAsync calls (rapid league changes)
        _startingUp = true;
        try
        {
            LeagueBox.IsEnabled = false;  // disable during reload
            _config.LeagueName = league;
            ConfigStore.Save(_config);
            await StartupAsync();   // re-fetch prices for the newly selected league
        }
        finally
        {
            LeagueBox.IsEnabled = true;
            _startingUp = false;
        }
    }

}
