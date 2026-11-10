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

/// <summary>"O que está pesando agora": aplicativos ordenados pelo recurso escolhido.</summary>
public sealed class ProcessesPage : PageBase
{
    enum Sort { Cpu, Ram, Disk, Net, Gpu }

    const int MaxRows = 25;
    static ProcessMonitor Mon => ProcessMonitor.Instance;
    readonly TextBlock _eyebrow = Label("");
    readonly Border _verdict = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock _netNote = Text("", 12, null, "TextMuted", wrap: true);
    readonly Grid _table = new();
    readonly RowView[] _rows = new RowView[MaxRows];
    readonly TextBlock[] _heads = new TextBlock[6];
    Sort _sort = Sort.Cpu;
    double _totalRam;
    int _sampling;

    static readonly double[] Widths = { 2.6, 0.8, 1.0, 1.0, 1.0, 0.7, 1.3 };

    public ProcessesPage()
    {
        var sort = Segments("proc-sort", new (string, bool, Action)[]
        {
            ("CPU", true, () => SetSort(Sort.Cpu)),
            (Loc.T("Memória", "Memory"), false, () => SetSort(Sort.Ram)),
            (Loc.T("Disco", "Disk"), false, () => SetSort(Sort.Disk)),
            (Loc.T("Rede", "Network"), false, () => SetSort(Sort.Net)),
            ("GPU", false, () => SetSort(Sort.Gpu)),
        });

        foreach (var w in Widths) _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] head = { Loc.T("Aplicativo", "App"), "CPU", Loc.T("Memória", "Memory"), Loc.T("Disco", "Disk"), Loc.T("Rede", "Network"), "GPU" };
        for (int c = 0; c < head.Length; c++)
        {
            var t = Text(head[c], 12, FontWeights.SemiBold, "Text3");
            t.Margin = new Thickness(0, 8, 10, 8);
            if (c > 0) t.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(t, c);
            _table.Children.Add(t);
            _heads[c] = t;
        }
        for (int i = 0; i < MaxRows; i++) _rows[i] = new RowView(this, _table, i + 1);

        var tip = Text(Loc.T("Processos do mesmo programa são somados (ex.: todas as abas do navegador). Disco inclui leitura e gravação de arquivos; GPU é o motor mais usado pelo app, como no Gerenciador de Tarefas.",
                             "Processes of the same program are added up (e.g. every browser tab). Disk includes file reads and writes; GPU is the app's busiest engine, as in Task Manager."), 12, null, "TextMuted", wrap: true);

        Content = Scroll(new Stack(18,
            Header(_eyebrow, Loc.T("O que está pesando", "What's slowing it down"), sort),
            _verdict,
            Card(new Stack(10, _table, _netNote, tip), 20, 14)));
        UpdateHeads();
    }

    void SetSort(Sort s)
    {
        _sort = s;
        UpdateHeads();
        Render();
    }

    void UpdateHeads()
    {
        for (int c = 1; c < _heads.Length; c++)
            _heads[c].SetResourceReference(TextBlock.ForegroundProperty, c - 1 == (int)_sort ? "Accent" : "Text3");
    }

    protected override void OnShown()
    {
        Mon.Gpu = true;
        Mon.StartNetwork();
        _netNote.Text = Mon.NetError ?? "";
        _netNote.Visibility = Mon.NetError == null ? Visibility.Collapsed : Visibility.Visible;
        _eyebrow.Text = Loc.T("Medindo…", "Measuring…");
        SensorHub.Instance.Poke();
    }

    protected override void OnHidden()
    {
        Mon.Gpu = false;
        Mon.StopNetwork();
    }

    public override void OnSnapshot(Snapshot s)
    {
        _totalRam = (s.Mem.TotalGB ?? 0) * 1024 * 1024 * 1024;
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        Task.Run(() =>
        {
            try { Mon.Sample(); }
            catch (Exception ex) { Paths.LogError("ProcessesPage.Sample", ex); }
        }).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _sampling, 0);
            if (Shown) Render();
        }));
    }

    static double Key(ProcessMonitor.AppUsage a, Sort s) => s switch
    {
        Sort.Cpu => a.Cpu,
        Sort.Ram => a.Ram,
        Sort.Disk => a.Disk,
        Sort.Net => a.Net,
        _ => a.Gpu,
    };

    void Render()
    {
        var apps = Mon.Apps;
        if (apps.Count == 0) return;
        var top = apps.OrderByDescending(a => Key(a, _sort)).ThenByDescending(a => a.Ram).Take(MaxRows).ToList();
        for (int i = 0; i < MaxRows; i++) _rows[i].Set(i < top.Count ? top[i] : null, _sort, _totalRam);
        _eyebrow.Text = Loc.T($"{apps.Count} aplicativos · {apps.Sum(a => a.Count)} processos · atualiza a cada {MainWindow.FormatInterval(SensorHub.Instance.EffectiveInterval)}",
                              $"{apps.Count} apps · {apps.Sum(a => a.Count)} processes · refreshes every {MainWindow.FormatInterval(SensorHub.Instance.EffectiveInterval)}");
        RenderVerdict(apps);
    }

    /// <summary>Uma frase direta quando um app sozinho está dominando algum recurso.</summary>
    void RenderVerdict(List<ProcessMonitor.AppUsage> apps)
    {
        string? msg = null;
        Health level = Health.Warning;
        var cpu = apps.Where(a => a.Image != "System").MaxBy(a => a.Cpu);
        var ram = apps.MaxBy(a => a.Ram);
        var disk = apps.MaxBy(a => a.Disk);
        var gpu = apps.Where(a => a.Image is not ("dwm.exe" or "System")).MaxBy(a => a.Gpu);
        if (cpu != null && cpu.Cpu >= 40)
        {
            msg = Loc.T($"{cpu.Name} está usando {Fmt.N(cpu.Cpu)}% do processador.", $"{cpu.Name} is using {Fmt.N(cpu.Cpu)}% of the processor.");
            if (cpu.Cpu >= 75) level = Health.Critical;
        }
        else if (ram != null && _totalRam > 0 && ram.Ram / _totalRam >= 0.35)
            msg = Loc.T($"{ram.Name} está ocupando {Fmt.Bytes(ram.Ram)} ({Fmt.N(ram.Ram * 100 / _totalRam)}% da memória).", $"{ram.Name} is taking {Fmt.Bytes(ram.Ram)} ({Fmt.N(ram.Ram * 100 / _totalRam)}% of memory).");
        else if (disk != null && disk.Disk >= 40 * 1048576)
            msg = Loc.T($"{disk.Name} está lendo/gravando {Fmt.Rate(disk.Disk)} no disco.", $"{disk.Name} is reading/writing {Fmt.Rate(disk.Disk)} on disk.");
        else if (gpu != null && gpu.Gpu >= 60)
            msg = Loc.T($"{gpu.Name} está usando {Fmt.N(gpu.Gpu)}% da placa de vídeo.", $"{gpu.Name} is using {Fmt.N(gpu.Gpu)}% of the graphics card.");
        _verdict.Child = msg == null ? null : Banner(msg, level, false);
        _verdict.Visibility = msg == null ? Visibility.Collapsed : Visibility.Visible;
    }

    async void KillApp(ProcessMonitor.AppUsage a)
    {
        string what = a.Count > 1 ? Loc.T($"os {a.Count} processos de {a.Name}", $"all {a.Count} processes of {a.Name}") : a.Name;
        if (MessageBox.Show(Window.GetWindow(this)!,
                Loc.T($"Finalizar {what}?\n\nO que não estiver salvo nesse programa será perdido.", $"End {what}?\n\nAnything unsaved in that program will be lost."),
                "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var (_, failed) = await Task.Run(() => ProcessMonitor.Kill(a));
        if (failed > 0)
            MessageBox.Show(Window.GetWindow(this)!,
                Loc.T($"{failed} processo(s) não puderam ser finalizados", $"{failed} process(es) could not be ended") + (Pulso.Platform.OS.Current.IsElevated ? "." : Loc.T(" (rode o Pulso como administrador).", " (run Pulso as administrator).")),
                "Pulso", MessageBoxButton.OK, MessageBoxImage.Information);
        SensorHub.Instance.Poke();
    }

    /// <summary>Linha reaproveitada a cada atualização (sem recriar elementos).</summary>
    sealed class RowView
    {
        readonly ProcessesPage _page;
        readonly Border _line;
        readonly TextBlock _name, _count;
        readonly TextBlock[] _vals = new TextBlock[5];
        readonly StackPanel _nameBox, _actions;
        readonly Button _open, _kill;
        ProcessMonitor.AppUsage? _app;

        public RowView(ProcessesPage page, Grid g, int row)
        {
            _page = page;
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) }.Res(Border.BorderBrushProperty, "Divider");
            Grid.SetRow(_line, row); Grid.SetColumnSpan(_line, Widths.Length);
            g.Children.Add(_line);

            _name = Text("", 13, FontWeights.SemiBold);
            _count = Text("", 12, null, "TextMuted");
            _count.Margin = new Thickness(6, 0, 0, 0);
            _nameBox = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 10, 9), VerticalAlignment = VerticalAlignment.Center };
            _nameBox.Children.Add(_name); _nameBox.Children.Add(_count);
            Grid.SetRow(_nameBox, row);
            g.Children.Add(_nameBox);
            for (int c = 0; c < _vals.Length; c++)
            {
                var t = Num("", 12.5, "Text", FontWeights.Normal);
                t.TextAlignment = TextAlignment.Right;
                t.Margin = new Thickness(0, 9, 10, 9);
                t.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(t, row); Grid.SetColumn(t, c + 1);
                g.Children.Add(t);
                _vals[c] = t;
            }
            _open = Ui.Button(Loc.T("Local", "Folder"), () => { if (_app?.Path is { Length: > 0 } p) StartupApps.ShowInExplorer(p); }, "LinkButton");
            _open.ToolTip = Loc.T("Abrir a pasta do programa", "Open the program's folder");
            _kill = Ui.Button(Loc.T("Finalizar", "End"), () => { if (_app != null) _page.KillApp(_app); }, "LinkButton");
            _kill.Margin = new Thickness(14, 0, 0, 0);
            _actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            _actions.Children.Add(_open); _actions.Children.Add(_kill);
            Grid.SetRow(_actions, row); Grid.SetColumn(_actions, Widths.Length - 1);
            g.Children.Add(_actions);
        }

        public void Set(ProcessMonitor.AppUsage? a, Sort sort, double totalRam)
        {
            _app = a;
            var vis = a == null ? Visibility.Collapsed : Visibility.Visible;
            _line.Visibility = _nameBox.Visibility = _actions.Visibility = vis;
            foreach (var v in _vals) v.Visibility = vis;
            if (a == null) return;
            _name.Text = a.Name;
            _nameBox.ToolTip = a.Path.Length > 0 ? a.Path : a.Image;
            _count.Text = a.Count > 1 ? $"({a.Count})" : "";
            _vals[0].Text = Fmt.N(a.Cpu, a.Cpu < 10 ? 1 : 0) + "%";
            _vals[1].Text = Fmt.Bytes(a.Ram);
            _vals[2].Text = a.Disk < 1024 ? "0" : Fmt.Rate(a.Disk);
            _vals[3].Text = !Mon.Network ? "—" : a.Net < 1024 ? "0" : Fmt.Rate(a.Net);
            _vals[4].Text = a.Gpu < 0.5 ? "0%" : Fmt.N(a.Gpu) + "%";
            Color(0, a.Cpu, 15, 40);
            Color(1, totalRam > 0 ? a.Ram * 100 / totalRam : 0, 15, 30);
            Color(2, a.Disk / 1048576, 10, 50);
            Color(3, a.Net / 1048576, 2, 10);
            Color(4, a.Gpu, 30, 70);
            for (int c = 0; c < _vals.Length; c++) _vals[c].FontWeight = c == (int)sort ? FontWeights.Bold : FontWeights.Normal;
            _open.Visibility = a.Path.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _kill.Visibility = ProcessMonitor.CanKill(a) ? Visibility.Visible : Visibility.Collapsed;
        }

        void Color(int col, double v, double warn, double crit) =>
            _vals[col].SetResourceReference(TextBlock.ForegroundProperty, v >= crit ? "Crit" : v >= warn ? "Warn" : v < 0.05 ? "TextMuted" : "Text");
    }
}
