# Removes TaskbarMonitor: stops the app and deletes the Scheduled Task.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

Get-Process TaskbarMonitor -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName "TaskbarMonitor" -Confirm:$false -ErrorAction SilentlyContinue
Write-Host "TaskbarMonitor removed (app stopped and scheduled task deleted)."
Start-Sleep -Seconds 2
