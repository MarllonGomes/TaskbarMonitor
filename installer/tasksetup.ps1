# Used by the installer. Registers/removes the Scheduled Task that starts
# TaskbarMonitor at logon, elevated (required to read CPU/disk temperatures),
# with no execution time limit.
param(
    [switch]$Install,
    [switch]$Uninstall
)
$taskName = "TaskbarMonitor"

# Never fail the installer over a task hiccup — best-effort, always exit 0.
try {
    if ($Install) {
        $exe = Join-Path $PSScriptRoot "TaskbarMonitor.exe"
        $user = "$env:USERDOMAIN\$env:USERNAME"
        # Start with Windows by default, elevated (RunLevel Highest) so
        # temperatures work, at logon, with no execution time limit.
        $action    = New-ScheduledTaskAction -Execute $exe
        $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $settings  = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -MultipleInstances IgnoreNew -StartWhenAvailable
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Force | Out-Null

        # Start immediately after install. Fall back to launching the exe
        # directly (elevated, since the installer is elevated) if the task
        # engine is briefly busy.
        try { Start-ScheduledTask -TaskName $taskName } catch { }
        Start-Sleep -Milliseconds 800
        if (-not (Get-Process TaskbarMonitor -ErrorAction SilentlyContinue)) {
            Start-Process -FilePath $exe -ErrorAction SilentlyContinue
        }
    }

    if ($Uninstall) {
        Get-Process TaskbarMonitor -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}
catch { }
exit 0
