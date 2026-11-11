using SmartMonitor.Core.Native;
using SmartMonitor.Core.Windows;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.Core.Tests;

public class WindowFilterTests
{
    private static readonly HashSet<string> NoExclusions = new(StringComparer.OrdinalIgnoreCase);

    private static WindowFacts Normal(Func<WindowFacts, WindowFacts>? tweak = null)
    {
        var f = new WindowFacts
        {
            Handle = new IntPtr(0x1234),
            ClassName = "Chrome_WidgetWin_1",
            Title = "Example",
            ProcessName = "chrome.exe",
            Style = WS_CAPTION | WS_THICKFRAME | WS_VISIBLE,
            ExStyle = 0,
            IsVisible = true,
            IsTopLevel = true,
            Frame = new RECT(100, 100, 1100, 800),
            MonitorBounds = new RECT(0, 0, 1920, 1200),
            IsElevated = false,
        };
        return tweak == null ? f : tweak(f);
    }

    private static string? Reject(WindowFacts f, bool elevated = false, ISet<string>? excl = null)
        => WindowFilter.RejectReason(f, excl ?? NoExclusions, skipFullscreen: true, weAreElevated: elevated);

    [Fact] public void AcceptsNormalAppWindow() => Assert.Null(Reject(Normal()));

    [Fact] public void RejectsOwnedDialogs() => Assert.NotNull(Reject(Normal(f => f with { HasOwner = true })));

    [Fact] public void RejectsToolWindows() => Assert.NotNull(Reject(Normal(f => f with { ExStyle = WS_EX_TOOLWINDOW })));

    [Fact] public void AcceptsToolWindowMarkedAsAppWindow() => Assert.Null(Reject(Normal(f => f with { ExStyle = WS_EX_TOOLWINDOW | WS_EX_APPWINDOW })));

    [Theory]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("Progman")]
    [InlineData("XamlExplorerHostIslandWindow")]
    [InlineData("Windows.UI.Core.CoreWindow")]
    public void RejectsShellWindows(string cls) => Assert.NotNull(Reject(Normal(f => f with { ClassName = cls })));

    [Fact] public void RejectsShellProcesses() => Assert.NotNull(Reject(Normal(f => f with { ProcessName = "SearchHost.exe" })));

    [Fact]
    public void RejectsUserExcluded()
    {
        var excl = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CHROME.EXE" };
        Assert.Equal("excluded by user", Reject(Normal(), excl: excl));
    }

    [Fact] public void RejectsFramelessPopups() => Assert.NotNull(Reject(Normal(f => f with { Style = WS_POPUP | WS_VISIBLE })));

    [Fact] public void RejectsTinyWindows() => Assert.NotNull(Reject(Normal(f => f with { Frame = new RECT(0, 0, 50, 30) })));

    [Fact]
    public void RejectsBorderlessFullscreen()
    {
        var f = Normal(x => x with { Style = WS_POPUP | WS_THICKFRAME | WS_VISIBLE, Frame = new RECT(0, 0, 1920, 1200) });
        Assert.Equal("full-screen", Reject(f));
    }

    [Fact]
    public void MaximizedIsNotFullscreen()
    {
        var f = Normal(x => x with { IsMaximized = true, Frame = new RECT(0, 0, 1920, 1200) });
        Assert.Null(Reject(f));
    }

    [Fact]
    public void ElevatedWindowsNeedElevation()
    {
        var f = Normal(x => x with { IsElevated = true });
        Assert.NotNull(Reject(f, elevated: false));
        Assert.Null(Reject(f, elevated: true));
    }

    [Fact] public void UnknownElevationIsAttempted() => Assert.Null(Reject(Normal(x => x with { IsElevated = null })));

    [Fact] public void RejectsHungWindows() => Assert.NotNull(Reject(Normal(x => x with { IsHung = true })));

    [Theory]
    [InlineData("chrome", "chrome.exe")]
    [InlineData(" Discord.exe ", "Discord.exe")]
    [InlineData(@"C:\Program Files\App\app.EXE", "app.EXE")]
    public void NormalizesExeNames(string input, string expected) => Assert.Equal(expected, FollowCursorEngine.NormalizeExe(input));
}
