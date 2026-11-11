using SmartMonitor.Core.Native;

namespace SmartMonitor.Core.Monitors;

public enum DisplayOrientation { Landscape = 0, Portrait = 1, LandscapeFlipped = 2, PortraitFlipped = 3 }

/// <summary>Snapshot of one display. All rectangles are in physical (per-monitor-DPI-aware) pixels.</summary>
public sealed record MonitorInfo
{
    public required IntPtr Handle { get; init; }
    /// <summary>1-based index, ordered left-to-right then top-to-bottom (stable for display purposes).</summary>
    public required int Number { get; init; }
    public required string DeviceName { get; init; }
    public string FriendlyName { get; init; } = "";
    public required RECT Bounds { get; init; }
    public required RECT WorkArea { get; init; }
    public required bool IsPrimary { get; init; }
    public required uint Dpi { get; init; }
    public DisplayOrientation Orientation { get; init; }
    public int RefreshRate { get; init; }

    public double ScalePercent => Dpi * 100.0 / 96.0;

    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(FriendlyName) ? $"Monitor {Number}" : $"Monitor {Number} — {FriendlyName}";

    public override string ToString() =>
        $"Monitor {Number} ({DeviceName}{(IsPrimary ? ", primary" : "")}) {Bounds.Width}x{Bounds.Height} @ {Bounds.Left},{Bounds.Top} work {WorkArea} {ScalePercent:0}% {Orientation}";
}
