using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace TaskbarMonitor;

/// <summary>
/// Manages the Scheduled Task that starts the app at logon with elevated
/// privileges (RunLevel Highest) and no execution time limit — required for
/// continuous monitoring and for reading temperatures.
/// </summary>
public static class Autostart
{
    public const string TaskName = "TaskbarMonitor";

    public static bool IsEnabled() =>
        RunSchtasks($"/Query /TN \"{TaskName}\"", elevated: false) == 0;

    public static bool Enable()
    {
        string xmlPath = Path.Combine(Settings.Dir, "task.xml");
        Directory.CreateDirectory(Settings.Dir);
        File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);

        string args = $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F";
        if (RunSchtasks(args, elevated: false) == 0) return true;
        return RunSchtasks(args, elevated: true) == 0;   // fallback: UAC prompt
    }

    public static bool Disable()
    {
        string args = $"/Delete /TN \"{TaskName}\" /F";
        if (RunSchtasks(args, elevated: false) == 0) return true;
        return RunSchtasks(args, elevated: true) == 0;
    }

    private static string BuildTaskXml()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string user = SecurityElement.Escape(identity.Name);
        string exe = SecurityElement.Escape(Application.ExecutablePath);

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Hardware monitor in the taskbar</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{user}</UserId>
      <Delay>PT3S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{user}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{exe}</Command>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static int RunSchtasks(string args, bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = elevated,
            };
            if (elevated) psi.Verb = "runas";
            using var p = Process.Start(psi);
            if (p == null) return -1;
            if (!p.WaitForExit(20000)) return -1;
            return p.ExitCode;
        }
        catch
        {
            return -1;   // UAC declined or failed to start
        }
    }
}
