using System.Windows;
using System.Windows.Controls;
using Pulso.Bench;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Hardware;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class StressPage : PageBase
{
    static readonly StressEngine Engine = new();

    readonly TextBlock _eyebrow = Label("");
    readonly Button _startBtn = new();
    readonly CheckBox _cCpu, _cGpu, _cRam;
    readonly TextBlock _elapsed = Num("00:00", 34, "Text", FontWeights.ExtraBold), _of = Text("", 13, null, "TextMuted"), _state = Text("", 12.5, FontWeights.Bold);
    readonly Bar _progress = new(6);
    readonly LineChart _temps, _clock;
    readonly Series _sCpuT = new(900), _sGpuT = new(900), _sClk = new(900);
    readonly TextBlock _tCpu, _tGpu, _tClk, _tThr, _tErr;
    readonly Border _reportHost = new();
    readonly TextBox _custom = new() { Width = 64, Text = "45", Visibility = Visibility.Collapsed };
    readonly TextBlock _cpuLim = Num("", 16), _gpuLim = Num("", 16);
    TimeSpan _dur = TimeSpan.FromMinutes(15);

    public StressPage()
    {
        _startBtn.Click += (_, _) => Toggle();

        _cCpu = Tick(Loc.T("Processador", "Processor"), true);
        _cGpu = Tick(Loc.T("Placa de vídeo", "Graphics card"), true);
        _cRam = Tick(Loc.T("Memória RAM", "Memory (RAM)"), false);

        var durs = new WrapPanel();
        foreach (var (label, min) in new[] { ("5 min", 5), ("15 min", 15), ("30 min", 30), ("1 h", 60), (Loc.T("Personalizado", "Custom"), -1) })
        {
            int m = min;
            var r = Segment(label, "stressdur", m == 15, () =>
            {
                _custom.Visibility = m < 0 ? Visibility.Visible : Visibility.Collapsed;
                if (m > 0) _dur = TimeSpan.FromMinutes(m); else ReadCustom();
                UpdateIdle();
            });
            r.Margin = new Thickness(0, 0, 6, 6);
            r.Padding = new Thickness(11, 6, 11, 6);
            r.FontSize = 12;
            durs.Children.Add(r);
        }
        _custom.TextChanged += (_, _) => { ReadCustom(); UpdateIdle(); };
        var customRow = new StackPanel { Orientation = Orientation.Horizontal };
        customRow.Children.Add(_custom);
        var mins = Label(Loc.T("minutos", "minutes"));
        mins.VerticalAlignment = VerticalAlignment.Center; mins.Margin = new Thickness(8, 0, 0, 0);
        customRow.Children.Add(mins);
        customRow.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = _custom });

        var limHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        limHead.Children.Add(Label(Loc.T("Limite de segurança", "Safety limit")));
        limHead.Children.Add(Help(Loc.T("Se qualquer temperatura passar deste valor, o teste para sozinho para proteger o hardware.", "If any temperature exceeds this value, the test stops by itself to protect the hardware.")));

        var left = Card(new Stack(10,
            Label(Loc.T("Componentes", "Components")), _cCpu, _cGpu, _cRam,
            new Border { Height = 8 }, Label(Loc.T("Duração", "Duration")), durs, customRow,
            limHead,
            Stepper("CPU", _cpuLim, d => { Settings.Current.StressCpuLimit = Math.Clamp(Settings.Current.StressCpuLimit + d, 60, 105); Settings.Save(); UpdateIdle(); }),
            Stepper("GPU", _gpuLim, d => { Settings.Current.StressGpuLimit = Math.Clamp(Settings.Current.StressGpuLimit + d, 60, 105); Settings.Save(); UpdateIdle(); })), 18, 18);
        left.VerticalAlignment = VerticalAlignment.Top;

        // gráfico: temperaturas e clock sobrepostos (escalas diferentes)
        _temps = new LineChart { Height = 180, ShowAxis = false, ShowGrid = true, FixedMin = 30, FixedMax = 100, Points = 300, Unit = Unit.Temp };
        _temps.Lines.Add(new ChartLine { Data = _sCpuT, BrushKey = "SeriesAmber" });
        _temps.Lines.Add(new ChartLine { Data = _sGpuT, BrushKey = "SeriesWhite", Fill = false });
        _clock = new LineChart { Height = 180, ShowAxis = false, FixedMin = null, Points = 300, Unit = Unit.MHz };
        _clock.Lines.Add(new ChartLine { Data = _sClk, BrushKey = "SeriesLime", Fill = false });
        var chart = new Grid();
        chart.Children.Add(_temps);
        chart.Children.Add(_clock);

        var top = new DockPanel();
        DockPanel.SetDock(_state, Dock.Right);
        _state.VerticalAlignment = VerticalAlignment.Bottom;
        top.Children.Add(_state);
        var el = new StackPanel { Orientation = Orientation.Horizontal };
        el.Children.Add(_elapsed);
        _of.VerticalAlignment = VerticalAlignment.Bottom; _of.Margin = new Thickness(10, 0, 0, 8);
        el.Children.Add(_of);
        top.Children.Add(el);

        var legend = new WrapPanel();
        legend.Children.Add(Legend(Loc.T("Temperatura CPU", "CPU temperature"), "SeriesAmber", true));
        legend.Children.Add(Legend(Loc.T("Clock CPU", "CPU clock"), "SeriesLime", true));
        legend.Children.Add(Legend(Loc.T("Temperatura GPU", "GPU temperature"), "SeriesWhite", true));

        var (a, tCpu) = Stat(Loc.T("Temp. máx. CPU", "Max CPU temp"), "—", 15, "Warn");
        var (b, tGpu) = Stat(Loc.T("Temp. máx. GPU", "Max GPU temp"), "—", 15, "Warn");
        var (c, tClk) = Stat(Loc.T("Clock médio CPU", "Avg CPU clock"), "—", 15);
        var (d, tThr) = Stat("Throttling", "—", 15);
        var (e, tErr) = Stat(Loc.T("Erros", "Errors"), "—", 15);
        _tCpu = tCpu; _tGpu = tGpu; _tClk = tClk; _tThr = tThr; _tErr = tErr;
        d.ToolTip = Loc.T("Thermal throttling: o processador ou a placa de vídeo reduz o clock sozinho para não esquentar demais. Indica refrigeração insuficiente.",
                          "Thermal throttling: the CPU or GPU lowers its own clock to avoid overheating. Indicates insufficient cooling.");
        e.ToolTip = Loc.T("Cálculos ou dados de memória que deram resultado errado. Qualquer erro indica instabilidade (overclock, undervolt, temperatura ou peça com defeito).",
                          "Calculations or memory data that came out wrong. Any error indicates instability (overclock, undervolt, temperature or a faulty part).");

        var live = Card(new Stack(14, top, _progress, legend, chart, Cols(10, new[] { 1.0, 1.0, 1.0, 1.0, 1.0 }, a, b, c, d, e)));
        var right = new Stack(18, live, _reportHost);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        right.Margin = new Thickness(18, 0, 0, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        Content = Scroll(new Stack(18, Header(_eyebrow, Loc.T("Teste de estresse", "Stress test"), _startBtn), grid));

        Action tick = () => Dispatcher.BeginInvoke(OnTick);
        Action<StressReport> fin = r => Dispatcher.BeginInvoke(() => { UpdateIdle(); RenderReport(r); });
        Loaded += (_, _) =>
        {
            Engine.Tick += tick;
            Engine.Finished += fin;
            UpdateIdle();
            if (!Engine.Running) RenderReport(StressEngine.LastReport());
        };
        Unloaded += (_, _) => { Engine.Tick -= tick; Engine.Finished -= fin; };
    }

    static CheckBox Tick(string text, bool on)
    {
        var c = new CheckBox { Content = text, IsChecked = on };
        c.SetResourceReference(StyleProperty, "Tick");
        return c;
    }

    void ReadCustom()
    {
        if (int.TryParse(_custom.Text, out int m) && m > 0) _dur = TimeSpan.FromMinutes(Math.Min(m, 24 * 60));
    }

    static Border Stepper(string label, TextBlock value, Action<int> change)
    {
        var minus = Ui.Button("−", () => change(-1), "StepButton");
        var plus = Ui.Button("+", () => change(+1), "StepButton");
        var lbl = Text(label, 12, FontWeights.Bold, "TextMuted");
        lbl.VerticalAlignment = VerticalAlignment.Center;
        lbl.Width = 34;
        var g = new DockPanel();
        DockPanel.SetDock(lbl, Dock.Left);
        DockPanel.SetDock(minus, Dock.Left);
        DockPanel.SetDock(plus, Dock.Right);
        g.Children.Add(lbl); g.Children.Add(minus); g.Children.Add(plus);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(value);
        return Tile(g, 12, 8);
    }

    void Toggle()
    {
        if (Engine.Running) { Engine.Stop(Loc.T("Parado pelo usuário", "Stopped by user")); return; }
        Engine.Cpu = _cCpu.IsChecked == true;
        Engine.Gpu = _cGpu.IsChecked == true;
        Engine.Ram = _cRam.IsChecked == true;
        if (!(Engine.Cpu || Engine.Gpu || Engine.Ram)) return;
        Engine.Duration = _dur;
        Engine.CpuLimit = Settings.Current.StressCpuLimit;
        Engine.GpuLimit = Settings.Current.StressGpuLimit;
        _sCpuT.Clear(); _sGpuT.Clear(); _sClk.Clear();
        Engine.Start();
        UpdateIdle();
    }

    void UpdateIdle()
    {
        var cfg = Settings.Current;
        _cpuLim.Text = Fmt.Value(cfg.StressCpuLimit, Unit.Temp);
        _gpuLim.Text = Fmt.Value(cfg.StressGpuLimit, Unit.Temp);
        _eyebrow.Text = Loc.T($"Parada automática se a CPU passar de {Fmt.Value(cfg.StressCpuLimit, Unit.Temp)} ou a GPU de {Fmt.Value(cfg.StressGpuLimit, Unit.Temp)}",
                              $"Automatic stop if the CPU exceeds {Fmt.Value(cfg.StressCpuLimit, Unit.Temp)} or the GPU {Fmt.Value(cfg.StressGpuLimit, Unit.Temp)}");
        bool on = Engine.Running;
        _startBtn.Content = on ? Loc.T("Parar agora", "Stop now") : Loc.T("Iniciar teste", "Start test");
        _startBtn.SetResourceReference(StyleProperty, on ? "DangerPill" : "PrimaryPill");
        foreach (var c in new[] { _cCpu, _cGpu, _cRam }) c.IsEnabled = !on;
        if (!on)
        {
            _elapsed.Text = Fmt.Duration(Engine.Report.Elapsed > TimeSpan.Zero ? Engine.Elapsed : TimeSpan.Zero);
            _of.Text = Loc.T("de ", "of ") + DurText(_dur);
            _state.Text = Loc.T("Parado", "Stopped");
            _state.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            if (!SensorHub.Instance.HasCpuTemp)
            {
                _state.Text = Loc.T("Sem sensor de temperatura da CPU — a parada de segurança da CPU não funciona", "No CPU temperature sensor — CPU safety stop unavailable");
                _state.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            }
        }
    }

    static string DurText(TimeSpan t) => t.TotalMinutes >= 60 && t.TotalMinutes % 60 == 0 ? $"{t.TotalHours:0} h" : $"{t.TotalMinutes:0} min";

    void OnTick()
    {
        if (!Engine.Running) return;
        var r = Engine.Report;
        _elapsed.Text = Fmt.Duration(Engine.Elapsed);
        _of.Text = Loc.T("de ", "of ") + DurText(Engine.Duration) + (Engine.Gpu && Engine.GpuFps > 0 ? $" · GPU {Fmt.N(Engine.GpuFps)} FPS" : "");
        _progress.Value = Engine.Elapsed.TotalSeconds / Engine.Duration.TotalSeconds * 100;
        bool bad = r.Errors > 0;
        _state.Text = bad ? Loc.T("Em execução · erros detectados", "Running · errors detected") : r.Throttling ? Loc.T("Em execução · throttling detectado", "Running · throttling detected") : Loc.T("Em execução · estável até agora", "Running · stable so far");
        _state.SetResourceReference(TextBlock.ForegroundProperty, bad ? "Crit" : r.Throttling ? "Warn" : "Good");
        if (SensorHub.Instance.Last is Snapshot s)
        {
            _sCpuT.Add(s.Cpu.Temp);
            _sGpuT.Add(s.Gpu?.Temp);
            _sClk.Add(s.Cpu.ClockAvg);
        }
        if (Shown) { _temps.Refresh(); _clock.Refresh(); }
        FillStats(r);
    }

    void FillStats(StressReport r)
    {
        _tCpu.Text = Fmt.Value(r.MaxCpuTemp, Unit.Temp);
        _tGpu.Text = Fmt.Value(r.MaxGpuTemp, Unit.Temp);
        _tClk.Text = r.AvgCpuClock is double c ? Fmt.N(c / 1000, 2) + " GHz" : "—";
        _tThr.Text = r.Throttling ? Loc.T("Detectado", "Detected") : Loc.T("Não detectado", "Not detected");
        _tThr.SetResourceReference(TextBlock.ForegroundProperty, r.Throttling ? "Warn" : "Good");
        _tThr.ToolTip = r.ThrottleDetail;
        _tErr.Text = Fmt.N(r.Errors);
        _tErr.SetResourceReference(TextBlock.ForegroundProperty, r.Errors > 0 ? "Crit" : "Good");
    }

    void RenderReport(StressReport? r)
    {
        if (r == null) { _reportHost.Child = null; return; }
        FillStats(r);
        bool ok = r.Stable && !r.Throttling;
        string key = r.SafetyStop || r.Errors > 0 ? "Crit" : r.Throttling ? "Warn" : "Good";
        var inner = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Child = new TextBlock { Text = ok ? "✓" : "!", FontSize = 22, FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Res(TextBlock.ForegroundProperty, "AccentText") }.Res(Border.BackgroundProperty, key == "Good" ? "AccentFill" : key);
        var ring = new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(48), BorderThickness = new Thickness(10), Child = inner }.Res(Border.BackgroundProperty, "LogoRing");
        ring.SetResourceReference(Border.BorderBrushProperty, "LogoRing2");

        var head = new DockPanel();
        var date = Label(r.Date.ToString("g", Fmt.Culture));
        DockPanel.SetDock(date, Dock.Right);
        head.Children.Add(date);
        head.Children.Add(Title(Loc.T($"Último relatório · {r.Components} · {Fmt.Duration(r.Elapsed)}", $"Last report · {r.Components} · {Fmt.Duration(r.Elapsed)}")));

        var details = new WrapPanel();
        void D(string k, string v)
        {
            var t = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 0, 18, 6) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text3");
            t.Inlines.Add(k + " ");
            var b = new System.Windows.Documents.Run(v) { FontWeight = FontWeights.Bold };
            b.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Text");
            t.Inlines.Add(b);
            details.Children.Add(t);
        }
        D(Loc.T("CPU máx", "CPU max"), Fmt.Value(r.MaxCpuTemp, Unit.Temp));
        D(Loc.T("GPU máx", "GPU max"), Fmt.Value(r.MaxGpuTemp, Unit.Temp));
        D(Loc.T("Clock médio", "Avg clock"), r.AvgCpuClock is double c ? Fmt.N(c / 1000, 2) + " GHz" : "—");
        if (r.AvgGpuClock is double gc) D(Loc.T("Clock GPU", "GPU clock"), Fmt.N(gc) + " MHz");
        D("Throttling", r.Throttling ? Loc.T("sim", "yes") : Loc.T("não", "no"));
        D(Loc.T("Erros", "Errors"), Fmt.N(r.Errors));

        var verdict = Text(r.Verdict, 20, FontWeights.ExtraBold, key);
        var info = new Stack(8, head, verdict, details);
        string? explain = r.StopReason ?? r.ThrottleDetail;
        if (r.Throttling && r.ThrottleDetail != null && r.StopReason != null) explain = r.StopReason + " · " + r.ThrottleDetail;
        if (explain != null) info.Children.Add(Text(explain, 12.5, null, "TextMuted", wrap: true));
        if (r.Errors > 0) info.Children.Add(Text(Loc.T("Erros indicam instabilidade: desfaça overclock/undervolt, verifique temperaturas e teste a RAM com o MemTest86.", "Errors indicate instability: undo overclocks/undervolts, check temperatures and test RAM with MemTest86."), 12.5, null, "CritText", wrap: true));

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        info.Margin = new Thickness(20, 0, 0, 0);
        info.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(info, 1);
        g.Children.Add(ring);
        g.Children.Add(info);
        _reportHost.Child = Card(g, 20, 18);
    }

    protected override void OnShown()
    {
        _temps.Refresh();
        _clock.Refresh();
    }
}
