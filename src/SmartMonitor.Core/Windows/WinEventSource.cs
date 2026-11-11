using SmartMonitor.Core.Native;
using SmartMonitor.Core.Logging;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.Core.Windows;

public readonly record struct WinEvent(uint Type, IntPtr Hwnd, DateTime Time);

/// <summary>
/// Owns a dedicated thread with a Win32 message loop and out-of-context WinEvent hooks
/// (no DLL injection). Only top-level window object events are forwarded.
/// The callback runs on the hook thread and must be fast: it just hands events off.
/// </summary>
public sealed class WinEventSource : IDisposable
{
    private static readonly (uint min, uint max)[] Ranges =
    {
        (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
        (EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND),
        (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
        (EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE),
        (EVENT_OBJECT_UNCLOAKED, EVENT_OBJECT_UNCLOAKED),
    };

    private readonly Action<WinEvent> _sink;
    private readonly WinEventProc _proc; // kept alive for the lifetime of the hooks
    private readonly List<IntPtr> _hooks = new();
    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _started = new();

    public WinEventSource(Action<WinEvent> sink)
    {
        _sink = sink;
        _proc = Callback;
    }

    public bool IsRunning => _thread != null;

    public void Start()
    {
        if (_thread != null) return;
        _started.Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "SmartMonitor WinEvent hooks" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        foreach (var (min, max) in Ranges)
        {
            IntPtr h = SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (h == IntPtr.Zero) Log.Error($"SetWinEventHook(0x{min:X}-0x{max:X}) failed");
            else _hooks.Add(h);
        }
        Log.Info($"Window event hooks installed ({_hooks.Count}/{Ranges.Length})");
        _started.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        Log.Info("Window event hooks removed");
    }

    private void Callback(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        // Object show/hide/destroy fire for every control in the system; only top-level windows matter.
        if (type >= EVENT_OBJECT_CREATE && GetAncestor(hwnd, GA_ROOT) != hwnd) return;
        try { _sink(new WinEvent(type, hwnd, DateTime.UtcNow)); }
        catch (Exception ex) { Log.Error("WinEvent sink failed", ex); }
    }

    public void Stop()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
        _thread = null;
    }

    public void Dispose() => Stop();
}
