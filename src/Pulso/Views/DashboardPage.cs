using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Hardware;
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class DashboardPage : PageBase
{
    readonly MainWindow _main;
    readonly TextBlock _eyebrow = Label("");
    readonly Grid _strip = new();
    readonly Stack _alerts = new(10);
    readonly UnitCard _cpu, _gpu;
    readonly TextBlock _ramUsed = Num("—", 30), _ramOf = Text("", 13, null, "TextMuted"), _ramCache = Text("", 12, null, "Text3");
    readonly Grid _ramBar = new() { Height = 12 };
    readonly ColumnDefinition _cUsed = new(), _cCache = new(), _cFree = new();
    readonly Border _ramWarn = new();
    readonly Stack _disks = new(16);
    readonly TextBlock _diskActLabel = Text("", 12, null, "TextMuted"), _diskAct = Num("—", 12, "Text", FontWeights.Normal);
    readonly Stack _heavyCpu = new(0), _heavyRam = new(0);
    int _sampling;
    SystemSummary.Data? _sum;

    public DashboardPage(MainWindow main)
    {
        _main = main;
        _cpu = new UnitCard(Loc.T("Processador", "Processor"), "cpu.load");
        _gpu = new UnitCard(Loc.T("Placa de vídeo", "Graphics card"), "gpu0.load");

        var export = Ui.Button(Loc.T("Exportar relatório", "Export report"), () => ReportExporter.ShowMenu(this, null));
        var header = Header(_eyebrow, Loc.T("Painel", "Dashboard"), export);

        // faixa de hardware
        var stripCard = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(6, 18, 6, 18), Child = _strip }.Res(Border.BackgroundProperty, "Card");
        foreach (var w in new[] { 1.3, 1.3, 0.9, 1.2, 1.0 }) _strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });

        // RAM
        _ramBar.ColumnDefinitions.Add(_cUsed); _ramBar.ColumnDefinitions.Add(_cCache); _ramBar.ColumnDefinitions.Add(_cFree);
        var track = new Border { CornerRadius = new CornerRadius(6) }.Res(Border.BackgroundProperty, "Divider");
        Grid.SetColumnSpan(track, 3);
        var used = new Border { CornerRadius = new CornerRadius(6, 0, 0, 6) }.Res(Border.BackgroundProperty, "AccentFill");
        var cache = new Border { Margin = new Thickness(2, 0, 0, 0) }.Res(Border.BackgroundProperty, "AccentDim");
        Grid.SetColumn(cache, 1);
        _ramBar.Children.Add(track); _ramBar.Children.Add(used); _ramBar.Children.Add(cache);
        var ramLegend = new WrapPanel();
        ramLegend.Children.Add(Legend(Loc.T("Em uso", "In use"), "AccentFill"));
        var lc = Legend("", "AccentDim"); lc.Children.RemoveAt(1); lc.Children.Add(_ramCache); ramLegend.Children.Add(lc);
        ramLegend.Children.Add(Legend(Loc.T("Livre", "Free"), "Divider"));
        _ramWarn.Visibility = Visibility.Collapsed;
        var ramNum = new StackPanel { Orientation = Orientation.Horizontal };
        ramNum.Children.Add(_ramUsed);
        _ramOf.VerticalAlignment = VerticalAlignment.Bottom; _ramOf.Margin = new Thickness(6, 0, 0, 5);
        ramNum.Children.Add(_ramOf);
        var ramStack = new DockPanel();
        var ramTop = new Stack(14, Label(Loc.T("Memória RAM", "Memory (RAM)")), ramNum, _ramBar, ramLegend);
        DockPanel.SetDock(ramTop, Dock.Top);
        ramStack.Children.Add(ramTop);
        _ramWarn.VerticalAlignment = VerticalAlignment.Bottom;
        _ramWarn.Margin = new Thickness(0, 14, 0, 0);
        ramStack.Children.Add(_ramWarn);
        var ramCard = Card(ramStack);

        // armazenamento
        var diskFoot = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
        DockPanel.SetDock(_diskAct, Dock.Right);
        diskFoot.Children.Add(_diskAct);
        diskFoot.Children.Add(_diskActLabel);
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Child = diskFoot }.Res(Border.BorderBrushProperty, "Divider");
        var diskDock = new DockPanel();
        DockPanel.SetDock(foot, Dock.Bottom);
        diskDock.Children.Add(foot);
        diskDock.Children.Add(new Stack(16, Label(Loc.T("Armazenamento", "Storage")), _disks) { Margin = new Thickness(0, 0, 0, 14) });
        var diskCard = Card(diskDock);

        // testes rápidos
        var quick = new Stack(10, Text(Loc.T("Testes rápidos", "Quick tests"), 18, FontWeights.Bold));
        quick.Children.Add(QuickRow(Loc.T("Benchmark de CPU", "CPU benchmark"), Loc.T("Single e multi-core · ~1 min", "Single and multi-core · ~1 min"), "bench"));
        quick.Children.Add(QuickRow(Loc.T("Velocidade da internet", "Internet speed"), Loc.T("Download, upload e ping", "Download, upload and ping"), "internet"));
        quick.Children.Add(QuickRow(Loc.T("Detector de gargalo", "Bottleneck detector"), Loc.T("Analisa o jogo aberto", "Analyzes the running game"), "gargalo"));
        var stressBtn = Ui.Button(Loc.T("Iniciar teste de estresse", "Start stress test"), () => _main.Navigate("stress"), "Primary");
        var quickDock = new DockPanel();
        DockPanel.SetDock(stressBtn, Dock.Bottom);
        stressBtn.Margin = new Thickness(0, 10, 0, 0);
        quickDock.Children.Add(stressBtn);
        quickDock.Children.Add(quick);
        var quickCard = Card(quickDock);

        // o que está pesando agora
        var all = Ui.Button(Loc.T("Ver tudo →", "See all →"), () => _main.Navigate("processos"), "LinkButton");
        var heavyHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(all, Dock.Right);
        all.VerticalAlignment = VerticalAlignment.Center;
        heavyHead.Children.Add(all);
        heavyHead.Children.Add(Text(Loc.T("O que está pesando agora", "What's heavy right now"), 18, FontWeights.Bold));
        var heavyCard = Card(new Stack(8, heavyHead,
            Cols(28, new[] { 1.0, 1.0 }, new Stack(4, Label(Loc.T("Mais processador", "Most CPU")), _heavyCpu), new Stack(4, Label(Loc.T("Mais memória", "Most memory")), _heavyRam))));

        var content = new Stack(18,
            header,
            _alerts,
            stripCard,
            Cols(18, new[] { 1.0, 1.0 }, _cpu.Root, _gpu.Root),
            heavyCard,
            Cols(18, new[] { 1.0, 1.15, 0.95 }, ramCard, diskCard, quickCard));
        Content = Scroll(content);
        _ = LoadAsync();
    }

    Button QuickRow(string title, string sub, string target)
    {
        var dock = new DockPanel();
        var arrow = Text("→", 16, null, "Accent");
        arrow.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(arrow, Dock.Right);
        dock.Children.Add(arrow);
        dock.Children.Add(new Stack(2, Text(title, 13, FontWeights.Bold), Text(sub, 11.5, null, "TextMuted")));
        var b = new Button { Content = dock };
        b.Classes.Add("row");
        b.Click += (_, _) => _main.Navigate(target);
        return b;
    }

    async Task LoadAsync()
    {
        _sum = await SystemSummary.GetAsync();
        var s = _sum;
        _strip.Children.Clear();
        var gpu = s.Gpus.FirstOrDefault();
        var mem = s.Memory;
        double memGb = mem.Modules.Sum(m => m.SizeGB);
        var items = new (string k, string v, string sub)[]
        {
            (Loc.T("Processador", "Processor"), ShortCpu(s.Cpu.Brand), Loc.T($"{s.Cpu.Cores} núcleos · {s.Cpu.Threads} threads", $"{s.Cpu.Cores} cores · {s.Cpu.Threads} threads")),
            (Loc.T("Placa de vídeo", "Graphics card"), gpu != null ? ShortGpu(gpu.Name) : Fmt.NA, gpu == null ? "" : gpu.Integrated ? Loc.T("Integrada", "Integrated") : gpu.DedicatedMB > 0 ? $"{Fmt.N(Math.Round(gpu.DedicatedMB / 1024))} GB {gpu.MemType}".Trim() : ""),
            (Loc.T("Memória", "Memory"), memGb > 0 ? $"{Fmt.N(memGb)} GB {mem.Modules.FirstOrDefault()?.Type}" : SensorHub.Instance.Last?.Mem.TotalGB is double tg ? $"{Fmt.N(Math.Round(tg))} GB" : Fmt.NA, mem.ChannelMode.Split(" — ")[0]),
            (Loc.T("Discos", "Drives"), s.Disks.Count > 0 ? string.Join(" · ", s.Disks.Take(3).Select(d => ShortDisk(d))) : Fmt.NA, string.Join(" + ", s.Disks.Select(d => d.Kind).Where(k => k != "").Distinct())),
            (Loc.T("Sistema", "System"), s.OsName.Replace("Microsoft ", ""), $"{s.OsVersion} · build {s.OsBuild}".Trim(' ', '·')),
        };
        for (int i = 0; i < items.Length; i++)
        {
            var (k, v, sub) = items[i];
            var val = Text(v, 14, FontWeights.Bold, wrap: true);
            var sp = new Stack(5, Caps(k), val, Text(sub, 12, null, "TextMuted")) { Margin = new Thickness(16, 0, 16, 0) };
            var b = new Border { Child = sp, BorderThickness = new Thickness(0, 0, i < items.Length - 1 ? 1 : 0, 0) }.Res(Border.BorderBrushProperty, "Divider");
            Grid.SetColumn(b, i);
            _strip.Children.Add(b);
        }
        _cpu.Model.Text = s.Cpu.Brand;
        _gpu.Model.Text = gpu?.Name ?? Fmt.NA;

        if (mem.Warning != null)
        {
            var banner = Banner(mem.Warning, Health.Warning, bigIcon: false, onClick: () => _main.Navigate("sistema/ram"));
            banner.Padding = new Thickness(12);
            banner.CornerRadius = new CornerRadius(12);
            ((TextBlock)((DockPanel)banner.Child).Children[1]).FontSize = 12.5;
            _ramWarn.Child = banner;
            _ramWarn.Visibility = Visibility.Visible;
        }
        RenderDisks();
        if (SensorHub.Instance.Last is Snapshot last) OnSnapshot(last);
    }

    static string ShortCpu(string b) => System.Text.RegularExpressions.Regex.Replace(b, @"\(R\)|\(TM\)|\s+CPU|\s+@.*$|\d+(th|rd|nd|st) Gen |\s+\d+-Core Processor|\s+Processor", "").Replace("  ", " ").Trim();
    static string ShortGpu(string n) => n.Replace("NVIDIA GeForce ", "NVIDIA ").Replace("AMD Radeon ", "Radeon ").Replace("(R)", "").Replace("(TM)", "").Trim();
    static string ShortDisk(StorageInfo.Disk d)
    {
        string m = System.Text.RegularExpressions.Regex.Replace(d.Model, @"^(Samsung SSD |WDC |KINGSTON |Samsung |Crucial |CT\d+\w+ )", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Length > 18) m = m[..18].Trim();
        return $"{m} {Fmt.Capacity(d.Size).Replace(",0", "")}";
    }

    void RenderDisks()
    {
        _disks.Children.Clear();
        if (_sum == null) return;
        foreach (var d in _sum.Disks.Take(3))
        {
            long size = d.Partitions.Sum(p => p.Size), free = d.Partitions.Sum(p => p.Free);
            string letters = string.Join(" ", d.Partitions.Select(p => p.Letter));
            var name = Text((letters != "" ? letters + " " : "") + d.Model, 13.5, FontWeights.Bold);
            var usedTxt = Num(size > 0 ? $"{Fmt.Bytes(size - free)} / {Fmt.Bytes(size)}" : Fmt.Capacity(d.Size), 12, "TextMuted", FontWeights.Normal);
            var top = new DockPanel();
            DockPanel.SetDock(usedTxt, Dock.Right);
            top.Children.Add(usedTxt);
            top.Children.Add(name);
            var bar = new Bar(8) { Value = size > 0 ? (size - free) * 100.0 / size : 0 };
            var health = Text(Loc.T("Saúde ", "Health ") + (d.Life is int l ? $"{l}%" : HealthText.Of(d.Status)), 12, null, HealthKey(d.Status) == "Text" ? "TextMuted" : HealthKey(d.Status));
            var bottom = new DockPanel();
            DockPanel.SetDock(health, Dock.Right);
            bottom.Children.Add(health);
            bottom.Children.Add(Text(d.Kind, 12, null, "TextMuted"));
            _disks.Children.Add(new Stack(8, top, bar, bottom));
        }
        var sys = _sum.Disks.FirstOrDefault(d => d.Partitions.Any(p => OperatingSystem.IsWindows() ? p.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase) : p.Letter == "/"));
        _diskActLabel.Text = OperatingSystem.IsWindows() ? Loc.T("Atividade do disco C:", "Drive C: activity") : Loc.T("Atividade do disco do sistema", "System drive activity");
        _sysDiskIndex = sys == null ? 0 : _sum.Disks.IndexOf(sys);
    }

    int _sysDiskIndex;

    public override void OnSnapshot(Snapshot s)
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        _eyebrow.Text = $"{Environment.MachineName} · {Loc.T("ligado há", "up for")} {Fmt.Duration(up)}";

        var c = s.Cpu;
        _cpu.Update(c.Load, c.Temp, Fmt.Value(c.ClockAvg, Unit.MHz), c.Power, CpuHealth(s));
        var g = s.Gpu;
        _gpu.Update(g?.Load, g?.Temp, g?.CoreClock is double gc ? Fmt.N(gc) + " MHz" : Fmt.Dash, g?.Power, GpuHealth(g));

        var m = s.Mem;
        if (m.TotalGB is double tot && m.UsedGB is double u)
        {
            _ramUsed.Text = Fmt.N(u, 1) + " GB";
            _ramOf.Text = Loc.T($"de {Fmt.N(Math.Round(tot))} GB", $"of {Fmt.N(Math.Round(tot))} GB");
            double cache = Math.Min(m.CachedGB ?? 0, Math.Max(0, tot - u));
            _cUsed.Width = new GridLength(u, GridUnitType.Star);
            _cCache.Width = new GridLength(cache, GridUnitType.Star);
            _cFree.Width = new GridLength(Math.Max(0, tot - u - cache), GridUnitType.Star);
            _ramCache.Text = Loc.T($"Em cache {Fmt.N(cache, 1)} GB", $"Cached {Fmt.N(cache, 1)} GB");
        }

        SampleHeavy(m.TotalGB ?? 0);

        var disk = _sysDiskIndex < s.Disks.Count ? s.Disks[_sysDiskIndex] : s.Disks.FirstOrDefault();
        _diskAct.Text = Fmt.Value(disk?.Activity, Unit.Percent);

        // alertas ativos no topo
        var active = AlertService.Active;
        _alerts.Children.Clear();
        foreach (var a in active) _alerts.Children.Add(Banner(a.Text, Health.Critical));
        if (!SensorHub.Instance.HasCpuTemp && !SensorHub.Instance.PawnIoInstalled && !Settings.Current.PawnIoPromptDismissed)
        {
            var b = Banner(Loc.T("Temperaturas, consumo e voltagem do processador precisam do driver gratuito PawnIO. Clique para instalar em Configurações.",
                                 "CPU temperature, power and voltage need the free PawnIO driver. Click to install it in Settings."), Health.Warning, true, () => _main.Navigate("config"));
            _alerts.Children.Add(b);
        }
        _alerts.Visibility = _alerts.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Top 5 por CPU e por memória (só CPU/RAM aqui: GPU e rede ficam na tela de processos).</summary>
    void SampleHeavy(double totalGb)
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        Task.Run(() =>
        {
            try { ProcessMonitor.Instance.Sample(); }
            catch (Exception ex) { Paths.LogError("Dashboard.Heavy", ex); }
        }).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _sampling, 0);
            if (!Shown) return;
            var apps = ProcessMonitor.Instance.Apps;
            if (apps.Count == 0) return;
            double total = totalGb * 1024 * 1024 * 1024;
            FillHeavy(_heavyCpu, apps.Where(a => a.Image != "System").OrderByDescending(a => a.Cpu).Take(5), a => Fmt.N(a.Cpu, a.Cpu < 10 ? 1 : 0) + "%", a => a.Cpu >= 40 ? "Crit" : a.Cpu >= 15 ? "Warn" : "Text");
            FillHeavy(_heavyRam, apps.OrderByDescending(a => a.Ram).Take(5), a => Fmt.Bytes(a.Ram), a => total > 0 && a.Ram / total >= 0.3 ? "Warn" : "Text");
        }));
    }

    static void FillHeavy(Stack host, IEnumerable<ProcessMonitor.AppUsage> apps, Func<ProcessMonitor.AppUsage, string> value, Func<ProcessMonitor.AppUsage, string> brush)
    {
        int i = 0;
        foreach (var a in apps)
        {
            if (i >= host.Children.Count)
            {
                var dock = new DockPanel();
                var v = Num("", 12.5, "Text", FontWeights.SemiBold);
                DockPanel.SetDock(v, Dock.Right);
                dock.Children.Add(v);
                dock.Children.Add(Text("", 13));
                host.Children.Add(new Border { Child = dock, BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0), Padding = new Thickness(0, 7, 0, 7) }.Res(Border.BorderBrushProperty, "Divider"));
            }
            var d = (DockPanel)((Border)host.Children[i]).Child;
            var val = (TextBlock)d.Children[0];
            val.Text = value(a);
            val.SetResourceReference(TextBlock.ForegroundProperty, brush(a));
            ((TextBlock)d.Children[1]).Text = a.Name + (a.Count > 1 ? $" ({a.Count})" : "");
            i++;
        }
    }

    static Health CpuHealth(Snapshot s)
    {
        var c = s.Cpu;
        if (c.Temp is not double t) return Health.Unknown;
        double tj = c.TjMax ?? 100;
        if (t >= tj - 3 || c.ThermalThrottle == true) return Health.Critical;
        if (t >= Math.Min(90, tj - 10)) return Health.Warning;
        return Health.Good;
    }

    static Health GpuHealth(GpuState? g)
    {
        if (g == null) return Health.Unknown;
        if (g.Temp is not double t) return g.Load != null ? Health.Good : Health.Unknown;
        if (t >= 90 || (g.Hotspot ?? 0) >= 105) return Health.Critical;
        if (t >= 83 || g.Throttling == true) return Health.Warning;
        return Health.Good;
    }

    /// <summary>Cartão grande de CPU/GPU com gráfico e três blocos.</summary>
    sealed class UnitCard
    {
        public readonly Border Root;
        public readonly TextBlock Model = Text("", 13, FontWeights.SemiBold, "Text2");
        readonly TextBlock _use = Num("—", 40, "Text", FontWeights.ExtraBold);
        readonly TextBlock _temp, _clk, _pow;
        readonly Border _pill = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top };
        readonly TextBlock _pillText = Text("", 12, FontWeights.SemiBold);
        readonly LineChart _chart;

        public UnitCard(string name, string metric)
        {
            _pill.Child = _pillText;
            var head = new DockPanel();
            DockPanel.SetDock(_pill, Dock.Right);
            head.Children.Add(_pill);
            head.Children.Add(new Stack(4, Label(name), Model));
            var useRow = new StackPanel { Orientation = Orientation.Horizontal };
            useRow.Children.Add(_use);
            var de = Text(Loc.T("de uso", "usage"), 13, null, "TextMuted");
            de.VerticalAlignment = VerticalAlignment.Bottom; de.Margin = new Thickness(8, 0, 0, 9);
            useRow.Children.Add(de);
            _chart = new LineChart { Height = 120, ShowAxis = false, FixedMin = 0, FixedMax = 100, Points = 90, Unit = Unit.Percent, SyncRoot = SensorHub.Instance.HistoryLock };
            if (SensorHub.Instance.GetSeries(metric) is Series ser) _chart.Lines.Add(new ChartLine { Data = ser, BrushKey = "SeriesLime" });
            _metric = metric;
            Stat(Loc.T("Temperatura", "Temperature"), out var t1, out _temp);
            Stat("Clock", out var t2, out _clk);
            Stat(Loc.T("Consumo", "Power"), out var t3, out _pow);
            Root = Card(new Stack(14, head, useRow, _chart, Cols(10, new[] { 1.0, 1.0, 1.0 }, t1, t2, t3)));
        }

        readonly string _metric;

        static void Stat(string label, out Border tile, out TextBlock value)
        {
            var (t, v) = Ui.Stat(label);
            tile = t; value = v;
        }

        public void Update(double? load, double? temp, string clock, double? power, Health h)
        {
            if (_chart.Lines.Count == 0 && SensorHub.Instance.GetSeries(_metric) is Series ser) _chart.Lines.Add(new ChartLine { Data = ser, BrushKey = "SeriesLime" });
            _use.Text = Fmt.Value(load, Unit.Percent);
            _temp.Text = Fmt.Value(temp, Unit.Temp);
            _clk.Text = clock;
            _pow.Text = Fmt.Value(power, Unit.Watt);
            string key = h == Health.Unknown ? "Accent" : HealthKey(h);
            _pill.SetResourceReference(Border.BorderBrushProperty, key);
            _pillText.SetResourceReference(TextBlock.ForegroundProperty, key);
            _pillText.Text = h == Health.Unknown ? Loc.T("Ao vivo", "Live") : "● " + HealthText.Of(h);
            _pill.ToolTip = h == Health.Unknown ? null : Loc.T("Estado geral pela temperatura e limitação de clock.", "Overall state from temperature and clock throttling.");
            _chart.Refresh();
        }
    }
}
