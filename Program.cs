using System.Diagnostics;
using System.Security.Principal;

namespace TaskbarMonitor;

static class Program
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    [STAThread]
    static void Main()
    {
        // Replace older instances (e.g. after an update); killing requires the
        // same privilege level as the old instance — the Scheduled Task runs elevated.
        foreach (var p in Process.GetProcessesByName("TaskbarMonitor"))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        using var mutex = new Mutex(true, @"Local\TaskbarMonitor_SingleInstance", out bool isFirst);
        if (!isFirst) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject);
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MonitorAppContext());
    }

    private static void LogCrash(object? ex)
    {
        // Benign teardown race: a timer tick may touch a disposed form while
        // an old instance is being replaced. Not worth polluting the log.
        if (ex is ObjectDisposedException) return;
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(Path.Combine(Settings.Dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
