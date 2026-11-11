using System.Text.Json;
using System.Text.Json.Serialization;
using SmartMonitor.Core.Logging;

namespace SmartMonitor.Core.Config;

public enum IfRunningBehavior
{
    /// <summary>Bring the existing window to the launch monitor instead of starting another copy.</summary>
    ActivateExisting,
    /// <summary>Always start a new instance (the engine still moves its window).</summary>
    AlwaysLaunchNew,
}

public sealed class LauncherApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>Exe, .lnk, URL or shell:AppsFolder\AUMID.</summary>
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    /// <summary>Exe names whose windows belong to this app (e.g. Discord's Update.exe launches Discord.exe).</summary>
    public List<string> ProcessNames { get; set; } = new();
    public IfRunningBehavior IfRunning { get; set; } = IfRunningBehavior.ActivateExisting;
}

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;

    // Triggers
    /// <summary>Window activated without the user clicking it: link redirects, taskbar clicks, app self-activation.</summary>
    public bool FollowOnActivation { get; set; } = true;
    /// <summary>Activations while Alt/Win is held (Alt+Tab, Win+Tab).</summary>
    public bool FollowOnKeyboardSwitch { get; set; } = true;
    public bool FollowOnRestore { get; set; } = true;
    public bool FollowOnNewWindow { get; set; } = true;
    /// <summary>Move new windows while they are still hidden (right after creation) so they first appear on the right monitor.</summary>
    public bool PlaceBeforeShow { get; set; } = true;

    public bool SkipFullscreen { get; set; } = true;
    /// <summary>Move games (borderless full-screen) and splash/loading windows of apps that are just starting.</summary>
    public bool MoveStartingGames { get; set; } = true;

    // Timing (ms)
    public int ActivationDelayMs { get; set; } = 60;
    public int NewWindowDelayMs { get; set; } = 0;
    public int RecheckDelayMs { get; set; } = 1000;
    public int CooldownMs { get; set; } = 600;

    public List<string> ExcludedProcesses { get; set; } = new();
    public List<LauncherApp> Apps { get; set; } = new();

    public bool StartWithWindows { get; set; }
    public bool StartElevated { get; set; }
    public bool DebugLogging { get; set; }
    public bool ShowWindowOnStart { get; set; } = true;

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

public static class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartMonitor");
    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {FilePath}; using defaults (the broken file is kept as .bak)", ex);
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save settings to {FilePath}", ex);
        }
    }
}
