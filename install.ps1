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

# Security: the startup task runs elevated at logon. If the exe sits in a
# user-writable folder, any code running as the user could replace it and gain
# elevated access — a privilege-escalation / UAC-bypass vector. Only Program
# Files and the Windows folder are safe. Warn (and require confirmation) if the
# exe is elsewhere; recommend the setup installer.
$exeFull = (Resolve-Path $exe).Path
$safeRoots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:SystemRoot) |
    Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') + '\' }
$isSafe = $false
foreach ($root in $safeRoots) {
    if ($exeFull.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { $isSafe = $true; break }
}
if (-not $isSafe) {
    Write-Warning "TaskbarMonitor.exe is in a user-writable location:"
    Write-Warning "  $exeFull"
    Write-Warning "The startup task runs elevated, so this is a privilege-escalation risk."
    Write-Warning "Recommended: use the setup installer, which installs to Program Files."
    $answer = Read-Host "Create the elevated startup task anyway? (y/N)"
    if ($answer -notmatch '^(y|yes)$') {
        Write-Host "Aborted. No changes made."
        Start-Sleep -Seconds 2
        exit 1
    }
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
