using System.Windows;
using System.Windows.Controls;
using SharpHook.Data;
// WinForms is referenced (tray icon), so Button is ambiguous — pin it to the WPF control.
using Button = System.Windows.Controls.Button;

namespace PoeAncientsPriceHelper;

// Modal settings dialog opened from the gear button on MainWindow. Holds the controls that used to
// clutter the main window (the three hotkey rebinds and the theme picker) plus two new options:
// the capture backend and auto-start. It mutates the SAME AppConfig instance the main window holds
// (passed in), so changes are visible there immediately; every change persists on the spot via
// ConfigStore.Save — there is no OK/Cancel, just close. Hotkey changes also re-arm the live global
// hook through App; theme changes apply app-wide via ThemePresets so both windows update live.
public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly Func<Task<RumourRefreshResult>>? _refreshRumours;
    private bool _loading;

    // internal: takes the internal AppConfig and is only ever constructed from MainWindow. The optional
    // callback performs a rumour-sheet refresh (owned by MainWindow, which holds the scanner).
    internal SettingsWindow(AppConfig config, Func<Task<RumourRefreshResult>>? refreshRumours = null)
    {
        _config = config;
        _refreshRumours = refreshRumours;
        InitializeComponent();
        Populate();
    }

    private void Populate()
    {
        _loading = true;

        HotkeyLabel.Text = HotkeyBinding.Display(HotkeyBinding.ParseChord(_config.StartStopHotkey));
        DebugHotkeyLabel.Text = HotkeyBinding.Display(HotkeyBinding.ParseChord(_config.DebugHotkey));
        CalibrateHotkeyLabel.Text = HotkeyBinding.Display(HotkeyBinding.ParseChord(_config.CalibrateHotkey));

        ThemeBox.ItemsSource = ThemePresets.Names;
        ThemeBox.SelectedItem = ThemePresets.Resolve(_config.Theme);

        // Two backends: "GDI" forces legacy BitBlt; anything else is WGC (GPU) with GDI fallback.
        // Tag carries the value persisted to config.CaptureBackend.
        CaptureBox.Items.Clear();
        CaptureBox.Items.Add(new ComboBoxItem { Content = "Auto (GPU + fallback)", Tag = "Auto" });
        CaptureBox.Items.Add(new ComboBoxItem { Content = "Legacy (GDI)", Tag = "GDI" });
        CaptureBox.SelectedIndex = _config.CaptureBackend == "GDI" ? 1 : 0;

        // Game language (#29): English (no translation) first, then every locale file the app found.
        // Tag carries the code persisted to config.GameLanguage; the engine builds its translator from
        // it on next start. Unknown saved code (e.g. a removed file) falls back to English for display.
        LanguageBox.Items.Clear();
        LanguageBox.Items.Add(new ComboBoxItem { Content = "English (default)", Tag = "en" });
        foreach (var loc in NameTranslator.AvailableLocales())
            LanguageBox.Items.Add(new ComboBoxItem { Content = loc.DisplayName, Tag = loc.Code });
        var langItems = LanguageBox.Items.Cast<ComboBoxItem>().ToList();
        LanguageBox.SelectedItem =
            langItems.FirstOrDefault(i => string.Equals((string)i.Tag!, _config.GameLanguage, StringComparison.OrdinalIgnoreCase))
            ?? langItems[0];

        AutoStartBox.IsChecked = _config.AutoStart;
        PauseWhenUnfocusedBox.IsChecked = _config.PauseWhenGameNotFocused;

        // Rumour helper (#36): on/off + scan rate presets. Tag carries the interval in ms persisted to
        // config.RumourScanIntervalMs. If the saved value isn't a preset, fall back to Normal for display
        // (the stored value is left untouched until the user picks one).
        RumourEnabledBox.IsChecked = _config.RumourHelperEnabled;
        RumourIntervalBox.Items.Clear();
        RumourIntervalBox.Items.Add(new ComboBoxItem { Content = "Fast (1.2s)", Tag = 1200 });
        RumourIntervalBox.Items.Add(new ComboBoxItem { Content = "Normal (1.8s)", Tag = 1800 });
        RumourIntervalBox.Items.Add(new ComboBoxItem { Content = "Relaxed (3s)", Tag = 3000 });
        var items = RumourIntervalBox.Items.Cast<ComboBoxItem>().ToList();
        RumourIntervalBox.SelectedItem =
            items.FirstOrDefault(i => (int)i.Tag! == _config.RumourScanIntervalMs)
            ?? items.First(i => (int)i.Tag! == 1800);

        // Atlas gate (#45): auto-detect on/off + the hand-drawn WORLD region for manual mode.
        RumourAutoWorldBox.IsChecked = _config.RumourWorldAutoDetect;
        UpdateWorldRegionUi();

        _loading = false;
    }

    // Reflect the current auto-detect / manual-region state: the "Set WORLD region" button is only
    // relevant when auto-detect is off, and the label shows the saved box (or a prompt to set one).
    private void UpdateWorldRegionUi()
    {
        bool manual = _config.RumourWorldAutoDetect == false;
        SetWorldRegionButton.IsEnabled = manual;
        WorldRegionLabel.Visibility = manual ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        WorldRegionLabel.Text = !manual
            ? ""
            : _config.HasManualWorldRegion
                ? $"WORLD region: x={_config.RumourWorldX} y={_config.RumourWorldY} {_config.RumourWorldWidth}×{_config.RumourWorldHeight}"
                : "No region set yet — open the Atlas in-game, then click “Set WORLD region…”.";
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Esc closes — but not while a rebind capture is listening (Esc cancels that instead, handled
        // on the global hook), so don't steal it mid-rebind.
        if (e.Key == System.Windows.Input.Key.Escape && _rebindButton is null) Close();
        base.OnKeyDown(e);
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not string theme) return;
        _config.Theme = theme;
        ConfigStore.Save(_config);
        ThemePresets.Apply(theme);   // app-wide, so the main window updates live too
    }

    private void CaptureBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CaptureBox.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        _config.CaptureBackend = value;
        ConfigStore.Save(_config);   // read by the engine the next time it starts
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        _config.GameLanguage = code;
        ConfigStore.Save(_config);   // the engine reads this to build its translator the next time it starts
    }

    private void AutoStartBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.AutoStart = AutoStartBox.IsChecked == true;
        ConfigStore.Save(_config);
    }

    // Foreground gate (#49). The scan loop reads _config live each tick, so toggling this pauses or
    // resumes pricing within a cycle — no restart needed.
    private void PauseWhenUnfocusedBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.PauseWhenGameNotFocused = PauseWhenUnfocusedBox.IsChecked == true;
        ConfigStore.Save(_config);
    }

    // The rumour engine reads these straight off _config each loop tick, so toggling on/off or changing
    // the scan rate takes effect within a tick — no restart needed.
    private void RumourEnabledBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.RumourHelperEnabled = RumourEnabledBox.IsChecked == true;
        ConfigStore.Save(_config);
    }

    private void RumourIntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RumourIntervalBox.SelectedItem is not ComboBoxItem { Tag: int ms }) return;
        _config.RumourScanIntervalMs = RumourScanEngine.ClampInterval(ms);
        ConfigStore.Save(_config);
    }

    // Atlas auto-detect toggle (#45). Read live by the running loop, so no restart needed.
    private void RumourAutoWorldBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.RumourWorldAutoDetect = RumourAutoWorldBox.IsChecked == true;
        ConfigStore.Save(_config);
        UpdateWorldRegionUi();
    }

    // Manual WORLD-region picker (#45): drag a box over the atlas "WORLD" label. Reuses the price
    // calibration overlay (full-desktop snapshot, absolute-pixel result). The settings window is hidden
    // during the pick so it can't cover the label; the running loop reads the saved region live.
    private void SetWorldRegionButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        System.Drawing.Rectangle? rect;
        try
        {
            rect = CalibrationOverlay.RunOnStaThread(
                "Open the Atlas in-game, then drag a box around the \"WORLD\" label at the top. ENTER to confirm, ESC to cancel.");
        }
        finally
        {
            Show();
            Activate();
        }
        if (rect is null) return;
        _config.RumourWorldRect = rect.Value;
        ConfigStore.Save(_config);
        UpdateWorldRegionUi();
    }

    // The community spreadsheet the rumour ratings come from (public read-only view).
    private const string RumourSheetUrl =
        "https://docs.google.com/spreadsheets/d/16YU8mSS7TdLPdmOunVjiPn_NrKVGfcnMkuMQDy8jgZA/htmlview";

    private void RumourSourceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RumourSheetUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Settings] open data source failed: {ex.Message}");
        }
    }

    // Pull the latest rumour data from the sheet on demand; the callback caches it and swaps it into
    // the running scanner. Any failure falls back to the current data and is surfaced in the status line.
    private async void RefreshRumoursButton_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshRumours is null) return;
        RefreshRumoursButton.IsEnabled = false;
        RumourRefreshStatus.Text = "Refreshing from sheet…";
        try
        {
            var result = await _refreshRumours();
            RumourRefreshStatus.Text = result.Message;
        }
        catch (Exception ex)
        {
            RumourRefreshStatus.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshRumoursButton.IsEnabled = true;
        }
    }

    // ---- Hotkey rebinding (moved verbatim from MainWindow; one capture at a time) ----

    private HotkeyBinding.Action _rebindAction;
    private Button? _rebindButton;
    private TextBlock? _rebindLabel;

    private void RebindButton_Click(object sender, RoutedEventArgs e) =>
        BeginRebind(HotkeyBinding.Action.StartStop, RebindButton, HotkeyLabel);

    private void RebindDebugButton_Click(object sender, RoutedEventArgs e) =>
        BeginRebind(HotkeyBinding.Action.Debug, RebindDebugButton, DebugHotkeyLabel);

    private void RebindCalibrateButton_Click(object sender, RoutedEventArgs e) =>
        BeginRebind(HotkeyBinding.Action.Calibrate, RebindCalibrateButton, CalibrateHotkeyLabel);

    private void BeginRebind(HotkeyBinding.Action action, Button button, TextBlock label)
    {
        _rebindAction = action;
        _rebindButton = button;
        _rebindLabel = label;
        SetRebindButtonsEnabled(false);
        button.Content = "Press a key or chord… (Esc to cancel)";
        App.BeginHotkeyCapture(action, OnHotkeyCaptured);   // outcome arrives on the UI thread
    }

    private void OnHotkeyCaptured(App.CaptureOutcome outcome, Chord chord)
    {
        switch (outcome)
        {
            case App.CaptureOutcome.Captured:
                switch (_rebindAction)
                {
                    case HotkeyBinding.Action.StartStop:
                        _config.StartStopHotkey = HotkeyBinding.ToStorage(chord);
                        App.SetStartStopChord(chord);
                        break;
                    case HotkeyBinding.Action.Debug:
                        _config.DebugHotkey = HotkeyBinding.ToStorage(chord);
                        App.SetDebugChord(chord);
                        break;
                    case HotkeyBinding.Action.Calibrate:
                        _config.CalibrateHotkey = HotkeyBinding.ToStorage(chord);
                        App.SetCalibrateChord(chord);
                        break;
                }
                ConfigStore.Save(_config);
                if (_rebindLabel is not null) _rebindLabel.Text = HotkeyBinding.Display(chord);
                EndRebind();
                break;
            case App.CaptureOutcome.Reserved:
                if (_rebindButton is not null)
                    _rebindButton.Content = $"{HotkeyBinding.Display(chord)} is in use — try another";
                break;
            case App.CaptureOutcome.Cancelled:
                EndRebind();
                break;
        }
    }

    private void EndRebind()
    {
        if (_rebindButton is not null) _rebindButton.Content = "Rebind";
        _rebindButton = null;
        _rebindLabel = null;
        SetRebindButtonsEnabled(true);
    }

    private void SetRebindButtonsEnabled(bool enabled)
    {
        RebindButton.IsEnabled = enabled;
        RebindDebugButton.IsEnabled = enabled;
        RebindCalibrateButton.IsEnabled = enabled;
    }
}
