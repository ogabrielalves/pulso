using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Controls;

public sealed class ChartLine
{
    public required Series Data { get; init; }
    public string BrushKey { get; init; } = "SeriesCpu";
    public bool Fill { get; init; } = true;
    public string? Label { get; init; }
}

/// <summary>
/// Gráfico de linhas desenhado direto com StreamGeometry: sem bibliotecas,
/// sem objetos por ponto, re-renderiza só quando chamado (1x por leitura).
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public List<ChartLine> Lines { get; } = new();
    public object? SyncRoot { get; set; }
    public double? FixedMin { get; set; } = 0;
    public double? FixedMax { get; set; }
    public int Points { get; set; } = 120;
    public bool ShowAxis { get; set; } = true;
    public Unit Unit { get; set; } = Unit.None;
    /// <summary>Linha horizontal de referência (ex.: limite de temperatura).</summary>
    public double? Threshold { get; set; }
    public double LineWidth { get; set; } = 2;
    public byte FillAlpha { get; set; } = 120;
    /// <summary>Desenha linhas de grade mesmo sem os rótulos do eixo.</summary>
    public bool ShowGrid { get; set; }

    float[][] _buf = Array.Empty<float[]>();
    int[] _counts = Array.Empty<int>();

    public LineChart()
    {
        SnapsToDevicePixels = true;
        ClipToBounds = true;
    }

    public void Refresh()
    {
        if (!IsVisible) return;
        if (_buf.Length != Lines.Count) { _buf = new float[Lines.Count][]; _counts = new int[Lines.Count]; }
        void Copy()
        {
            for (int i = 0; i < Lines.Count; i++)
            {
                var s = Lines[i].Data;
                int n = Math.Min(s.Count, Points);
                if (_buf[i] == null || _buf[i].Length < Points) _buf[i] = new float[Points];
                int start = s.Count - n;
                for (int k = 0; k < n; k++) _buf[i][k] = s[start + k];
                _counts[i] = n;
            }
        }
        if (SyncRoot != null) lock (SyncRoot) Copy(); else Copy();
        InvalidateVisual();
    }

    static Brush Res(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // escala
        double max = double.MinValue, min = double.MaxValue;
        for (int i = 0; i < _buf.Length; i++)
            for (int k = 0; k < _counts[i]; k++)
            {
                float v = _buf[i][k];
                if (float.IsNaN(v)) continue;
                double dv = Unit == Unit.Temp ? Fmt.ToDisplayTemp(v) : v;
                if (dv > max) max = dv;
                if (dv < min) min = dv;
            }
        if (max == double.MinValue) { max = 1; min = 0; }
        double lo = FixedMin is double fmin ? (Unit == Unit.Temp && fmin != 0 ? Fmt.ToDisplayTemp(fmin) : fmin) : Math.Floor(min * 0.9);
        double hi = FixedMax is double fmax ? (Unit == Unit.Temp ? Fmt.ToDisplayTemp(fmax) : fmax) : Nice(Math.Max(max * 1.12, lo + 1));
        if (FixedMax is double && max > hi) hi = Nice(max * 1.05);
        if (hi <= lo) hi = lo + 1;

        double left = 0, right = ShowAxis ? 46 : 0, top = ShowAxis ? 6 : 2, bottom = ShowAxis ? 6 : 2;
        double pw = w - left - right, ph = h - top - bottom;
        var grid = Res("Grid", Brushes.DimGray);
        var gridPen = new Pen(grid, 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
        gridPen.Freeze();

        if (ShowGrid && !ShowAxis)
            for (int i = 1; i <= 3; i++)
            {
                double y = Math.Round(top + ph * i / 4.0) + 0.5;
                dc.DrawLine(gridPen, new Point(left, y), new Point(left + pw, y));
            }
        if (ShowAxis)
        {
            var muted = Res("TextFaint", Brushes.Gray);
            for (int i = 0; i <= 3; i++)
            {
                double y = top + ph * i / 3.0;
                dc.DrawLine(gridPen, new Point(left, Math.Round(y) + 0.5), new Point(left + pw, Math.Round(y) + 0.5));
                double val = hi - (hi - lo) * i / 3.0;
                var ft = new FormattedText(Label(val), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10.5, muted, dpi);
                dc.DrawText(ft, new Point(w - ft.Width - 2, Math.Clamp(y - ft.Height / 2, 0, h - ft.Height)));
            }
        }

        if (Threshold is double th)
        {
            double tv = Unit == Unit.Temp ? Fmt.ToDisplayTemp(th) : th;
            if (tv > lo && tv < hi)
            {
                double y = top + ph * (1 - (tv - lo) / (hi - lo));
                var p = new Pen(Res("Crit", Brushes.Red), 1) { DashStyle = DashStyles.Dash };
                p.Freeze();
                dc.DrawLine(p, new Point(left, y), new Point(left + pw, y));
            }
        }

        double step = pw / Math.Max(1, Points - 1);
        for (int i = 0; i < _buf.Length; i++)
        {
            int n = _counts[i];
            if (n < 2) continue;
            var brush = Res(Lines[i].BrushKey, Brushes.DeepSkyBlue);
            var pen = new Pen(brush, LineWidth) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var geo = new StreamGeometry();
            var area = Lines[i].Fill ? new StreamGeometry() : null;
            using (var g = geo.Open())
            using (var a = area?.Open())
            {
                bool started = false, areaStarted = false;
                double lastX = 0;
                for (int k = 0; k < n; k++)
                {
                    float v = _buf[i][k];
                    if (float.IsNaN(v)) { started = false; continue; }
                    double dv = Unit == Unit.Temp ? Fmt.ToDisplayTemp(v) : v;
                    double x = left + pw - (n - 1 - k) * step;
                    double y = top + ph * (1 - Math.Clamp((dv - lo) / (hi - lo), 0, 1));
                    if (!started)
                    {
                        g.BeginFigure(new Point(x, y), false, false);
                        started = true;
                    }
                    else g.LineTo(new Point(x, y), true, true);

                    if (a != null)
                    {
                        if (!areaStarted) { a.BeginFigure(new Point(x, top + ph), true, true); areaStarted = true; }
                        a.LineTo(new Point(x, y), false, false);
                    }
                    lastX = x;
                }
                if (a != null && areaStarted) a.LineTo(new Point(lastX, top + ph), false, false);
            }
            geo.Freeze();
            if (area != null)
            {
                area.Freeze();
                var fill = new LinearGradientBrush(Color.FromArgb(FillAlpha, ((SolidColorBrush)brush).Color.R, ((SolidColorBrush)brush).Color.G, ((SolidColorBrush)brush).Color.B),
                                                   Color.FromArgb(0, ((SolidColorBrush)brush).Color.R, ((SolidColorBrush)brush).Color.G, ((SolidColorBrush)brush).Color.B), 90);
                fill.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            dc.DrawGeometry(null, pen, geo);
        }
    }

    string Label(double v) => Unit switch
    {
        Unit.Percent => Fmt.N(v) + "%",
        Unit.Temp => Fmt.N(v) + "°",
        Unit.MHz => v >= 1000 ? Fmt.N(v / 1000, 1) + "G" : Fmt.N(v),
        Unit.BytesPerSec => v >= 1048576 ? Fmt.N(v / 1048576, v >= 10485760 ? 0 : 1) + "M" : Fmt.N(v / 1024) + "K",
        Unit.Watt => Fmt.N(v) + "W",
        Unit.Ms => Fmt.N(v) + "ms",
        Unit.MB => v >= 1024 ? Fmt.N(v / 1024, 1) + "G" : Fmt.N(v) + "M",
        _ => Fmt.N(v),
    };

    static double Nice(double v)
    {
        if (v <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double n = v / mag;
        double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
        return nice * mag;
    }
}
