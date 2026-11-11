using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.Core.Windows;

/// <summary>
/// Decides whether a window is an ordinary application window that the engine may move.
/// Pure function of <see cref="WindowFacts"/> so it is unit-testable.
/// </summary>
public static class WindowFilter
{
    /// <summary>Shell / system UI window classes that must never be moved.</summary>
    public static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",          // Start, Search, Action Center, etc. when top-level
        "XamlExplorerHostIslandWindow",        // Alt+Tab, Task View, Snap layouts
        "MultitaskingViewFrame", "ForegroundStaging", "TaskListThumbnailWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Shell_InputSwitchTopLevelWindow", "LockScreenBackstopFrame", "Shell_LightDismissOverlay",
        "#32768", "#32769", "#32770_AltTab", "tooltips_class32", "SysShadow", "Xaml_WindowedPopupClass",
        "Microsoft.UI.Content.PopupWindowSiteBridge", "DummyDWMListenerWindow", "EdgeUiInputTopWndClass",
        "ApplicationManager_ImmersiveShellWindow", "Internet Explorer_Hidden", "IME", "MSCTFIME UI",
        "CiceroUIWndFrame", "Windows.Internal.Shell.TabProxyWindow",
    };

    /// <summary>Processes that only host shell UI.</summary>
    public static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe",
        "TextInputHost.exe", "LockApp.exe", "consent.exe", "LogonUI.exe", "dwm.exe", "csrss.exe",
        "ShellHost.exe", "Widgets.exe", "WidgetBoard.exe",
    };

    private const int MinWidth = 120;
    private const int MinHeight = 60;

    /// <returns>null if the window may be moved, otherwise a short reason why not.</returns>
    public static string? RejectReason(WindowFacts f, ISet<string> userExcludedProcesses, bool skipFullscreen, bool weAreElevated)
    {
        if (f.Handle == IntPtr.Zero) return "no window";
        if (!f.IsVisible) return "not visible";
        if (!f.IsTopLevel || (f.Style & WS_CHILD) != 0) return "child window";
        if (f.HasOwner) return "owned window (stays with its parent)";
        if ((f.ExStyle & WS_EX_TOOLWINDOW) != 0 && (f.ExStyle & WS_EX_APPWINDOW) == 0) return "tool window";
        if ((f.ExStyle & WS_EX_NOACTIVATE) != 0) return "non-activating window";
        if (ShellClasses.Contains(f.ClassName)) return "shell window";
        if (f.ProcessName.Length > 0 && ShellProcesses.Contains(f.ProcessName)) return "shell process";
        if (f.ProcessName.Length > 0 && userExcludedProcesses.Contains(f.ProcessName)) return "excluded by user";

        bool framed = (f.Style & WS_CAPTION) == WS_CAPTION || (f.Style & WS_THICKFRAME) != 0 || (f.ExStyle & WS_EX_APPWINDOW) != 0;
        if (!framed) return "not an application window (no frame)";

        if (f.Frame.Width < MinWidth || f.Frame.Height < MinHeight) return "too small";
        if (f.IsHung) return "not responding";

        if (skipFullscreen && IsFullscreen(f)) return "full-screen";

        // UIPI: a non-elevated process cannot move windows of elevated (or inaccessible system) processes.
        // When elevation can't be determined (null) we still try; a blocked SetWindowPos fails harmlessly
        // and the mover reports it.
        if (!weAreElevated && f.IsElevated == true) return "elevated window (enable \"Run elevated\" to manage admin windows)";
        return null;
    }

    /// <summary>
    /// For windows of an app that is just starting (games: splash/loading window, borderless full-screen window).
    /// Unlike <see cref="RejectReason"/> this accepts frameless popups and full-screen windows.
    /// </summary>
    /// <returns>null if the window may be moved, otherwise why not.</returns>
    public static string? StartupRejectReason(WindowFacts f, ISet<string> userExcludedProcesses, bool weAreElevated)
    {
        if (f.Handle == IntPtr.Zero) return "no window";
        if (!f.IsVisible) return "not visible";
        if (!f.IsTopLevel || (f.Style & WS_CHILD) != 0) return "child window";
        if (f.HasOwner) return "owned window";
        if ((f.ExStyle & WS_EX_TOOLWINDOW) != 0 && (f.ExStyle & WS_EX_APPWINDOW) == 0) return "tool window";
        if ((f.ExStyle & WS_EX_NOACTIVATE) != 0) return "non-activating window";
        if (ShellClasses.Contains(f.ClassName)) return "shell window";
        if (f.ProcessName.Length > 0 && ShellProcesses.Contains(f.ProcessName)) return "shell process";
        if (f.ProcessName.Length > 0 && userExcludedProcesses.Contains(f.ProcessName)) return "excluded by user";
        if (f.Frame.Width < 200 || f.Frame.Height < 120) return "too small";
        if (f.IsHung) return "not responding";
        if (!weAreElevated && f.IsElevated == true) return "elevated window";
        return null;
    }

    /// <summary>Frameless window that exactly covers (or exceeds) its monitor: borderless full-screen.</summary>
    public static bool CoversMonitor(WindowFacts f)
    {
        var m = f.MonitorBounds;
        return f.Frame.Left <= m.Left && f.Frame.Top <= m.Top && f.Frame.Right >= m.Right && f.Frame.Bottom >= m.Bottom;
    }

    public static bool IsFullscreen(WindowFacts f)
    {
        var m = f.MonitorBounds;
        bool coversMonitor = f.Frame.Left <= m.Left && f.Frame.Top <= m.Top && f.Frame.Right >= m.Right && f.Frame.Bottom >= m.Bottom;
        return coversMonitor && !f.IsMaximized && (f.Style & WS_CAPTION) != WS_CAPTION;
    }
}
