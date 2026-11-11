using SmartMonitor.Core.Monitors;
using SmartMonitor.Core.Native;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.Core.Launch;

public enum LaunchContextSource
{
    /// <summary>Explorer passed the monitor of the click in STARTUPINFO (shortcut / taskbar / Start launch).</summary>
    ShellStartupInfo,
    /// <summary>Monitor under the cursor when the launcher process started.</summary>
    Cursor,
    /// <summary>Explicitly provided (e.g. chosen in the UI).</summary>
    Explicit,
}

public sealed record LaunchContext(MonitorInfo Monitor, LaunchContextSource Source, bool StartedFromShortcut)
{
    public override string ToString() => $"Monitor {Monitor.Number} via {Source}{(StartedFromShortcut ? " (shortcut)" : "")}";
}

public static class LaunchContextResolver
{
    /// <summary>
    /// Determines which monitor this process was launched for. Must be called early in Main:
    /// the cursor fallback is only meaningful close to the click.
    /// <para>
    /// When the shell (Explorer: desktop, taskbar, Start, Run dialog) starts a program it calls ShellExecuteEx
    /// with SEE_MASK_HMONITOR, and CreateProcess receives that HMONITOR in STARTUPINFO.hStdOutput when
    /// STARTF_USESTDHANDLES is not set. This is how well-behaved Win32 apps can open on the "right" monitor.
    /// We validate the handle against the real monitor list before trusting it.
    /// </para>
    /// </summary>
    public static LaunchContext Resolve(IReadOnlyList<MonitorInfo> monitors)
    {
        GetStartupInfo(out STARTUPINFO si);
        bool fromShortcut = (si.dwFlags & STARTF_TITLEISLINKNAME) != 0;
        if ((si.dwFlags & STARTF_USESTDHANDLES) == 0 && si.hStdOutput != IntPtr.Zero)
        {
            var m = monitors.FirstOrDefault(x => x.Handle == si.hStdOutput);
            if (m != null) return new LaunchContext(m, LaunchContextSource.ShellStartupInfo, fromShortcut);
        }
        GetCursorPos(out POINT p);
        IntPtr h = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var cm = monitors.FirstOrDefault(x => x.Handle == h) ?? monitors.First(x => x.IsPrimary);
        return new LaunchContext(cm, LaunchContextSource.Cursor, fromShortcut);
    }

    /// <summary>Raw STARTUPINFO details for diagnostics in the log.</summary>
    public static string DescribeStartupInfo()
    {
        GetStartupInfo(out STARTUPINFO si);
        return $"STARTUPINFO flags=0x{si.dwFlags:X} hStdOutput=0x{si.hStdOutput.ToInt64():X}";
    }
}
