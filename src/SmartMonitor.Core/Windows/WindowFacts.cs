using System.Collections.Concurrent;
using SmartMonitor.Core.Native;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.Core.Windows;

/// <summary>Everything the filter needs to know about a window, read once from Win32.</summary>
public sealed record WindowFacts
{
    public IntPtr Handle { get; init; }
    public string ClassName { get; init; } = "";
    public string Title { get; init; } = "";
    public uint ProcessId { get; init; }
    /// <summary>Executable file name, e.g. "chrome.exe". For UWP apps, the hosted app's exe, not ApplicationFrameHost.exe.</summary>
    public string ProcessName { get; init; } = "";
    public string? ProcessPath { get; init; }
    public long Style { get; init; }
    public long ExStyle { get; init; }
    public bool IsVisible { get; init; }
    public bool IsMinimized { get; init; }
    public bool IsMaximized { get; init; }
    public bool IsCloaked { get; init; }
    public bool IsTopLevel { get; init; }
    public bool HasOwner { get; init; }
    public bool IsHung { get; init; }
    /// <summary>null when the process could not be opened (typically: more privileged than us).</summary>
    public bool? IsElevated { get; init; }
    public RECT Frame { get; init; }
    public RECT MonitorBounds { get; init; }

    public string Describe() => $"{(ProcessName.Length > 0 ? ProcessName : "pid " + ProcessId)} \"{Truncate(Title, 60)}\" [{ClassName}] 0x{Handle.ToInt64():X}";

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static readonly ConcurrentDictionary<uint, (string? path, DateTime at)> PathCache = new();

    public static WindowFacts Read(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        string cls = NativeMethods.GetClassName(hwnd);
        string? path = GetCachedPath(pid);

        // UWP apps: the top-level frame belongs to ApplicationFrameHost; the real app owns a child CoreWindow.
        if (cls == "ApplicationFrameWindow")
        {
            IntPtr core = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero)
            {
                GetWindowThreadProcessId(core, out uint corePid);
                if (corePid != 0 && corePid != pid)
                {
                    pid = corePid;
                    path = GetCachedPath(corePid);
                }
            }
        }

        RECT frame = GetFrameBounds(hwnd);
        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi);

        return new WindowFacts
        {
            Handle = hwnd,
            ClassName = cls,
            Title = GetWindowTitle(hwnd),
            ProcessId = pid,
            ProcessPath = path,
            ProcessName = path != null ? Path.GetFileName(path) : "",
            Style = GetStyle(hwnd),
            ExStyle = GetExStyle(hwnd),
            IsVisible = IsWindowVisible(hwnd),
            IsMinimized = IsIconic(hwnd),
            IsMaximized = IsZoomed(hwnd),
            IsCloaked = IsCloaked(hwnd),
            IsTopLevel = GetAncestor(hwnd, GA_ROOT) == hwnd,
            HasOwner = GetWindow(hwnd, GW_OWNER) != IntPtr.Zero,
            IsHung = IsHungAppWindow(hwnd),
            IsElevated = IsProcessElevated(pid),
            Frame = frame,
            MonitorBounds = mi.rcMonitor,
        };
    }

    private static string? GetCachedPath(uint pid)
    {
        if (pid == 0) return null;
        var now = DateTime.UtcNow;
        // PIDs get reused; a short cache lifetime keeps lookups cheap without serving stale names for long.
        if (PathCache.TryGetValue(pid, out var c) && now - c.at < TimeSpan.FromSeconds(30)) return c.path;
        string? p = GetProcessPath(pid);
        PathCache[pid] = (p, now);
        if (PathCache.Count > 512)
            foreach (var k in PathCache.Where(kv => now - kv.Value.at > TimeSpan.FromSeconds(30)).Select(kv => kv.Key).ToList())
                PathCache.TryRemove(k, out _);
        return p;
    }
}
