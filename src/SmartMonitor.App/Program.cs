using SmartMonitor.Core.Config;
using SmartMonitor.Core.Launch;
using SmartMonitor.Core.Logging;
using SmartMonitor.Core.Monitors;
using SmartMonitor.Core.Native;

namespace SmartMonitor.App;

/// <summary>
/// Entry point.
///   (no args)          start (or show) the tray app with its window
///   --tray             start the tray app hidden (used by autostart)
///   --launch &lt;id&gt;      launch a configured app on the monitor it was launched from (used by Smart shortcuts)
///   --replace          ask a running instance to exit, then take over (used when restarting elevated)
/// </summary>
public static class Program
{
    private const string MutexName = @"Local\SmartMonitor.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        Log.Initialize();
        try
        {
            if (args.Length >= 2 && args[0].Equals("--launch", StringComparison.OrdinalIgnoreCase))
                return RunLauncher(args[1]);
            return RunTray(args);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            NativeDialogs.ShowError($"Smart Monitor hit an unexpected error and has to close.\n\n{ex.Message}\n\nDetails are in the log:\n{Log.LogDirectory}");
            return 1;
        }
        finally
        {
            Log.Shutdown();
        }
    }

    /// <summary>
    /// The Smart-shortcut path. Runs without WPF so it starts fast, determines the launch monitor immediately,
    /// performs the launch itself (as the normal, non-elevated user) and lets the tray engine place the window.
    /// </summary>
    private static int RunLauncher(string appId)
    {
        var monitors = MonitorService.GetMonitors();
        var ctx = LaunchContextResolver.Resolve(monitors);
        Log.Info($"--launch {appId}: {ctx} [{LaunchContextResolver.DescribeStartupInfo()}]");

        var settings = SettingsStore.Load();
        var app = settings.Apps.FirstOrDefault(a => a.Id == appId);
        if (app == null)
        {
            Log.Error($"--launch: no app with id '{appId}' (was it removed from Smart Monitor?)");
            NativeDialogs.ShowError($"This Smart Monitor shortcut refers to an app that is no longer configured (id {appId}).\nRe-create the shortcut from Smart Monitor > Apps.");
            return 2;
        }

        // Only try the pipe when the tray instance exists (avoids waiting for a connection timeout).
        bool trayRunning = Mutex.TryOpenExisting(MutexName, out var m) && InstancePipe.Send($"context\t{app.Name}\t{ctx.Monitor.Number}\t{ctx.Source}");
        m?.Dispose();
        Action<IEnumerable<string>, string, TimeSpan>? expect = trayRunning
            ? (names, device, timeout) => InstancePipe.Send($"expect\t{device}\t{(int)timeout.TotalMilliseconds}\t{string.Join(",", names)}")
            : null;

        var outcome = AppLauncher.Launch(app, ctx.Monitor, monitors, expect);
        return outcome.Success ? 0 : 3;
    }

    private static int RunTray(string[] args)
    {
        bool hidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        bool replace = args.Any(a => a.Equals("--replace", StringComparison.OrdinalIgnoreCase));

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (replace)
            {
                InstancePipe.Send("exit");
                try { createdNew = mutex.WaitOne(TimeSpan.FromSeconds(8)); }
                catch (AbandonedMutexException) { createdNew = true; }
                if (!createdNew) { Log.Warn("--replace: the running instance did not exit"); return 4; }
            }
            else
            {
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                if (!hidden) InstancePipe.Send("show");
                return 0;
            }
        }

        Log.Info($"Smart Monitor {typeof(Program).Assembly.GetName().Version} starting (pid {Environment.ProcessId}, elevated: {NativeMethods.IsCurrentProcessElevated()})");
        var app = new App();
        app.InitializeComponent();
        using var host = new AppHost(app, startHidden: hidden);
        app.Host = host;
        app.Startup += (_, _) => host.Start();
        int code = app.Run();
        Log.Info("Smart Monitor exiting");
        mutex.ReleaseMutex();
        return code;
    }
}

internal static class NativeDialogs
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static void ShowError(string text) => MessageBoxW(IntPtr.Zero, text, "Smart Monitor", 0x10 /*MB_ICONERROR*/);
}
