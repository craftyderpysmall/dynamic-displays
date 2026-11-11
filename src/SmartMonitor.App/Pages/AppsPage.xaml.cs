using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SmartMonitor.Core.Config;
using SmartMonitor.Core.Launch;
using SmartMonitor.Core.Native;
using SmartMonitor.Core.Windows;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.App.Pages;

public partial class AppsPage : UserControl
{
    private readonly AppHost _host;
    private bool _loadingEditor;

    private sealed record RunningApp(string Exe, string Path, string Title)
    {
        public override string ToString() => string.IsNullOrEmpty(Title) ? Exe : $"{Exe} — {Title}";
    }

    public AppsPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        RefreshExclusions();
        RefreshApps();
    }

    // ------------------------------------------------------------- exclusions

    private void RefreshExclusions() =>
        ExclusionList.ItemsSource = _host.Settings.ExcludedProcesses.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    private void ExclusionInput_DropDownOpened(object? sender, EventArgs e)
    {
        ExclusionInput.ItemsSource = GetRunningApps().Select(a => a.Exe).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        string exe = FollowCursorEngine.NormalizeExe(ExclusionInput.Text ?? "");
        if (exe.Length == 0 || exe == ".exe") return;
        if (!_host.Settings.ExcludedProcesses.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            _host.Settings.ExcludedProcesses.Add(exe);
            _host.SaveSettings();
        }
        ExclusionInput.Text = "";
        RefreshExclusions();
    }

    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (ExclusionList.SelectedItem is not string exe) return;
        _host.Settings.ExcludedProcesses.RemoveAll(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase));
        _host.SaveSettings();
        RefreshExclusions();
    }

    /// <summary>Apps that currently have a normal top-level window.</summary>
    private static List<RunningApp> GetRunningApps()
    {
        var list = new List<RunningApp>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || NativeMethods.IsCloaked(h)) return true;
            if ((GetExStyle(h) & WS_EX_TOOLWINDOW) != 0) return true;
            var f = WindowFacts.Read(h);
            if (f.ProcessPath == null || f.Title.Length == 0 || WindowFilter.ShellClasses.Contains(f.ClassName)) return true;
            if (f.ProcessId == (uint)Environment.ProcessId) return true;
            list.Add(new RunningApp(f.ProcessName, f.ProcessPath, f.Title));
            return true;
        }, IntPtr.Zero);
        return list.GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(a => a.Exe).ToList();
    }

    // ------------------------------------------------------------- launcher apps

    private LauncherApp? Selected => AppList.SelectedItem as LauncherApp;

    private void RefreshApps(LauncherApp? select = null)
    {
        AppList.ItemsSource = null;
        AppList.ItemsSource = _host.Settings.Apps;
        if (select != null) AppList.SelectedItem = select;
        Editor.Visibility = Selected == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a program or shortcut",
            Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs",
            DereferenceLinks = false,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        AddApp(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName);
    }

    private void RunningAppPicker_DropDownOpened(object? sender, EventArgs e) => RunningAppPicker.ItemsSource = GetRunningApps();

    private void RunningAppPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RunningAppPicker.SelectedItem is not RunningApp ra) return;
        RunningAppPicker.SelectedItem = null;
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(ra.Path);
        string name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription! : System.IO.Path.GetFileNameWithoutExtension(ra.Exe);
        AddApp(name, ra.Path);
    }

    private void AddApp(string name, string target)
    {
        var app = new LauncherApp { Name = name, Target = target };
        // Known stub launchers: the window belongs to a different exe than the one started.
        if (target.Contains("Discord", StringComparison.OrdinalIgnoreCase)) app.ProcessNames.Add("Discord.exe");
        _host.Settings.Apps.Add(app);
        _host.SaveSettings();
        RefreshApps(app);
        AppStatus.Text = $"Added {name}.";
    }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var a = Selected;
        Editor.Visibility = a == null ? Visibility.Collapsed : Visibility.Visible;
        if (a == null) return;
        _loadingEditor = true;
        NameBox.Text = a.Name;
        TargetBox.Text = a.Target;
        ArgsBox.Text = a.Arguments;
        ProcessBox.Text = string.Join(", ", a.ProcessNames);
        IfRunningBox.SelectedIndex = a.IfRunning == IfRunningBehavior.ActivateExisting ? 0 : 1;
        _loadingEditor = false;
    }

    private void Editor_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingEditor || Selected is not LauncherApp a) return;
        a.Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : a.Name;
        a.Target = TargetBox.Text.Trim();
        a.Arguments = ArgsBox.Text.Trim();
        a.ProcessNames = ProcessBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(FollowCursorEngine.NormalizeExe).ToList();
        a.IfRunning = IfRunningBox.SelectedIndex == 1 ? IfRunningBehavior.AlwaysLaunchNew : IfRunningBehavior.ActivateExisting;
        _host.SaveSettings();
        if (sender == NameBox) RefreshApps(a);
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is LauncherApp a) _host.LaunchApp(a);
    }

    private void DesktopShortcut_Click(object sender, RoutedEventArgs e) =>
        CreateShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "desktop");

    private void StartShortcut_Click(object sender, RoutedEventArgs e) =>
        CreateShortcut(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Smart Monitor"), "Start menu");

    private void CreateShortcut(string folder, string where)
    {
        if (Selected is not LauncherApp a) return;
        bool ok = AppLauncher.CreateShortcut(a, folder, StartupManager.ExePath);
        AppStatus.Text = ok
            ? $"Created “{a.Name} (Smart Monitor)” on the {where}. To pin it to the taskbar: right-click it › Show more options › Pin to taskbar."
            : "Could not create the shortcut — see the Log page.";
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not LauncherApp a) return;
        _host.Settings.Apps.Remove(a);
        _host.SaveSettings();
        RefreshApps();
        AppStatus.Text = $"Removed {a.Name}. Shortcuts you created for it will stop working; delete them too.";
    }
}
