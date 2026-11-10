using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Hardware;
using Pulso.Platform;
using Pulso.Views;
using Path = Avalonia.Controls.Shapes.Path;
using static Pulso.Controls.Ui;

namespace Pulso;

public sealed class MainWindow : Window
{
    // Precisa vir antes de Menu: campos estáticos são inicializados na ordem em que aparecem.
    public static readonly (string id, string pt, string en)[] SystemSubs =
    {
        ("cpu", "Processador", "Processor"), ("gpu", "Placa de vídeo", "Graphics card"), ("ram", "Memória RAM", "Memory"),
        ("disk", "Armazenamento", "Storage"), ("board", "Placa-mãe", "Motherboard"), ("net", "Rede", "Network"), ("os", "Sistema operacional", "Operating system"),
    };

    // Ícones do design (traço 1.8, caixa 24×24). Itens com subitens viram grupos recolhíveis.
    static readonly (string id, string pt, string en, string icon, (string key, string pt, string en)[]? subs)[] Menu =
    {
        ("painel", "Painel", "Dashboard", "M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z", null),
        ("sistema", "Sistema", "System", "M7 7h10v10H7zM9 3v4M15 3v4M9 17v4M15 17v4M3 9h4M3 15h4M17 9h4M17 15h4",
            SystemSubs.Select(x => ("sistema/" + x.id, x.pt, x.en)).Append(("bateria", "Bateria", "Battery")).ToArray()),
        ("monitorar", "Monitoramento", "Monitoring", "M3 12h4l3-7 4 14 3-7h4", new[]
        {
            ("monitor", "Sensores", "Sensors"), ("processos", "O que está pesando", "What's heavy"),
        }),
        ("rede", "Aparelhos na rede", "Network devices", "M12 19h.01M8.5 15.5a5 5 0 0 1 7 0M5 12a10 10 0 0 1 14 0M2 8.5a15 15 0 0 1 20 0", null),
        ("overlay", "Overlay", "Overlay", "M3 5h18v14H3zM13 8h5v4h-5z", null),
        ("testes", "Testes", "Tests", "M12 14l4-4M4 18a8 8 0 1 1 16 0", new[]
        {
            ("bench", "Benchmark", "Benchmark"), ("stress", "Teste de estresse", "Stress test"), ("gargalo", "Detector de gargalo", "Bottleneck detector"), ("internet", "Internet", "Internet"),
        }),
        ("manutencao", "Manutenção", "Maintenance", "M14.7 6.3a4 4 0 0 0-5.4 5.4L4 17v3h3l5.3-5.3a4 4 0 0 0 5.4-5.4l-2.5 2.5-2.4-.6-.6-2.4z", new[]
        {
            ("reparo", "Reparo e limpeza", "Repair & cleanup"), ("programas", "Programas", "Programs"), ("drivers", "Drivers", "Drivers"),
            ("inicializacao", "Inicialização", "Startup apps"), ("travamentos", "Histórico de travamentos", "Crash history"),
        }),
        ("config", "Configurações", "Settings", "M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M14 4v4M8 10v4M16 16v4", null),
    };

    /// <summary>Telas que só existem no Windows (dependem de WMI, winget, registro, ETW, Direct3D…).</summary>
    static readonly HashSet<string> WindowsOnly = new() { "drivers", "inicializacao", "travamentos", "reparo", "programas" };

    static bool IsAvailable(string key) => OperatingSystem.IsWindows() || !WindowsOnly.Contains(key);

    /// <summary>Bateria só em notebook: nobreak USB no PC de mesa também aparece como bateria.</summary>
    static bool ShowBattery => Features.Battery.Present && Info.CpuInfo.IsLaptop();

    sealed class NavGroup
    {
        public required ToggleButton Toggle;
        public required Border Line;
        public required Path Chevron;
        public required string[] Keys;
    }

    readonly Dictionary<string, PageBase> _pages = new();
    readonly Dictionary<string, RadioButton> _navButtons = new();
    readonly List<NavGroup> _groups = new();
    readonly Dictionary<string, NavGroup> _groupOf = new();
    readonly StackPanel _nav = new();
    readonly ContentControl _host = new() { Focusable = false };
    readonly TextBlock _appCpu = new(), _appRam = new(), _appInterval = new(), _appNote = new();
    PageBase? _current;
    string _currentKey = "";
    int _pending;
    readonly DispatcherTimer _selfTimer;
    TimeSpan _lastCpu;
    DateTime _lastCpuTime;
    bool _reallyClose;

    public MainWindow()
    {
        Title = "Pulso";
        Width = 1360; Height = 880; MinWidth = 1000; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Pulso/Assets/logo.png"))); } catch { }
        this.SetResourceReference(BackgroundProperty, "Bg");
        this.SetResourceReference(ForegroundProperty, "Text");
        FontFamily = Ui.UiFont;
        FontSize = 13;

        Content = BuildLayout();
        BuildNav();

        Closing += OnClosing;
        SensorHub.Instance.Updated += OnHubUpdated;
        Closed += (_, _) =>
        {
            SensorHub.Instance.Updated -= OnHubUpdated;
            _selfTimer?.Stop();
            _current?.Hide();
        };

        _selfTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _selfTimer.Tick += (_, _) => UpdateSelfUsage();
        _selfTimer.Start();
        UpdateSelfUsage();

        Navigate("painel");
    }

    // ------------------------------------------------------------- layout

    Control BuildLayout()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(228, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        // topo: logo + nome + versão
        var logo = new Image { Width = 46, Height = 46 };
        try { logo.Source = new Avalonia.Media.Imaging.Bitmap(AssetLoader.Open(new Uri("avares://Pulso/Assets/logo.png"))); } catch { }
        var version = Text($"v{typeof(App).Assembly.GetName().Version?.ToString(2)} · {OsShort()}", 11, null, "TextMuted");
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 22) };
        head.Children.Add(logo);
        var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Text("Pulso", 17, FontWeights.ExtraBold));
        names.Children.Add(version);
        head.Children.Add(names);
        DockPanel.SetDock(head, Dock.Top);

        // aviso de modo limitado
        Border? limited = null;
        if (!OS.Current.IsElevated)
        {
            string who = OperatingSystem.IsWindows() ? Loc.T("administrador", "administrator") : "root";
            var t = Text(Loc.T($"Modo limitado — sem {who}", $"Limited mode — not {who}"), 11, FontWeights.SemiBold, "Warn", wrap: true);
            limited = new Border { Child = t, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 0, 12) }.Res(Border.BackgroundProperty, "WarnBg");
            ToolTip.SetTip(limited, OperatingSystem.IsWindows()
                ? Loc.T("Sem administrador o Pulso não lê temperaturas, SMART dos discos nem FPS. Reabra o app e aceite o pedido do Windows.",
                        "Without administrator rights Pulso cannot read temperatures, disk SMART or FPS. Reopen the app and accept the Windows prompt.")
                : Loc.T("Sem root o Pulso não lê o consumo de energia (RAPL) e alguns dados de disco. Temperaturas e uso funcionam normalmente. Clique aqui para reabrir como root e ver tudo.",
                        "Without root Pulso cannot read power draw (RAPL) and some disk data. Temperatures and usage work normally. Click here to reopen as root and see everything."));
            if (OperatingSystem.IsLinux())
            {
                t.Text += Loc.T(" · clique para reabrir como root", " · click to reopen as root");
                limited.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                limited.PointerReleased += (_, _) => App.Current.RelaunchAsRoot();
            }
            DockPanel.SetDock(limited, Dock.Top);
        }

        // rodapé: consumo do próprio app
        string[] _ = { };
        var footprint = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14), Margin = new Thickness(0, 16, 0, 0) }.Res(Border.BackgroundProperty, "SidebarCard");
        var fp = new StackPanel();
        fp.Children.Add(new TextBlock { Text = Loc.T("CONSUMO DO PULSO", "PULSO FOOTPRINT"), FontSize = 11, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 10) }.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted"));
        fp.Children.Add(FootRow("CPU", _appCpu));
        fp.Children.Add(FootRow("RAM", _appRam));
        fp.Children.Add(FootRow(Loc.T("Leitura", "Sampling"), _appInterval));
        _appNote.FontSize = 11; _appNote.TextWrapping = TextWrapping.Wrap; _appNote.LineHeight = 15;
        _appNote.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        fp.Children.Add(_appNote);
        footprint.Child = fp;
        DockPanel.SetDock(footprint, Dock.Bottom);

        var scroll = new ScrollViewer { Content = _nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

        var dock = new DockPanel { Margin = new Thickness(14, 20, 14, 20) };
        dock.Children.Add(head);
        if (limited != null) dock.Children.Add(limited);
        dock.Children.Add(footprint);
        dock.Children.Add(scroll);
        var side = new Border { Child = dock, BorderThickness = new Thickness(0, 0, 1, 0) }
            .Res(Border.BackgroundProperty, "Sidebar").Res(Border.BorderBrushProperty, "Border");
        grid.Children.Add(side);

        Grid.SetColumn(_host, 1);
        grid.Children.Add(_host);
        return grid;
    }

    static Control FootRow(string label, TextBlock value)
    {
        value.FontFamily = Ui.MonoFont; value.FontSize = 12.5;
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(value, Dock.Right);
        d.Children.Add(value);
        d.Children.Add(Text(label, 12.5, null, "Text3"));
        return d;
    }

    static string OsShort()
    {
        if (OperatingSystem.IsWindows()) return Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
        return Info.SystemSummary.LinuxPrettyName() ?? "Linux";
    }

    // ------------------------------------------------------------- navegação

    static Control NavContent(string text, string? icon, IBrush? unused = null)
    {
        var row = new DockPanel { LastChildFill = true };
        if (icon != null)
        {
            var path = new Path { Data = Geometry.Parse(icon), Width = 24, Height = 24, StrokeThickness = 1.8, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Stretch = Stretch.None };
            path.Bind(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(TemplatedControl) } });
            var vb = new Viewbox { Width = 17, Height = 17, Child = path };
            DockPanel.SetDock(vb, Dock.Left);
            row.Children.Add(vb);
        }
        row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(icon != null ? 12 : 0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    void BuildNav()
    {
        foreach (var (id, pt, en, icon, subs) in Menu)
        {
            if (subs == null)
            {
                if (!IsAvailable(id)) continue;
                var b = new RadioButton { Content = NavContent(Loc.T(pt, en), icon), GroupName = "nav" };
                b.Classes.Add("nav");
                string pid = id;
                b.IsCheckedChanged += (_, _) => { if (b.IsChecked == true) Navigate(pid); };
                _navButtons[id] = b;
                _nav.Children.Add(b);
                continue;
            }

            var items = subs.Where(x => (x.key != "bateria" || ShowBattery) && IsAvailable(x.key)).ToArray();
            if (items.Length == 0) continue;
            var chevron = new Path { Data = Geometry.Parse("M9 6l6 6-6 6"), Width = 24, Height = 24, StrokeThickness = 2.2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Stretch = Stretch.None, RenderTransformOrigin = RelativePoint.Center };
            chevron.Bind(Shape.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(TemplatedControl) } });
            var content = (DockPanel)NavContent(Loc.T(pt, en), icon);
            var chevBox = new Viewbox { Width = 12, Height = 12, Child = chevron };
            DockPanel.SetDock(chevBox, Dock.Right);
            content.Children.Insert(0, chevBox);

            var toggle = new ToggleButton { Content = content };
            toggle.Classes.Add("navparent");
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
            var line = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = panel, Margin = new Thickness(20, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), IsVisible = false }
                .Res(Border.BorderBrushProperty, "Help");
            var g = new NavGroup { Toggle = toggle, Line = line, Chevron = chevron, Keys = items.Select(x => x.key).ToArray() };
            foreach (var (key, spt, sen) in items)
            {
                var r = new RadioButton { Content = new TextBlock { Text = Loc.T(spt, sen), TextTrimming = TextTrimming.CharacterEllipsis }, GroupName = "nav" };
                r.Classes.Add("subnav");
                string k = key;
                r.IsCheckedChanged += (_, _) => { if (r.IsChecked == true) Navigate(k); };
                _navButtons[k] = r;
                _groupOf[k] = g;
                panel.Children.Add(r);
            }
            toggle.Click += (_, _) =>
            {
                bool open = toggle.IsChecked == true;
                SetGroupOpen(g, open);
                if (open && !g.Keys.Contains(_currentKey)) Navigate(g.Keys[0]);
            };
            _groups.Add(g);
            _nav.Children.Add(toggle);
            _nav.Children.Add(line);
        }
    }

    static void SetGroupOpen(NavGroup g, bool open)
    {
        g.Toggle.IsChecked = open;
        g.Line.IsVisible = open;
        g.Chevron.RenderTransform = open ? new RotateTransform(90) : null;
    }

    public void Navigate(string key)
    {
        string page = key.Split('/')[0];
        string? sub = key.Contains('/') ? key.Split('/')[1] : null;
        if (page == "sistema") sub ??= "cpu";
        string navKey = page == "sistema" ? "sistema/" + sub : page;
        if (!IsAvailable(page)) { page = "painel"; navKey = "painel"; sub = null; }
        _currentKey = navKey;

        if (_navButtons.TryGetValue(navKey, out var btn) && btn.IsChecked != true) btn.IsChecked = true;
        // Sanfona: abre o grupo da tela atual (cabeçalho em limão) e fecha os outros.
        _groupOf.TryGetValue(navKey, out var active);
        foreach (var g in _groups)
        {
            bool on = g == active;
            SetGroupOpen(g, on);
            if (on) g.Toggle.SetResourceReference(ForegroundProperty, "Accent");
            else g.Toggle.ClearValue(ForegroundProperty);
        }

        if (!_pages.TryGetValue(page, out var p))
        {
            p = PageFactory.Create(page, this);
            _pages[page] = p;
        }
        if (p is SystemPage sp && sub != null) sp.Select(sub);
        if (_current != p)
        {
            _current?.Hide();
            _current = p;
            _host.Content = p;
            p.Show();
        }
    }

    // ------------------------------------------------------------- atualização

    void OnHubUpdated(Snapshot s)
    {
        // Agrupa leituras: se a interface ainda não desenhou a anterior, não empilha outra.
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                if (WindowState == WindowState.Minimized || !IsVisible) return;
                if (SensorHub.Instance.Last is Snapshot last && _current?.Shown == true)
                {
                    try { _current.OnSnapshot(last); }
                    catch (Exception ex) { Paths.LogError("Page.OnSnapshot", ex); }
                }
            }, DispatcherPriority.Background);
        }
        catch (InvalidOperationException) { /* fechando o aplicativo */ }
    }

    void UpdateSelfUsage()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var cpu = p.TotalProcessorTime;
            if (_lastCpuTime != default)
            {
                double pct = (cpu - _lastCpu).TotalMilliseconds / (now - _lastCpuTime).TotalMilliseconds / Environment.ProcessorCount * 100;
                _appCpu.Text = Fmt.N(Math.Max(0, pct), 1) + "%";
            }
            _lastCpu = cpu; _lastCpuTime = now;
            _appRam.Text = Fmt.N(p.WorkingSet64 / 1048576.0) + " MB";
            _appInterval.Text = FormatInterval(SensorHub.Instance.EffectiveInterval);
            _appNote.Text = Loc.T($"Minimizado, a leitura cai para {FormatInterval(Settings.Current.MinimizedIntervalMs)}.",
                                  $"When minimized, sampling slows to {FormatInterval(Settings.Current.MinimizedIntervalMs)}.");
        }
        catch { }
    }

    public static string FormatInterval(int ms) => ms < 1000 ? Fmt.N(ms / 1000.0, 1) + " s" : Fmt.N(ms / 1000.0, ms % 1000 == 0 ? 0 : 1) + " s";

    // ------------------------------------------------------------- janela

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) OnStateChanged();
    }

    void OnStateChanged()
    {
        if (WindowState == WindowState.Minimized)
        {
            SensorHub.Instance.Minimized = true;
            if (Settings.Current.MinimizeToTray && App.TrayUsable) Dispatcher.UIThread.Post(() => App.Current.HideToTray());
        }
        else
        {
            SensorHub.Instance.Minimized = false;
            SensorHub.Instance.Poke();
        }
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_reallyClose || (Settings.Current.CloseToTray && App.TrayUsable)) return;
        e.Cancel = true;
        _reallyClose = true;
        App.Current.Exit();
    }
}
