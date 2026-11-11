using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SmartMonitor.Core.Monitors;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.App;

/// <summary>Big "1", "2", … badges shown in the middle of each monitor (like Windows' Identify).</summary>
public sealed class IdentifyOverlay : Window
{
    private readonly MonitorInfo _monitor;

    private IdentifyOverlay(MonitorInfo monitor, bool highlight, string? caption)
    {
        _monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 240;
        Height = caption == null ? 200 : 240;
        Title = $"Monitor {monitor.Number}";

        var accent = highlight ? Color.FromRgb(0, 120, 212) : Color.FromRgb(32, 32, 32);
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = monitor.Number.ToString(),
            FontSize = 110, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = caption ?? (monitor.IsPrimary ? "Primary" : monitor.DeviceName.TrimStart('\\', '.')),
            FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 0),
        });
        Content = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(230, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = panel,
        };
        SourceInitialized += (_, _) => CenterOnMonitor();
    }

    /// <summary>Positions in physical pixels: move first (so the window adopts the monitor's DPI), then center.</summary>
    private void CenterOnMonitor()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        var b = _monitor.Bounds;
        SetWindowPos(hwnd, IntPtr.Zero, b.Left + b.Width / 2 - 100, b.Top + b.Height / 2 - 100, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        GetWindowRect(hwnd, out var r);
        SetWindowPos(hwnd, IntPtr.Zero, b.Left + (b.Width - r.Width) / 2, b.Top + (b.Height - r.Height) / 2, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static void ShowAll(IntPtr? highlightMonitor = null, string? highlightCaption = null, double seconds = 2.5)
    {
        foreach (var m in MonitorService.GetMonitors())
        {
            bool hl = highlightMonitor == m.Handle;
            var w = new IdentifyOverlay(m, hl, hl ? highlightCaption : null);
            w.Show();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += (_, _) => { t.Stop(); w.Close(); };
            t.Start();
        }
    }
}
