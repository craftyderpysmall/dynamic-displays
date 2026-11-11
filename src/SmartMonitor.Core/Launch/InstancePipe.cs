using System.IO.Pipes;
using System.Text;
using SmartMonitor.Core.Logging;

namespace SmartMonitor.Core.Launch;

/// <summary>
/// One-line text messages from short-lived launcher invocations (or a second start of the app) to the
/// running tray instance. Restricted to the current user.
/// </summary>
public sealed class InstancePipe : IDisposable
{
    public static string PipeName => $"SmartMonitor-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _onMessage;
    private Task? _loop;

    public InstancePipe(Action<string> onMessage) => _onMessage = onMessage;

    public void Start() => _loop = Task.Run(ServeAsync);

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(_cts.Token);
                if (line != null)
                {
                    try { _onMessage(line); await writer.WriteLineAsync("ok"); }
                    catch (Exception ex) { Log.Error($"Pipe message '{line}' failed", ex); await writer.WriteLineAsync("error"); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"Pipe server error: {ex.Message}");
                await Task.Delay(500);
            }
        }
    }

    /// <summary>Sends a message to the running instance. Returns false if none is listening.</summary>
    public static bool Send(string message, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
            writer.WriteLine(message);
            return reader.ReadLine() == "ok";
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { }
    }
}
