using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Pulso.Core;
using Pulso.Hardware;
using Pulso.Views;

namespace Pulso;

public partial class MainWindow : Window
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

    /// <summary>Bateria só em notebook: nobreak USB no PC de mesa também aparece para o Windows como bateria.</summary>
    static bool ShowBattery => Features.Battery.Present && Info.CpuInfo.IsLaptop();

    sealed class NavGroup
    {
        public required ToggleButton Toggle;
        public required Border Line;
        public required string[] Keys;
    }

    readonly Dictionary<string, PageBase> _pages = new();
    readonly Dictionary<string, RadioButton> _navButtons = new();
    readonly List<NavGroup> _groups = new();
    readonly Dictionary<string, NavGroup> _groupOf = new();
    PageBase? _current;
    string _currentKey = "";
    int _pending;
    readonly DispatcherTimer _selfTimer;
    TimeSpan _lastCpu;
    DateTime _lastCpuTime;

    public MainWindow()
    {
        InitializeComponent();
        // O tamanho do design (1440×920) não cabe em telas com zoom (ex.: 1366×768 ou 1920×1080 a 125%):
        // ajusta à área útil para a barra de título e os botões de janela ficarem sempre visíveis.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1440, work.Width - 40);
        Height = Math.Min(920, work.Height - 40);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
        VersionText.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(2)} · {OsShort()}";
        AdminBadge.Visibility = Native.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        AdminBadge.ToolTip = Loc.T("Sem administrador o Pulso não lê temperaturas, SMART dos discos nem FPS. Reabra o app e aceite o pedido do Windows.",
                                   "Without administrator rights Pulso cannot read temperatures, disk SMART or FPS. Reopen the app and accept the Windows prompt.");
        BuildNav();

        SourceInitialized += (_, _) =>
        {
            ApplyTitleBar();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        StateChanged += OnStateChanged;
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

    static string OsShort()
    {
        var v = Environment.OSVersion.Version;
        return v.Build >= 22000 ? "Windows 11" : "Windows 10";
    }

    // ------------------------------------------------------------- navegação

    void BuildNav()
    {
        foreach (var (id, pt, en, icon, subs) in Menu)
        {
            var geo = Geometry.Parse(icon);
            if (subs == null)
            {
                var b = new RadioButton { Content = Loc.T(pt, en), Tag = geo, GroupName = "nav" };
                b.SetResourceReference(StyleProperty, "NavItem");
                string pid = id;
                b.Checked += (_, _) => Navigate(pid);
                _navButtons[id] = b;
                Nav.Children.Add(b);
                continue;
            }

            var items = subs.Where(x => x.key != "bateria" || ShowBattery).ToArray();
            var toggle = new ToggleButton { Content = Loc.T(pt, en), Tag = geo };
            toggle.SetResourceReference(StyleProperty, "NavParent");
            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
            var line = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Child = panel, Margin = new Thickness(20, 0, 0, 0), Visibility = Visibility.Collapsed };
            line.SetResourceReference(Border.BorderBrushProperty, "Help");
            var g = new NavGroup { Toggle = toggle, Line = line, Keys = items.Select(x => x.key).ToArray() };
            foreach (var (key, spt, sen) in items)
            {
                var r = new RadioButton { Content = Loc.T(spt, sen), GroupName = "nav" };
                r.SetResourceReference(StyleProperty, "SubNavItem");
                string k = key;
                r.Checked += (_, _) => Navigate(k);
                _navButtons[k] = r;
                _groupOf[k] = g;
                panel.Children.Add(r);
            }
            toggle.Click += (_, _) =>
            {
                bool open = toggle.IsChecked == true;
                line.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                if (open && !g.Keys.Contains(_currentKey)) Navigate(g.Keys[0]);
            };
            _groups.Add(g);
            Nav.Children.Add(toggle);
            Nav.Children.Add(line);
        }
    }

    public void Navigate(string key)
    {
        string page = key.Split('/')[0];
        string? sub = key.Contains('/') ? key.Split('/')[1] : null;
        if (page == "sistema") sub ??= "cpu";
        string navKey = page == "sistema" ? "sistema/" + sub : page;
        _currentKey = navKey;

        if (_navButtons.TryGetValue(navKey, out var btn) && btn.IsChecked != true) btn.IsChecked = true;
        // Sanfona: abre o grupo da tela atual (cabeçalho em limão) e fecha os outros.
        _groupOf.TryGetValue(navKey, out var active);
        foreach (var g in _groups)
        {
            bool on = g == active;
            g.Toggle.IsChecked = on;
            g.Line.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) g.Toggle.SetResourceReference(ForegroundProperty, "Accent");
            else g.Toggle.ClearValue(ForegroundProperty);
        }

        if (!_pages.TryGetValue(page, out var p))
        {
            p = page switch
            {
                "painel" => new DashboardPage(this),
                "sistema" => new SystemPage(),
                "monitor" => new MonitorPage(),
                "bench" => new BenchmarkPage(),
                "stress" => new StressPage(),
                "gargalo" => new BottleneckPage(),
                "internet" => new InternetPage(),
                "drivers" => new DriversPage(),
                "processos" => new ProcessesPage(),
                "bateria" => new BatteryPage(),
                "inicializacao" => new StartupPage(),
                "reparo" => new MaintenancePage(),
                "rede" => new LanPage(),
                "programas" => new ProgramsPage(),
                "travamentos" => new CrashesPage(),
                "overlay" => new OverlayPage(),
                _ => new SettingsPage(),
            };
            _pages[page] = p;
        }
        if (p is SystemPage sp && sub != null) sp.Select(sub);
        if (_current != p)
        {
            _current?.Hide();
            _current = p;
            Host.Content = p;
            p.Show();
        }
    }

    // ------------------------------------------------------------- atualização

    void OnHubUpdated(Snapshot s)
    {
        // Agrupa leituras: se a interface ainda não desenhou a anterior, não empilha outra.
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _pending, 0);
            if (WindowState == WindowState.Minimized || !IsVisible) return;
            if (SensorHub.Instance.Last is Snapshot last && _current?.Shown == true)
            {
                try { _current.OnSnapshot(last); }
                catch (Exception ex) { Paths.LogError("Page.OnSnapshot", ex); }
            }
        });
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
                AppCpu.Text = Fmt.N(Math.Max(0, pct), 1) + "%";
            }
            _lastCpu = cpu; _lastCpuTime = now;
            AppRam.Text = Fmt.N(p.PrivateMemorySize64 / 1048576.0) + " MB";
            AppInterval.Text = FormatInterval(SensorHub.Instance.EffectiveInterval);
            AppNote.Text = Loc.T($"Minimizado, a leitura cai para {FormatInterval(Settings.Current.MinimizedIntervalMs)}.",
                                 $"When minimized, sampling slows to {FormatInterval(Settings.Current.MinimizedIntervalMs)}.");
        }
        catch { }
    }

    public static string FormatInterval(int ms) => ms < 1000 ? Fmt.N(ms / 1000.0, 1) + " s" : Fmt.N(ms / 1000.0, ms % 1000 == 0 ? 0 : 1) + " s";

    // ------------------------------------------------------------- janela

    public void ApplyTitleBar()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == 0) return;
        Native.SetDarkTitleBar(h, App.IsDark);
        int round = 2; // cantos arredondados do Windows 11
        Native.DwmSetWindowAttribute(h, 33, ref round, 4);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MINMAXINFO { public int rx, ry, maxSizeX, maxSizeY, maxPosX, maxPosY, minTrackX, minTrackY, maxTrackX, maxTrackY; }

    /// <summary>Maximizar respeitando a barra de tarefas (janela sem moldura padrão).</summary>
    nint WndProc(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            nint mon = Native.MonitorFromWindow(hwnd, 2);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
            if (mon != 0 && Native.GetMonitorInfo(mon, ref mi))
            {
                var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(l);
                mmi.maxPosX = mi.rcWork.Left - mi.rcMonitor.Left;
                mmi.maxPosY = mi.rcWork.Top - mi.rcMonitor.Top;
                mmi.maxSizeX = mi.rcWork.Right - mi.rcWork.Left;
                mmi.maxSizeY = mi.rcWork.Bottom - mi.rcWork.Top;
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, l, false);
            }
        }
        return 0;
    }

    void OnStateChanged(object? sender, EventArgs e)
    {
        // Maximizada com WindowChrome a janela "vaza" alguns pixels para fora da tela.
        Frame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        if (WindowState == WindowState.Minimized)
        {
            SensorHub.Instance.Minimized = true;
            if (Settings.Current.MinimizeToTray) Dispatcher.BeginInvoke(() => App.Current.HideToTray());
        }
        else
        {
            SensorHub.Instance.Minimized = false;
            SensorHub.Instance.Poke();
        }
    }

    bool _reallyClose;

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClose || Settings.Current.CloseToTray) return;
        e.Cancel = true;
        _reallyClose = true;
        App.Current.Exit();
    }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
