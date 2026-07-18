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
        // Replace older instances (e.g. after an update). Only touch processes
        // launched from our exact image — never kill an unrelated process that
        // merely happens to be named "TaskbarMonitor". MainModule is unreadable
        // for processes owned by another user/integrity level, which throws and
        // is skipped, so we never act across a security boundary.
        string? self = Environment.ProcessPath;
        if (self != null)
        {
            foreach (var p in Process.GetProcessesByName("TaskbarMonitor"))
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    if (!string.Equals(p.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase))
                        continue;
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch { }
                finally { p.Dispose(); }
            }
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
            string path = Path.Combine(Settings.Dir, "error.log");
            // Cap the log so a persistent crash loop can never fill the disk.
            try { if (new FileInfo(path).Length > 512 * 1024) File.Delete(path); } catch { }
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
