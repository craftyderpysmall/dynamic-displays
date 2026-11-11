using System.Diagnostics;
using Microsoft.Win32;
using SmartMonitor.Core.Logging;

namespace SmartMonitor.Core.Config;

/// <summary>Start-with-Windows registration (normal: HKCU Run key; elevated: a Task Scheduler logon task).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SmartMonitor";
    public const string TaskName = "SmartMonitor (elevated)";

    public static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path");

    public static bool IsRunKeySet()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    public static void SetRunKey(bool enable)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) k.SetValue(ValueName, $"\"{ExePath}\" --tray");
        else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName);
        Log.Info(enable ? "Start with Windows: enabled (HKCU Run)" : "Start with Windows: disabled (HKCU Run)");
    }

    public static bool IsElevatedTaskRegistered()
    {
        var r = Run("schtasks.exe", $"/Query /TN \"{TaskName}\"", elevate: false);
        return r == 0;
    }

    /// <summary>The exe the elevated logon task starts, or null if there is no task.</summary>
    public static string? GetElevatedTaskExe()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\" /XML")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (p.ExitCode != 0) return null;
            var m = System.Text.RegularExpressions.Regex.Match(xml, "<Command>\"?([^<\"]+)\"?</Command>");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not query the elevated task: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// If the Run entry exists but points at another copy of SmartMonitor.exe (e.g. after reinstalling elsewhere),
    /// point it at this one.
    /// </summary>
    public static void RepairRunKeyPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(ValueName) is not string v) return;
            if (v.Contains(ExePath, StringComparison.OrdinalIgnoreCase)) return;
            k.SetValue(ValueName, $"\"{ExePath}\" --tray");
            Log.Info($"Start with Windows entry updated to {ExePath} (was: {v})");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check the Start with Windows entry: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates/deletes a logon task that starts SmartMonitor with highest privileges (no UAC prompt at logon).
    /// Changing it requires one UAC confirmation now.
    /// </summary>
    public static bool SetElevatedTask(bool enable)
    {
        string args = enable
            ? $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /IT /DELAY 0000:10 /TR \"\\\"{ExePath}\\\" --tray\""
            : $"/Delete /F /TN \"{TaskName}\"";
        int code = Run("schtasks.exe", args, elevate: true);
        Log.Info($"Elevated startup task {(enable ? "create" : "delete")}: exit code {code}");
        return code == 0;
    }

    private static int Run(string file, string args, bool elevate)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = elevate,
                Verb = elevate ? "runas" : "",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (!elevate) { psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warn("UAC confirmation was declined");
            return -2;
        }
        catch (Exception ex)
        {
            Log.Error($"Running {file} failed", ex);
            return -1;
        }
    }

    /// <summary>Read-only view of the UAC policy relevant to "does the admin prompt take over all displays".</summary>
    public static (bool secureDesktop, int consentBehaviorAdmin) ReadUacPolicy()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        int secure = k?.GetValue("PromptOnSecureDesktop") is int s ? s : 1;
        int behavior = k?.GetValue("ConsentPromptBehaviorAdmin") is int b ? b : 5;
        return (secure != 0, behavior);
    }
}
