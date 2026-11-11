using SmartMonitor.Core.Native;
using SmartMonitor.Core.Windows;
using C = SmartMonitor.Core.Windows.WindowPlacementCalculator;

namespace SmartMonitor.Core.Tests;

public class PlacementCalculatorTests
{
    // Mirrors the dev machine: 1920x1200 primary (taskbar 60px), 2560x1440 to the right.
    private static readonly RECT Mon1Work = new(0, 0, 1920, 1140);
    private static readonly RECT Mon2Work = new(1920, 0, 4480, 1380);

    private static void AssertInside(RECT r, RECT area)
    {
        Assert.True(r.Left >= area.Left && r.Top >= area.Top && r.Right <= area.Right && r.Bottom <= area.Bottom,
            $"{r} is not inside {area}");
    }

    [Fact]
    public void KeepsSizeWhenItFits()
    {
        var frame = new RECT(100, 100, 900, 700);
        var r = C.MapFrame(frame, Mon1Work, Mon2Work);
        Assert.Equal(800, r.Width);
        Assert.Equal(600, r.Height);
        AssertInside(r, Mon2Work);
    }

    [Fact]
    public void CenteredStaysCentered()
    {
        var frame = new RECT(560, 270, 1360, 870); // centered in 1920x1140
        var r = C.MapFrame(frame, Mon1Work, Mon2Work);
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        Assert.InRange(cx, 3200 - 1, 3200 + 1);
        Assert.InRange(cy, 690 - 1, 690 + 1);
    }

    [Fact]
    public void RightSnappedStaysRight()
    {
        var frame = new RECT(960, 0, 1920, 1140); // right half
        var r = C.MapFrame(frame, Mon1Work, Mon2Work);
        Assert.Equal(Mon2Work.Right, r.Right);
        Assert.Equal(Mon2Work.Top, r.Top);
    }

    [Fact]
    public void ClampsToSmallerTarget()
    {
        var frame = new RECT(1920, 0, 4480, 1380); // fills monitor 2
        var r = C.MapFrame(frame, Mon2Work, Mon1Work);
        Assert.Equal(Mon1Work.Width, r.Width);
        Assert.Equal(Mon1Work.Height, r.Height);
        AssertInside(r, Mon1Work);
    }

    [Fact]
    public void PartlyOffscreenWindowEndsUpInside()
    {
        var frame = new RECT(-300, -50, 500, 600);
        var r = C.MapFrame(frame, Mon1Work, Mon2Work);
        AssertInside(r, Mon2Work);
        Assert.Equal(800, r.Width);
    }

    [Fact]
    public void AppliesDpiScale()
    {
        var frame = new RECT(0, 0, 1000, 500);
        var r = C.MapFrame(frame, Mon1Work, Mon2Work, sizeScale: 1.5);
        Assert.Equal(1500, r.Width);
        Assert.Equal(750, r.Height);
    }

    [Theory]
    // monitor above (negative Y), to the left (negative X), below, and a portrait monitor
    [InlineData(0, -1080, 1920, 0)]
    [InlineData(-1920, 0, 0, 1040)]
    [InlineData(0, 1140, 1920, 2220)]
    [InlineData(-1080, -400, 0, 1480)]
    public void WorksForAnyArrangement(int l, int t, int r, int b)
    {
        var target = new RECT(l, t, r, b);
        var frame = new RECT(200, 150, 1400, 950);
        var res = C.MapFrame(frame, Mon1Work, target);
        AssertInside(res, target);
        // and back again
        var back = C.MapFrame(res, target, Mon1Work);
        AssertInside(back, Mon1Work);
    }

    [Fact]
    public void ExplicitSizeKeepsRelativePosition()
    {
        var frame = new RECT(960, 0, 1920, 1140); // right half of mon1
        var r = C.MapFrameWithSize(frame, Mon1Work, Mon2Work, 1200, 1380);
        Assert.Equal(Mon2Work.Right, r.Right);
        Assert.Equal(1200, r.Width);
    }

    [Fact]
    public void FrameToWindowRectAddsInvisibleBorders()
    {
        var win = new RECT(93, 100, 1007, 707);   // 7px invisible borders left/right/bottom
        var frame = new RECT(100, 100, 1000, 700);
        var desired = new RECT(2000, 50, 2900, 650);
        var w = C.FrameToWindowRect(desired, win, frame);
        Assert.Equal(new RECT(1993, 50, 2907, 657), w);
    }

    [Fact]
    public void FrameToWindowRectIgnoresBogusMargins()
    {
        var win = new RECT(0, 0, 100, 100);
        var frame = new RECT(-500, 0, 100, 100); // negative margin -> ignore
        var desired = new RECT(10, 10, 20, 20);
        Assert.Equal(desired, C.FrameToWindowRect(desired, win, frame));
    }

    [Fact]
    public void WorkspaceConversionRoundTripsWithTopTaskbar()
    {
        var primaryBounds = new RECT(0, 0, 1920, 1200);
        var primaryWork = new RECT(0, 48, 1920, 1200); // taskbar on top
        var screen = new RECT(2000, 100, 2800, 700);
        var ws = C.ScreenToWorkspace(screen, primaryBounds, primaryWork);
        Assert.Equal(52, ws.Top);
        Assert.Equal(screen, C.WorkspaceToScreen(ws, primaryBounds, primaryWork));
    }

    [Fact]
    public void MonitorIndexAt()
    {
        var mons = new List<RECT> { new(0, 0, 1920, 1200), new(1920, 0, 4480, 1440), new(-1080, -500, 0, 1420) };
        Assert.Equal(0, C.MonitorIndexAt(mons, 5, 5));
        Assert.Equal(1, C.MonitorIndexAt(mons, 1920, 0));
        Assert.Equal(2, C.MonitorIndexAt(mons, -1, -499));
        Assert.Equal(-1, C.MonitorIndexAt(mons, 5000, 5));
    }
}
