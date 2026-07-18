using System.Drawing.Imaging;
using System.Drawing.Text;

namespace TaskbarMonitor;

/// <summary>
/// Minimalist overlay (one instance per screen) pinned to the left corner of
/// the taskbar. Layered window with a transparent background — only the text
/// shows, straight over the taskbar — topmost and never steals focus. Grid
/// layout: cell edges are shared between the two rows and values are always
/// left-aligned inside their cell, so nothing jitters as digits change.
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

    /// <summary>A labeled group of cells. Cell edges are shared between rows of
    /// the same column and values are always drawn left-aligned in their cell,
    /// forming a stable grid.</summary>
    private sealed record GroupDef(string Label, string[] Templates);

    private static readonly GroupDef[][] Rows =
    [
        [
            new("CPU", ["100%", "99°"]),
            new("GPU", ["100%", "99°"]),
            new("RAM", ["100%"]),
        ],
        [
            new("DSK", ["100%", "99°"]),
            new("UP", ["888M"]),
            new("DO", ["888M"]),
        ],
    ];

    // precomputed positions: [row][group] -> label x / start x of each cell
    private float[][] _labelX = [];
    private float[][][] _cellAnchor = [];

    public OverlayForm(SensorService sensors, string screenDevice)
    {
        _sensors = sensors;
        ScreenDevice = screenDevice;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "TaskbarMonitor";

        SetTheme(dark: true, force: true);
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

    // ----- fonts / grid layout ---------------------------------------------

    private void BuildFonts()
    {
        _labelFont?.Dispose();
        _valueFont?.Dispose();
        _labelFont = new Font("Segoe UI", Math.Max(8, Dpi(9)), FontStyle.Regular, GraphicsUnit.Pixel);
        _valueFont = new Font("Segoe UI", Math.Max(10, Dpi(12)), FontStyle.Regular, GraphicsUnit.Pixel);
        BuildLayout();
    }

    private void BuildLayout()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        var sf = StringFormat.GenericTypographic;
        float gapGroup = Dpi(10), gapPair = Dpi(4), gapLabel = Dpi(4), pad = Dpi(8);

        float MeasureL(string t) => g.MeasureString(t, _labelFont, PointF.Empty, sf).Width;
        float MeasureV(string t) => g.MeasureString(t, _valueFont, PointF.Empty, sf).Width;

        int rows = Rows.Length;
        int maxGroups = Rows.Max(r => r.Length);
        _labelX = new float[rows][];
        _cellAnchor = new float[rows][][];
        for (int r = 0; r < rows; r++)
        {
            _labelX[r] = new float[Rows[r].Length];
            _cellAnchor[r] = new float[Rows[r].Length][];
        }

        float x = pad;
        float rightMost = 0;
        for (int gi = 0; gi < maxGroups; gi++)
        {
            // label column = widest label in the column; cell widths are
            // shared between rows (widest template in the column)
            float maxLabel = 0;
            int maxCells = 0;
            for (int r = 0; r < rows; r++)
            {
                if (gi >= Rows[r].Length) continue;
                maxLabel = Math.Max(maxLabel, MeasureL(Rows[r][gi].Label));
                maxCells = Math.Max(maxCells, Rows[r][gi].Templates.Length);
            }

            var cellLeft = new float[maxCells];
            float cx = x + maxLabel + gapLabel;
            for (int c = 0; c < maxCells; c++)
            {
                cellLeft[c] = cx;
                float cw = 0;
                for (int r = 0; r < rows; r++)
                {
                    if (gi >= Rows[r].Length || c >= Rows[r][gi].Templates.Length) continue;
                    cw = Math.Max(cw, MeasureV(Rows[r][gi].Templates[c]));
                }
                cx += cw + gapPair;
            }
            float slotEnd = cx - gapPair;

            for (int r = 0; r < rows; r++)
            {
                if (gi >= Rows[r].Length) continue;
                var def = Rows[r][gi];
                _labelX[r][gi] = x;
                var anchors = new float[def.Templates.Length];
                for (int c = 0; c < def.Templates.Length; c++) anchors[c] = cellLeft[c];
                _cellAnchor[r][gi] = anchors;
            }

            rightMost = slotEnd;
            x = slotEnd + gapGroup;
        }

        ContentWidth = (int)Math.Ceiling(rightMost + pad);
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
        string P(float? v) => v.HasValue ? $"{Math.Round(v.Value)}%" : "--";
        string T(float? v) => v.HasValue ? $"{Math.Round(v.Value)}°" : "--";
        Color LoadC(float? v) => v >= 95 ? _hotColor : v >= 85 ? _warnColor : _valueColor;
        Color TempC(float? v) => v >= 85 ? _hotColor : v >= 70 ? _warnColor : _valueColor;

        // values/colors follow the same structure as Rows
        string[][][] vals =
        [
            [
                [P(s.CpuLoad), T(s.CpuTemp)],
                [P(s.GpuLoad), T(s.GpuTemp)],
                [P(s.RamLoad)],
            ],
            [
                [P(s.DiskLoad), T(s.DiskTemp)],
                [FormatSpeed(s.NetUpBps)],
                [FormatSpeed(s.NetDownBps)],
            ],
        ];
        Color[][][] cols =
        [
            [
                [LoadC(s.CpuLoad), TempC(s.CpuTemp)],
                [LoadC(s.GpuLoad), TempC(s.GpuTemp)],
                [LoadC(s.RamLoad)],
            ],
            [
                [LoadC(s.DiskLoad), TempC(s.DiskTemp)],
                [_valueColor],
                [_valueColor],
            ],
        ];

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(BackAlpha, _backColor));

            float rowGap = Math.Max(1f, (h - 2 * _valueH) / 3f);
            DrawRowAt(g, 0, vals[0], cols[0], rowGap);
            DrawRowAt(g, 1, vals[1], cols[1], rowGap * 2 + _valueH);
        }
        Win32.UpdateLayered(Handle, bmp, Left, Top);
    }

    private void DrawRowAt(Graphics g, int row, string[][] vals, Color[][] cols, float y)
    {
        var sf = StringFormat.GenericTypographic;
        using var labelBrush = new SolidBrush(_labelColor);
        float labelY = y + (_valueH - _labelH) - 1;

        for (int gi = 0; gi < Rows[row].Length; gi++)
        {
            var def = Rows[row][gi];
            g.DrawString(def.Label, _labelFont, labelBrush, new PointF(_labelX[row][gi], labelY), sf);

            for (int c = 0; c < def.Templates.Length; c++)
            {
                using var brush = new SolidBrush(cols[gi][c]);
                g.DrawString(vals[gi][c], _valueFont, brush,
                    new PointF(_cellAnchor[row][gi][c], y), sf);
            }
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
