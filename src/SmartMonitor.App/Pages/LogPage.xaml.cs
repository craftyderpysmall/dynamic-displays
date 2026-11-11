using System.Windows;
using System.Windows.Controls;
using SmartMonitor.Core.Logging;

namespace SmartMonitor.App.Pages;

public partial class LogPage : UserControl, IDisposable
{
    public LogPage()
    {
        InitializeComponent();
        Subtitle.Text = $"Recent activity. The full log is kept in {Log.LogDirectory}.";
        LogText.Text = string.Join(Environment.NewLine, Log.GetRecent().Reverse());
        Log.EntryAdded += OnEntry;
    }

    private void OnEntry(LogEntry e) => Dispatcher.BeginInvoke(() =>
    {
        LogText.Text = e + Environment.NewLine + LogText.Text;
        if (LogText.Text.Length > 200_000) LogText.Text = LogText.Text[..150_000];
    });

    public void Dispose() => Log.EntryAdded -= OnEntry;

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => TrayIcon.OpenLogFolder();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text); } catch (Exception ex) { Log.Warn($"Clipboard: {ex.Message}"); }
    }
}
