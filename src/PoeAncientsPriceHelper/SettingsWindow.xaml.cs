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
    private bool _loading;

    // internal: takes the internal AppConfig and is only ever constructed from MainWindow.
    internal SettingsWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();
        Populate();
    }

    private void Populate()
    {
        _loading = true;

        HotkeyLabel.Text = HotkeyBinding.Display(HotkeyBinding.Parse(_config.StartStopHotkey));
        DebugHotkeyLabel.Text = HotkeyBinding.Display(HotkeyBinding.Parse(_config.DebugHotkey));
        CalibrateHotkeyLabel.Text = HotkeyBinding.Display(HotkeyBinding.Parse(_config.CalibrateHotkey));

        ThemeBox.ItemsSource = ThemePresets.Names;
        ThemeBox.SelectedItem = ThemePresets.Resolve(_config.Theme);

        // Two backends: "GDI" forces legacy BitBlt; anything else is WGC (GPU) with GDI fallback.
        // Tag carries the value persisted to config.CaptureBackend.
        CaptureBox.Items.Clear();
        CaptureBox.Items.Add(new ComboBoxItem { Content = "Auto (GPU + fallback)", Tag = "Auto" });
        CaptureBox.Items.Add(new ComboBoxItem { Content = "Legacy (GDI)", Tag = "GDI" });
        CaptureBox.SelectedIndex = _config.CaptureBackend == "GDI" ? 1 : 0;

        AutoStartBox.IsChecked = _config.AutoStart;

        _loading = false;
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

    private void AutoStartBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.AutoStart = AutoStartBox.IsChecked == true;
        ConfigStore.Save(_config);
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
        button.Content = "Press a key… (Esc to cancel)";
        App.BeginHotkeyCapture(action, OnHotkeyCaptured);   // outcome arrives on the UI thread
    }

    private void OnHotkeyCaptured(App.CaptureOutcome outcome, KeyCode code)
    {
        switch (outcome)
        {
            case App.CaptureOutcome.Captured:
                switch (_rebindAction)
                {
                    case HotkeyBinding.Action.StartStop:
                        _config.StartStopHotkey = HotkeyBinding.ToStorage(code);
                        App.SetStartStopKey(code);
                        break;
                    case HotkeyBinding.Action.Debug:
                        _config.DebugHotkey = HotkeyBinding.ToStorage(code);
                        App.SetDebugKey(code);
                        break;
                    case HotkeyBinding.Action.Calibrate:
                        _config.CalibrateHotkey = HotkeyBinding.ToStorage(code);
                        App.SetCalibrateKey(code);
                        break;
                }
                ConfigStore.Save(_config);
                if (_rebindLabel is not null) _rebindLabel.Text = HotkeyBinding.Display(code);
                EndRebind();
                break;
            case App.CaptureOutcome.Reserved:
                if (_rebindButton is not null)
                    _rebindButton.Content = $"{HotkeyBinding.Display(code)} is in use — try another";
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
