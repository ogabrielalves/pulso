using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Hardware;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class SettingsPage : PageBase
{
    static AppSettings S => Settings.Current;
    readonly TextBlock _cpuLim = Num("", 13, "Accent"), _gpuLim = Num("", 13, "Accent");
    readonly TextBlock _langNote = Text("", 12, null, "Warn", wrap: true);
    readonly TextBlock _pawnStatus = Text("", 12, null, "TextMuted", wrap: true);
    readonly Button _pawnBtn;

    public SettingsPage()
    {
        // --- geral
        var startup = Toggle(Startup.IsEnabled(), on =>
        {
            S.StartWithWindows = on;
            Settings.Save();
            if (!Startup.Set(on)) App.Current.Tray?.Notify("Pulso", Loc.T("Não foi possível alterar a inicialização com o Windows.", "Could not change start-with-Windows."));
        });
        var tray = Toggle(S.MinimizeToTray, on => { S.MinimizeToTray = on; Settings.Save(); });
        var close = Toggle(S.CloseToTray, on => { S.CloseToTray = on; Settings.Save(); });

        var units = Segments("unit", new (string, bool, Action)[]
        {
            ("°C", !S.Fahrenheit, () => { S.Fahrenheit = false; Settings.Save(); UpdateLimits(); }),
            ("°F", S.Fahrenheit, () => { S.Fahrenheit = true; Settings.Save(); UpdateLimits(); }),
        });
        var interval = Segments("set-interval", new (string, bool, Action)[]
        {
            ("0,5 s", S.IntervalMs == 500, () => SetInterval(500)),
            ("1 s", S.IntervalMs == 1000, () => SetInterval(1000)),
            ("2 s", S.IntervalMs == 2000, () => SetInterval(2000)),
        });
        var minimized = Segments("set-min", new (string, bool, Action)[]
        {
            ("2 s", S.MinimizedIntervalMs == 2000, () => { S.MinimizedIntervalMs = 2000; Settings.Save(); }),
            ("5 s", S.MinimizedIntervalMs == 5000, () => { S.MinimizedIntervalMs = 5000; Settings.Save(); }),
            ("10 s", S.MinimizedIntervalMs == 10000, () => { S.MinimizedIntervalMs = 10000; Settings.Save(); }),
        });

        var limits = new StackPanel { Orientation = Orientation.Horizontal };
        limits.Children.Add(LimitBox("CPU", _cpuLim, "cpu.temp"));
        limits.Children.Add(LimitBox("GPU", _gpuLim, "gpu0.temp"));
        var notify = Toggle(S.AlertsEnabled, on => { S.AlertsEnabled = on; Settings.Save(); });

        var theme = Segments("theme", new (string, bool, Action)[]
        {
            (Loc.T("Escuro", "Dark"), S.Theme != "light", () => SetTheme("dark")),
            (Loc.T("Claro", "Light"), S.Theme == "light", () => SetTheme("light")),
        });
        var lang = new ComboBox { MinWidth = 220 };
        lang.Items.Add(new ComboBoxItem { Content = "Português (Brasil)", Tag = "pt-BR" });
        lang.Items.Add(new ComboBoxItem { Content = "English", Tag = "en-US" });
        lang.Items.Add(new ComboBoxItem { Content = "Русский", Tag = "ru-RU" });
        lang.SelectedIndex = (int)Loc.ParseLanguage(S.Language) switch { 1 => 1, 2 => 2, _ => 0 };
        lang.SelectionChanged += (_, _) =>
        {
            S.Language = (string)((ComboBoxItem)lang.SelectedItem).Tag;
            Settings.Save();
            _langNote.Text = Loc.T("O idioma muda ao reabrir o Pulso.", "The language changes when Pulso restarts.");
        };

        var general = Card(new Stack(0,
            SettingRow(Loc.T("Iniciar com o Windows", "Start with Windows"), Loc.T("Abre minimizado na bandeja do sistema", "Opens minimized in the system tray"), startup, first: true),
            SettingRow(Loc.T("Minimizar para a bandeja", "Minimize to tray"), Loc.T("Ao minimizar, a janela fecha e o Pulso continua no relógio do Windows usando menos memória", "When minimized, the window closes and Pulso keeps running in the tray using less memory"), tray),
            SettingRow(Loc.T("Fechar para a bandeja", "Close to tray"), Loc.T("O botão ✕ mantém o Pulso rodando (alertas e overlay continuam). Para sair, use o ícone da bandeja.", "The ✕ button keeps Pulso running (alerts and overlay keep working). To quit, use the tray icon."), close),
            SettingRow(Loc.T("Unidade de temperatura", "Temperature unit"), null, units),
            SettingRow(Loc.T("Intervalo de leitura dos sensores", "Sensor reading interval"), Loc.T("Intervalos maiores consomem menos CPU", "Longer intervals use less CPU"), interval),
            SettingRow(Loc.T("Intervalo com a janela fechada", "Interval with window closed"), Loc.T("Usado na bandeja quando nada precisa de leitura rápida (overlay, log e testes mantêm o ritmo normal)", "Used in the tray when nothing needs fast readings (overlay, logging and tests keep the normal pace)"), minimized),
            SettingRow(Loc.T("Limites de alerta de temperatura", "Temperature alert limits"), Loc.T("Outros alertas ficam na aba Monitoramento", "Other alerts are in the Monitoring tab"), limits),
            SettingRow(Loc.T("Notificações de alerta", "Alert notifications"), Loc.T("Aviso do Windows quando um limite é ultrapassado", "Windows notification when a limit is exceeded"), notify),
            SettingRow(Loc.T("Tema", "Theme"), null, theme),
            SettingRow(Loc.T("Idioma", "Language"), null, new Stack(6, lang, _langNote))), 22, 6);

        // --- sensores
        _pawnBtn = Ui.Button(Loc.T("Instalar driver", "Install driver"), InstallPawn, "Primary");
        _pawnBtn.Padding = new Thickness(16, 9, 16, 9);
        var admin = Toggle(S.RequestAdmin, on => { S.RequestAdmin = on; Settings.Save(); });
        var adminState = Text(Native.IsAdmin ? Loc.T("Rodando como administrador ✓", "Running as administrator ✓") : Loc.T("Rodando sem administrador — reabra o Pulso e aceite o pedido do Windows", "Running without administrator — reopen Pulso and accept the Windows prompt"), 12, null, Native.IsAdmin ? "Good" : "Warn", wrap: true);
        var sensors = Card(new Stack(0,
            SettingRow(Loc.T("Driver de sensores (PawnIO)", "Sensor driver (PawnIO)"), null, new Stack(6, _pawnBtn, _pawnStatus) { Width = 260 }, first: true),
            SettingRow(Loc.T("Pedir permissão de administrador", "Ask for administrator rights"), Loc.T("Necessário para temperaturas, SMART dos discos, timings da RAM e contador de FPS", "Required for temperatures, disk SMART, RAM timings and the FPS counter"), new Stack(6, admin, adminState) { Width = 260 })), 22, 6);
        UpdatePawn();

        // --- pastas e sobre
        var folders = new StackPanel { Orientation = Orientation.Horizontal };
        folders.Children.Add(Ui.Button(Loc.T("Logs CSV", "CSV logs"), () => Open(Paths.Logs)));
        var rep = Ui.Button(Loc.T("Relatórios", "Reports"), () => Open(Paths.Reports)); rep.Margin = new Thickness(8, 0, 0, 0);
        folders.Children.Add(rep);
        var data = Ui.Button(Loc.T("Dados do app", "App data"), () => Open(Paths.AppData)); data.Margin = new Thickness(8, 0, 0, 0);
        folders.Children.Add(data);
        var about = Card(new Stack(0,
            SettingRow(Loc.T("Pastas", "Folders"), Loc.T("Logs em Documentos\\Pulso; histórico e configurações em %AppData%\\Pulso", "Logs in Documents\\Pulso; history and settings in %AppData%\\Pulso"), folders, first: true),
            SettingRow(Loc.T("Sobre", "About"), Loc.T($"Pulso {typeof(App).Assembly.GetName().Version?.ToString(3)} · sensores via LibreHardwareMonitor (MPL-2.0) · fontes Manrope e JetBrains Mono (OFL)",
                                                     $"Pulso {typeof(App).Assembly.GetName().Version?.ToString(3)} · sensors via LibreHardwareMonitor (MPL-2.0) · Manrope and JetBrains Mono fonts (OFL)"), new Border())), 22, 6);

        var content = new Stack(18, Header(Loc.T("Preferências gerais do Pulso", "General Pulso preferences"), Loc.T("Configurações", "Settings")),
            general, Ui.Title(Loc.T("Sensores", "Sensors")), sensors, about) { MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        Content = Scroll(content);
        UpdateLimits();
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    static void SetInterval(int ms)
    {
        S.IntervalMs = ms;
        Settings.Save();
        SensorHub.Instance.Poke();
    }

    static void SetTheme(string t)
    {
        S.Theme = t;
        Settings.Save();
        App.ApplyTheme(t);
    }

    Border LimitBox(string label, TextBlock value, string metric)
    {
        var minus = Ui.Button("−", () => ChangeLimit(metric, -1), "StepButton");
        var plus = Ui.Button("+", () => ChangeLimit(metric, +1), "StepButton");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var l = Text(label + " ", 12.5);
        l.VerticalAlignment = VerticalAlignment.Center;
        value.VerticalAlignment = VerticalAlignment.Center;
        value.MinWidth = 44;
        value.TextAlignment = TextAlignment.Center;
        minus.Margin = new Thickness(6, 0, 4, 0); plus.Margin = new Thickness(4, 0, 0, 0);
        sp.Children.Add(l); sp.Children.Add(minus); sp.Children.Add(value); sp.Children.Add(plus);
        var b = Tile(sp, 10, 5, 10);
        b.Margin = new Thickness(8, 0, 0, 0);
        return b;
    }

    void ChangeLimit(string metric, int delta)
    {
        var rule = S.Alerts.FirstOrDefault(a => a.MetricId == metric);
        if (rule == null) { rule = new AlertRule { MetricId = metric, Threshold = metric == "cpu.temp" ? 90 : 85 }; S.Alerts.Add(rule); }
        rule.Threshold = Math.Clamp(rule.Threshold + delta, 40, 110);
        rule.Enabled = true;
        Settings.Save();
        UpdateLimits();
    }

    void UpdateLimits()
    {
        _cpuLim.Text = Fmt.Value(S.Alerts.FirstOrDefault(a => a.MetricId == "cpu.temp")?.Threshold ?? 90, Unit.Temp);
        _gpuLim.Text = Fmt.Value(S.Alerts.FirstOrDefault(a => a.MetricId == "gpu0.temp")?.Threshold ?? 85, Unit.Temp);
    }

    void UpdatePawn()
    {
        bool inst = SensorHub.Instance.PawnIoInstalled;
        _pawnBtn.Visibility = inst ? Visibility.Collapsed : Visibility.Visible;
        _pawnStatus.Text = inst
            ? Loc.T("Instalado ✓ — temperatura, consumo e voltagem da CPU, sensores da placa-mãe e timings da RAM disponíveis.", "Installed ✓ — CPU temperature, power and voltage, motherboard sensors and RAM timings available.")
            : Loc.T("Driver gratuito e assinado usado para ler temperatura e consumo da CPU, ventoinhas da placa-mãe e timings da RAM. Sem ele esses dados aparecem como \"não disponível\".",
                    "Free, signed driver used to read CPU temperature and power, motherboard fans and RAM timings. Without it these show as \"not available\".");
        _pawnStatus.SetResourceReference(TextBlock.ForegroundProperty, inst ? "Good" : "TextMuted");
    }

    async void InstallPawn()
    {
        _pawnBtn.IsEnabled = false;
        _pawnBtn.Content = Loc.T("Instalando…", "Installing…");
        var (ok, msg) = await PawnIo.InstallAsync();
        if (ok)
        {
            _pawnStatus.Text = Loc.T("Instalado. Reiniciando o Pulso para carregar o driver…", "Installed. Restarting Pulso to load the driver…");
            await Task.Delay(1200);
            PawnIo.Restart();
            return;
        }
        _pawnBtn.IsEnabled = true;
        _pawnBtn.Content = Loc.T("Tentar novamente", "Try again");
        _pawnStatus.Text = Loc.T("Não foi possível instalar automaticamente (", "Could not install automatically (") + msg + Loc.T("). Baixe em pawnio.eu e reabra o Pulso.", "). Download it from pawnio.eu and reopen Pulso.");
        _pawnStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        PawnIo.OpenWebsite();
    }
}
