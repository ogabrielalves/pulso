using System.Windows;
using System.Windows.Controls;
using Pulso.Bench;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Hardware;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class BenchmarkPage : PageBase
{
    readonly BenchCard _cpu, _gpu, _ram, _disk;
    readonly ComboBox _drives = new();
    readonly Grid _history = new();
    readonly TextBox _note = new() { Width = 260 };
    readonly Button _clear;
    CancellationTokenSource? _cts;
    BenchCard? _running;

    public BenchmarkPage()
    {
        _cpu = new BenchCard("CPU", Loc.T("Single-core e multi-core", "Single-core and multi-core"), RunCpu, this);
        _gpu = new BenchCard("GPU", Loc.T("Renderização 3D · 1080p", "3D rendering · 1080p"), RunGpu, this);
        _ram = new BenchCard(Loc.T("Memória RAM", "Memory (RAM)"), Loc.T("Leitura, escrita, cópia e latência", "Read, write, copy and latency"), RunRam, this,
            (Loc.T("Testar erros", "Error test"), RunRamErrors));
        _disk = new BenchCard("SSD / HD", Loc.T("Sequencial e aleatório 4K", "Sequential and 4K random"), RunDisk, this);
        foreach (var (root, label) in DiskBench.Drives()) _drives.Items.Add(new ComboBoxItem { Content = label, Tag = root });
        _drives.SelectedIndex = 0;
        _disk.SetSelector(_drives);

        _cpu.Define(("single", "Single-core"), ("multi", "Multi-core"));
        _gpu.Define(("score", Loc.T("Pontuação 3D", "3D score")), ("fps", Loc.T("FPS médio", "Average FPS")));
        _ram.Define(("read", Loc.T("Leitura", "Read")), ("write", Loc.T("Escrita", "Write")), ("copy", Loc.T("Cópia", "Copy")), ("lat", Loc.T("Latência", "Latency")), ("err", Loc.T("Teste de erros", "Error test")));
        _disk.Define(("sr", Loc.T("Seq. leitura", "Seq. read")), ("sw", Loc.T("Seq. escrita", "Seq. write")), ("rr", Loc.T("4K leitura", "4K read")), ("rw", Loc.T("4K escrita", "4K write")));

        var cards = Cols(14, new[] { 1.0, 1.0, 1.0, 1.0 }, _cpu.Root, _gpu.Root, _ram.Root, _disk.Root);

        _note.Text = BenchHistory.Current?.Note ?? "";
        _note.ToolTip = Loc.T("Escreva o que mudou (ex.: \"Driver novo\", \"Cooler novo\") para comparar depois.", "Describe what changed (e.g. \"New driver\", \"New cooler\") to compare later.");
        _note.LostFocus += (_, _) => { if (BenchHistory.Current != null || _note.Text != "") { BenchHistory.Session().Note = _note.Text.Trim(); BenchHistory.Save(); RenderHistory(); } };
        var noteRow = new StackPanel { Orientation = Orientation.Horizontal };
        var nl = Label(Loc.T("Anotação desta sessão", "Note for this session"));
        nl.VerticalAlignment = VerticalAlignment.Center; nl.Margin = new Thickness(0, 0, 10, 0);
        noteRow.Children.Add(nl);
        noteRow.Children.Add(_note);
        _clear = Ui.Button(Loc.T("Limpar histórico", "Clear history"), ClearHistory);
        _clear.Padding = new Thickness(14, 8, 14, 8);
        _clear.Margin = new Thickness(10, 0, 0, 0);
        noteRow.Children.Add(_clear);
        var hHead = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(noteRow, Dock.Right);
        hHead.Children.Add(noteRow);
        var hTitle = new Stack(3, Title(Loc.T("Histórico", "History")), Label(Loc.T("Compare antes e depois de upgrades ou ajustes", "Compare before and after upgrades or tweaks")));
        hHead.Children.Add(hTitle);
        var historyCard = Card(new Stack(0, hHead, _history), 20, 18);

        Content = Scroll(new Stack(18,
            Header(Loc.T("Pontuação comparada com o último resultado salvo", "Score compared with the last saved result"), Loc.T("Testes e benchmark", "Tests & benchmark")),
            cards, historyCard));
        ShowLast();
        RenderHistory();
    }

    // ------------------------------------------------------------------ execução

    bool Begin(BenchCard card)
    {
        if (_running != null) return false;
        _running = card;
        _cts = new CancellationTokenSource();
        foreach (var c in new[] { _cpu, _gpu, _ram, _disk }) c.SetBusy(c == card);
        _clear.IsEnabled = false;
        return true;
    }

    void End(string? error = null)
    {
        _running?.Done(error);
        _running = null;
        foreach (var c in new[] { _cpu, _gpu, _ram, _disk }) c.SetBusy(false);
        _clear.IsEnabled = true;
        BenchHistory.Save();
        ShowLast();
        RenderHistory();
    }

    void ClearHistory()
    {
        if (_running != null || BenchHistory.All.Count == 0) return;
        if (MessageBox.Show(Window.GetWindow(this)!, Loc.T("Apagar todos os resultados salvos do histórico de testes? Não dá para desfazer.", "Delete all saved results from the test history? This cannot be undone."),
                "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        BenchHistory.Clear();
        _note.Text = "";
        foreach (var c in new[] { _cpu, _gpu, _ram, _disk }) c.Reset();
        ShowLast();
        RenderHistory();
    }

    public void Cancel() => _cts?.Cancel();
    public bool IsRunning(BenchCard c) => _running == c;

    async void RunCpu()
    {
        if (!Begin(_cpu)) return;
        using var demand = SensorHub.Instance.Demand();
        try
        {
            var ct = _cts!.Token;
            int threads = Environment.ProcessorCount;
            // O Progress precisa nascer na thread da interface (fora do Task.Run) para reportar nela.
            var singleProg = new Progress<double>(p => _cpu.Progress(p * 0.5, "Single-core…"));
            var single = await Task.Run(() => CpuBench.Run(1, TimeSpan.FromSeconds(10), singleProg, ct));
            BenchHistory.Session().CpuSingle = single.Score;
            ShowLast();
            var multiProg = new Progress<double>(p => _cpu.Progress(0.5 + p * 0.5, $"Multi-core ({threads} threads)…"));
            var multi = await Task.Run(() => CpuBench.Run(threads, TimeSpan.FromSeconds(10), multiProg, ct));
            BenchHistory.Session().CpuMulti = multi.Score;
            End();
        }
        catch (OperationCanceledException) { End(Loc.T("Cancelado", "Cancelled")); }
        catch (Exception ex) { Paths.LogError("Bench.Cpu", ex); End(ex.Message); }
    }

    async void RunGpu()
    {
        if (!Begin(_gpu)) return;
        using var demand = SensorHub.Instance.Demand();
        try
        {
            var r = await GpuRenderer.BenchmarkAsync(1920, 1080, TimeSpan.FromSeconds(20),
                (fps, p) => Dispatcher.BeginInvoke(() => _gpu.Progress(p, $"{Fmt.N(fps)} FPS…")), _cts!.Token);
            var e = BenchHistory.Session();
            e.GpuFps = Math.Round(r.AvgFps, 1);
            e.Gpu3D = Math.Round(r.AvgFps * 150);
            e.GpuRes = $"{r.Width}×{r.Height}";
            End();
        }
        catch (OperationCanceledException) { End(Loc.T("Cancelado", "Cancelled")); }
        catch (Exception ex) { Paths.LogError("Bench.Gpu", ex); End(ex.Message); }
    }

    async void RunRam()
    {
        if (!Begin(_ram)) return;
        try
        {
            var prog = new Progress<(double p, string s)>(x => _ram.Progress(x.p, x.s + "…"));
            var r = await Task.Run(() => RamBench.Run(prog, _cts!.Token));
            var e = BenchHistory.Session();
            e.RamRead = r.ReadGBs; e.RamWrite = r.WriteGBs; e.RamCopy = r.CopyGBs; e.RamLatency = r.LatencyNs;
            End();
        }
        catch (OperationCanceledException) { End(Loc.T("Cancelado", "Cancelled")); }
        catch (Exception ex) { Paths.LogError("Bench.Ram", ex); End(ex.Message); }
    }

    async void RunRamErrors()
    {
        if (!Begin(_ram)) return;
        try
        {
            long availMb = (long)((SensorHub.Instance.Last?.Mem.AvailableGB ?? 4) * 1024);
            long mb = Math.Clamp(availMb / 2, 256, 8192);
            var prog = new Progress<(double p, string s)>(x => _ram.Progress(x.p, x.s));
            var r = await Task.Run(() => RamBench.ErrorTest(mb, 2, prog, _cts!.Token));
            var e = BenchHistory.Session();
            e.RamErrors = r.Errors; e.RamTestedMB = r.TestedMB;
            End();
        }
        catch (OutOfMemoryException) { End(Loc.T("Memória livre insuficiente para o teste", "Not enough free memory for the test")); }
        catch (OperationCanceledException) { End(Loc.T("Cancelado", "Cancelled")); }
        catch (Exception ex) { Paths.LogError("Bench.RamErr", ex); End(ex.Message); }
    }

    async void RunDisk()
    {
        if ((_drives.SelectedItem as ComboBoxItem)?.Tag is not string root) return;
        if (!Begin(_disk)) return;
        try
        {
            var r = await DiskBench.RunAsync(root, 1024, new Progress<(double p, string s)>(x => _disk.Progress(x.p, x.s + "…")), _cts!.Token);
            var e = BenchHistory.Session();
            e.DiskDrive = ((ComboBoxItem)_drives.SelectedItem).Content as string;
            e.DiskSeqRead = r.SeqReadMBs; e.DiskSeqWrite = r.SeqWriteMBs; e.Disk4kRead = r.Rnd4kReadMBs; e.Disk4kWrite = r.Rnd4kWriteMBs;
            End();
        }
        catch (OperationCanceledException) { End(Loc.T("Cancelado", "Cancelled")); }
        catch (Exception ex) { Paths.LogError("Bench.Disk", ex); End(ex.Message); }
    }

    // ------------------------------------------------------------------ exibição

    static string Pct(double? cur, double? prev, bool lowerIsBetter = false)
    {
        if (cur is not double c || prev is not double p || p == 0) return "";
        double d = (c - p) / p * 100;
        if (Math.Abs(d) < 0.05) return "=";
        return (d > 0 ? "+" : "−") + Fmt.N(Math.Abs(d), 1) + "%";
    }

    static bool Better(double? cur, double? prev, bool lowerIsBetter = false) =>
        cur is double c && prev is double p && (lowerIsBetter ? c <= p : c >= p);

    void ShowLast()
    {
        var e = BenchHistory.Current ?? BenchHistory.All.FirstOrDefault();
        double? P(Func<BenchEntry, double?> f) => e == BenchHistory.Current ? BenchHistory.Previous(f) : BenchHistory.All.Skip(1).Select(f).FirstOrDefault(v => v != null);
        string GBs(double? v) => v is double x ? Fmt.N(x, 1) + " GB/s" : "—";
        string MBs(double? v) => v is double x ? Fmt.N(x, x < 100 ? 1 : 0) + " MB/s" : "—";
        if (e == null) return;
        _cpu.Set("single", e.CpuSingle is double a ? Fmt.N(a) : "—", Pct(e.CpuSingle, P(x => x.CpuSingle)), Better(e.CpuSingle, P(x => x.CpuSingle)));
        _cpu.Set("multi", e.CpuMulti is double b ? Fmt.N(b) : "—", Pct(e.CpuMulti, P(x => x.CpuMulti)), Better(e.CpuMulti, P(x => x.CpuMulti)));
        _gpu.Set("score", e.Gpu3D is double g ? Fmt.N(g) : "—", Pct(e.Gpu3D, P(x => x.Gpu3D)), Better(e.Gpu3D, P(x => x.Gpu3D)));
        var pf = P(x => x.GpuFps);
        _gpu.Set("fps", e.GpuFps is double f ? Fmt.N(f) : "—", e.GpuFps is double f1 && pf is double f2 ? (f1 - f2 >= 0 ? "+" : "−") + Fmt.N(Math.Abs(f1 - f2)) : "", Better(e.GpuFps, pf));
        _ram.Set("read", GBs(e.RamRead), Pct(e.RamRead, P(x => x.RamRead)), Better(e.RamRead, P(x => x.RamRead)));
        _ram.Set("write", GBs(e.RamWrite), Pct(e.RamWrite, P(x => x.RamWrite)), Better(e.RamWrite, P(x => x.RamWrite)));
        _ram.Set("copy", GBs(e.RamCopy), Pct(e.RamCopy, P(x => x.RamCopy)), Better(e.RamCopy, P(x => x.RamCopy)));
        _ram.Set("lat", e.RamLatency is double l ? Fmt.N(l, 0) + " ns" : "—", Pct(e.RamLatency, P(x => x.RamLatency)), Better(e.RamLatency, P(x => x.RamLatency), true));
        _ram.Set("err", e.RamErrors is long er ? (er == 0 ? Loc.T("0 erros", "0 errors") : Fmt.N(er) + Loc.T(" erros", " errors")) : "—",
                 e.RamErrors is long er2 ? (er2 == 0 ? "ok" : "!") : "", e.RamErrors is null or 0);
        _disk.Set("sr", MBs(e.DiskSeqRead), Pct(e.DiskSeqRead, P(x => x.DiskSeqRead)), Better(e.DiskSeqRead, P(x => x.DiskSeqRead)));
        _disk.Set("sw", MBs(e.DiskSeqWrite), Pct(e.DiskSeqWrite, P(x => x.DiskSeqWrite)), Better(e.DiskSeqWrite, P(x => x.DiskSeqWrite)));
        _disk.Set("rr", MBs(e.Disk4kRead), Pct(e.Disk4kRead, P(x => x.Disk4kRead)), Better(e.Disk4kRead, P(x => x.Disk4kRead)));
        _disk.Set("rw", MBs(e.Disk4kWrite), Pct(e.Disk4kWrite, P(x => x.Disk4kWrite)), Better(e.Disk4kWrite, P(x => x.Disk4kWrite)));
        if (e.RamTestedMB is double mb) _ram.SetTip("err", Loc.T($"{Fmt.N(mb)} MB testados em 2 passadas. O teste dentro do Windows não alcança toda a memória; para um diagnóstico completo use o MemTest86.",
                                                                  $"{Fmt.N(mb)} MB tested in 2 passes. Testing inside Windows cannot reach all memory; for a full diagnosis use MemTest86."));
    }

    void RenderHistory()
    {
        _history.Children.Clear();
        _history.ColumnDefinitions.Clear();
        _history.RowDefinitions.Clear();
        double[] w = { 1.1, 1.6, 1, 1, 1, 1, 1 };
        foreach (var x in w) _history.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(x, GridUnitType.Star) });
        string[] head = { Loc.T("Data", "Date"), Loc.T("Anotação", "Note"), "CPU single", "CPU multi", "GPU 3D", Loc.T("RAM leitura", "RAM read"), Loc.T("SSD seq.", "SSD seq.") };
        _history.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < head.Length; c++)
        {
            var t = Text(head[c], 12, FontWeights.SemiBold, "Accent");
            t.Margin = new Thickness(0, 8, 0, 8);
            if (c >= 2) t.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(t, c);
            _history.Children.Add(t);
        }
        int r = 1;
        foreach (var e in BenchHistory.All.Take(12))
        {
            _history.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) }.Res(Border.BorderBrushProperty, "Divider");
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, w.Length);
            _history.Children.Add(line);
            string[] cells =
            {
                e.Date.ToString("d", Fmt.Culture), e.Note == "" ? (e == BenchHistory.Current ? Loc.T("Sessão atual", "Current session") : "—") : e.Note,
                e.CpuSingle is double a ? Fmt.N(a) : "—", e.CpuMulti is double b ? Fmt.N(b) : "—", e.Gpu3D is double g ? Fmt.N(g) : "—",
                e.RamRead is double rr ? Fmt.N(rr, 1) + " GB/s" : "—", e.DiskSeqRead is double d ? Fmt.N(d) : "—",
            };
            for (int c = 0; c < cells.Length; c++)
            {
                var t = c == 1 ? Text(cells[c], 13, FontWeights.SemiBold) : Num(cells[c], 13, c == 0 ? "Text3" : "Text", FontWeights.Normal);
                t.Margin = new Thickness(0, 11, 8, 11);
                if (c >= 2) t.TextAlignment = TextAlignment.Right;
                Grid.SetRow(t, r); Grid.SetColumn(t, c);
                _history.Children.Add(t);
            }
            r++;
        }
        if (r == 1)
        {
            var empty = Text(Loc.T("Nenhum teste ainda. Execute um teste acima para começar o histórico.", "No tests yet. Run a test above to start the history."), 13, null, "TextMuted");
            empty.Margin = new Thickness(0, 12, 0, 4);
            Grid.SetRow(empty, 1); Grid.SetColumnSpan(empty, w.Length);
            _history.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _history.Children.Add(empty);
        }
    }

    /// <summary>Cartão de um teste.</summary>
    public sealed class BenchCard
    {
        public readonly Border Root;
        readonly Stack _rows = new(0);
        readonly Dictionary<string, (Border row, TextBlock v, TextBlock d)> _map = new();
        readonly Button _run;
        readonly Button? _extra;
        readonly ProgressBar _bar = new() { Maximum = 1, Visibility = Visibility.Collapsed };
        readonly TextBlock _status = Text("", 12, null, "TextMuted", wrap: true);
        readonly Stack _top;
        readonly BenchmarkPage _page;

        public BenchCard(string title, string desc, Action run, BenchmarkPage page, (string label, Action act)? extra = null)
        {
            _page = page;
            _top = new Stack(12, new Stack(3, Text(title, 16, FontWeights.ExtraBold), Text(desc, 12, null, "TextMuted", wrap: true)));
            _top.Children.Add(_rows);
            _run = Ui.Button(Loc.T("Executar", "Run"), () => { if (_page.IsRunning(this)) _page.Cancel(); else run(); }, "Primary");
            var bottom = new Stack(8, _bar, _status);
            if (extra is var (label, act))
            {
                _extra = Ui.Button(label, () => { if (_page.IsRunning(this)) _page.Cancel(); else act(); });
                _extra.Padding = new Thickness(14, 10, 14, 10);
                bottom.Children.Add(Cols(8, new[] { 1.0, 1.0 }, _run, _extra));
            }
            else bottom.Children.Add(_run);
            _status.Visibility = Visibility.Collapsed;
            var dock = new DockPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Margin = new Thickness(0, 12, 0, 0);
            dock.Children.Add(bottom);
            dock.Children.Add(_top);
            Root = Card(dock, 18, 18);
        }

        public void SetSelector(ComboBox combo) => _top.Children.Insert(1, combo);

        public void Define(params (string id, string label)[] rows)
        {
            foreach (var (id, label) in rows)
            {
                var v = Num("—", 15);
                var d = Text("", 11.5, FontWeights.Bold, "Good");
                d.Margin = new Thickness(8, 0, 0, 0);
                d.VerticalAlignment = VerticalAlignment.Bottom;
                var right = new StackPanel { Orientation = Orientation.Horizontal };
                right.Children.Add(v); right.Children.Add(d);
                var dock = new DockPanel();
                DockPanel.SetDock(right, Dock.Right);
                dock.Children.Add(right);
                var k = Text(label, 12.5, null, "Text3");
                k.VerticalAlignment = VerticalAlignment.Center;
                dock.Children.Add(k);
                var row = new Border { Child = dock, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 8) }.Res(Border.BorderBrushProperty, "Divider");
                _map[id] = (row, v, d);
                _rows.Children.Add(row);
            }
        }

        public void Set(string id, string value, string delta, bool good)
        {
            if (!_map.TryGetValue(id, out var x)) return;
            x.v.Text = value;
            x.d.Text = delta;
            x.d.SetResourceReference(TextBlock.ForegroundProperty, delta is "" or "=" ? "TextMuted" : good ? "Good" : "Warn");
        }

        public void Reset()
        {
            foreach (var id in _map.Keys) { Set(id, "—", "", true); _map[id].row.ToolTip = null; }
            Done(null);
        }

        public void SetTip(string id, string tip)
        {
            if (_map.TryGetValue(id, out var x)) x.row.ToolTip = tip;
        }

        public void SetBusy(bool mine)
        {
            bool any = mine || _page._running != null;
            _run.IsEnabled = mine || !any;
            if (_extra != null) _extra.IsEnabled = mine || !any;
            _run.Content = mine ? Loc.T("Cancelar", "Cancel") : Loc.T("Executar", "Run");
            if (mine)
            {
                _bar.Visibility = Visibility.Visible;
                _status.Visibility = Visibility.Visible;
                _bar.Value = 0;
                _status.Text = Loc.T("Preparando…", "Preparing…");
            }
        }

        public void Progress(double p, string text)
        {
            _bar.Value = Math.Clamp(p, 0, 1);
            _status.Text = text;
        }

        public void Done(string? error)
        {
            _bar.Visibility = Visibility.Collapsed;
            _status.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            _status.Text = error ?? "";
            _status.SetResourceReference(TextBlock.ForegroundProperty, error == null ? "TextMuted" : "Warn");
        }
    }
}
