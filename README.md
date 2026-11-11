# Smart Monitor

A lightweight Windows 11 tray utility that makes windows open where you are. **Any window that comes to you lands on the monitor your mouse cursor is on**, including:

- an app you launch from the taskbar, desktop or Start menu,
- a link you click (the browser comes forward on your monitor),
- a minimized app you restore,
- a window you Alt+Tab to,
- an app reopening from the system tray.

C# / .NET 10 / WPF (Windows 11 Fluent theme). The app is unpackaged and needs no admin rights. When idle it uses no CPU and about 20 MB of private memory.

---

## How it works, and how each launch path is handled

Windows has no public API that tells an app *which monitor's* taskbar or desktop icon was clicked. Smart Monitor uses two mechanisms and is explicit about which one it used:

| How the window appeared | How the target monitor is chosen | Reliability |
|---|---|---|
| **Smart shortcut** (launched through this utility: shortcut, pinned Smart shortcut, tray menu, Apps page) | Explorer passes the **monitor of the click** to the started program (`ShellExecuteEx` + `SEE_MASK_HMONITOR` → `STARTUPINFO.hStdOutput`). `SmartMonitor.exe --launch` reads that monitor handle directly and checks it against the real monitor list. It falls back to the cursor only if no handle was passed. | Exact |
| **Normal desktop shortcut / Start menu** | The engine sees the new window (`EVENT_OBJECT_SHOW`) and moves it to the monitor under the cursor. You just clicked, so that is the monitor you clicked on. | Very good for mouse launches |
| **Native taskbar button** (start, restore, switch) | Same as above, plus `EVENT_SYSTEM_MINIMIZEEND` / `EVENT_SYSTEM_FOREGROUND`. Your cursor is on the taskbar of the monitor you clicked. | Very good for mouse launches |
| **Link redirects / apps bringing themselves forward** | `EVENT_SYSTEM_FOREGROUND` → cursor's monitor. | Very good |
| **Alt+Tab / Win+Tab** | Cursor's monitor (can be turned off). | As configured |
| **Keyboard-only launches** (Win+1…9, typing in Start, Win+R) | There is no click, so the cursor's *current* monitor is used. | Heuristic |

The Test page shows the method used for the last Smart-shortcut launch ("the monitor Windows reported for the click" or "cursor position at launch"). The log records it for every launch.

### What is deliberately left alone

- **Focus fallback.** When you close or minimize a window, Windows activates the next window in the Z-order, which may be on the other monitor. Smart Monitor detects this by timing and does **not** move that window.
- Owned dialogs (Save As, settings dialogs…) stay with their app window.
- A window you are dragging or resizing, and a window you click directly (the cursor is over it).
- Windows' shell UI (taskbar, Start, search, Alt+Tab switcher), tool windows, popups and tooltips.
- Full-screen games and video. This is optional and on by default.
- Windows on other virtual desktops, including during a desktop switch.
- Apps in the **excluded apps** list.
- Everything, when Smart Monitor is switched off. The window hooks are then removed completely.

### Window placement

- The relative position is kept: a centered window stays centered, and a right-snapped window stays on the right.
- The window keeps its size, and is clamped to the target **work area**, so it never ends up behind the taskbar or off-screen.
- **Maximized** windows are re-maximized on the target monitor, and their "restore" size moves with them. **Minimized** windows keep their state.
- **Mixed DPI**: the window moves first so Windows and the app apply the new monitor's scaling, then it is placed precisely using the resulting size. The process is Per-Monitor-DPI-Aware v2, so all coordinates are physical pixels.
- **New windows are positioned before they appear.** Smart Monitor reacts to window *creation* (`EVENT_OBJECT_CREATE`) and moves the still-hidden window, so most apps (e.g. Notepad, Windows Terminal) first appear directly on the right monitor. Apps that ignore the move until after they have shown themselves (e.g. VS Code/Electron, whose UI thread is busy during startup) still briefly appear on their remembered monitor, about 50–70 ms in testing, and are then moved immediately (the new-window delay defaults to 0 ms). This can be turned off under Behavior › *Position new windows before they appear*.
- Some apps move themselves back to a saved position right after opening. Smart Monitor re-checks once after 1 s (configurable).
- Any monitor arrangement works: left/right/above/below, negative coordinates, portrait orientation.

---

## Limitations (honest list)

- **UAC (administrator) prompts can't be moved, duplicated or limited to one monitor.** Windows draws them on the *secure desktop*, which no app can draw on or interact with. With the secure desktop on (the default), every monitor is dimmed and blocked while a prompt is up.
  - If you turn off the policy *"User Account Control: Switch to the secure desktop when prompting for elevation"* (registry `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\PromptOnSecureDesktop = 0`), your other monitor stays usable during a prompt.
  - The cost is security: that makes the prompt easier for malware to imitate or interfere with, and Microsoft recommends leaving it on.
  - Even then, Windows decides where the prompt appears, and its window runs at a privilege level no app can move. Smart Monitor shows this setting's current value on the Behavior page and **never changes it**.
- **Admin windows:** Windows (UIPI) blocks normal apps from moving windows of elevated apps. Turn on **Behavior › Run elevated**. That registers a Task Scheduler logon task with highest privileges, which takes one UAC confirmation. Apps launched *from* Smart Monitor are still started without admin rights: an elevated Smart Monitor hands launches to Explorer.
- **Keyboard launches** have no click position. The cursor's current monitor is used.
- **Taskbar pinning:** Windows has no API for apps to pin to the taskbar, so you pin Smart shortcuts yourself (right-click › Show more options › Pin to taskbar). A pinned Smart shortcut shows as a separate taskbar button from the running app's button. With Smart Monitor running you usually don't need one, because the app's normal taskbar button already follows your cursor.
- **Apps that fight back:** a few apps (some games, launchers and overlays) keep repositioning themselves. Add them to *Excluded apps*.
- **UWP / Store apps:** these are hosted in `ApplicationFrameHost`. Moving works; exclusion matches the hosted app's exe on a best-effort basis.
- **Focus fallback vs. a real activation within ~0.7 s:** if you minimize or close a window and *then* activate another app less than ~0.7 s later, that activation is treated as a fallback and not moved.
- Windows' focus-stealing protection sometimes **flashes** an app's taskbar button instead of bringing it forward (for example, a link opened by a background process). A flashing button isn't "opened" yet. Click it and it comes to your monitor.

---

## Build

Requirements: Windows 10/11 and the **.NET 10 SDK**.

```powershell
winget install Microsoft.DotNet.SDK.10
dotnet build SmartMonitor.slnx
dotnet test  SmartMonitor.slnx          # placement/clamping/DPI/arrangement + window filter tests
```

Run from source: `src\SmartMonitor.App\bin\Debug\net10.0-windows\SmartMonitor.exe`

## Install (current user, no admin)

```powershell
powershell -ExecutionPolicy Bypass -File tools\install.ps1                 # build, install, Start menu shortcut, run
powershell -ExecutionPolicy Bypass -File tools\install.ps1 -StartWithWindows
powershell -ExecutionPolicy Bypass -File tools\install.ps1 -Uninstall
```

The installer publishes a Release build (ReadyToRun) to `%LOCALAPPDATA%\Programs\SmartMonitor`. It needs the .NET 10 Desktop Runtime, which the SDK includes. To build a copy you can move to another PC by hand:

```powershell
dotnet publish src\SmartMonitor.App -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o publish
# or fully self-contained (no runtime needed, ~70 MB):
dotnet publish src\SmartMonitor.App -c Release -r win-x64 --self-contained true -o publish-sc
```

## Use

- The app lives in the notification area.
  - **Left-click** the tray icon to open the window.
  - **Right-click** it for: Enabled, Launch here, Identify monitors, Open log folder, Exit.
  - The icon is greyed out when Smart Monitor is off.
- **Monitors**: the layout to scale, with the monitor under your cursor highlighted live. Each monitor shows its resolution, scale, orientation, work area and refresh rate. *Identify* shows big numbers on each screen.
- **Apps**:
  - *Excluded apps*: never moved.
  - *Launcher*: add programs or shortcuts, or pick a running app. Each one can launch now, create a desktop or Start menu **Smart shortcut**, and choose what happens if it's already running: bring the existing window here (no duplicate), or always start a new one. For stub launchers (e.g. Discord's `Update.exe`), list the real window process under *Window process names*.
- **Behavior**:
  - Choose which events make a window follow the cursor: activation, Alt+Tab, restore, new windows.
  - Set the full-screen exception and the timing.
  - Start with Windows, Run elevated.
  - See the UAC secure-desktop state.
  - Turn on detailed logging.
- **Test**:
  - *Detect in 3 seconds* lights up the monitor under your cursor.
  - *Move in 3 seconds* moves the window you click to the cursor's monitor.
  - Shows the last Smart-shortcut launch and how its monitor was determined.
  - *Live activity* shows every move, and optionally every window that was left alone and why.
- **Log**: recent entries. The files are in `%LOCALAPPDATA%\SmartMonitor\logs` (rolling, 3 × 2 MB).

Settings are stored in `%APPDATA%\SmartMonitor\settings.json`.

Command line:

```
SmartMonitor.exe              start (or show the running instance)
SmartMonitor.exe --tray       start hidden in the notification area (autostart uses this)
SmartMonitor.exe --launch ID  launch a configured app on the monitor it was launched from (Smart shortcuts)
```

---

## Project layout

```
src/SmartMonitor.Core/        engine and Win32 interop (no UI)
  Native/                     P/Invoke (user32, shcore, dwmapi, kernel32, shell32) + DisplayConfig friendly names
  Monitors/                   MonitorService: EnumDisplayMonitors, GetMonitorInfo, MonitorFromPoint, GetDpiForMonitor
  Windows/                    WinEventSource (out-of-context hooks on a message-loop thread)
                              FollowCursorEngine (decisions, focus-fallback/desktop-switch/drag handling)
                              WindowFilter, WindowFacts, WindowMover, WindowPlacementCalculator (pure, unit-tested)
  Launch/                     LaunchContextResolver (STARTUPINFO HMONITOR → cursor), AppLauncher, ShellLink, InstancePipe
  Config/                     AppSettings (JSON), StartupManager (Run key / elevated task / UAC policy read-out)
  Logging/                    rolling file + in-memory log
src/SmartMonitor.App/         WPF UI, tray icon, single instance, --launch fast path (Program.cs runs before WPF loads)
tests/SmartMonitor.Core.Tests xUnit
tools/                        install.ps1, make-icon.ps1
```

Design notes:

- **Hooks:** `SetWinEventHook` with `WINEVENT_OUTOFCONTEXT`, so no DLL is injected into other processes. The hook thread only queues events, and one worker thread makes all decisions, so a hung app can't stall the hooks.
- **Single instance:** a named mutex plus a current-user-only named pipe (`show`, `exit`, `context`, `expect`).
- **Short-lived launcher:** a Smart shortcut starts `SmartMonitor.exe --launch`, which runs as you (never elevated). It reads the click monitor, tells the tray engine which process and monitor to expect, starts or activates the app, and exits. If the tray app isn't running, it places the window itself.

## Verified on the development machine

Two monitors: 1920×1200 built-in display (primary) and 2560×1440 external display to its right, both at 125 %.

- **New window**:
  - Notepad started with the cursor on M2 → opened on M2.
  - Restored with the cursor on M1 → M1.
  - Activated with the cursor on M2 → M2.
- **Maximized window**: moved maximized M2→M1 and back, and its restore rectangle moved with it.
- **Link redirect**: Chrome maximized on M2, link opened from a foreground app with the cursor on M1 → Chrome came forward maximized on M1. Repeated 3/3 in both directions.
- **Alt+Tab**: the target window moved to the cursor's monitor.
- **Focus fallback**: minimizing the active window on M2 → the window that received focus on M1 stayed on M1.
- **Owned dialog**: Notepad's *Open* dialog was left with its window. The cursor over a window → not moved.
- **Smart shortcut**:
  - Launched with the "click" on M2 while the cursor was on M1 → `STARTUPINFO flags=0xC01, hStdOutput=HMONITOR` → opened on M2.
  - Worked both with and without the tray app running.
  - "Bring existing window" → no duplicate, moved to the click monitor.
- **Off switch**: with Follow cursor off, the hooks are removed and windows are not moved.
- **Idle cost**: 0 ms CPU over 10 s, ~21 MB private memory.

Not exercised on this machine:
- A DPI change between monitors (both run at 125 %; the DPI math is unit-tested).
- Physical clicks on the native taskbar and desktop. These go through the same events that were tested programmatically.
- Running elevated.

### Manual checklist

1. With the cursor on monitor 2, click a link in Discord, Teams or Outlook on monitor 2 → the browser comes forward on monitor 2.
2. Minimize an app, then click its taskbar button on the *other* monitor's taskbar → it restores there (with "show taskbar apps on all displays").
3. Double-click a desktop icon on each monitor, and start an app from the Start menu on each monitor.
4. Alt+Tab with the cursor on the other monitor.
5. Add a game to Excluded apps and confirm it stays put. Toggle Smart Monitor off and confirm nothing moves.
6. Turn on Start with Windows and sign out/in. Optionally turn on Run elevated and check that an admin app (e.g. an elevated Terminal) also follows.
