<#
.SYNOPSIS
  Builds Smart Monitor and installs it for the current user (no admin rights needed).

.DESCRIPTION
  - Publishes a Release build (framework-dependent, ReadyToRun) to %LOCALAPPDATA%\Programs\SmartMonitor
  - Creates a Start menu shortcut
  - Optionally enables "Start with Windows"
  - Starts Smart Monitor

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1 -StartWithWindows

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\install.ps1 -Uninstall
#>
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "Programs\SmartMonitor"),
    [switch]$StartWithWindows,
    [switch]$NoStartMenuShortcut,
    [switch]$NoLaunch,
    [switch]$Uninstall
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$startMenuLnk = Join-Path ([Environment]::GetFolderPath("Programs")) "Smart Monitor.lnk"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

function Stop-SmartMonitor {
    $running = Get-Process SmartMonitor -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($Destination, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        Write-Host "Stopping running Smart Monitor..."
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

if ($Uninstall) {
    Stop-SmartMonitor
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force; Write-Host "Removed $Destination" }
    if (Test-Path $startMenuLnk) { Remove-Item $startMenuLnk -Force; Write-Host "Removed Start menu shortcut" }
    if (Get-ItemProperty $runKey -Name SmartMonitor -ErrorAction SilentlyContinue) { Remove-ItemProperty $runKey -Name SmartMonitor; Write-Host "Removed autostart entry" }
    schtasks.exe /Query /TN "SmartMonitor (elevated)" 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "Note: the elevated startup task still exists. Remove it in Smart Monitor (Behavior > Run elevated) before uninstalling, or run: schtasks /Delete /TN `"SmartMonitor (elevated)`" /F (as administrator)" }
    Write-Host "Settings and logs were kept in $env:APPDATA\SmartMonitor and $env:LOCALAPPDATA\SmartMonitor. Delete those folders to remove everything."
    return
}

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet -and (Test-Path "C:\Program Files\dotnet\dotnet.exe")) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
if (-not $dotnet) { throw "The .NET 10 SDK is required to build. Install it with:  winget install Microsoft.DotNet.SDK.10" }

Stop-SmartMonitor
Write-Host "Building and publishing to $Destination ..."
& $dotnet publish (Join-Path $repo "src\SmartMonitor.App\SmartMonitor.App.csproj") -c Release -r win-x64 --self-contained false `
    -p:PublishReadyToRun=true -o $Destination --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

$exe = Join-Path $Destination "SmartMonitor.exe"

if (-not $NoStartMenuShortcut) {
    $ws = New-Object -ComObject WScript.Shell
    $s = $ws.CreateShortcut($startMenuLnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $Destination
    $s.Description = "Open windows on the monitor your cursor is on"
    $s.Save()
    Write-Host "Created Start menu shortcut: $startMenuLnk"
}

if ($StartWithWindows) {
    Set-ItemProperty $runKey -Name SmartMonitor -Value "`"$exe`" --tray"
    Write-Host "Enabled Start with Windows"
}

if (-not $NoLaunch) {
    Start-Process $exe
    Write-Host "Smart Monitor is running (look for its icon in the notification area)."
}
