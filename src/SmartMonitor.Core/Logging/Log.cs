using System.Collections.Concurrent;
using System.Text;

namespace SmartMonitor.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{Level,-5}] {Message}";
}

/// <summary>
/// Small, dependency-free logger: writes to a rolling file on a background thread and keeps
/// the most recent entries in memory for the UI.
/// </summary>
public static class Log
{
    private const int MaxMemoryEntries = 500;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int KeepFiles = 3;

    private static readonly BlockingCollection<LogEntry> Queue = new(new ConcurrentQueue<LogEntry>());
    private static readonly LinkedList<LogEntry> Recent = new();
    private static readonly object RecentLock = new();
    private static Thread? _writer;
    private static string? _filePath;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public static string LogDirectory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartMonitor", "logs");
    public static string? CurrentFile => _filePath;

    public static event Action<LogEntry>? EntryAdded;

    public static void Initialize(string? directory = null)
    {
        if (_writer != null) return;
        if (directory != null) LogDirectory = directory;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            _filePath = Path.Combine(LogDirectory, "smartmonitor.log");
        }
        catch
        {
            _filePath = null; // logging to file unavailable; memory log still works
        }
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "SmartMonitor log writer" };
        _writer.Start();
    }

    public static IReadOnlyList<LogEntry> GetRecent()
    {
        lock (RecentLock) return Recent.ToList();
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg, Exception? ex = null) =>
        Write(LogLevel.Error, ex == null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public static void Write(LogLevel level, string msg)
    {
        if (level < MinimumLevel) return;
        var entry = new LogEntry(DateTime.Now, level, msg);
        lock (RecentLock)
        {
            Recent.AddLast(entry);
            while (Recent.Count > MaxMemoryEntries) Recent.RemoveFirst();
        }
        if (!Queue.IsAddingCompleted) Queue.TryAdd(entry);
        try { EntryAdded?.Invoke(entry); } catch { /* never let UI listeners break logging */ }
    }

    public static void Shutdown()
    {
        Queue.CompleteAdding();
        _writer?.Join(1000);
    }

    private static void WriterLoop()
    {
        foreach (var entry in Queue.GetConsumingEnumerable())
        {
            if (_filePath == null) continue;
            try
            {
                RollIfNeeded();
                File.AppendAllText(_filePath, entry + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Disk full / file locked: drop the line rather than crash.
            }
        }
    }

    private static void RollIfNeeded()
    {
        var fi = new FileInfo(_filePath!);
        if (!fi.Exists || fi.Length < MaxFileBytes) return;
        for (int i = KeepFiles - 1; i >= 1; i--)
        {
            string src = _filePath + "." + i;
            string dst = _filePath + "." + (i + 1);
            if (File.Exists(dst)) File.Delete(dst);
            if (File.Exists(src)) File.Move(src, dst);
        }
        File.Move(_filePath!, _filePath + ".1", overwrite: true);
    }
}
