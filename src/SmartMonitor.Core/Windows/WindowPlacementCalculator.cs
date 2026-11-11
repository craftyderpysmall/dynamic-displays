using SmartMonitor.Core.Native;

namespace SmartMonitor.Core.Windows;

/// <summary>
/// Pure geometry for moving a window between monitors. No Win32 calls, so it is unit-tested directly.
/// </summary>
public static class WindowPlacementCalculator
{
    /// <summary>
    /// Maps a window's visible frame from one work area to another.
    /// <list type="bullet">
    /// <item>Size is kept (multiplied by <paramref name="sizeScale"/>, e.g. a DPI ratio) and clamped to the target work area.</item>
    /// <item>Position keeps the window's relative place in the free space: left-aligned stays left-aligned,
    /// centered stays centered, right-snapped stays right-snapped.</item>
    /// <item>The result always lies fully inside <paramref name="targetWork"/> (never behind the taskbar or off-screen).</item>
    /// </list>
    /// </summary>
    public static RECT MapFrame(RECT frame, RECT sourceWork, RECT targetWork, double sizeScale = 1.0)
    {
        int w = Math.Max(1, (int)Math.Round(frame.Width * sizeScale));
        int h = Math.Max(1, (int)Math.Round(frame.Height * sizeScale));
        return MapFrameWithSize(frame, sourceWork, targetWork, w, h);
    }

    /// <summary>
    /// Like <see cref="MapFrame"/> but with an explicit result size (e.g. the size the app chose for itself
    /// after a DPI change). The relative position is still taken from the original <paramref name="frame"/>.
    /// </summary>
    public static RECT MapFrameWithSize(RECT frame, RECT sourceWork, RECT targetWork, int w, int h)
    {
        w = Math.Min(Math.Max(1, w), targetWork.Width);
        h = Math.Min(Math.Max(1, h), targetWork.Height);

        double fx = RelativeFraction(frame.Left, frame.Width, sourceWork.Left, sourceWork.Width);
        double fy = RelativeFraction(frame.Top, frame.Height, sourceWork.Top, sourceWork.Height);

        int left = targetWork.Left + (int)Math.Round(fx * (targetWork.Width - w));
        int top = targetWork.Top + (int)Math.Round(fy * (targetWork.Height - h));
        return new RECT(left, top, left + w, top + h);
    }

    /// <summary>
    /// Fraction (0..1) of the free space to the left/top of the window. Windows that are as large as
    /// (or larger than) the work area, or partly off it, are treated sensibly via clamping.
    /// </summary>
    internal static double RelativeFraction(int pos, int size, int areaPos, int areaSize)
    {
        int free = areaSize - size;
        // Fills (or exceeds) the area in this dimension, e.g. a snapped full-height window: align to the start
        // so it stays flush with the top/left edge on a larger target.
        if (free <= 0) return 0.0;
        double f = (pos - areaPos) / (double)free;
        return Math.Clamp(f, 0.0, 1.0);
    }

    /// <summary>
    /// Converts a desired visible frame to the rectangle to pass to SetWindowPos, using the window's
    /// current invisible-border margins (window rect minus frame rect).
    /// </summary>
    public static RECT FrameToWindowRect(RECT desiredFrame, RECT currentWindowRect, RECT currentFrame)
    {
        int dl = currentFrame.Left - currentWindowRect.Left;
        int dt = currentFrame.Top - currentWindowRect.Top;
        int dr = currentWindowRect.Right - currentFrame.Right;
        int db = currentWindowRect.Bottom - currentFrame.Bottom;
        // Margins are small and non-negative in practice; guard against odd values from broken apps.
        if (dl < 0 || dt < 0 || dr < 0 || db < 0 || dl > 64 || dt > 64 || dr > 64 || db > 64) dl = dt = dr = db = 0;
        return new RECT(desiredFrame.Left - dl, desiredFrame.Top - dt, desiredFrame.Right + dr, desiredFrame.Bottom + db);
    }

    /// <summary>
    /// GetWindowPlacement's rcNormalPosition is in "workspace" coordinates for windows without
    /// WS_EX_TOOLWINDOW: offset by the primary monitor's work area origin (non-zero when the primary
    /// taskbar is on the top or left).
    /// </summary>
    public static RECT ScreenToWorkspace(RECT screen, RECT primaryBounds, RECT primaryWork)
    {
        int ox = primaryWork.Left - primaryBounds.Left;
        int oy = primaryWork.Top - primaryBounds.Top;
        return new RECT(screen.Left - ox, screen.Top - oy, screen.Right - ox, screen.Bottom - oy);
    }

    public static RECT WorkspaceToScreen(RECT ws, RECT primaryBounds, RECT primaryWork)
    {
        int ox = primaryWork.Left - primaryBounds.Left;
        int oy = primaryWork.Top - primaryBounds.Top;
        return new RECT(ws.Left + ox, ws.Top + oy, ws.Right + ox, ws.Bottom + oy);
    }

    /// <summary>Index of the monitor (by bounds) containing the point, or -1.</summary>
    public static int MonitorIndexAt(IReadOnlyList<RECT> monitorBounds, int x, int y)
    {
        for (int i = 0; i < monitorBounds.Count; i++)
        {
            var b = monitorBounds[i];
            if (x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom) return i;
        }
        return -1;
    }
}
