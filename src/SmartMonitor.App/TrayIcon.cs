using System.Drawing;
using System.Windows.Forms;
using SmartMonitor.Core.Logging;

namespace SmartMonitor.App;

/// <summary>Notification-area icon and menu (WinForms NotifyIcon, hosted on the WPF dispatcher thread).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppHost _host;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _launchMenu;
    private readonly Icon _onIcon;
    private readonly Icon _offIcon;

    public TrayIcon(AppHost host)
    {
        _host = host;
        _onIcon = LoadAppIcon();
        _offIcon = MakeDisabledIcon(_onIcon);

        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => _host.SetEnabled(!_host.Settings.Enabled));
        _launchMenu = new ToolStripMenuItem("Launch here");

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem("Open Smart Monitor", null, (_, _) => _host.ShowMainWindow()) { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_launchMenu);
        menu.Items.Add(new ToolStripMenuItem("Identify monitors", null, (_, _) => _host.IdentifyMonitors()));
        menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _host.Exit()));
        menu.Opening += (_, _) => RebuildLaunchMenu();

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _host.ShowMainWindow(); };
        Refresh();
    }

    public void Refresh()
    {
        bool on = _host.Settings.Enabled;
        _enabledItem.Checked = on;
        _icon.Icon = on ? _onIcon : _offIcon;
        string state = on ? "on" : "off";
        _icon.Text = $"Smart Monitor ({state}){(_host.IsElevated ? " — elevated" : "")}";
    }

    private void RebuildLaunchMenu()
    {
        _launchMenu.DropDownItems.Clear();
        foreach (var app in _host.Settings.Apps)
        {
            var a = app;
            _launchMenu.DropDownItems.Add(new ToolStripMenuItem(a.Name, null, (_, _) => _host.LaunchApp(a)));
        }
        if (_launchMenu.DropDownItems.Count == 0)
            _launchMenu.DropDownItems.Add(new ToolStripMenuItem("(add apps in Smart Monitor > Apps)") { Enabled = false });
    }

    public void ShowBalloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Warning); }
        catch (Exception ex) { Log.Debug($"Balloon failed: {ex.Message}"); }
    }

    public static void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Log.LogDirectory);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Log.LogDirectory}\"");
        }
        catch (Exception ex) { Log.Error("Could not open log folder", ex); }
    }

    private static Icon LoadAppIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var s = info!.Stream;
        int size = SystemInformation.SmallIconSize.Width;
        return new Icon(s, size, size);
    }

    /// <summary>Greyed-out version of the icon to show at a glance that Smart Monitor is off.</summary>
    private static Icon MakeDisabledIcon(Icon source)
    {
        using var bmp = source.ToBitmap();
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                int gray = (int)(c.R * 0.3 + c.G * 0.59 + c.B * 0.11);
                bmp.SetPixel(x, y, Color.FromArgb(c.A / 2 + c.A / 4, gray, gray, gray));
            }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
