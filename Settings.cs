namespace TaskbarMonitor;

public static class Settings
{
    /// <summary>App data folder (autostart task.xml, error.log).</summary>
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarMonitor");
}
