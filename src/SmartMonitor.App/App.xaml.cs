using System.Windows;
using System.Windows.Threading;
using SmartMonitor.Core.Logging;

namespace SmartMonitor.App;

public partial class App : Application
{
    public AppHost? Host { get; set; }

    public App()
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray app alive: log UI errors instead of crashing the background engine.
        Log.Error("UI error", e.Exception);
        e.Handled = true;
    }
}
