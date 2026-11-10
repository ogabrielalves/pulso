using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pulso.Bench;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Hardware;
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class BottleneckPage : PageBase
{
    readonly TextBlock _eyebrow = Label("");
    readonly TextBlock _title = Text("", 34, FontWeights.ExtraBold), _text = Text("", 14, null, "Text2", wrap: true), _cpuPct = new() { FontSize = 12.5 };
    readonly Canvas _scale = new() { Height = 12, ClipToBounds = false };
    readonly Border _track = new() { Height = 12, CornerRadius = new CornerRadius(6) };
    readonly Border _fill = new() { Height = 12 };
    readonly Ellipse _knob = new() { Width = 22, Height = 22, StrokeThickness = 4 };
    readonly Rectangle _center = new() { Width = 1, Height = 20 };
    readonly (TextBlock v, Bar bar, TextBlock s)[] _meters = new (TextBlock, Bar, TextBlock)[4];
    readonly Stack _factors = new(0), _recs = new(10);
    readonly RadioButton _modeGame, _modeTest;
    readonly Button _runTest;
    readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    CancellationTokenSource? _testCts;
    double _balance;
    DateTime _since = DateTime.Now;

    bool GameMode => _modeGame.IsChecked == true;

    public BottleneckPage()
    {
        _modeGame = Segment(Loc.T("Durante o jogo", "While gaming"), "bnmode", true, () => { BottleneckAnalyzer.Reset(); _since = DateTime.Now; _runTest!.Visibility = Visibility.Collapsed; Refresh(); });
        _modeTest = Segment(Loc.T("Durante um teste", "During a test"), "bnmode", false, () => { _runTest!.Visibility = Visibility.Visible; Refresh(); });
        foreach (var m in new[] { _modeGame, _modeTest }) { m.Padding = new Thickness(14, 9, 14, 9); m.FontWeight = FontWeights.Bold; }
        _runTest = Ui.Button(Loc.T("Rodar teste 3D (30 s)", "Run 3D test (30 s)"), RunTest, "Primary");
        _runTest.Padding = new Thickness(16, 9, 16, 9);
        _runTest.Visibility = Visibility.Collapsed;

        // escala CPU ← → GPU
        _track.SetResourceReference(Border.BackgroundProperty, "Divider");
        _fill.SetResourceReference(Border.BackgroundProperty, "AccentFill");
        _knob.SetResourceReference(Shape.FillProperty, "AccentFill");
        _knob.SetResourceReference(Shape.StrokeProperty, "Card");
        _center.SetResourceReference(Shape.FillProperty, "BorderStrong");
        _scale.Children.Add(_track); _scale.Children.Add(_fill); _scale.Children.Add(_center); _scale.Children.Add(_knob);
        _scale.SizeChanged += (_, _) => LayoutScale();
        var labels = new Grid();
        for (int i = 0; i < 3; i++) labels.ColumnDefinitions.Add(new ColumnDefinition());
        var l1 = Label(Loc.T("Limitado pela CPU", "CPU-bound"));
        var l2 = Label(Loc.T("Equilibrado", "Balanced")); l2.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(l2, 1);
        var l3 = Label(Loc.T("Limitado pela GPU", "GPU-bound")); l3.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(l3, 2);
        labels.Children.Add(l1); labels.Children.Add(l2); labels.Children.Add(l3);
        _cpuPct.SetResourceReference(TextBlock.ForegroundProperty, "Text3");
        _text.LineHeight = 21.7;
        var leftCol = new Stack(14, Label(Loc.T("Resultado", "Result")), _title, _text, new Stack(8, labels, _scale, _cpuPct) { Margin = new Thickness(0, 10, 0, 0) });

        var meterTiles = new List<UIElement>();
        string[] names = { Loc.T("Uso da GPU", "GPU usage"), Loc.T("Uso da CPU", "CPU usage"), "RAM", "VRAM" };
        for (int i = 0; i < 4; i++)
        {
            var v = Num("—", 26, "Text", FontWeights.ExtraBold);
            var bar = new Bar(6, i == 0 ? "AccentFill" : "SeriesWhite", "Well");
            var s = Text("", 11.5, null, "TextMuted");
            _meters[i] = (v, bar, s);
            meterTiles.Add(Tile(new Stack(8, Label(names[i]), v, bar, s), 14, 14, 14));
        }
        var rightCol = Wrap(2, 12, meterTiles);
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rightCol.Margin = new Thickness(32, 0, 0, 0);
        Grid.SetColumn(rightCol, 1);
        main.Children.Add(leftCol); main.Children.Add(rightCol);
        var mainCard = Card(main, 24, 24);

        var factorsCard = Card(new Stack(0, WithPad(Title(Loc.T("Outros fatores verificados", "Other factors checked")), 10), _factors), 20, 18);
        var recsCard = Card(new Stack(10, Title(Loc.T("Recomendações", "Recommendations")), _recs), 20, 18);
        var header = Header(_eyebrow, Loc.T("Detector de gargalo", "Bottleneck detector"), _runTest, _modeGame, _modeTest);
        Content = Scroll(new Stack(18, header, mainCard, Cols(18, new[] { 1.0, 1.0 }, factorsCard, recsCard)));
        _timer.Tick += (_, _) => Refresh();
    }

    static FrameworkElement WithPad(FrameworkElement e, double bottom) { e.Margin = new Thickness(0, 0, 0, bottom); return e; }

    protected override void OnShown()
    {
        BottleneckAnalyzer.Begin();
        _timer.Start();
        Refresh();
    }

    protected override void OnHidden()
    {
        _timer.Stop();
        if (_testCts == null) BottleneckAnalyzer.End();
    }

    async void RunTest()
    {
        if (_testCts != null) { _testCts.Cancel(); return; }
        _testCts = new CancellationTokenSource();
        BottleneckAnalyzer.Begin();
        BottleneckAnalyzer.Reset();
        _runTest.Content = Loc.T("Parar teste", "Stop test");
        try { await GpuRenderer.BenchmarkAsync(1920, 1080, TimeSpan.FromSeconds(30), null, _testCts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Paths.LogError("Bottleneck.Test", ex); }
        _testCts = null;
        _runTest.Content = Loc.T("Rodar teste 3D (30 s)", "Run 3D test (30 s)");
        BottleneckAnalyzer.End();
        Refresh();
    }

    void Refresh()
    {
        var r = BottleneckAnalyzer.Analyze(GameMode, GameMode ? 30 : 25);
        var mins = (DateTime.Now - _since).TotalMinutes;
        _eyebrow.Text = GameMode
            ? (r.Process != null ? Loc.T($"Analisando {r.Process}.exe · {(r.Fps is double f && !double.IsNaN(f) ? Fmt.N(f) + " FPS · " : "")}há {Math.Max(1, (int)mins)} min", $"Analyzing {r.Process}.exe · {(r.Fps is double f2 && !double.IsNaN(f2) ? Fmt.N(f2) + " FPS · " : "")}for {Math.Max(1, (int)mins)} min")
                                 : Loc.T("Nenhum jogo detectado em primeiro plano", "No game detected in the foreground"))
            : Loc.T("Usando o teste 3D do Pulso como carga", "Using Pulso's 3D test as the load");
        _title.Text = r.Title;
        _text.Text = r.Text;
        _balance = r.Enough ? r.Balance : 0;
        _knob.Visibility = _fill.Visibility = r.Enough ? Visibility.Visible : Visibility.Collapsed;
        LayoutScale();
        _cpuPct.Inlines.Clear();
        if (r.Enough)
        {
            _cpuPct.Inlines.Add(Loc.T("Gargalo estimado de CPU: ", "Estimated CPU bottleneck: "));
            var b = new Avalonia.Controls.Documents.Run(Fmt.N(r.CpuBottleneck) + "%") { FontFamily = MonoFont, FontWeight = FontWeights.Bold };
            b.SetResourceReference(Avalonia.Controls.Documents.TextElement.ForegroundProperty, "Text");
            _cpuPct.Inlines.Add(b);
        }
        _cpuPct.ToolTip = Loc.T("Quanto de desempenho da placa de vídeo está sobrando porque o processador não acompanha. 0–10% é normal.", "How much GPU performance is left unused because the processor can't keep up. 0–10% is normal.");

        var s = SensorHub.Instance.Last;
        double gpu = r.Enough ? r.Gpu : s?.Gpu?.Load ?? 0, cpu = r.Enough ? r.Cpu : s?.Cpu.Load ?? 0, top = r.Enough ? r.Top : s?.Cpu.MaxThreadLoad ?? 0, ram = r.Enough ? r.Ram : s?.Mem.Load ?? 0;
        double vram = r.Enough && !double.IsNaN(r.Vram) ? r.Vram : s?.Gpu?.VramPct ?? double.NaN;
        Meter(0, gpu, gpu >= 90 ? Loc.T("Perto do máximo", "Near the maximum") : gpu >= 60 ? Loc.T("Carga média", "Medium load") : Loc.T("Com folga", "Plenty of headroom"));
        Meter(1, cpu, Loc.T($"Núcleo mais usado: {Fmt.N(top)}%", $"Busiest core: {Fmt.N(top)}%"));
        Meter(2, ram, $"{Fmt.N(r.RamUsedGB, 1)} {Loc.T("de", "of")} {Fmt.N(Math.Round(r.RamTotalGB))} GB");
        Meter(3, vram, double.IsNaN(r.VramTotalGB) ? Fmt.NA : $"{Fmt.N(r.VramUsedGB, 1)} {Loc.T("de", "of")} {Fmt.N(Math.Round(r.VramTotalGB))} GB");

        _factors.Children.Clear();
        foreach (var (k, v, h) in r.Factors)
        {
            string key = h == Health.Good ? "Good" : h == Health.Unknown ? "TextFaint" : HealthKey(h);
            var val = new StackPanel { Orientation = Orientation.Horizontal };
            val.Children.Add(new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }.Res(Shape.FillProperty, key));
            val.Children.Add(Text(v, 12.5, FontWeights.Bold, key));
            var d = new DockPanel();
            DockPanel.SetDock(val, Dock.Right);
            d.Children.Add(val);
            d.Children.Add(Text(k, 13, null, "Text2"));
            _factors.Children.Add(new Border { Child = d, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 10) }.Res(Border.BorderBrushProperty, "Divider"));
        }
        if (r.Factors.Count == 0) _factors.Children.Add(Text(Loc.T("Os fatores aparecem quando houver dados suficientes.", "Factors appear once there's enough data."), 12.5, null, "TextMuted"));

        _recs.Children.Clear();
        int n = 1;
        foreach (var (t, d) in r.Recs.Take(4))
        {
            var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = (n++).ToString(), FontSize = 12, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Res(TextBlock.ForegroundProperty, "AccentText") }.Res(Border.BackgroundProperty, "AccentFill");
            var body = new Stack(3, Text(t, 13.5, FontWeights.Bold, wrap: true), Text(d, 12.5, null, "Text3", wrap: true)) { Margin = new Thickness(12, 0, 0, 0) };
            var dock = new DockPanel();
            DockPanel.SetDock(badge, Dock.Left);
            dock.Children.Add(badge); dock.Children.Add(body);
            _recs.Children.Add(Tile(dock, 12, 12));
        }
        if (r.Recs.Count == 0) _recs.Children.Add(Text(Loc.T("As recomendações aparecem junto com o resultado.", "Recommendations appear with the result."), 12.5, null, "TextMuted"));
    }

    void Meter(int i, double v, string sub)
    {
        var (t, bar, s) = _meters[i];
        t.Text = double.IsNaN(v) ? "—" : Fmt.N(v) + "%";
        bar.Value = double.IsNaN(v) ? 0 : v;
        s.Text = sub;
    }

    void LayoutScale()
    {
        double w = _scale.Bounds.Width;
        if (w <= 0) return;
        _track.Width = w;
        double center = w / 2;
        double pos = center + _balance / 100 * center;
        Canvas.SetLeft(_center, center); Canvas.SetTop(_center, -4);
        double left = Math.Min(center, pos), width = Math.Abs(pos - center);
        Canvas.SetLeft(_fill, left);
        _fill.Width = width;
        _fill.CornerRadius = _balance >= 0 ? new CornerRadius(0, 6, 6, 0) : new CornerRadius(6, 0, 0, 6);
        Canvas.SetLeft(_knob, pos - 11); Canvas.SetTop(_knob, -5);
    }
}
