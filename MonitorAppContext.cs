using System.Diagnostics;
using Microsoft.Win32;

namespace TaskbarMonitor;

/// <summary>
/// Coordinates one OverlayForm per screen: pins each to the left corner of
/// that screen's taskbar (Shell_TrayWnd / Shell_SecondaryTrayWnd), hides it
/// while a fullscreen window (game, video) covers that screen or while the
/// taskbar is auto-hidden, and owns the tray icon + context menu.
/// </summary>
public sealed class MonitorAppContext : ApplicationContext
{
    private readonly SensorService _sensors = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _tray = new();
    private readonly Dictionary<string, OverlayForm> _overlays = new();
    private int _tick;
    private bool _dark = true;

    public MonitorAppContext()
    {
        BuildMenu();
        BuildTrayIcon();
        RebuildOverlays();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => RebuildOverlays();

    private void RebuildOverlays()
    {
        var current = Screen.AllScreens.Select(s => s.DeviceName).ToHashSet();

        foreach (var (device, form) in _overlays.Where(kv => !current.Contains(kv.Key)).ToList())
        {
            form.Dispose();
            _overlays.Remove(device);
        }
        foreach (string device in current)
        {
            if (_overlays.ContainsKey(device)) continue;
            _overlays[device] = new OverlayForm(_sensors, device) { ContextMenuStrip = _menu };
        }
    }

    // ----- main loop -------------------------------------------------------

    private void OnTick()
    {
        _tick++;
        bool repaint = _tick % 2 == 0;   // 1 s: fresh sensor snapshot

        var taskbars = GetTaskbars();
        var fullscreens = GetFullscreenRects();

        foreach (var (device, form) in _overlays)
        {
            var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device);
            if (screen == null) { form.HideOverlay(); continue; }

            // any fullscreen window (game/video) covering this screen?
            // (maximized windows don't cover the taskbar, so they don't trigger this)
            if (fullscreens.Any(r => r.Contains(screen.Bounds)))
            {
                form.HideOverlay();
                continue;
            }

            Rectangle tbRect;
            var tb = taskbars.FirstOrDefault(t => t.Device == device);
            if (tb.Hwnd != IntPtr.Zero)
            {
                tbRect = tb.Rect;
                var visiblePart = Rectangle.Intersect(tbRect, screen.Bounds);
                // taskbar auto-hidden (slid off-screen) or invisible
                if (!tb.Visible || visiblePart.Height < 16 || visiblePart.Width < 16)
                {
                    form.HideOverlay();
                    continue;
                }
            }
            else
            {
                // screen without its own taskbar: strip along the bottom edge
                int h = form.Dpi(48);
                tbRect = new Rectangle(screen.Bounds.Left, screen.Bounds.Bottom - h, screen.Bounds.Width, h);
            }

            bool changed = form.ApplyBounds(new Rectangle(
                tbRect.Left, tbRect.Top, form.ContentWidth, tbRect.Height));
            if (changed || repaint) form.Render();
        }

        if (_tick % 20 == 0) ApplyTheme();      // 10 s
        if (_tick % 10 == 0) UpdateTooltips();  // 5 s
    }

    // ----- taskbars and fullscreen windows ---------------------------------

    private readonly record struct TaskbarInfo(IntPtr Hwnd, Rectangle Rect, string Device, bool Visible);

    private static List<TaskbarInfo> GetTaskbars()
    {
        var list = new List<TaskbarInfo>();

        void Add(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var r)) return;
            list.Add(new TaskbarInfo(hwnd, Win32.ToRectangle(r),
                Screen.FromHandle(hwnd).DeviceName, Win32.IsWindowVisible(hwnd)));
        }

        Add(Win32.FindWindow("Shell_TrayWnd", null));
        IntPtr sec = IntPtr.Zero;
        while ((sec = Win32.FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            Add(sec);

        return list;
    }

    private static readonly HashSet<string> ShellClasses = new()
    {
        "WorkerW", "Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>
    /// Rectangles of every visible window that may be "fullscreen": excludes
    /// the shell, cloaked (ghost) windows, click-through overlays (e.g. the
    /// NVIDIA overlay) and our own windows.
    /// </summary>
    private static List<Rectangle> GetFullscreenRects()
    {
        var rects = new List<Rectangle>();
        int myPid = Environment.ProcessId;

        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd)) return true;

            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == myPid) return true;

            if (!Win32.GetWindowRect(hwnd, out var r)) return true;
            var rect = Win32.ToRectangle(r);
            if (rect.Width < 600 || rect.Height < 500) return true;   // cheap early out

            long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            if ((ex & Win32.WS_EX_TRANSPARENT) != 0) return true;     // click-through overlay

            if (Win32.IsCloaked(hwnd)) return true;
            if (ShellClasses.Contains(Win32.GetClassNameOf(hwnd))) return true;

            rects.Add(rect);
            return true;
        }, IntPtr.Zero);

        return rects;
    }

    // ----- theme / tooltip -------------------------------------------------

    private void ApplyTheme()
    {
        bool dark = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("SystemUsesLightTheme") is not int v || v == 0;
        }
        catch { }
        if (dark == _dark) return;
        _dark = dark;
        foreach (var f in _overlays.Values)
        {
            f.SetTheme(dark);
            f.Render();
        }
    }

    private void UpdateTooltips()
    {
        var s = _sensors.Current;
        string gb(float? v) => v.HasValue ? $"{v:0.0}" : "-";
        string diskText = s.Disks.Count == 0
            ? "Disk: --"
            : string.Join("\n", s.Disks.Select((d, i) =>
                $"Disk{(s.Disks.Count > 1 ? $" {i + 1}" : "")} ({d.Name}): {Fmt(d.Load, "%")} • {Fmt(d.Temp, "°C")}"));
        string text =
            $"CPU: {Fmt(s.CpuLoad, "%")} • {Fmt(s.CpuTemp, "°C")}\n" +
            $"GPU{(s.GpuName != null ? $" ({s.GpuName})" : "")}: {Fmt(s.GpuLoad, "%")} • {Fmt(s.GpuTemp, "°C")}\n" +
            $"RAM: {Fmt(s.RamLoad, "%")} ({gb(s.RamUsedGb)} / {gb(s.RamTotalGb)} GB)\n" +
            diskText + "\n" +
            $"Net: ↑ {Spd(s.NetUpBps)}  ↓ {Spd(s.NetDownBps)}";
        if (!Program.IsElevated)
            text += "\n\n⚠ Not running as administrator — CPU/disk temperatures need elevation.";

        foreach (var f in _overlays.Values) f.SetTooltip(text);
        _tray.Text = Truncate($"CPU {Fmt(s.CpuLoad, "%")} {Fmt(s.CpuTemp, "°")}  RAM {Fmt(s.RamLoad, "%")}", 63);

        static string Fmt(float? v, string suffix) =>
            v.HasValue ? $"{Math.Round(v.Value)}{suffix}" : "--";
        static string Spd(float? v) => v switch
        {
            null => "--",
            >= 1024f * 1024f => $"{v / (1024f * 1024f):0.00} MB/s",
            >= 1024f => $"{v / 1024f:0} KB/s",
            _ => $"{v:0} B/s",
        };
        static string Truncate(string t, int max) => t.Length <= max ? t : t[..max];
    }

    // ----- menu / tray -----------------------------------------------------

    private void BuildMenu()
    {
        var autostartItem = new ToolStripMenuItem("Start with Windows");
        autostartItem.Click += (_, _) =>
        {
            if (autostartItem.Checked)
            {
                if (!Autostart.Disable()) ShowAutostartError();
                return;
            }

            // The startup task runs elevated. Refuse to create it while the app
            // runs from a user-writable location — otherwise the task becomes a
            // privilege-escalation vector (see Autostart.IsExePathSecure).
            if (!Autostart.IsExePathSecure())
            {
                var choice = MessageBox.Show(
                    "For security, \"Start with Windows\" should only be enabled when " +
                    "TaskbarMonitor is installed in Program Files.\n\n" +
                    "This copy runs from:\n" + Application.ExecutablePath + "\n\n" +
                    "That folder can be modified without administrator rights, and the " +
                    "startup task runs elevated — so enabling it here would let any program " +
                    "running as you replace the app and gain elevated access at logon.\n\n" +
                    "Recommended: install with the setup installer instead.\n\n" +
                    "Enable anyway?",
                    "TaskbarMonitor — security warning",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (choice != DialogResult.Yes) return;
            }

            if (!Autostart.Enable()) ShowAutostartError();
        };

        var elevateItem = new ToolStripMenuItem("Restart as administrator (enables temperatures)");
        elevateItem.Click += (_, _) => RestartElevated();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        _menu.Opening += (_, _) =>
        {
            autostartItem.Checked = Autostart.IsEnabled();
            elevateItem.Visible = !Program.IsElevated;
        };
        _menu.Items.Add(autostartItem);
        _menu.Items.Add(elevateItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
    }

    private static void ShowAutostartError() =>
        MessageBox.Show(
            "Could not change the autostart setting.\n" +
            "Accept the administrator prompt (UAC) or run install.ps1.",
            "TaskbarMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void BuildTrayIcon()
    {
        _tray.Icon = MakeTrayIcon();
        _tray.Text = "TaskbarMonitor";
        _tray.Visible = true;
        _tray.ContextMenuStrip = _menu;
    }

    private static Icon MakeTrayIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(Color.FromArgb(0, 200, 120));
            g.FillRectangle(b, 2, 9, 3, 5);
            g.FillRectangle(b, 6, 5, 3, 9);
            g.FillRectangle(b, 10, 7, 3, 7);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally { Win32.DestroyIcon(h); }
    }

    private void RestartElevated()
    {
        try
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            Process.Start(psi);
            ExitApp();
        }
        catch { /* UAC declined */ }
    }

    private void ExitApp()
    {
        _timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        foreach (var f in _overlays.Values) f.Dispose();
        _overlays.Clear();
        var icon = _tray.Icon;   // NotifyIcon.Dispose doesn't free the assigned Icon/HICON
        _tray.Visible = false;
        _tray.Dispose();
        icon?.Dispose();
        _sensors.Dispose();
        ExitThread();
    }
}
