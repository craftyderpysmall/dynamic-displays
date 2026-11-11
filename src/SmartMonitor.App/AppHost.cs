using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using SmartMonitor.Core.Config;
using SmartMonitor.Core.Launch;
using SmartMonitor.Core.Logging;
using SmartMonitor.Core.Monitors;
using SmartMonitor.Core.Native;
using SmartMonitor.Core.Windows;

namespace SmartMonitor.App;

/// <summary>Owns the long-lived pieces: settings, engine, tray icon, instance pipe, main window.</summary>
public sealed class AppHost : IDisposable
{
    private readonly App _app;
    private readonly bool _startHidden;
    private FollowCursorEngine? _engine;
    private InstancePipe? _pipe;
    private TrayIcon? _tray;
    private MainWindow? _window;

    public AppSettings Settings { get; private set; }
    public bool IsElevated { get; } = NativeMethods.IsCurrentProcessElevated();
    public bool EngineRunning => _engine?.IsRunning == true;

    /// <summary>Recent engine decisions for the Test page (UI thread only).</summary>
    public ObservableCollection<EngineDecision> Decisions { get; } = new();
    public string LastLaunchContext { get; private set; } = "No Smart-shortcut launch yet.";

    public event Action? SettingsChanged;
    public event Action? MonitorsChanged;
    public event Action? LaunchContextChanged;

    public AppHost(App app, bool startHidden)
    {
        _app = app;
        _startHidden = startHidden;
        Settings = SettingsStore.Load();
        Log.MinimumLevel = Settings.DebugLogging ? LogLevel.Debug : LogLevel.Info;
    }

    public void Start()
    {
        _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        StartupManager.RepairRunKeyPath();
        _tray = new TrayIcon(this);
        ApplyEnabled();

        _pipe = new InstancePipe(OnPipeMessage);
        _pipe.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        if (!_startHidden) ShowMainWindow();
        Log.Info($"Ready. {MonitorService.GetMonitors().Count} monitor(s): " + string.Join("; ", MonitorService.GetMonitors()));
    }

    // ------------------------------------------------------------- settings

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
        _engine?.UpdateSettings(Settings.Clone());
        ApplyEnabled();
        SettingsChanged?.Invoke();
    }

    public void SetEnabled(bool enabled)
    {
        if (Settings.Enabled == enabled) return;
        Settings.Enabled = enabled;
        Log.Info(enabled ? "Enabled" : "Disabled");
        SaveSettings();
    }

    /// <summary>Hooks only exist while enabled: when disabled, Smart Monitor does not observe windows at all.</summary>
    private void ApplyEnabled()
    {
        if (Settings.Enabled && _engine == null)
        {
            _engine = new FollowCursorEngine(Settings.Clone());
            _engine.Decision += OnDecision;
            _engine.Start();
        }
        else if (!Settings.Enabled && _engine != null)
        {
            _engine.Decision -= OnDecision;
            _engine.Dispose();
            _engine = null;
            Log.Info("Engine stopped");
        }
        _tray?.Refresh();
    }

    private void OnDecision(EngineDecision d)
    {
        _app.Dispatcher.BeginInvoke(() =>
        {
            Decisions.Insert(0, d);
            while (Decisions.Count > 100) Decisions.RemoveAt(Decisions.Count - 1);
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Log.Info("Display configuration changed: " + string.Join("; ", MonitorService.GetMonitors()));
        _engine?.InvalidateMonitors();
        _app.Dispatcher.BeginInvoke(() => MonitorsChanged?.Invoke());
    }

    // ------------------------------------------------------------- pipe

    private void OnPipeMessage(string line)
    {
        var parts = line.Split('\t');
        switch (parts[0])
        {
            case "show":
                _app.Dispatcher.BeginInvoke(ShowMainWindow);
                break;
            case "exit":
                _app.Dispatcher.BeginInvoke(Exit);
                break;
            case "context" when parts.Length >= 4:
                LastLaunchContext = $"{DateTime.Now:HH:mm:ss}  {parts[1]} → Monitor {parts[2]} (detected via {DescribeSource(parts[3])})";
                _app.Dispatcher.BeginInvoke(() => LaunchContextChanged?.Invoke());
                break;
            case "expect" when parts.Length >= 4:
                var names = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries);
                _engine?.ExpectLaunch(names, parts[1], TimeSpan.FromMilliseconds(int.TryParse(parts[2], out int ms) ? ms : 15000));
                break;
            default:
                Log.Warn($"Unknown pipe message: {line}");
                break;
        }
    }

    public static string DescribeSource(string source) => source switch
    {
        nameof(LaunchContextSource.ShellStartupInfo) => "the monitor Windows reported for the click",
        nameof(LaunchContextSource.Cursor) => "cursor position at launch",
        _ => source,
    };

    // ------------------------------------------------------------- actions

    public void ShowMainWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow(this);
            _window.Closed += (_, _) => _window = null; // free the UI when closed; the tray keeps running
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void IdentifyMonitors() => IdentifyOverlay.ShowAll();

    /// <summary>Launch from the UI/tray: the target is the monitor under the cursor (where the user clicked).</summary>
    public void LaunchApp(LauncherApp app)
    {
        var monitors = MonitorService.GetMonitors();
        var target = MonitorService.FromHandle(MonitorService.MonitorAtCursor(), monitors) ?? monitors.First(m => m.IsPrimary);
        LastLaunchContext = $"{DateTime.Now:HH:mm:ss}  {app.Name} → Monitor {target.Number} (launched from Smart Monitor; cursor position)";
        LaunchContextChanged?.Invoke();
        var engine = _engine;
        Task.Run(() =>
        {
            var r = AppLauncher.Launch(app, target, monitors,
                engine != null ? engine.ExpectLaunch : null, deElevate: IsElevated);
            if (!r.Success) _tray?.ShowBalloon("Could not launch", r.Message);
        });
    }

    public bool RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(StartupManager.ExePath, "--tray --replace") { UseShellExecute = true, Verb = "runas" });
            return true; // the new instance asks us to exit via the pipe
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Restart elevated: UAC declined");
            return false;
        }
    }

    public void Exit()
    {
        _window?.Close();
        Dispose();
        _app.Shutdown();
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _pipe?.Dispose(); _pipe = null;
        if (_engine != null) { _engine.Decision -= OnDecision; _engine.Dispose(); _engine = null; }
        _tray?.Dispose(); _tray = null;
    }
}
