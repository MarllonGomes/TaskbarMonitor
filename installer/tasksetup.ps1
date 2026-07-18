# Used by the installer. Registers/removes the Scheduled Task that starts
# TaskbarMonitor at logon, elevated (required to read CPU/disk temperatures),
# with no execution time limit.
param(
    [switch]$Install,
    [switch]$Uninstall
)
$taskName = "TaskbarMonitor"

if ($Install) {
    $exe = Join-Path $PSScriptRoot "TaskbarMonitor.exe"
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action    = New-ScheduledTaskAction -Execute $exe
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
}

if ($Uninstall) {
    Get-Process TaskbarMonitor -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
}
