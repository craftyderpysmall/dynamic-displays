using System.Runtime.InteropServices;
using SmartMonitor.Core.Logging;
using SmartMonitor.Core.Monitors;
using SmartMonitor.Core.Native;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.Core.Windows;

public sealed record MoveResult(bool Success, string Message)
{
    public static MoveResult Ok(string m) => new(true, m);
    public static MoveResult Fail(string m) => new(false, m);
}

/// <summary>Moves a top-level window onto another monitor's work area, respecting its show state.</summary>
public static class WindowMover
{
    public static MoveResult MoveToMonitor(IntPtr hwnd, MonitorInfo target, IReadOnlyList<MonitorInfo> monitors)
    {
        if (!IsWindow(hwnd)) return MoveResult.Fail("window no longer exists");

        IntPtr srcHandle = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var source = monitors.FirstOrDefault(m => m.Handle == srcHandle);
        if (source == null) return MoveResult.Fail("source monitor unknown (display configuration changed?)");
        if (source.Handle == target.Handle) return MoveResult.Ok("already on target monitor");

        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return MoveResult.Fail($"GetWindowPlacement failed ({Marshal.GetLastWin32Error()})");

        bool toolWindow = (GetExStyle(hwnd) & WS_EX_TOOLWINDOW) != 0;
        RECT NormalToScreen(RECT r) => toolWindow ? r : WindowPlacementCalculator.WorkspaceToScreen(r, primary.Bounds, primary.WorkArea);
        RECT ScreenToNormal(RECT r) => toolWindow ? r : WindowPlacementCalculator.ScreenToWorkspace(r, primary.Bounds, primary.WorkArea);

        double dpiScale = source.Dpi == 0 ? 1.0 : target.Dpi / (double)source.Dpi;

        if (IsIconic(hwnd))
        {
            // Retarget where it will restore to, keep it minimized.
            var normal = NormalToScreen(wp.rcNormalPosition);
            var mapped = WindowPlacementCalculator.MapFrame(normal, source.WorkArea, target.WorkArea, dpiScale);
            wp.rcNormalPosition = ScreenToNormal(mapped);
            wp.showCmd = SW_SHOWMINNOACTIVE;
            wp.flags = 0;
            return SetWindowPlacement(hwnd, ref wp)
                ? MoveResult.Ok("restore position moved (window stays minimized)")
                : MoveResult.Fail($"SetWindowPlacement failed ({Marshal.GetLastWin32Error()})");
        }

        if (IsZoomed(hwnd))
            return MoveMaximized(hwnd, wp, source, target, dpiScale, NormalToScreen, ScreenToNormal);

        return MoveNormal(hwnd, source, target);
    }

    private static MoveResult MoveMaximized(IntPtr hwnd, WINDOWPLACEMENT wp, MonitorInfo source, MonitorInfo target, double dpiScale,
        Func<RECT, RECT> normalToScreen, Func<RECT, RECT> screenToNormal)
    {
        var normal = normalToScreen(wp.rcNormalPosition);
        var mapped = WindowPlacementCalculator.MapFrame(normal, source.WorkArea, target.WorkArea, dpiScale);

        // Preferred: retarget the restore rectangle; Windows re-maximizes onto the monitor containing it.
        wp.rcNormalPosition = screenToNormal(mapped);
        wp.showCmd = SW_SHOWMAXIMIZED;
        wp.flags = 0;
        SetWindowPlacement(hwnd, ref wp);
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target.Handle)
            return MoveResult.Ok("maximized on target");

        // Fallback for apps that ignore placement changes while maximized: restore, move, maximize.
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(hwnd, IntPtr.Zero, mapped.Left, mapped.Top, mapped.Width, mapped.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        ShowWindow(hwnd, SW_SHOWMAXIMIZED);
        return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target.Handle
            ? MoveResult.Ok("maximized on target (restore/move/maximize)")
            : MoveResult.Fail("window refused to move while maximized");
    }

    private static MoveResult MoveNormal(IntPtr hwnd, MonitorInfo source, MonitorInfo target)
    {
        const uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        RECT frame0 = GetFrameBounds(hwnd);
        GetWindowRect(hwnd, out RECT win0);

        // Phase 1: move (keeping size) so that a DPI change, if any, is applied by Windows / the app itself.
        // Per-monitor-aware apps resize themselves in WM_DPICHANGED; unaware apps are scaled by Windows.
        var roughFrame = WindowPlacementCalculator.MapFrame(frame0, source.WorkArea, target.WorkArea);
        var roughWin = WindowPlacementCalculator.FrameToWindowRect(roughFrame, win0, frame0);
        if (!SetWindowPos(hwnd, IntPtr.Zero, roughWin.Left, roughWin.Top, 0, 0, flags | SWP_NOSIZE))
            return MoveResult.Fail($"SetWindowPos failed ({Marshal.GetLastWin32Error()}) — likely blocked (elevated or protected window)");

        // Phase 2: with the size the window now has on the target, place it precisely and clamp to the work area.
        RECT frame1 = GetFrameBounds(hwnd);
        GetWindowRect(hwnd, out RECT win1);
        var finalFrame = WindowPlacementCalculator.MapFrameWithSize(frame0, source.WorkArea, target.WorkArea, frame1.Width, frame1.Height);
        var finalWin = WindowPlacementCalculator.FrameToWindowRect(finalFrame, win1, frame1);
        bool resize = finalFrame.Width != frame1.Width || finalFrame.Height != frame1.Height;
        SetWindowPos(hwnd, IntPtr.Zero, finalWin.Left, finalWin.Top, finalWin.Width, finalWin.Height, flags | (resize ? 0 : SWP_NOSIZE));

        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != target.Handle)
            return MoveResult.Fail("window did not end up on the target monitor (app may control its own position)");
        return MoveResult.Ok(resize ? $"moved and resized to fit {finalFrame}" : $"moved to {finalFrame}");
    }

    /// <summary>
    /// Moves a window that has been created but not shown yet, without showing or activating it, so that it
    /// first appears on the target monitor. Keeps the size (the app/Windows apply any DPI change themselves)
    /// and the relative position, clamped to the target work area. Maximized-at-creation windows get their
    /// restore rectangle retargeted so they maximize on the target monitor.
    /// </summary>
    public static MoveResult PrePlaceHidden(IntPtr hwnd, MonitorInfo target, IReadOnlyList<MonitorInfo> monitors)
    {
        if (!IsWindow(hwnd)) return MoveResult.Fail("window no longer exists");
        if (IsWindowVisible(hwnd)) return MoveResult.Fail("already visible");
        var source = monitors.FirstOrDefault(m => m.Handle == MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        if (source == null) return MoveResult.Fail("source monitor unknown");
        if (source.Handle == target.Handle) return MoveResult.Ok("already on target monitor");

        if (IsZoomed(hwnd) || IsIconic(hwnd))
        {
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref wp)) return MoveResult.Fail("GetWindowPlacement failed");
            var normal = WindowPlacementCalculator.WorkspaceToScreen(wp.rcNormalPosition, primary.Bounds, primary.WorkArea);
            var mapped = WindowPlacementCalculator.MapFrame(normal, source.WorkArea, target.WorkArea);
            wp.rcNormalPosition = WindowPlacementCalculator.ScreenToWorkspace(mapped, primary.Bounds, primary.WorkArea);
            wp.showCmd = SW_HIDE; // update the placement only; the app shows the window itself
            wp.flags = 0;
            return SetWindowPlacement(hwnd, ref wp)
                ? MoveResult.Ok($"restore position set to {mapped} before showing")
                : MoveResult.Fail($"SetWindowPlacement failed ({Marshal.GetLastWin32Error()})");
        }

        GetWindowRect(hwnd, out RECT r);
        var dest = WindowPlacementCalculator.MapFrame(r, source.WorkArea, target.WorkArea);
        SetWindowPos(hwnd, IntPtr.Zero, dest.Left, dest.Top, dest.Width, dest.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target.Handle)
            return MoveResult.Ok($"placed at {dest} before showing");

        // Some apps (e.g. Electron/Chromium) reject direct moves of their hidden window. Retargeting the restore
        // rectangle instead still helps when the app then shows the window maximized: it maximizes on the
        // monitor that contains that rectangle.
        var primary2 = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        var wp2 = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (GetWindowPlacement(hwnd, ref wp2))
        {
            wp2.rcNormalPosition = WindowPlacementCalculator.ScreenToWorkspace(dest, primary2.Bounds, primary2.WorkArea);
            wp2.showCmd = SW_HIDE;
            wp2.flags = 0;
            SetWindowPlacement(hwnd, ref wp2);
            if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target.Handle)
                return MoveResult.Ok($"restore position set to {dest} before showing");
        }
        return MoveResult.Fail("the app kept its own position (it will be moved right after it appears)");
    }

    /// <summary>
    /// Moves a borderless full-screen window (e.g. a game) to another monitor and makes it cover that monitor
    /// exactly. The app receives a normal WM_SIZE and adapts its rendering like any resized window.
    /// </summary>
    public static MoveResult MoveFullscreen(IntPtr hwnd, MonitorInfo target)
    {
        if (!IsWindow(hwnd)) return MoveResult.Fail("window no longer exists");
        var b = target.Bounds;
        if (!SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER))
            return MoveResult.Fail($"SetWindowPos failed ({Marshal.GetLastWin32Error()})");
        return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target.Handle
            ? MoveResult.Ok($"full screen on Monitor {target.Number} ({b.Width}x{b.Height})")
            : MoveResult.Fail("window did not move");
    }

    /// <summary>Restores (if minimized), moves to target and brings to foreground. Used by the launcher.</summary>
    public static MoveResult BringToMonitor(IntPtr hwnd, MonitorInfo target, IReadOnlyList<MonitorInfo> monitors)
    {
        MoveResult r = MoveToMonitor(hwnd, target, monitors);
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (!SetForegroundWindow(hwnd)) Log.Debug("SetForegroundWindow was refused by Windows (foreground lock)");
        return r;
    }
}
