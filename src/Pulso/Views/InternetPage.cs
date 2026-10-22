using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Hardware;
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

/// <summary>Medidor semicircular do teste de velocidade.</summary>
public sealed class Gauge : FrameworkElement
{
    public double Value { get; set; }
    public string Caption { get; set; } = "";

    public Gauge() { Width = 220; Height = 124; }

    static double Scale(double v) => v <= 100 ? 100 : v <= 250 ? 250 : v <= 500 ? 500 : v <= 1000 ? 1000 : v <= 2500 ? 2500 : 10000;

    protected override void OnRender(DrawingContext dc)
    {
        var track = Ui.Brush("Divider");
        var fill = Ui.Brush("AccentFill");
        var cx = 110.0; var cy = 112.0; var r = 90.0;
        var bg = new Pen(track, 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, bg, Arc(cx, cy, r, 1));
        double f = Math.Clamp(Value / Scale(Value), 0, 1);
        if (f > 0.005)
            dc.DrawGeometry(null, new Pen(fill, 14) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(cx, cy, r, f));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var num = new FormattedText(Fmt.N(Value, Value >= 100 ? 0 : 1), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Ui.MonoFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 30, Ui.Brush("Text"), dpi);
        dc.DrawText(num, new Point(cx - num.Width / 2, 92 - num.Height + 6));
        var cap = new FormattedText(Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Ui.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, Ui.Brush("TextMuted"), dpi);
        dc.DrawText(cap, new Point(cx - cap.Width / 2, 100));
    }

    static Geometry Arc(double cx, double cy, double r, double f)
    {
        var start = new Point(cx - r, cy);
        double a = Math.PI - f * Math.PI;
        var end = new Point(cx + r * Math.Cos(a), cy - r * Math.Sin(a));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(start, false, false);
            c.ArcTo(end, new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}

public sealed class InternetPage : PageBase
{
    readonly TextBlock _eyebrow = Label("");
    readonly Gauge _gauge = new() { Caption = Loc.T("Mbps · download", "Mbps · download") };
    readonly Button _start;
    readonly ComboBox _servers = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock _up = Num("—", 20, "Text", FontWeights.ExtraBold), _ping = Num("—", 20, "Text", FontWeights.ExtraBold), _jit = Num("—", 20, "Text", FontWeights.ExtraBold), _loss = Num("—", 20, "Text", FontWeights.ExtraBold);
    readonly TextBlock _phase = Text("", 12, null, "TextMuted");
    readonly Stack _conn = new(0);
    readonly TextBox _target = new() { Width = 220, FontFamily = MonoFont };
    readonly Button _pingBtn;
    readonly TextBlock _pNow = new(), _pMin = new(), _pMax = new(), _pLoss = new(), _pMode = Text("", 11.5, null, "TextMuted");
    readonly LineChart _pingChart;
    readonly PingMonitor _pingMon = new();
    readonly Grid _hist = new();
    CancellationTokenSource? _cts;

    public InternetPage()
    {
        _start = Ui.Button(Loc.T("Iniciar teste", "Start test"), StartStop, "Primary");
        _start.HorizontalAlignment = HorizontalAlignment.Stretch;
        _servers.SelectionChanged += (_, _) => { if (_servers.SelectedItem is SpeedServer s) { Settings.Current.SpeedTestServer = s.Id; Settings.Save(); } };

        var gaugeCol = new Stack(14, _gauge, _start, _servers, _phase) { Width = 240 };
        _gauge.HorizontalAlignment = HorizontalAlignment.Center;
        _phase.HorizontalAlignment = HorizontalAlignment.Center;
        var tiles = Wrap(2, 10, new UIElement[]
        {
            SpeedTile("Upload", _up, "Mbps", Loc.T("Velocidade de envio: importa para chamadas de vídeo, lives e enviar arquivos.", "Upload speed: matters for video calls, streaming and sending files.")),
            SpeedTile("Ping", _ping, "ms", Loc.T("Tempo de resposta do servidor. Abaixo de 30 ms é ótimo para jogos.", "Server response time. Below 30 ms is great for games.")),
            SpeedTile("Jitter", _jit, "ms", Loc.T("Variação do ping. Valores altos causam travadas em jogos online e chamadas.", "Ping variation. High values cause lag in online games and calls.")),
            SpeedTile(Loc.T("Perda de pacotes", "Packet loss"), _loss, "%", Loc.T("Dados que não chegam ao destino. Qualquer valor acima de 1% atrapalha jogos online.", "Data that never arrives. Anything above 1% hurts online gaming.")),
        });
        var speedGrid = new Grid();
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition());
        tiles.Margin = new Thickness(24, 0, 0, 0);
        tiles.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tiles, 1);
        speedGrid.Children.Add(gaugeCol); speedGrid.Children.Add(tiles);
        var speedCard = Card(speedGrid, 22, 22);
        var connCard = Card(new Stack(0, WithBottom(Title(Loc.T("Conexão", "Connection")), 10), _conn), 20, 18);

        // ping contínuo
        _target.Text = Settings.Current.PingTarget;
        _target.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) TogglePing(); };
        _pingBtn = Ui.Button(Loc.T("Iniciar", "Start"), TogglePing);
        _pingBtn.Padding = new Thickness(14, 7, 14, 7);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var pt = Title(Loc.T("Ping contínuo", "Continuous ping")); pt.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(pt);
        _target.Margin = new Thickness(12, 0, 8, 0);
        left.Children.Add(_target);
        left.Children.Add(_pingBtn);
        _pMode.VerticalAlignment = VerticalAlignment.Center; _pMode.Margin = new Thickness(10, 0, 0, 0);
        left.Children.Add(_pMode);
        var stats = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (lbl, tb, key) in new[] { (Loc.T("atual", "now"), _pNow, "Accent"), (Loc.T("mín", "min"), _pMin, "Text"), (Loc.T("máx", "max"), _pMax, "Text"), (Loc.T("perda", "loss"), _pLoss, "Text") })
        {
            var t = new TextBlock { FontSize = 12.5, Margin = new Thickness(18, 0, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text3");
            t.Inlines.Add(lbl + " ");
            tb.FontFamily = MonoFont; tb.FontWeight = FontWeights.Bold; tb.Text = "—";
            tb.SetResourceReference(TextBlock.ForegroundProperty, key);
            t.Inlines.Add(new System.Windows.Documents.InlineUIContainer(tb) { BaselineAlignment = BaselineAlignment.Baseline });
            stats.Children.Add(t);
        }
        var pingHead = new DockPanel();
        DockPanel.SetDock(stats, Dock.Right);
        pingHead.Children.Add(stats); pingHead.Children.Add(left);
        _pingChart = new LineChart { Height = 110, ShowAxis = true, Points = 180, Unit = Unit.Ms, FixedMin = 0 };
        _pingChart.Lines.Add(new ChartLine { Data = _pingMon.Data, BrushKey = "SeriesLime" });
        var pingCard = Card(new Stack(12, pingHead, _pingChart));
        _pingMon.Updated += () => Dispatcher.BeginInvoke(UpdatePing);

        var histCard = Card(new Stack(0, WithBottom(Title(Loc.T("Histórico", "History")), 10), _hist), 20, 18);

        Content = Scroll(new Stack(18,
            Header(_eyebrow, "Internet"),
            Cols(18, new[] { 1.25, 1.0 }, speedCard, connCard),
            pingCard, histCard));
        RenderHistory();
        Unloaded += (_, _) => { if (!IsVisible) _pingMon.Stop(); };
    }

    static FrameworkElement WithBottom(FrameworkElement e, double b) { e.Margin = new Thickness(0, 0, 0, b); return e; }

    static Border SpeedTile(string label, TextBlock value, string unit, string tip)
    {
        var l = Text(label, 11.5, null, "TextMuted");
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(l);
        head.Children.Add(Help(tip));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(value);
        var u = Text(" " + unit, 12, null, "TextMuted");
        u.VerticalAlignment = VerticalAlignment.Bottom; u.Margin = new Thickness(4, 0, 0, 3);
        row.Children.Add(u);
        return Tile(new Stack(3, head, row), 14, 12);
    }

    protected override async void OnShown()
    {
        _ = LoadConnectionAsync();
        if (_servers.Items.Count == 0)
        {
            var list = await SpeedTest.ServersAsync();
            foreach (var s in list) _servers.Items.Add(s);
            _servers.SelectedItem = list.FirstOrDefault(s => s.Id == Settings.Current.SpeedTestServer) ?? list[0];
        }
        _pingChart.Refresh();
    }

    protected override void OnHidden() { }

    async Task LoadConnectionAsync()
    {
        var pub = await Http.GetPublicInfoAsync();
        var wifi = await Task.Run(InfoPages.Wifi);
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(SensorHub.IsPhysical)
            .Where(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.ToString() != "0.0.0.0"))
            .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .FirstOrDefault();
        bool wl = nic?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        string type = nic == null ? Fmt.NA : (wl ? "Wi-Fi" : "Ethernet") + (nic.Speed > 0 ? " · " + Fmt.Mbps(nic.Speed).Replace("1.000,0 Mbps", "1 Gbps").Replace("1,000.0 Mbps", "1 Gbps") : "");
        _eyebrow.Text = string.Join(" · ", new[] { nic == null ? null : wl ? "Wi-Fi" : "Ethernet", nic?.Description, pub.Isp }.Where(x => !string.IsNullOrEmpty(x)));
        var w = wifi.FirstOrDefault();
        _conn.Children.Clear();
        _conn.Children.Add(Row(Loc.T("IP público", "Public IP"), pub.Ipv4 ?? pub.Ip ?? Fmt.NA));
        _conn.Children.Add(Row(Loc.T("Provedor", "Provider"), pub.Isp ?? Fmt.NA, null, pub.Isp == null ? "TextFaint" : "Text"));
        _conn.Children.Add(Row(Loc.T("Tipo de conexão", "Connection type"), type));
        _conn.Children.Add(Row(Loc.T("Sinal do Wi-Fi", "Wi-Fi signal"), wl && w != null ? $"{w.Signal}%" + (w.Rssi != 0 ? $" · {w.Rssi} dBm" : "") + (w.Band != "" ? $" · {w.Band}" : "") : Fmt.NA,
            Loc.T("Acima de 60% é bom. Para jogar, cabo de rede é sempre mais estável.", "Above 60% is good. For gaming, a network cable is always more stable."),
            wl && w != null ? (w.Signal >= 60 ? "Good" : w.Signal >= 35 ? "Warn" : "Crit") : "TextFaint"));
        _conn.Children.Add(Row("DNS", nic == null ? Fmt.NA : string.Join(" · ", nic.GetIPProperties().DnsAddresses.Select(a => a.ToString()).Take(2))));
        _conn.Children.Add(Row(Loc.T("Localização", "Location"), string.Join(", ", new[] { pub.City, pub.Country }.Where(x => !string.IsNullOrEmpty(x))) is var loc && loc != "" ? loc : Fmt.NA));
    }

    async void StartStop()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (_servers.SelectedItem is not SpeedServer s) return;
        _cts = new CancellationTokenSource();
        _start.Content = Loc.T("Cancelar", "Cancel");
        _servers.IsEnabled = false;
        _up.Text = _ping.Text = _jit.Text = _loss.Text = "—";
        try
        {
            var r = await SpeedTest.RunAsync(s, new Progress<SpeedTest.Progress>(p =>
            {
                if (p.Phase == "down") { _gauge.Caption = Loc.T("Mbps · download", "Mbps · download"); _gauge.Value = p.Mbps; _gauge.InvalidateVisual(); }
                if (p.Phase == "up") { _up.Text = Fmt.N(p.Mbps, 1); }
                if (p.Ping is double pg) { _ping.Text = Fmt.N(pg); _jit.Text = Fmt.N(p.Jitter ?? 0, 1); }
                _phase.Text = p.Phase switch
                {
                    "ping" => Loc.T("Medindo latência…", "Measuring latency…"),
                    "down" => Loc.T("Medindo download…", "Measuring download…"),
                    "up" => Loc.T("Medindo upload…", "Measuring upload…"),
                    "loss" => Loc.T("Medindo perda de pacotes…", "Measuring packet loss…"),
                    _ => "",
                };
            }), _cts.Token);
            _gauge.Value = r.Down; _gauge.InvalidateVisual();
            _up.Text = Fmt.N(r.Up, 1);
            _loss.Text = r.Loss is double l ? Fmt.N(l, l < 1 && l > 0 ? 1 : 0) : Fmt.Dash;
            _loss.ToolTip = r.Loss == null ? Loc.T("O servidor não responde a ping (ICMP), então a perda não pôde ser medida.", "The server does not answer ping (ICMP), so loss could not be measured.") : null;
            _phase.Text = Loc.T("Concluído", "Done");
            SpeedTest.SaveResult(r);
            RenderHistory();
        }
        catch (OperationCanceledException) { _phase.Text = Loc.T("Cancelado", "Cancelled"); }
        catch (Exception ex) { _phase.Text = Loc.T("Falhou: ", "Failed: ") + ex.Message; Paths.LogError("SpeedTest", ex); }
        _cts = null;
        _start.Content = Loc.T("Iniciar teste", "Start test");
        _servers.IsEnabled = true;
    }

    void TogglePing()
    {
        if (_pingMon.Running) { _pingMon.Stop(); _pingBtn.Content = Loc.T("Iniciar", "Start"); return; }
        var t = _target.Text.Trim();
        if (t == "") return;
        Settings.Current.PingTarget = t;
        Settings.Save();
        _pingMon.Start(t);
        _pingBtn.Content = Loc.T("Parar", "Stop");
    }

    void UpdatePing()
    {
        string Ms(double? v) => v is double x ? Fmt.N(x) + " ms" : "—";
        _pNow.Text = _pingMon.Last is double l ? Ms(l) : (_pingMon.Sent > 0 ? Loc.T("perdido", "lost") : "—");
        _pMin.Text = Ms(_pingMon.Min);
        _pMax.Text = Ms(_pingMon.Max);
        _pLoss.Text = Fmt.N(_pingMon.LossPct, _pingMon.LossPct is > 0 and < 10 ? 1 : 0) + "%";
        _pMode.Text = _pingMon.UsingTcp ? Loc.T("via TCP (o destino bloqueia ping)", "via TCP (target blocks ping)") : "";
        if (Shown) _pingChart.Refresh();
    }

    void RenderHistory()
    {
        _hist.Children.Clear(); _hist.ColumnDefinitions.Clear(); _hist.RowDefinitions.Clear();
        double[] w = { 1.4, 1.6, 1, 1, 1, 1, 1 };
        foreach (var x in w) _hist.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(x, GridUnitType.Star) });
        string[] head = { Loc.T("Data e hora", "Date and time"), Loc.T("Servidor", "Server"), "Download", "Upload", "Ping", "Jitter", Loc.T("Perda", "Loss") };
        _hist.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < head.Length; c++)
        {
            var t = Text(head[c], 12, FontWeights.SemiBold, "Accent");
            t.Margin = new Thickness(0, 8, 0, 8);
            if (c >= 2) t.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(t, c); _hist.Children.Add(t);
        }
        int r = 1;
        foreach (var e in SpeedTest.History.Take(10))
        {
            _hist.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) }.Res(Border.BorderBrushProperty, "Divider");
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, w.Length); _hist.Children.Add(line);
            string[] cells = { e.Date.ToString("g", Fmt.Culture), e.Server, Fmt.N(e.Down, 1), Fmt.N(e.Up, 1), Fmt.N(e.Ping) + " ms", Fmt.N(e.Jitter, 1) + " ms", e.Loss is double l ? Fmt.N(l, 1) + "%" : "—" };
            for (int c = 0; c < cells.Length; c++)
            {
                var t = c == 1 ? Text(cells[c], 13) : Num(cells[c], 13, c == 0 ? "Text3" : "Text", FontWeights.Normal);
                t.Margin = new Thickness(0, 10, 8, 10);
                if (c >= 2) t.TextAlignment = TextAlignment.Right;
                Grid.SetRow(t, r); Grid.SetColumn(t, c); _hist.Children.Add(t);
            }
            r++;
        }
        if (r == 1)
        {
            var empty = Text(Loc.T("Nenhum teste ainda.", "No tests yet."), 13, null, "TextMuted");
            empty.Margin = new Thickness(0, 10, 0, 4);
            _hist.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(empty, 1); Grid.SetColumnSpan(empty, w.Length); _hist.Children.Add(empty);
        }
    }
}
