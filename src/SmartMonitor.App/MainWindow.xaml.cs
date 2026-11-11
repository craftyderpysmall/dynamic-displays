using System.Windows;
using System.Windows.Controls;
using SmartMonitor.App.Pages;

namespace SmartMonitor.App;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly Dictionary<string, Func<FrameworkElement>> _pages;
    private FrameworkElement? _current;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _pages = new()
        {
            ["monitors"] = () => new MonitorsPage(host),
            ["apps"] = () => new AppsPage(host),
            ["settings"] = () => new SettingsPage(host),
            ["test"] = () => new TestPage(host),
            ["log"] = () => new LogPage(),
            ["about"] = () => new LimitationsPage(),
        };
        _host.SettingsChanged += RefreshStatus;
        Closed += (_, _) =>
        {
            _host.SettingsChanged -= RefreshStatus;
            (_current as IDisposable)?.Dispose();
        };
        RefreshStatus();
        Nav.SelectedIndex = 0;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string key } || !_pages.TryGetValue(key, out var make)) return;
        (_current as IDisposable)?.Dispose();
        _current = make();
        PageHost.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _current,
        };
    }

    // Checked/Unchecked (not Click) so keyboard, mouse and UI Automation / screen readers all work.
    // SetEnabled ignores no-op changes, so programmatic updates from RefreshStatus are harmless.
    private void EnabledToggle_Click(object sender, RoutedEventArgs e) => _host.SetEnabled(EnabledToggle.IsChecked == true);

    private void RefreshStatus()
    {
        EnabledToggle.IsChecked = _host.Settings.Enabled;
        StatusText.Text = _host.Settings.Enabled
            ? "Windows that open, restore or activate move to the monitor your cursor is on."
            : "Off. Windows are not observed or moved.";
        if (_host.IsElevated) StatusText.Text += " Running elevated.";
    }
}
