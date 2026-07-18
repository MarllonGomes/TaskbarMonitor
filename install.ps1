# Installs TaskbarMonitor: creates the Scheduled Task (at logon, elevated,
# no execution time limit) and starts the app. Self-elevates via UAC if needed.
#
# Looks for TaskbarMonitor.exe next to this script (release zip layout) or in
# .\dist (source tree layout after running build.ps1).
$ErrorActionPreference = "Stop"

$exe = Join-Path $PSScriptRoot "TaskbarMonitor.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $PSScriptRoot "dist\TaskbarMonitor.exe" }
if (-not (Test-Path $exe)) {
    Write-Error "TaskbarMonitor.exe not found. Run build.ps1 first, or run this script from the extracted release zip."
    exit 1
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

$user      = "$env:USERDOMAIN\$env:USERNAME"
$action    = New-ScheduledTaskAction -Execute $exe
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew -StartWhenAvailable

Register-ScheduledTask -TaskName "TaskbarMonitor" -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Scheduled task 'TaskbarMonitor' created (starts at logon, elevated)."

# Stop any previous instance and start the new one, already elevated
Get-Process TaskbarMonitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Start-ScheduledTask -TaskName "TaskbarMonitor"
Write-Host "TaskbarMonitor started. Done!"
Start-Sleep -Seconds 2
