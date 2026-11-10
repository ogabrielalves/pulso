using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Hardware;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class MonitorPage : PageBase
{
    readonly TextBlock _eyebrow = Label("");
    readonly Grid _cardsHost = new();
    readonly List<MetricCard> _cards = new();
    readonly Stack _alertRows = new(0);
    readonly Grid _allTable = new();
    readonly Dictionary<string, (TextBlock now, TextBlock min, TextBlock avg, TextBlock max)> _allCells = new();
    readonly Button _rec = new();
    readonly TextBlock _recText = Text("", 12.5, FontWeights.Bold);
    readonly Border _recDot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Stack _newAlert = new(10) { Visibility = Visibility.Collapsed };
    bool _built;

    public MonitorPage()
    {
        var intervals = Segments("interval", new (string, bool, Action)[]
        {
            ("0,5 s", Settings.Current.IntervalMs == 500, () => SetInterval(500)),
            ("1 s", Settings.Current.IntervalMs == 1000, () => SetInterval(1000)),
            ("2 s", Settings.Current.IntervalMs == 2000, () => SetInterval(2000)),
        }, host: "Card");
        var lbl = Label(Loc.T("Atualizar a cada", "Refresh every"));
        lbl.VerticalAlignment = VerticalAlignment.Center;

        var recContent = new StackPanel { Orientation = Orientation.Horizontal };
        recContent.Children.Add(_recDot);
        recContent.Children.Add(_recText);
        _rec.Content = recContent;
        _rec.Click += (_, _) => { if (CsvLogger.Active) { CsvLogger.Stop(); OpenLogFolder(); } else CsvLogger.Start(); };
        CsvLogger.StateChanged += () => Dispatcher.UIThread.Post(UpdateRec);
        UpdateRec();

        var reset = Ui.Button(Loc.T("Zerar estatísticas", "Reset statistics"), () => SensorHub.Instance.ResetStats());
        var header = Header(_eyebrow, Loc.T("Monitoramento em tempo real", "Real-time monitoring"), lbl, intervals, reset, _rec);

        // Alertas
        var add = Ui.Button(Loc.T("+ Novo alerta", "+ New alert"), () => _newAlert.Visibility = _newAlert.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, "LinkButton");
        var alertHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(add, Dock.Right);
        alertHead.Children.Add(add);
        alertHead.Children.Add(Title(Loc.T("Alertas", "Alerts")));
        var alertCard = Card(new Stack(6, alertHead, _newAlert, _alertRows), 20, 18);

        var allCard = Card(new Stack(10, Title(Loc.T("Todos os sensores", "All sensors")), _allTable), 20, 18);

        Content = Scroll(new Stack(18, header, _cardsHost, alertCard, allCard));
    }

    void SetInterval(int ms)
    {
        Settings.Current.IntervalMs = ms;
        Settings.Save();
        SensorHub.Instance.Poke();
    }

    void UpdateRec()
    {
        bool on = CsvLogger.Active;
        _rec.SetResourceReference(BackgroundProperty, on ? "CritBg" : "Tile");
        _recText.SetResourceReference(TextBlock.ForegroundProperty, on ? "CritText" : "Text2");
        _recDot.SetResourceReference(Border.BackgroundProperty, on ? "Crit" : "TextFaint");
        _recText.Text = on ? Loc.T("Gravando CSV · parar", "Recording CSV · stop") : Loc.T("Gravar log CSV", "Record CSV log");
        _rec.ToolTip = on ? CsvLogger.FilePath : Loc.T("Grava todas as leituras (incluindo FPS) num arquivo CSV em Documentos\\Pulso\\Logs para analisar depois, por exemplo no Excel.",
                                                       "Records every reading (including FPS) to a CSV file in Documents\\Pulso\\Logs for later analysis, e.g. in Excel.");
    }

    static void OpenLogFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{CsvLogger.FilePath}\"") { UseShellExecute = true }); } catch { }
    }

    IDisposable? _fps;

    protected override void OnShown()
    {
        if (!_built && SensorHub.Instance.Ready) Build();
        RenderAlerts();
        _fps ??= FpsMonitor.Instance.Acquire();
    }

    protected override void OnHidden()
    {
        _fps?.Dispose();
        _fps = null;
    }

    void Build()
    {
        _built = true;
        var hub = SensorHub.Instance;
        var defs = new List<(string id, string name, string brush)>
        {
            ("cpu.load", Loc.T("CPU · uso", "CPU · usage"), "SeriesLime"),
            ("cpu.temp", Loc.T("CPU · temperatura", "CPU · temperature"), "SeriesAmber"),
            ("cpu.clock", "CPU · clock", "SeriesLime"),
            ("cpu.power", Loc.T("CPU · consumo", "CPU · power"), "SeriesWhite"),
            ("gpu0.load", Loc.T("GPU · uso", "GPU · usage"), "SeriesLime"),
            ("gpu0.temp", Loc.T("GPU · temperatura", "GPU · temperature"), "SeriesAmber"),
            ("gpu0.clock", "GPU · clock", "SeriesLime"),
            ("gpu0.power", Loc.T("GPU · consumo", "GPU · power"), "SeriesWhite"),
            ("ram.load", Loc.T("RAM · uso", "RAM · usage"), "SeriesLime"),
            ("gpu0.vram", Loc.T("VRAM · uso", "VRAM · usage"), "SeriesLime"),
            ("disk0.activity", Loc.T("Disco · atividade", "Disk · activity"), "SeriesWhite"),
            ("net.down", Loc.T("Rede · download", "Network · download"), "SeriesLime"),
            ("net.up", Loc.T("Rede · upload", "Network · upload"), "SeriesWhite"),
            ("disk0.read", Loc.T("Disco · leitura", "Disk · read"), "SeriesLime"),
            ("disk0.write", Loc.T("Disco · escrita", "Disk · write"), "SeriesAmber"),
            ("fps", Loc.T("Jogo · FPS", "Game · FPS"), "SeriesLime"),
        };
        var items = new List<UIElement>();
        foreach (var (id, name, brush) in defs)
        {
            if (!hub.MetricById.TryGetValue(id, out var m)) continue;
            var c = new MetricCard(m, name, brush);
            _cards.Add(c);
            items.Add(c.Root);
        }
        _cardsHost.Children.Add(Wrap(4, 14, items));
        BuildAllTable();
    }

    void BuildAllTable()
    {
        var hub = SensorHub.Instance;
        _allTable.Children.Clear();
        _allTable.ColumnDefinitions.Clear();
        _allTable.RowDefinitions.Clear();
        foreach (var w in new[] { 2.4, 1, 1, 1, 1 }) _allTable.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        string[] head = { Loc.T("Sensor", "Sensor"), Loc.T("Atual", "Current"), Loc.T("Mínimo", "Min"), Loc.T("Média", "Average"), Loc.T("Máximo", "Max") };
        _allTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < head.Length; c++)
        {
            var t = Text(head[c], 12, FontWeights.SemiBold, "Accent");
            t.Margin = new Thickness(0, 4, 0, 8);
            if (c > 0) t.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(t, c);
            _allTable.Children.Add(t);
        }
        int r = 1;
        foreach (var m in hub.MetricList)
        {
            _allTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) }.Res(Border.BorderBrushProperty, "Divider");
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, 5);
            _allTable.Children.Add(line);
            var name = Text(m.FullLabel, 13, null, "Text3");
            name.Margin = new Thickness(0, 8, 0, 8);
            Grid.SetRow(name, r);
            _allTable.Children.Add(name);
            TextBlock Cell(int col)
            {
                var t = Num("—", 12.5, "Text", FontWeights.Normal);
                t.TextAlignment = TextAlignment.Right;
                t.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(t, r); Grid.SetColumn(t, col);
                _allTable.Children.Add(t);
                return t;
            }
            _allCells[m.Id] = (Cell(1), Cell(2), Cell(3), Cell(4));
            r++;
        }
    }

    public override void OnSnapshot(Snapshot s)
    {
        var hub = SensorHub.Instance;
        if (!_built && hub.Ready) Build();
        var dur = DateTime.Now - hub.StatsSince;
        _eyebrow.Text = Loc.T($"Mínimo, máximo e média desde o início · sessão de {Fmt.Duration(dur)}", $"Minimum, maximum and average since start · session {Fmt.Duration(dur)}");
        foreach (var c in _cards) c.Update(s);
        foreach (var m in hub.MetricList)
        {
            if (!_allCells.TryGetValue(m.Id, out var cells)) continue;
            var st = hub.GetStats(m.Id);
            cells.now.Text = Fmt.Value(m.Get(s), m.Unit, true);
            cells.min.Text = Fmt.Value(st?.MinV, m.Unit, true);
            cells.avg.Text = Fmt.Value(st?.Avg, m.Unit, true);
            cells.max.Text = Fmt.Value(st?.MaxV, m.Unit, true);
        }
    }

    // ------------------------------------------------------------------- alertas

    void RenderAlerts()
    {
        var hub = SensorHub.Instance;
        _alertRows.Children.Clear();
        foreach (var rule in Settings.Current.Alerts.ToList())
        {
            if (!hub.MetricById.TryGetValue(rule.MetricId, out var m)) continue;
            var text = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            text.Inlines.Add(Loc.T("Notificar se ", "Notify if "));
            text.Inlines.Add(new Avalonia.Controls.Documents.Run(m.FullLabel) { FontWeight = FontWeights.Bold });
            text.Inlines.Add(Loc.T(" passar de ", " goes above "));
            var v = new Avalonia.Controls.Documents.Run(Fmt.Value(rule.Threshold, m.Unit)) { FontWeight = FontWeights.Bold, FontFamily = MonoFont };
            v.SetResourceReference(Avalonia.Controls.Documents.TextElement.ForegroundProperty, "Accent");
            text.Inlines.Add(v);
            var r = rule;
            var tog = Toggle(rule.Enabled, on => { r.Enabled = on; Settings.Save(); });
            var del = Ui.Button("✕", () => { Settings.Current.Alerts.Remove(r); Settings.Save(); RenderAlerts(); }, "LinkButton");
            del.SetResourceReference(ForegroundProperty, "TextFaint");
            del.Margin = new Thickness(14, 0, 0, 0);
            del.VerticalAlignment = VerticalAlignment.Center;
            del.ToolTip = Loc.T("Remover alerta", "Remove alert");
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(tog); right.Children.Add(del);
            var dock = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            dock.Children.Add(right);
            dock.Children.Add(text);
            _alertRows.Children.Add(new Border { Child = dock, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 10) }.Res(Border.BorderBrushProperty, "Divider"));
        }
        if (!Settings.Current.AlertsEnabled)
            _alertRows.Children.Add(Text(Loc.T("As notificações estão desligadas em Configurações.", "Notifications are turned off in Settings."), 12, null, "TextMuted"));
        BuildNewAlertForm();
    }

    void BuildNewAlertForm()
    {
        var hub = SensorHub.Instance;
        _newAlert.Children.Clear();
        var combo = new ComboBox { Width = 300, MaxDropDownHeight = 360 };
        foreach (var m in hub.MetricList.Where(m => m.Unit is Unit.Temp or Unit.Percent or Unit.Watt or Unit.MHz or Unit.MB or Unit.Fps or Unit.Ms)) combo.Items.Add(m);
        combo.SelectedIndex = 0;
        var value = new TextBox { Width = 90, Text = "90" };
        var unit = Label("");
        void Sync() { if (combo.SelectedItem is Metric m) unit.Text = Fmt.UnitSuffix(m.Unit); }
        combo.SelectionChanged += (_, _) => Sync();
        Sync();
        unit.VerticalAlignment = VerticalAlignment.Center;
        var save = Ui.Button(Loc.T("Adicionar", "Add"), () =>
        {
            if (combo.SelectedItem is not Metric m || !double.TryParse(value.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var th)) return;
            if (m.Unit == Unit.Temp && Settings.Current.Fahrenheit) th = (th - 32) * 5 / 9;
            Settings.Current.Alerts.Add(new AlertRule { MetricId = m.Id, Threshold = th, Enabled = true });
            Settings.Save();
            _newAlert.Visibility = Visibility.Collapsed;
            RenderAlerts();
        }, "Primary");
        save.Padding = new Thickness(16, 8, 16, 8);
        var row = new Stack(10, Text(Loc.T("Notificar se", "Notify if"), 13), combo, Text(Loc.T("passar de", "goes above"), 13), value, unit, save) { Orientation = Orientation.Horizontal };
        foreach (FrameworkElement c in row.Children) c.VerticalAlignment = VerticalAlignment.Center;
        _newAlert.Children.Add(new Border { Child = row, Padding = new Thickness(0, 6, 0, 10) });
    }

    /// <summary>Cartão com valor atual, minigráfico e mín/méd/máx.</summary>
    sealed class MetricCard
    {
        public readonly Border Root;
        readonly Metric _m;
        readonly TextBlock _val, _min, _avg, _max;
        readonly LineChart _chart;

        public MetricCard(Metric m, string name, string brush)
        {
            _m = m;
            _val = Num("—", 18, brush);
            var head = new DockPanel();
            DockPanel.SetDock(_val, Dock.Right);
            head.Children.Add(_val);
            var n = Label(name); n.VerticalAlignment = VerticalAlignment.Bottom; n.Margin = new Thickness(0, 0, 0, 3);
            head.Children.Add(n);
            _chart = new LineChart { Height = 64, ShowAxis = false, Points = 60, Unit = m.Unit, SyncRoot = SensorHub.Instance.HistoryLock, FillAlpha = 90, LineWidth = 1.6 };
            if (m.Unit == Unit.Percent) { _chart.FixedMin = 0; _chart.FixedMax = 100; }
            else _chart.FixedMin = m.Unit is Unit.Temp or Unit.MHz ? null : 0;
            if (SensorHub.Instance.GetSeries(m.Id) is Series s) _chart.Lines.Add(new ChartLine { Data = s, BrushKey = brush });
            TextBlock Stat(string label, Grid g, int col, TextAlignment al)
            {
                var t = new TextBlock { FontSize = 11, TextAlignment = al };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                t.Inlines.Add(label + " ");
                var v = new Avalonia.Controls.Documents.Run("—") { FontFamily = MonoFont, FontWeight = FontWeights.Medium };
                v.SetResourceReference(Avalonia.Controls.Documents.TextElement.ForegroundProperty, "Text2");
                t.Inlines.Add(v);
                t.Tag = v;
                Grid.SetColumn(t, col);
                g.Children.Add(t);
                return t;
            }
            var stats = new Grid();
            for (int i = 0; i < 3; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
            _min = Stat(Loc.T("mín", "min"), stats, 0, TextAlignment.Left);
            _avg = Stat(Loc.T("méd", "avg"), stats, 1, TextAlignment.Left);
            _max = Stat(Loc.T("máx", "max"), stats, 2, TextAlignment.Right);
            Root = Card(new Stack(8, head, _chart, stats), 16, 14);
            Root.CornerRadius = new CornerRadius(16);
        }

        public void Update(Snapshot s)
        {
            var st = SensorHub.Instance.GetStats(_m.Id);
            _val.Text = Fmt.Value(_m.Get(s), _m.Unit, true);
            ((Avalonia.Controls.Documents.Run)_min.Tag).Text = Fmt.Value(st?.MinV, _m.Unit, true);
            ((Avalonia.Controls.Documents.Run)_avg.Tag).Text = Fmt.Value(st?.Avg, _m.Unit, true);
            ((Avalonia.Controls.Documents.Run)_max.Tag).Text = Fmt.Value(st?.MaxV, _m.Unit, true);
            _chart.Refresh();
        }
    }
}
