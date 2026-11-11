using System.Runtime.InteropServices;

namespace SmartMonitor.Core.Native;

/// <summary>
/// Reads monitor friendly names (as shown in Settings > Display) via the DisplayConfig API.
/// EnumDisplayDevices usually only returns "Generic PnP Monitor".
/// </summary>
public static class DisplayConfig
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen;
        public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME requestPacket);

    /// <summary>Map of GDI device name (\\.\DISPLAY1) to friendly monitor name. Empty on failure.</summary>
    public static Dictionary<string, string> GetFriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
        var paths = new PATH_INFO[np];
        var modes = new MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;

        for (int i = 0; i < np; i++)
        {
            var p = paths[i];
            var src = new SOURCE_DEVICE_NAME
            {
                header = new DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>(),
                    adapterId = p.sourceInfo.adapterId,
                    id = p.sourceInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

            var tgt = new TARGET_DEVICE_NAME
            {
                header = new DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>(),
                    adapterId = p.targetInfo.adapterId,
                    id = p.targetInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

            string name = tgt.monitorFriendlyDeviceName;
            if (string.IsNullOrWhiteSpace(name))
                name = tgt.outputTechnology == 0x80000000 ? "Built-in display" : "";
            if (!string.IsNullOrWhiteSpace(name) && !result.ContainsKey(src.viewGdiDeviceName))
                result[src.viewGdiDeviceName] = name;
        }
        return result;
    }
}
