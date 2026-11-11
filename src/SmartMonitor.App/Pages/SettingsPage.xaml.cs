using System.Windows;
using System.Windows.Controls;
using SmartMonitor.Core.Config;
using SmartMonitor.Core.Logging;

namespace SmartMonitor.App.Pages;

public partial class SettingsPage : UserControl
{
    private readonly AppHost _host;
    // True while the page sets toggle states itself; those changes must not save or trigger UAC.
    private bool _refreshing = true;

    public SettingsPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        DataContext = host.Settings;
        Loaded += (_, _) => RefreshSystemState();
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing || !IsLoaded) return;
        var s = _host.Settings;
        s.NewWindowDelayMs = Math.Clamp(s.NewWindowDelayMs, 0, 5000);
        s.RecheckDelayMs = Math.Clamp(s.RecheckDelayMs, 0, 10000);
        s.ActivationDelayMs = Math.Clamp(s.ActivationDelayMs, 0, 2000);
        _host.SaveSettings();
    }

    private void RefreshSystemState()
    {
        _refreshing = true;
        try { RefreshSystemStateCore(); }
        finally { _refreshing = false; }
    }

    private void RefreshSystemStateCore()
    {
        bool taskExists = false;
        try
        {
            taskExists = StartupManager.IsElevatedTaskRegistered();
            StartupToggle.IsChecked = StartupManager.IsRunKeySet() || taskExists;
        }
        catch (Exception ex) { Log.Warn($"Could not read startup state: {ex.Message}"); }

        ElevatedToggle.IsChecked = taskExists;
        ElevationState.Text = _host.IsElevated ? "Currently running elevated." : "Currently running without admin rights.";
        if (taskExists && StartupManager.GetElevatedTaskExe() is string taskExe &&
            !taskExe.Equals(StartupManager.ExePath, StringComparison.OrdinalIgnoreCase))
        {
            ElevationState.Text += $" Note: the startup task starts a different copy ({taskExe}). Turn this off and on again to point it at this copy.";
        }
        RestartElevatedButton.Visibility = _host.IsElevated ? Visibility.Collapsed : Visibility.Visible;

        try
        {
            var (secure, _) = StartupManager.ReadUacPolicy();
            UacState.Text = secure
                ? "Secure desktop for UAC prompts: On (Windows default) — prompts dim and block all monitors."
                : "Secure desktop for UAC prompts: Off — other monitors stay usable while a prompt is shown.";
        }
        catch (Exception ex)
        {
            UacState.Text = "Could not read the UAC policy.";
            Log.Warn($"UAC policy read failed: {ex.Message}");
        }
    }

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        bool on = StartupToggle.IsChecked == true;
        try
        {
            if (StartupManager.IsElevatedTaskRegistered() && !on)
                StartupManager.SetElevatedTask(false);
            StartupManager.SetRunKey(on && !StartupManager.IsElevatedTaskRegistered());
            _host.Settings.StartWithWindows = on;
            _host.SaveSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Changing start-with-Windows failed", ex);
        }
        RefreshSystemState();
    }

    private void ElevatedToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        bool on = ElevatedToggle.IsChecked == true;
        if (StartupManager.SetElevatedTask(on))
        {
            // The elevated task replaces the normal Run entry (and vice versa).
            StartupManager.SetRunKey(!on && _host.Settings.StartWithWindows);
            _host.Settings.StartElevated = on;
            if (on) _host.Settings.StartWithWindows = true;
            _host.SaveSettings();
        }
        else
        {
            MessageBox.Show(Window.GetWindow(this)!, "The scheduled task could not be changed (the UAC prompt may have been declined). See the log for details.",
                "Smart Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshSystemState();
    }

    private void RestartElevated_Click(object sender, RoutedEventArgs e) => _host.RestartElevated();
}
