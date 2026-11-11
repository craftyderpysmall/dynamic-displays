using System.Runtime.InteropServices;
using SmartMonitor.Core.Logging;

namespace SmartMonitor.Core.Launch;

/// <summary>Reads and writes .lnk files through the WScript.Shell automation object (built into Windows).</summary>
public static class ShellLink
{
    public sealed record LinkInfo(string TargetPath, string Arguments, string WorkingDirectory, string IconLocation);

    public static LinkInfo? Read(string lnkPath)
    {
        object? shell = null, link = null;
        try
        {
            shell = CreateShell();
            dynamic d = shell!;
            link = d.CreateShortcut(lnkPath);
            dynamic l = link!;
            return new LinkInfo((string)l.TargetPath, (string)l.Arguments, (string)l.WorkingDirectory, (string)l.IconLocation);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read shortcut {lnkPath}: {ex.Message}");
            return null;
        }
        finally { Release(link); Release(shell); }
    }

    public static bool Create(string lnkPath, string targetPath, string arguments, string workingDirectory, string iconLocation, string description)
    {
        object? shell = null, link = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            shell = CreateShell();
            dynamic d = shell!;
            link = d.CreateShortcut(lnkPath);
            dynamic l = link!;
            l.TargetPath = targetPath;
            l.Arguments = arguments;
            l.WorkingDirectory = workingDirectory;
            if (!string.IsNullOrEmpty(iconLocation)) l.IconLocation = iconLocation;
            l.Description = description;
            l.Save();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not create shortcut {lnkPath}", ex);
            return false;
        }
        finally { Release(link); Release(shell); }
    }

    private static object? CreateShell()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available");
        return Activator.CreateInstance(t);
    }

    private static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
    }
}
