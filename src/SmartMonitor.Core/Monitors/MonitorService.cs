using System.Runtime.InteropServices;
using SmartMonitor.Core.Logging;
using SmartMonitor.Core.Native;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.Core.Monitors;

/// <summary>
/// Enumerates displays. The calling process must be Per-Monitor DPI Aware (v2) so that all
/// values are physical pixels; the app manifest takes care of that.
/// </summary>
public static class MonitorService
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var raw = new List<(IntPtr h, MONITORINFOEX mi)>();
        MonitorEnumProc proc = (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi)) raw.Add((hMon, mi));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero))
            Log.Warn("EnumDisplayMonitors failed");
        GC.KeepAlive(proc);

        var friendly = GetFriendlyNames();

        return raw
            .OrderBy(r => r.mi.rcMonitor.Left).ThenBy(r => r.mi.rcMonitor.Top)
            .Select((r, i) =>
            {
                uint dpi = 96;
                if (GetDpiForMonitor(r.h, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0) dpi = dx;
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                bool haveMode = EnumDisplaySettings(r.mi.szDevice, ENUM_CURRENT_SETTINGS, ref dm);
                return new MonitorInfo
                {
                    Handle = r.h,
                    Number = i + 1,
                    DeviceName = r.mi.szDevice,
                    FriendlyName = friendly.TryGetValue(r.mi.szDevice, out var f) ? f : "",
                    Bounds = r.mi.rcMonitor,
                    WorkArea = r.mi.rcWork,
                    IsPrimary = (r.mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    Dpi = dpi,
                    Orientation = haveMode ? (DisplayOrientation)(dm.dmDisplayOrientation & 3) : DisplayOrientation.Landscape,
                    RefreshRate = haveMode ? dm.dmDisplayFrequency : 0,
                };
            })
            .ToList();
    }

    public static IntPtr MonitorAtCursor()
    {
        GetCursorPos(out POINT p);
        return MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
    }

    public static MonitorInfo? FromHandle(IntPtr hMonitor, IReadOnlyList<MonitorInfo>? monitors = null)
        => (monitors ?? GetMonitors()).FirstOrDefault(m => m.Handle == hMonitor);

    /// <summary>Monitor name as shown in Windows Settings (e.g. "DELL U2720Q"), keyed by \\.\DISPLAYn.</summary>
    private static Dictionary<string, string> GetFriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            result = DisplayConfig.GetFriendlyNames();
            if (result.Count > 0) return result;
        }
        catch (Exception ex)
        {
            Log.Debug($"DisplayConfig names unavailable: {ex.Message}");
        }
        try
        {
            for (uint i = 0; ; i++)
            {
                var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (EnumDisplayDevices(adapter.DeviceName, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString))
                    result[adapter.DeviceName] = mon.DeviceString;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Friendly monitor names unavailable: {ex.Message}");
        }
        return result;
    }
}
