using System.Collections.Concurrent;
using SmartMonitor.Core.Config;
using SmartMonitor.Core.Logging;
using SmartMonitor.Core.Monitors;
using SmartMonitor.Core.Native;
using static SmartMonitor.Core.Native.NativeMethods;

namespace SmartMonitor.Core.Windows;

public enum Trigger { Activation, KeyboardSwitch, Restore, NewWindow, PreShow, Startup, Recheck, Launch }

public sealed record EngineDecision(DateTime Time, Trigger Trigger, string Window, bool Moved, string Detail);

/// <summary>
/// The core: whenever a window "comes to the user" (activated, restored, newly shown), move it to the
/// monitor under the mouse cursor. All decisions run on one worker thread; the hook thread only enqueues.
/// </summary>
public sealed class FollowCursorEngine : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly WinEventSource _events;
    private Thread? _worker;
    private volatile AppSettings _settings;
    private readonly bool _weAreElevated = IsCurrentProcessElevated();

    // ---- worker-thread state ----
    private readonly HashSet<IntPtr> _known = new();
    // Windows that were shown while DWM-cloaked; their uncloak is their real first appearance, not a desktop switch.
    private readonly HashSet<IntPtr> _shownWhileCloaked = new();
    private readonly Dictionary<IntPtr, DateTime> _cooldown = new();
    // Focus-fallback detection: the last two app (non-shell) foreground windows and when one of them
    // was last minimized/hidden/closed.
    private IntPtr _lastAppForeground;
    private IntPtr _prevAppForeground;
    private DateTime _appForegroundLostAt = DateTime.MinValue;
    private IntPtr _appForegroundLostWindow;
    // When the Alt+Tab / Task View switcher was last in front.
    private DateTime _lastSwitcherAt = DateTime.MinValue;
    private IntPtr _moveSizeWindow;
    private DateTime _desktopSwitchUntil;
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    private DateTime _monitorsAt;
    private readonly List<PendingLaunch> _pendingLaunches = new();
    private DateTime _lastPrune = DateTime.UtcNow;

    private sealed record PendingLaunch(HashSet<string> ProcessNames, string MonitorDevice, DateTime Until);

    public event Action<EngineDecision>? Decision;

    public FollowCursorEngine(AppSettings settings)
    {
        _settings = settings;
        _events = new WinEventSource(e => _work.TryAdd(() => Handle(e)));
        ApplySettingsInternal(settings);
    }

    public bool IsRunning => _events.IsRunning;
    public bool IsElevated => _weAreElevated;

    public void UpdateSettings(AppSettings s) => _work.TryAdd(() => ApplySettingsInternal(s));

    private void ApplySettingsInternal(AppSettings s)
    {
        _settings = s;
        _excluded = new HashSet<string>(s.ExcludedProcesses.Select(NormalizeExe), StringComparer.OrdinalIgnoreCase);
        Log.MinimumLevel = s.DebugLogging ? LogLevel.Debug : LogLevel.Info;
    }

    public static string NormalizeExe(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return name;
        name = Path.GetFileName(name);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    public void Start()
    {
        if (_worker != null) return;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "SmartMonitor engine" };
        _worker.Start();
        _work.TryAdd(SeedKnownWindows);
        _events.Start();
        Log.Info($"Engine started (elevated: {_weAreElevated})");
    }

    public void Dispose()
    {
        _events.Dispose();
        _work.CompleteAdding();
        _worker?.Join(2000);
    }

    /// <summary>
    /// Tell the engine that a launch is in progress: new windows from these processes go to the given
    /// monitor (captured at click time) instead of wherever the cursor is when they appear.
    /// </summary>
    public void ExpectLaunch(IEnumerable<string> processNames, string monitorDevice, TimeSpan timeout)
    {
        var set = new HashSet<string>(processNames.Select(NormalizeExe).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return;
        _work.TryAdd(() =>
        {
            // The launcher may report extra process names for the same launch; merge them into one pending entry.
            var existing = _pendingLaunches.LastOrDefault(p => p.MonitorDevice == monitorDevice && p.Until - DateTime.UtcNow > timeout - TimeSpan.FromSeconds(3));
            if (existing != null) existing.ProcessNames.UnionWith(set);
            else _pendingLaunches.Add(new PendingLaunch(set, monitorDevice, DateTime.UtcNow + timeout));
            Log.Debug($"Expecting launch on {monitorDevice}: {string.Join(", ", set)}");
        });
    }

    private void WorkerLoop()
    {
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Engine work item failed", ex); }
        }
    }

    private void Schedule(int delayMs, Action action)
    {
        if (delayMs <= 0) { _work.TryAdd(action); return; }
        Timer? t = null;
        t = new Timer(_ =>
        {
            t?.Dispose();
            if (!_work.IsAddingCompleted) _work.TryAdd(action);
        }, null, delayMs, Timeout.Infinite);
    }

    private void SeedKnownWindows()
    {
        EnumWindows((h, _) => { if (IsWindowVisible(h)) _known.Add(h); return true; }, IntPtr.Zero);
        _lastAppForeground = GetForegroundWindow();
        Log.Debug($"Seeded {_known.Count} existing windows");
    }

    private IReadOnlyList<MonitorInfo> Monitors()
    {
        if (_monitors.Count == 0 || DateTime.UtcNow - _monitorsAt > TimeSpan.FromSeconds(3))
        {
            _monitors = MonitorService.GetMonitors();
            _monitorsAt = DateTime.UtcNow;
        }
        return _monitors;
    }

    public void InvalidateMonitors() => _work.TryAdd(() => _monitors = Array.Empty<MonitorInfo>());

    // ------------------------------------------------------------------ event handling

    private void Handle(WinEvent e)
    {
        PruneOccasionally();
        switch (e.Type)
        {
            case EVENT_SYSTEM_FOREGROUND: OnForeground(e.Hwnd, e.Time); break;
            case EVENT_SYSTEM_MINIMIZEEND: OnRestore(e.Hwnd); break;
            case EVENT_OBJECT_CREATE: OnCreate(e.Hwnd); break;
            case EVENT_OBJECT_SHOW: OnShow(e.Hwnd); break;
            case EVENT_OBJECT_UNCLOAKED: OnUncloaked(e.Hwnd); break;
            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_OBJECT_HIDE:
                OnAppWindowLost(e.Hwnd, e.Time);
                break;
            case EVENT_OBJECT_DESTROY:
                OnAppWindowLost(e.Hwnd, e.Time);
                _known.Remove(e.Hwnd); _cooldown.Remove(e.Hwnd); _shownWhileCloaked.Remove(e.Hwnd);
                _startupTargets.Remove(e.Hwnd); _userPlaced.Remove(e.Hwnd);
                break;
            case EVENT_SYSTEM_MOVESIZESTART: _moveSizeWindow = e.Hwnd; break;
            case EVENT_SYSTEM_MOVESIZEEND:
                _moveSizeWindow = IntPtr.Zero;
                // The user just placed this window deliberately; don't touch it right away.
                _cooldown[e.Hwnd] = DateTime.UtcNow.AddMilliseconds(Math.Max(_settings.CooldownMs, 1000));
                _userPlaced.Add(e.Hwnd);
                break;
        }
    }

    private static readonly TimeSpan FallbackWindow = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan SwitcherWindow = TimeSpan.FromMilliseconds(1500);

    private void OnForeground(IntPtr hwnd, DateTime time)
    {
        string cls = NativeMethods.GetClassName(hwnd);
        if (Log.MinimumLevel <= LogLevel.Debug) Log.Debug($"foreground event: [{cls}] 0x{hwnd.ToInt64():X}");
        // Transitional window Windows activates briefly during minimize/switch animations.
        if (cls == "ForegroundStaging") return;
        if (cls is "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame") _lastSwitcherAt = time;
        if (IsShellUi(hwnd, cls)) return; // shell UI is never moved and doesn't count as an app window

        if (hwnd != _lastAppForeground)
        {
            _prevAppForeground = _lastAppForeground;
            _lastAppForeground = hwnd;
        }

        var s = _settings;
        if (!s.Enabled) return;

        // A window of an app launched through a Smart shortcut goes to the monitor captured at launch time.
        var launch = MatchPendingLaunch(hwnd);
        if (launch != null)
        {
            GetCursorPos(out POINT lc);
            Schedule(s.ActivationDelayMs, () => PlaceForLaunch(hwnd, launch, lc));
            return;
        }
        if (!s.FollowOnActivation) return;

        // Capture input state now; by the time the delayed evaluation runs, keys may be released.
        // Alt+Tab activates the chosen window when Alt is released, so also look at whether the switcher was just up.
        bool keyboardSwitch = IsKeyDown(VK_MENU) || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN) || time - _lastSwitcherAt < SwitcherWindow;
        if (keyboardSwitch && !s.FollowOnKeyboardSwitch) return;
        GetCursorPos(out POINT cursor);
        var trigger = keyboardSwitch ? Trigger.KeyboardSwitch : Trigger.Activation;

        Schedule(s.ActivationDelayMs, () =>
        {
            if (DateTime.UtcNow < _desktopSwitchUntil) { Skip(trigger, hwnd, "virtual desktop switch"); return; }
            if (IsFocusFallback(hwnd, time)) { Skip(trigger, hwnd, "focus fell back after another window closed/minimized"); return; }
            if (GetForegroundWindow() != hwnd) { Skip(trigger, hwnd, "no longer in front (transient activation)"); return; }
            Evaluate(hwnd, trigger, cursor, forcedTarget: null, ignoreCooldown: false);
        });
    }

    /// <summary>The previously active app window was minimized, hidden or closed.</summary>
    private void OnAppWindowLost(IntPtr hwnd, DateTime time)
    {
        if (hwnd == IntPtr.Zero) return;
        if (hwnd == _lastAppForeground || hwnd == _prevAppForeground)
        {
            _appForegroundLostAt = time;
            _appForegroundLostWindow = hwnd;
        }
    }

    /// <summary>
    /// Closing or minimizing the active window makes Windows activate the next window in Z-order, which may
    /// be on another monitor. That is not the user "opening" anything, so it must not move. Detected by timing:
    /// an app window that was just active disappeared within a short interval around this activation.
    /// (Evaluated after the activation delay, so hide/minimize events that arrive slightly late are seen too.)
    /// </summary>
    private bool IsFocusFallback(IntPtr activated, DateTime activatedAt)
    {
        if (_appForegroundLostWindow == activated) return false;
        var delta = activatedAt - _appForegroundLostAt;
        return delta > -FallbackWindow && delta < FallbackWindow;
    }

    /// <summary>Start menu, search, Alt+Tab switcher, taskbarâ€¦</summary>
    private static bool IsShellUi(IntPtr hwnd, string cls)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (WindowFilter.ShellClasses.Contains(cls)) return true;
        GetWindowThreadProcessId(hwnd, out uint pid);
        string? path = GetProcessPath(pid);
        return path != null && WindowFilter.ShellProcesses.Contains(Path.GetFileName(path));
    }

    /// <summary>Cheap pre-check so the many popups/menus/tooltips that get shown cost almost nothing.</summary>
    private static bool CouldBeAppWindow(IntPtr hwnd)
    {
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        long ex = GetExStyle(hwnd);
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0) return false;
        return (GetStyle(hwnd) & WS_CHILD) == 0;
    }

    private void OnRestore(IntPtr hwnd)
    {
        var s = _settings;
        if (!s.Enabled || !s.FollowOnRestore) return;
        GetCursorPos(out POINT cursor);
        // Small delay lets the restore animation start so the window has its real show state.
        Schedule(30, () => Evaluate(hwnd, Trigger.Restore, cursor, null, ignoreCooldown: false));
    }

    /// <summary>
    /// A top-level window was just created and is usually still hidden. Moving it now means it first appears on
    /// the right monitor instead of appearing elsewhere and then jumping. If the app positions it again before
    /// showing it, the normal show handling still corrects it.
    /// </summary>
    private void OnCreate(IntPtr hwnd)
    {
        var s = _settings;
        if (!s.Enabled || !s.PlaceBeforeShow) return;
        if (IsWindowVisible(hwnd)) return; // already shown by the time we got here: OnShow handles it
        if (!CouldBeAppWindow(hwnd)) return;
        long style = GetStyle(hwnd);
        if ((style & WS_CAPTION) != WS_CAPTION && (style & WS_THICKFRAME) == 0) return; // not a normal app frame

        var launch = MatchPendingLaunch(hwnd);
        if (launch == null && !s.FollowOnNewWindow) return;

        // Same rules as for visible windows, except visibility itself.
        var facts = WindowFacts.Read(hwnd) with { IsVisible = true };
        string? reject = WindowFilter.RejectReason(facts, _excluded, s.SkipFullscreen, _weAreElevated);
        if (reject != null) { Skip(Trigger.PreShow, hwnd, reject, facts); return; }

        var monitors = Monitors();
        MonitorInfo? target = launch != null
            ? monitors.FirstOrDefault(m => string.Equals(m.DeviceName, launch.MonitorDevice, StringComparison.OrdinalIgnoreCase))
            : null;
        if (target == null)
        {
            GetCursorPos(out POINT cursor);
            target = monitors.FirstOrDefault(m => m.Handle == MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST));
        }
        if (target == null) return;
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target.Handle) { Skip(Trigger.PreShow, hwnd, $"already on Monitor {target.Number}", facts); return; }

        // A frameless window covering its whole monitor is a borderless full-screen window (game): make it cover the target.
        bool fullscreen = (style & WS_CAPTION) != WS_CAPTION && WindowFilter.CoversMonitor(facts);
        if (fullscreen) _fullscreenIntent.Add(hwnd);
        var r = fullscreen ? WindowMover.MoveFullscreen(hwnd, target) : WindowMover.PrePlaceHidden(hwnd, target, monitors);
        if (r.Success) Log.Info($"[PreShow] {facts.Describe()}: â†’ Monitor {target.Number}: {r.Message}");
        else Log.Debug($"[PreShow] {facts.Describe()}: not placed: {r.Message}");
        if (r.Success) Decision?.Invoke(new EngineDecision(DateTime.Now, Trigger.PreShow, facts.Describe(), true, $"before showing â†’ Monitor {target.Number}"));
    }

    private void OnShow(IntPtr hwnd)
    {
        bool isNew = _known.Add(hwnd);
        var s = _settings;
        if (!s.Enabled) return;
        if (!CouldBeAppWindow(hwnd)) return;
        if (NativeMethods.IsCloaked(hwnd)) { _shownWhileCloaked.Add(hwnd); return; } // handled when it uncloaks (UWP apps, Terminal)

        GetCursorPos(out POINT cursor);
        if (s.FollowOnNewWindow && s.MoveStartingGames && TryHandleStartingApp(hwnd, cursor)) return;
        var launch = MatchPendingLaunch(hwnd);
        if (launch != null)
        {
            Schedule(s.NewWindowDelayMs, () => PlaceForLaunch(hwnd, launch, cursor));
            return;
        }
        if (!s.FollowOnNewWindow) return;

        Schedule(s.NewWindowDelayMs, () =>
        {
            IntPtr? target = Evaluate(hwnd, Trigger.NewWindow, cursor, null, ignoreCooldown: false);
            // Some apps restore their saved position a moment after showing; check once more.
            if (target is IntPtr t && s.RecheckDelayMs > 0)
                Schedule(s.RecheckDelayMs, () => Recheck(hwnd, t));
        });
        if (!isNew) Log.Debug($"Re-shown window {Describe(hwnd)}");
    }

    // ------------------------------------------------------------------ apps that are starting (games, splash screens)

    private static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(30);
    private static readonly int[] StartupRecheckMs = { 500, 1000, 2000, 3000, 5000, 8000, 12000, 18000, 25000 };
    private readonly Dictionary<uint, DateTime> _processStart = new();
    private readonly Dictionary<IntPtr, IntPtr> _startupTargets = new(); // window -> monitor it should open on
    private readonly HashSet<IntPtr> _userPlaced = new();               // dragged by the user: stop correcting
    private readonly HashSet<IntPtr> _fullscreenIntent = new();         // opened as borderless full screen

    private static bool FillsMonitor(IntPtr hwnd, MonitorInfo m)
    {
        var f = NativeMethods.GetFrameBounds(hwnd);
        return f.Left <= m.Bounds.Left && f.Top <= m.Bounds.Top && f.Right >= m.Bounds.Right && f.Bottom >= m.Bounds.Bottom;
    }

    private bool IsYoungProcess(uint pid)
    {
        if (!_processStart.TryGetValue(pid, out var started))
        {
            try { started = System.Diagnostics.Process.GetProcessById((int)pid).StartTime.ToUniversalTime(); }
            catch { started = DateTime.MinValue; } // exited or inaccessible: treat as not starting
            _processStart[pid] = started;
            if (_processStart.Count > 256) _processStart.Clear();
        }
        return DateTime.UtcNow - started < StartupWindow;
    }

    private static bool IsExclusiveFullscreenActive() =>
        SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN;

    /// <summary>
    /// Frameless windows (splash/loading screens) and borderless full-screen windows are normally left alone.
    /// While their app is starting, though, they are exactly what "opening" looks like for games and many big
    /// apps, so they go to the cursor's monitor too: splash screens are moved, borderless full-screen windows
    /// are moved and resized to cover the target monitor. A few re-checks follow, because games often
    /// re-position or re-create their window when loading finishes.
    /// </summary>
    /// <returns>true if this window was taken over by the startup handling.</returns>
    private bool TryHandleStartingApp(IntPtr hwnd, POINT cursor)
    {
        long style = GetStyle(hwnd);
        bool framed = (style & WS_CAPTION) == WS_CAPTION || (style & WS_THICKFRAME) != 0;
        var facts = WindowFacts.Read(hwnd);
        bool covers = WindowFilter.CoversMonitor(facts);
        if (framed && !covers) return false;                 // ordinary window: normal handling
        if (!IsYoungProcess(facts.ProcessId)) return false;  // popups of running apps: normal handling (rejects them)

        string? reject = WindowFilter.StartupRejectReason(facts, _excluded, _weAreElevated);
        if (reject != null) { Skip(Trigger.Startup, hwnd, reject, facts); return true; }

        var target = Monitors().FirstOrDefault(m => m.Handle == MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST));
        if (target == null) return true;
        _startupTargets[hwnd] = target.Handle;
        if (covers) _fullscreenIntent.Add(hwnd);
        Schedule(_settings.NewWindowDelayMs, () => PlaceStarting(hwnd));
        foreach (int ms in StartupRecheckMs) Schedule(ms, () => PlaceStarting(hwnd));
        return true;
    }

    private void PlaceStarting(IntPtr hwnd)
    {
        if (!_startupTargets.TryGetValue(hwnd, out IntPtr targetHandle)) return;
        if (!IsWindow(hwnd) || _userPlaced.Contains(hwnd) || _moveSizeWindow == hwnd) { _startupTargets.Remove(hwnd); return; }
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return;
        var target = Monitors().FirstOrDefault(m => m.Handle == targetHandle);
        if (target == null) return;
        var current = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (current == target.Handle && (!_fullscreenIntent.Contains(hwnd) || FillsMonitor(hwnd, target))) return;

        var facts = WindowFacts.Read(hwnd);
        if (!IsYoungProcess(facts.ProcessId)) { _startupTargets.Remove(hwnd); return; }
        if ((_fullscreenIntent.Contains(hwnd) || WindowFilter.CoversMonitor(facts)) && !facts.IsMaximized)
        {
            if (IsExclusiveFullscreenActive() && GetForegroundWindow() == hwnd)
            {
                Skip(Trigger.Startup, hwnd, "exclusive full-screen: the game itself picks the monitor (usually the primary). " +
                    "Set the game to borderless/windowed full screen so Smart Monitor can place it", facts);
                _startupTargets.Remove(hwnd);
                return;
            }
            var r = WindowMover.MoveFullscreen(hwnd, target);
            Report(Trigger.Startup, hwnd, r.Success, $"borderless full screen â†’ {r.Message}", facts);
        }
        else
        {
            var r = WindowMover.MoveToMonitor(hwnd, target, Monitors());
            Report(Trigger.Startup, hwnd, r.Success, $"starting app â†’ Monitor {target.Number}: {r.Message}", facts);
        }
        _cooldown[hwnd] = DateTime.UtcNow.AddMilliseconds(_settings.CooldownMs);
    }

    /// <summary>
    /// Places the first suitable window of a Smart-shortcut launch on the monitor captured at launch time,
    /// then forgets the pending launch so later activations follow the cursor again.
    /// </summary>
    private void PlaceForLaunch(IntPtr hwnd, PendingLaunch launch, POINT cursor)
    {
        if (!_pendingLaunches.Contains(launch)) // already satisfied by another window: normal rules apply
        {
            Evaluate(hwnd, Trigger.NewWindow, cursor, null, ignoreCooldown: false);
            return;
        }
        IntPtr? target = Evaluate(hwnd, Trigger.Launch, cursor, launch.MonitorDevice, ignoreCooldown: true);
        if (target is not IntPtr t) return; // not an app window (splash, tool windowâ€¦): keep waiting for the real one
        _pendingLaunches.Remove(launch);
        if (_settings.RecheckDelayMs > 0) Schedule(_settings.RecheckDelayMs, () => Recheck(hwnd, t));
    }

    private void OnUncloaked(IntPtr hwnd)
    {
        if (_shownWhileCloaked.Remove(hwnd)) { if (IsWindowVisible(hwnd)) OnShow(hwnd); return; } // first real appearance
        if (_known.Contains(hwnd))
        {
            // Existing app windows uncloak when switching virtual desktops; suppress the activation that follows.
            // Shell UI (e.g. the Alt+Tab switcher) also uncloaks itself and must not count.
            if (CouldBeAppWindow(hwnd) && !IsShellUi(hwnd, NativeMethods.GetClassName(hwnd)))
                _desktopSwitchUntil = DateTime.UtcNow.AddMilliseconds(800);
            return;
        }
        if (IsWindowVisible(hwnd)) OnShow(hwnd);
    }

    private PendingLaunch? MatchPendingLaunch(IntPtr hwnd)
    {
        if (_pendingLaunches.Count == 0) return null;
        _pendingLaunches.RemoveAll(p => DateTime.UtcNow > p.Until);
        if (_pendingLaunches.Count == 0) return null;
        var f = WindowFacts.Read(hwnd);
        if (f.ProcessName.Length == 0) return null;
        return _pendingLaunches.FirstOrDefault(p => p.ProcessNames.Contains(f.ProcessName));
    }

    private void Recheck(IntPtr hwnd, IntPtr targetMonitor)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return;
        if (_moveSizeWindow == hwnd) return;
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == targetMonitor) return;
        var target = Monitors().FirstOrDefault(m => m.Handle == targetMonitor);
        if (target == null) return;
        var r = WindowMover.MoveToMonitor(hwnd, target, Monitors());
        Report(Trigger.Recheck, hwnd, r.Success, $"app re-positioned itself; moved back to Monitor {target.Number}: {r.Message}");
        if (r.Success) _cooldown[hwnd] = DateTime.UtcNow.AddMilliseconds(_settings.CooldownMs);
    }

    /// <returns>The target monitor handle if the window was moved (or already there), otherwise null.</returns>
    private IntPtr? Evaluate(IntPtr hwnd, Trigger trigger, POINT cursor, string? forcedTarget, bool ignoreCooldown)
    {
        var s = _settings;
        if (!s.Enabled) return null;
        if (!IsWindow(hwnd)) return null;
        if (_moveSizeWindow == hwnd) { Skip(trigger, hwnd, "being dragged/resized"); return null; }
        if (!ignoreCooldown && _cooldown.TryGetValue(hwnd, out var until) && DateTime.UtcNow < until)
        { Skip(trigger, hwnd, "handled moments ago (cooldown)"); return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST); } // still allow the recheck

        var facts = WindowFacts.Read(hwnd);
        if (facts.IsMinimized) { Skip(trigger, hwnd, "minimized", facts); return null; }
        string? reject = WindowFilter.RejectReason(facts, _excluded, s.SkipFullscreen, _weAreElevated);
        if (reject != null) { Skip(trigger, hwnd, reject, facts); return null; }

        var monitors = Monitors();
        MonitorInfo? target = forcedTarget != null
            ? monitors.FirstOrDefault(m => string.Equals(m.DeviceName, forcedTarget, StringComparison.OrdinalIgnoreCase))
            : null;
        target ??= monitors.FirstOrDefault(m => m.Handle == MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST));
        if (target == null)
        {
            // Display configuration changed since the cache was filled.
            _monitors = Array.Empty<MonitorInfo>();
            return null;
        }

        IntPtr current = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (current == target.Handle) { Skip(trigger, hwnd, $"already on Monitor {target.Number}", facts); return target.Handle; }

        // If the cursor is over the window, the user is interacting with it where it is (e.g. clicked it).
        if (forcedTarget == null && GetAncestor(WindowFromPoint(cursor), GA_ROOT) == hwnd)
        {
            Skip(trigger, hwnd, "cursor is over the window", facts);
            return null;
        }

        var source = monitors.FirstOrDefault(m => m.Handle == current);
        var r = WindowMover.MoveToMonitor(hwnd, target, monitors);
        Report(trigger, hwnd, r.Success,
            $"Monitor {source?.Number.ToString() ?? "?"} â†’ Monitor {target.Number}: {r.Message}", facts);
        if (r.Success) _cooldown[hwnd] = DateTime.UtcNow.AddMilliseconds(s.CooldownMs);
        return r.Success ? target.Handle : null;
    }

    private void PruneOccasionally()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPrune < TimeSpan.FromMinutes(2)) return;
        _lastPrune = now;
        _known.RemoveWhere(h => !IsWindow(h));
        _userPlaced.RemoveWhere(h => !IsWindow(h));
        _fullscreenIntent.RemoveWhere(h => !IsWindow(h));
        foreach (var h in _startupTargets.Keys.Where(h => !IsWindow(h)).ToList()) _startupTargets.Remove(h);
        foreach (var k in _cooldown.Where(kv => kv.Value < now || !IsWindow(kv.Key)).Select(kv => kv.Key).ToList())
            _cooldown.Remove(k);
    }

    private static string Describe(IntPtr hwnd) => WindowFacts.Read(hwnd).Describe();

    private void Skip(Trigger t, IntPtr hwnd, string reason, WindowFacts? f = null)
    {
        if (Log.MinimumLevel > LogLevel.Debug && Decision == null) return;
        string desc = (f ?? WindowFacts.Read(hwnd)).Describe();
        Log.Debug($"[{t}] skip {desc}: {reason}");
        Decision?.Invoke(new EngineDecision(DateTime.Now, t, desc, false, reason));
    }

    private void Report(Trigger t, IntPtr hwnd, bool success, string detail, WindowFacts? f = null)
    {
        string desc = (f ?? WindowFacts.Read(hwnd)).Describe();
        if (success) Log.Info($"[{t}] moved {desc}: {detail}");
        else Log.Warn($"[{t}] could not move {desc}: {detail}");
        Decision?.Invoke(new EngineDecision(DateTime.Now, t, desc, success, detail));
    }
}

