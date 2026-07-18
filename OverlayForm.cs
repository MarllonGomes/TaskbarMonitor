using System.Drawing.Imaging;
using System.Drawing.Text;

namespace TaskbarMonitor;

/// <summary>
/// Minimalist overlay (one instance per screen) pinned to the left corner of
/// the taskbar. Layered window with a transparent background — only the text
/// shows, straight over the taskbar — topmost and never steals focus.
///
/// Column-per-device layout: a small header label on top (CPU, GPU, RAM,
/// DISK 1, DISK 2, NET) and up to two value lines below it (load, temperature;
/// for NET: upload, download). Column widths come from worst-case templates so
/// nothing jitters, and adding a device is just adding a column.
/// MonitorAppContext drives position and visibility.
/// </summary>
public sealed class OverlayForm : Form
{
    private readonly SensorService _sensors;
    private readonly ToolTip _tip = new() { InitialDelay = 400, ReshowDelay = 400, AutoPopDelay = 20000 };

    public string ScreenDevice { get; }
    public int ContentWidth { get; private set; } = 240;

    private Font _labelFont = null!;
    private Font _valueFont = null!;
    private float _labelH, _valueH;
    private bool _dark = true;
    private Color _backColor, _labelColor, _valueColor, _warnColor, _hotColor;

    /// <summary>Nearly invisible background: alpha 2 keeps mouse interaction
    /// working (alpha 0 would make the window click-through).</summary>
    private const int BackAlpha = 2;

    /// <summary>One device column: header label + up to two value lines.
    /// ArrowValues = the first character of each value (↑/↓) is drawn in the
    /// label color, glued to the number.</summary>
    private sealed record ColumnDef(string Label, string?[] Templates, bool ArrowValues = false);

    private ColumnDef[] _columns = [];
    private int _diskCount = -1;
    private float[] _colX = [];

    public OverlayForm(SensorService sensors, string screenDevice)
    {
        _sensors = sensors;
        ScreenDevice = screenDevice;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "TaskbarMonitor";

        SetTheme(dark: true, force: true);
        EnsureColumns(1);
        BuildFonts();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW |
                          Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED;
            return cp;
        }
    }

    // content is drawn exclusively via UpdateLayeredWindow
    protected override void OnPaintBackground(PaintEventArgs e) { }

    // ----- driven by MonitorAppContext -------------------------------------

    /// <summary>Positions over the taskbar; returns true if geometry changed.</summary>
    public bool ApplyBounds(Rectangle b)
    {
        bool changed = !Visible || Bounds != b;
        if (!Visible) Show();
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        return changed;
    }

    public void HideOverlay()
    {
        if (Visible) Hide();
    }

    public void SetTheme(bool dark, bool force = false)
    {
        if (!force && dark == _dark) return;
        _dark = dark;
        if (_dark)
        {
            _backColor = Color.FromArgb(32, 32, 32);
            _labelColor = Color.FromArgb(150, 150, 150);
            _valueColor = Color.FromArgb(240, 240, 240);
        }
        else
        {
            _backColor = Color.FromArgb(240, 240, 240);
            _labelColor = Color.FromArgb(105, 105, 105);
            _valueColor = Color.FromArgb(20, 20, 20);
        }
        _warnColor = Color.FromArgb(255, 185, 70);
        _hotColor = Color.FromArgb(255, 95, 95);
    }

    public void SetTooltip(string text) => _tip.SetToolTip(this, text);

    public int Dpi(int px) => (int)Math.Round(px * DeviceDpi / 96f);

    // ----- columns / fonts / layout ----------------------------------------

    /// <summary>Rebuilds the column list when the number of drives changes.
    /// The bar shows at most two drives; the tooltip lists them all.</summary>
    private void EnsureColumns(int diskCount)
    {
        int n = Math.Clamp(diskCount, 1, 2);
        if (n == _diskCount) return;
        _diskCount = n;

        var cols = new List<ColumnDef>
        {
            new("CPU", ["100%", "99°"]),
            new("GPU", ["100%", "99°"]),
            new("RAM", ["100%", "88.8G"]),
        };
        if (n == 1)
            cols.Add(new ColumnDef("DISK", ["100%", "99°"]));
        else
            for (int i = 0; i < n; i++)
                cols.Add(new ColumnDef($"DISK {i + 1}", ["100%", "99°"]));
        cols.Add(new ColumnDef("NET", ["↑888M", "↓888M"], ArrowValues: true));

        _columns = cols.ToArray();
        if (_valueFont != null) BuildLayout();
    }

    private void BuildFonts()
    {
        _labelFont?.Dispose();
        _valueFont?.Dispose();
        // ~500 weight for the small header labels (closest GDI static face)
        _labelFont = new Font("Segoe UI Semibold", Math.Max(7, Dpi(8)), FontStyle.Regular, GraphicsUnit.Pixel);
        _valueFont = new Font("Segoe UI", Math.Max(10, Dpi(12)), FontStyle.Regular, GraphicsUnit.Pixel);
        BuildLayout();
    }

    private void BuildLayout()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        var sf = StringFormat.GenericTypographic;
        float gapCol = Dpi(12), pad = Dpi(8);

        _colX = new float[_columns.Length];
        float x = pad;
        for (int i = 0; i < _columns.Length; i++)
        {
            _colX[i] = x;
            var c = _columns[i];
            float w = g.MeasureString(c.Label, _labelFont, PointF.Empty, sf).Width;
            foreach (string? t in c.Templates)
            {
                if (t == null) continue;
                w = Math.Max(w, g.MeasureString(t, _valueFont, PointF.Empty, sf).Width);
            }
            x += w + gapCol;
        }
        ContentWidth = (int)Math.Ceiling(x - gapCol + pad);

        _labelH = g.MeasureString("0", _labelFont, PointF.Empty, sf).Height;
        _valueH = g.MeasureString("0", _valueFont, PointF.Empty, sf).Height;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BuildFonts();
        Render();
    }

    // ----- rendering (layered) ---------------------------------------------

    /// <summary>Compact gigabytes: "9.8G", "13.1G", "128G".</summary>
    public static string FormatGb(float? gb)
    {
        if (!gb.HasValue) return "--";
        return gb.Value >= 100 ? $"{gb.Value:0}G" : $"{gb.Value:0.0}G";
    }

    /// <summary>Compact, at most 3 digits + unit: "0K", "87K", "999K", "2M", "118M".</summary>
    public static string FormatSpeed(float? bps)
    {
        if (!bps.HasValue) return "--";
        float v = bps.Value;
        if (v >= 1024f * 1024f) return $"{Math.Min(999, Math.Round(v / (1024f * 1024f)))}M";
        if (v >= 1024f) return $"{Math.Round(v / 1024f)}K";
        return "0K";
    }

    public void Render()
    {
        if (!IsHandleCreated || !Visible) return;
        int w = Math.Max(1, Width), h = Math.Max(1, Height);

        var s = _sensors.Current;
        EnsureColumns(s.Disks.Count);

        string P(float? v) => v.HasValue ? $"{Math.Round(v.Value)}%" : "--";
        string T(float? v) => v.HasValue ? $"{Math.Round(v.Value)}°" : "--";
        Color LoadC(float? v) => v >= 95 ? _hotColor : v >= 85 ? _warnColor : _valueColor;
        Color TempC(float? v) => v >= 85 ? _hotColor : v >= 70 ? _warnColor : _valueColor;

        // per-column values (line 1, line 2) mirroring _columns
        int count = _columns.Length;
        var v1 = new string?[count]; var c1 = new Color[count];
        var v2 = new string?[count]; var c2 = new Color[count];
        int idx = 0;
        Set(P(s.CpuLoad), LoadC(s.CpuLoad), T(s.CpuTemp), TempC(s.CpuTemp));
        Set(P(s.GpuLoad), LoadC(s.GpuLoad), T(s.GpuTemp), TempC(s.GpuTemp));
        Set(P(s.RamLoad), LoadC(s.RamLoad), FormatGb(s.RamUsedGb), _valueColor);
        for (int i = 0; i < _diskCount; i++)
        {
            var d = i < s.Disks.Count ? s.Disks[i] : null;
            Set(P(d?.Load), LoadC(d?.Load), T(d?.Temp), TempC(d?.Temp));
        }
        Set("↑" + FormatSpeed(s.NetUpBps), _valueColor, "↓" + FormatSpeed(s.NetDownBps), _valueColor);

        void Set(string? a, Color ca, string? b, Color cb)
        {
            v1[idx] = a; c1[idx] = ca; v2[idx] = b; c2[idx] = cb; idx++;
        }

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(BackAlpha, _backColor));

            var sf = StringFormat.GenericTypographic;
            using var labelBrush = new SolidBrush(_labelColor);

            // three lines: header + two value lines. Weighted distribution:
            // extra padding on top (detaches the header from the taskbar edge)
            // and a small breathing gap between the lines.
            float unit = Math.Max(0f, (h - _labelH - 2 * _valueH) / 4f);
            float yLabel = unit * 1.6f;
            float yVal1 = yLabel + _labelH + unit * 1.0f;
            float yVal2 = yVal1 + _valueH + unit * 0.8f;

            for (int i = 0; i < count; i++)
            {
                var col = _columns[i];
                float x = _colX[i];
                g.DrawString(col.Label, _labelFont, labelBrush, new PointF(x, yLabel), sf);
                if (v1[i] != null) DrawValue(g, v1[i]!, col.ArrowValues, x, yVal1, c1[i], labelBrush, sf);
                if (v2[i] != null) DrawValue(g, v2[i]!, col.ArrowValues, x, yVal2, c2[i], labelBrush, sf);
            }
        }
        Win32.UpdateLayered(Handle, bmp, Left, Top);
    }

    private void DrawValue(Graphics g, string text, bool arrow, float x, float y,
        Color color, SolidBrush labelBrush, StringFormat sf)
    {
        if (arrow && text.Length > 1)
        {
            // arrow in the label color, number in the value color, glued together
            string a = text[..1], rest = text[1..];
            float wa = g.MeasureString(a, _valueFont, PointF.Empty, sf).Width;
            g.DrawString(a, _valueFont, labelBrush, new PointF(x, y), sf);
            using var brush = new SolidBrush(color);
            g.DrawString(rest, _valueFont, brush, new PointF(x + wa, y), sf);
        }
        else
        {
            using var brush = new SolidBrush(color);
            g.DrawString(text, _valueFont, brush, new PointF(x, y), sf);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
            _labelFont?.Dispose();
            _valueFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
