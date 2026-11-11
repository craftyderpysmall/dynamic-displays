using System.Runtime.InteropServices;
using SmartMonitor.Core.Config;
using SmartMonitor.Core.Logging;
using SmartMonitor.Core.Monitors;
using SmartMonitor.Core.Native;
using SmartMonitor.Core.Windows;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.Core.Launch;

public sealed record LaunchOutcome(bool Success, string Message);

public static class AppLauncher
{
    private static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Exe names whose windows count as this app: configured names plus the target's own exe.</summary>
    public static HashSet<string> GetProcessNames(LauncherApp app)
    {
        var names = new HashSet<string>(app.ProcessNames.Select(FollowCursorEngine.NormalizeExe).Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        string? exe = ResolveExe(app.Target);
        if (exe != null) names.Add(Path.GetFileName(exe));
        return names;
    }

    /// <summary>The executable behind a target (.exe directly, or a .lnk's target). Null for URLs / packaged apps.</summary>
    public static string? ResolveExe(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return target;
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            var info = ShellLink.Read(target);
            if (info != null && info.TargetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return info.TargetPath;
        }
        return null;
    }

    /// <summary>Visible (including minimized) top-level application windows belonging to the app.</summary>
    public static List<IntPtr> FindAppWindows(HashSet<string> processNames)
    {
        var result = new List<IntPtr>();
        if (processNames.Count == 0) return result;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            long ex = GetExStyle(h);
            if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
            if (NativeMethods.IsCloaked(h) && !IsIconic(h)) return true; // other virtual desktop / hidden UWP
            var f = WindowFacts.Read(h);
            if (processNames.Contains(f.ProcessName)) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result; // EnumWindows order is Z-order: most recently active first
    }

    /// <param name="expectLaunch">
    /// When the tray engine is running: tells it which processes/monitor to expect so it places the new window.
    /// When null, this call waits for the window itself (standalone launcher).
    /// </param>
    /// <param name="deElevate">
    /// True when the caller is elevated: start the app through Explorer so it does not inherit admin rights.
    /// </param>
    public static LaunchOutcome Launch(LauncherApp app, MonitorInfo target, IReadOnlyList<MonitorInfo> monitors,
        Action<IEnumerable<string>, string, TimeSpan>? expectLaunch, bool deElevate = false)
    {
        var names = GetProcessNames(app);

        if (app.IfRunning == IfRunningBehavior.ActivateExisting)
        {
            var existing = FindAppWindows(names);
            if (existing.Count > 0)
            {
                // Register first so the engine places the activated window on the launch monitor, not the cursor's.
                expectLaunch?.Invoke(names, target.DeviceName, TimeSpan.FromSeconds(3));
                var r = WindowMover.BringToMonitor(existing[0], target, monitors);
                string msg = $"{app.Name}: activated existing window on Monitor {target.Number} ({r.Message})";
                Log.Info(msg);
                return new LaunchOutcome(r.Success, msg);
            }
        }

        var before = new HashSet<IntPtr>(FindAppWindows(names));
        expectLaunch?.Invoke(names, target.DeviceName, WatchTimeout);

        AllowSetForegroundWindow(ASFW_ANY);
        if (deElevate)
            return LaunchViaExplorer(app, target, names, before, monitors, expectLaunch);

        var sei = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_HMONITOR,
            lpVerb = "open",
            lpFile = app.Target,
            lpParameters = string.IsNullOrWhiteSpace(app.Arguments) ? null : app.Arguments,
            lpDirectory = string.IsNullOrWhiteSpace(app.WorkingDirectory) ? null : app.WorkingDirectory,
            nShow = SW_SHOWNORMAL,
            hIconOrMonitor = target.Handle,
        };
        if (!ShellExecuteEx(ref sei))
        {
            int err = Marshal.GetLastWin32Error();
            string msg = $"{app.Name}: failed to start \"{app.Target}\" (error {err}: {new System.ComponentModel.Win32Exception(err).Message})";
            Log.Error(msg);
            return new LaunchOutcome(false, msg);
        }

        if (sei.hProcess != IntPtr.Zero)
        {
            uint pid = GetProcessId(sei.hProcess);
            if (GetProcessPath(pid) is string p && names.Add(Path.GetFileName(p)))
                expectLaunch?.Invoke(new[] { Path.GetFileName(p) }, target.DeviceName, WatchTimeout);
            CloseHandle(sei.hProcess);
        }
        Log.Info($"{app.Name}: started for Monitor {target.Number} (watching: {string.Join(", ", names)})");

        if (expectLaunch == null)
            return WaitAndPlace(app, names, before, target, monitors);
        return new LaunchOutcome(true, $"{app.Name}: started on Monitor {target.Number}");
    }

    /// <summary>
    /// Asks the already-running (non-elevated) Explorer to open the target, which drops elevation.
    /// Arguments are carried by a temporary shortcut because explorer.exe only accepts a path.
    /// </summary>
    private static LaunchOutcome LaunchViaExplorer(LauncherApp app, MonitorInfo target, HashSet<string> names, HashSet<IntPtr> before,
        IReadOnlyList<MonitorInfo> monitors, Action<IEnumerable<string>, string, TimeSpan>? expectLaunch)
    {
        string path = app.Target;
        if (!string.IsNullOrWhiteSpace(app.Arguments) || !string.IsNullOrWhiteSpace(app.WorkingDirectory))
        {
            string tmp = Path.Combine(Path.GetTempPath(), "SmartMonitor", $"launch-{app.Id}.lnk");
            if (ShellLink.Create(tmp, app.Target, app.Arguments, app.WorkingDirectory, "", app.Name)) path = tmp;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error($"{app.Name}: explorer hand-off failed", ex);
            return new LaunchOutcome(false, $"{app.Name}: could not start ({ex.Message})");
        }
        Log.Info($"{app.Name}: started non-elevated via Explorer for Monitor {target.Number}");
        return expectLaunch == null
            ? WaitAndPlace(app, names, before, target, monitors)
            : new LaunchOutcome(true, $"{app.Name}: started on Monitor {target.Number}");
    }

    /// <summary>Standalone mode (tray app not running): poll for the new window and move it.</summary>
    private static LaunchOutcome WaitAndPlace(LauncherApp app, HashSet<string> names, HashSet<IntPtr> before, MonitorInfo target, IReadOnlyList<MonitorInfo> monitors)
    {
        var deadline = DateTime.UtcNow + WatchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            var fresh = FindAppWindows(names).Where(h => !before.Contains(h) && !IsIconic(h)).ToList();
            if (fresh.Count == 0) continue;
            var hwnd = fresh[0];
            Thread.Sleep(150); // let the app finish its own initial positioning
            var r = WindowMover.MoveToMonitor(hwnd, target, monitors);
            Thread.Sleep(1000);
            if (IsWindow(hwnd) && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != target.Handle)
                r = WindowMover.MoveToMonitor(hwnd, target, monitors);
            string msg = $"{app.Name}: window placed on Monitor {target.Number} ({r.Message})";
            Log.Info(msg);
            return new LaunchOutcome(r.Success, msg);
        }
        string timeout = $"{app.Name}: no new window appeared within {WatchTimeout.TotalSeconds:0}s (the app may reuse a hidden window or run in the tray)";
        Log.Warn(timeout);
        return new LaunchOutcome(false, timeout);
    }

    /// <summary>Creates a shortcut that launches the app through SmartMonitor (pinnable to the taskbar).</summary>
    public static bool CreateShortcut(LauncherApp app, string folder, string smartMonitorExe)
    {
        string safeName = string.Concat(app.Name.Split(Path.GetInvalidFileNameChars()));
        string lnk = Path.Combine(folder, $"{safeName} (Smart Monitor).lnk");
        string icon = ResolveExe(app.Target) is string exe ? exe + ",0" : smartMonitorExe + ",0";
        if (app.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShellLink.Read(app.Target) is { } li && li.IconLocation.Trim(',', '0').Length > 0)
            icon = li.IconLocation;
        bool ok = ShellLink.Create(lnk, smartMonitorExe, $"--launch {app.Id}", Path.GetDirectoryName(smartMonitorExe)!, icon,
            $"Open {app.Name} on the monitor you launched it from");
        if (ok) Log.Info($"Created shortcut {lnk}");
        return ok;
    }
}
