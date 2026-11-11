using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartMonitor.Core.Logging;
using SmartMonitor.Core.Monitors;
using SmartMonitor.Core.Native;
using SmartMonitor.Core.Windows;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.App.Pages;

public partial class TestPage : UserControl, IDisposable
{
    private readonly AppHost _host;

    public TestPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _host.Decisions.CollectionChanged += OnDecisionsChanged;
        _host.LaunchContextChanged += RefreshLaunchContext;
        RefreshDecisions();
        RefreshLaunchContext();
    }

    public void Dispose()
    {
        _host.Decisions.CollectionChanged -= OnDecisionsChanged;
        _host.LaunchContextChanged -= RefreshLaunchContext;
    }

    private void RefreshLaunchContext() => LaunchContextText.Text = _host.LastLaunchContext;

    private void OnDecisionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshDecisions();
    private void ShowSkipped_Click(object sender, RoutedEventArgs e) => RefreshDecisions();

    private void RefreshDecisions()
    {
        bool all = ShowSkipped.IsChecked == true;
        var rows = _host.Decisions.Where(d => all || d.Moved || d.Detail.Contains("could not", StringComparison.OrdinalIgnoreCase))
            .Select(d => new { d.Time, d.Trigger, Result = d.Moved ? "Moved" : "Left alone", d.Window, d.Detail })
            .ToList();
        DecisionList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Countdown(Button button, string label, Action done)
    {
        int left = 3;
        button.IsEnabled = false;
        button.Content = $"{left}…";
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            left--;
            if (left > 0) { button.Content = $"{left}…"; return; }
            t.Stop();
            button.Content = label;
            button.IsEnabled = true;
            done();
        };
        t.Start();
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        Countdown(DetectButton, "Detect in 3 seconds", () =>
        {
            var monitors = MonitorService.GetMonitors();
            GetCursorPos(out POINT p);
            var m = MonitorService.FromHandle(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST), monitors);
            if (m == null) { DetectResult.Text = "Could not determine the monitor."; return; }
            DetectResult.Text = $"Detected Monitor {m.Number}{(m.IsPrimary ? " (primary)" : "")} — {m.Bounds.Width}×{m.Bounds.Height}, {m.ScalePercent:0}% — cursor at {p.X}, {p.Y}. New and activated windows would open here.";
            Log.Info($"Test: cursor at {p} is on {m}");
            IdentifyOverlay.ShowAll(m.Handle, "You are here", 3);
        });
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        Countdown(MoveButton, "Move in 3 seconds", () =>
        {
            IntPtr hwnd = GetForegroundWindow();
            var owner = Window.GetWindow(this);
            if (owner != null && new System.Windows.Interop.WindowInteropHelper(owner).Handle == hwnd)
            {
                MoveResult.Text = "Smart Monitor itself is in front — click another window during the countdown.";
                return;
            }
            var facts = WindowFacts.Read(hwnd);
            var monitors = MonitorService.GetMonitors();
            GetCursorPos(out POINT p);
            var target = MonitorService.FromHandle(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST), monitors);
            if (target == null) { MoveResult.Text = "Could not determine the monitor."; return; }
            var r = WindowMover.MoveToMonitor(hwnd, target, monitors);
            MoveResult.Text = $"{facts.Describe()} → Monitor {target.Number}: {r.Message}";
            Log.Info($"Test move: {MoveResult.Text}");
        });
    }
}
