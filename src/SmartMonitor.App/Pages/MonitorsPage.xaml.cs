using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SmartMonitor.Core.Monitors;
using SmartMonitor.Core.Native;

namespace SmartMonitor.App.Pages;

public partial class MonitorsPage : UserControl, IDisposable
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _cursorTimer;
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private readonly Dictionary<IntPtr, Border> _tiles = new();
    private IntPtr _cursorMonitor;

    public MonitorsPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _host.MonitorsChanged += Reload;
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _cursorTimer.Tick += (_, _) => UpdateCursor();
        Loaded += (_, _) => { Reload(); _cursorTimer.Start(); };
        Unloaded += (_, _) => _cursorTimer.Stop();
    }

    public void Dispose()
    {
        _cursorTimer.Stop();
        _host.MonitorsChanged -= Reload;
    }

    private void Reload()
    {
        _monitors = MonitorService.GetMonitors();
        DetailsList.ItemsSource = _monitors.Select(m => new
        {
            m.Number,
            Title = string.IsNullOrWhiteSpace(m.FriendlyName) ? $"Monitor {m.Number}" : m.FriendlyName,
            PrimaryVisibility = m.IsPrimary ? Visibility.Visible : Visibility.Collapsed,
            Line1 = $"{m.Bounds.Width} × {m.Bounds.Height}  ·  {m.ScalePercent:0}% scale  ·  {m.Orientation}" +
                    (m.RefreshRate > 1 ? $"  ·  {m.RefreshRate} Hz" : ""),
            Line2 = $"Position {m.Bounds.Left}, {m.Bounds.Top}  ·  Work area {m.WorkArea.Width} × {m.WorkArea.Height} at {m.WorkArea.Left}, {m.WorkArea.Top}  ·  {m.DeviceName}",
        }).ToList();
        DrawLayout();
        UpdateCursor(force: true);
    }

    private void LayoutHost_SizeChanged(object sender, SizeChangedEventArgs e) => DrawLayout();

    /// <summary>Draws the monitors to scale using their physical-pixel desktop coordinates.</summary>
    private void DrawLayout()
    {
        LayoutCanvas.Children.Clear();
        _tiles.Clear();
        if (_monitors.Count == 0 || LayoutHost.ActualWidth < 10) return;

        int minX = _monitors.Min(m => m.Bounds.Left), minY = _monitors.Min(m => m.Bounds.Top);
        int maxX = _monitors.Max(m => m.Bounds.Right), maxY = _monitors.Max(m => m.Bounds.Bottom);
        double pad = 8;
        double scale = Math.Min((LayoutHost.ActualWidth - 2 * pad) / (maxX - minX), (LayoutHost.ActualHeight - 2 * pad) / (maxY - minY));
        double offX = (LayoutHost.ActualWidth - (maxX - minX) * scale) / 2;
        double offY = (LayoutHost.ActualHeight - (maxY - minY) * scale) / 2;
        const double gap = 3;

        foreach (var m in _monitors)
        {
            double w = m.Bounds.Width * scale - gap * 2, h = m.Bounds.Height * scale - gap * 2;
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            label.Children.Add(new TextBlock { Text = m.Number.ToString(), FontSize = Math.Clamp(h / 3, 16, 56), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            if (h > 90)
                label.Children.Add(new TextBlock { Text = $"{m.Bounds.Width}×{m.Bounds.Height} · {m.ScalePercent:0}%{(m.IsPrimary ? " · Primary" : "")}", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis });

            var tile = new Border
            {
                Width = Math.Max(10, w), Height = Math.Max(10, h), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2), Child = label, ToolTip = m.ToString(),
            };
            tile.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            tile.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
            tile.SetResourceReference(TextElement_Foreground, "TextFillColorPrimaryBrush");
            Canvas.SetLeft(tile, offX + (m.Bounds.Left - minX) * scale + gap);
            Canvas.SetTop(tile, offY + (m.Bounds.Top - minY) * scale + gap);
            LayoutCanvas.Children.Add(tile);
            _tiles[m.Handle] = tile;
        }
        _cursorMonitor = IntPtr.Zero;
        UpdateCursor(force: true);
    }

    private static readonly DependencyProperty TextElement_Foreground = System.Windows.Documents.TextElement.ForegroundProperty;

    private void UpdateCursor(bool force = false)
    {
        IntPtr cur = MonitorService.MonitorAtCursor();
        if (!force && cur == _cursorMonitor) return;
        _cursorMonitor = cur;
        foreach (var (handle, tile) in _tiles)
        {
            bool on = handle == cur;
            if (on)
            {
                tile.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
                tile.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                tile.SetResourceReference(TextElement_Foreground, "TextOnAccentFillColorPrimaryBrush");
            }
            else
            {
                tile.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                tile.SetResourceReference(Border.BorderBrushProperty, "ControlStrongStrokeColorDefaultBrush");
                tile.SetResourceReference(TextElement_Foreground, "TextFillColorPrimaryBrush");
            }
        }
        var m = _monitors.FirstOrDefault(x => x.Handle == cur);
        NativeMethods.GetCursorPos(out var p);
        CursorText.Text = m == null ? "" : $"Cursor is on Monitor {m.Number} at {p.X}, {p.Y}";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Identify_Click(object sender, RoutedEventArgs e) => _host.IdentifyMonitors();
}
