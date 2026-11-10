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
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

/// <summary>Aparelhos conectados na rede local: lista, nomes, detalhes, presença e aviso de aparelho novo.</summary>
public sealed class LanPage : PageBase
{
    readonly TextBlock _eyebrow = Label("");
    readonly TextBlock _online = Num("—", 20), _total = Num("—", 20), _new = Num("—", 20), _last = Num("—", 20);
    readonly Button _scan;
    readonly Bar _bar = new(6) { Visibility = Visibility.Collapsed };
    readonly TextBlock _status = Text("", 12.5, null, "TextMuted");
    readonly Stack _banners = new(10);
    readonly Stack _list = new(0);
    readonly HashSet<string> _open = new();
    string _filter = "all";
    bool _loaded, _dirty;

    static LanSettings S => Settings.Current.Lan;

    public LanPage()
    {
        _scan = Ui.Button(Loc.T("Escanear agora", "Scan now"), () => _ = ScanAsync(), "PrimaryPill");
        var tiles = Wrap(4, 12, new UIElement[]
        {
            StatTile(Loc.T("Online agora", "Online now"), _online, null),
            StatTile(Loc.T("Aparelhos conhecidos", "Known devices"), _total, Loc.T("Todos os aparelhos que já apareceram nesta rede, online ou não.", "Every device ever seen on this network, online or not.")),
            StatTile(Loc.T("Novos", "New"), _new, Loc.T("Entraram depois da primeira varredura e ainda não foram nomeados nem marcados como conhecidos.", "Joined after the first scan and haven't been named or marked as known yet.")),
            StatTile(Loc.T("Última varredura", "Last scan"), _last, null),
        });

        var filters = Segments("lanfilter", new (string, bool, Action)[]
        {
            (Loc.T("Todos", "All"), true, () => SetFilter("all")),
            (Loc.T("Online", "Online"), false, () => SetFilter("online")),
            (Loc.T("Offline", "Offline"), false, () => SetFilter("offline")),
            (Loc.T("Novos", "New"), false, () => SetFilter("new")),
        });
        var how = Text(Loc.T("O Pulso pergunta a cada endereço da rede quem está lá e lê os nomes que os próprios aparelhos anunciam. Nada é instalado neles. Clique num aparelho para dar um nome e ver os detalhes.",
                             "Pulso asks every address on the network who's there and reads the names devices announce themselves. Nothing is installed on them. Click a device to name it and see details."), 12.5, null, "Text3", wrap: true);
        var listCard = Card(new Stack(14, filters, how, _list), 20, 18);

        var monitor = Toggle(S.Monitor, on => { S.Monitor = on; SaveSettings(); });
        var notify = Toggle(S.NotifyNew, on => { S.NotifyNew = on; SaveSettings(); });
        var every = Segments("laninterval", new[] { 2, 5, 15, 30 }.Select(m => (Loc.T($"{m} min", $"{m} min"), S.IntervalMin == m, (Action)(() => { S.IntervalMin = m; SaveSettings(); }))));
        var cfgCard = Card(new Stack(0,
            SettingRow(Loc.T("Monitorar a rede em segundo plano", "Monitor the network in the background"),
                Loc.T("Repete a varredura enquanto o Pulso estiver aberto, inclusive na bandeja. Assim o histórico de presença fica completo.",
                      "Repeats the scan while Pulso is open, including in the tray, so the presence history stays complete."), monitor, first: true),
            SettingRow(Loc.T("Intervalo", "Interval"), null, every),
            SettingRow(Loc.T("Avisar quando um aparelho novo entrar", "Notify when a new device joins"),
                Loc.T("Notificação do Windows quando aparece um aparelho que nunca esteve na rede.", "Windows notification when a device that has never been on the network shows up."), notify)), 20, 6);

        var limits = Text(Loc.T("Consumo de internet por aparelho e bloqueio não aparecem aqui: o tráfego dos outros aparelhos vai direto para o roteador e não passa por este PC. Isso depende de integração com o roteador.",
                                "Per-device internet usage and blocking aren't shown here: other devices' traffic goes straight to the router and never passes through this PC. That requires router integration."), 12, null, "TextMuted", wrap: true);

        Content = Scroll(new Stack(18,
            Header(_eyebrow, Loc.T("Aparelhos na rede", "Network devices"), _scan),
            new Stack(6, _bar, _status),
            _banners, tiles, listCard,
            new Stack(10, Caps(Loc.T("Monitoramento", "Monitoring")), cfgCard), limits));

        _status.Visibility = Visibility.Collapsed;
        LanScanner.Changed += () => Dispatcher.UIThread.Post(OnChanged);
        LanScanner.Progress += (p, t) => Dispatcher.UIThread.Post(() =>
        {
            _bar.Visibility = _status.Visibility = Visibility.Visible;
            _bar.Value = p;
            if (t.Length > 0) _status.Text = t;
        });
        // não redesenha a lista enquanto o usuário digita um nome
        _list.LostFocus += (_, _) => { if (_dirty && !_list.IsKeyboardFocusWithin) Render(); };
    }

    static Border StatTile(string label, TextBlock value, string? tip)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Text(label, 11.5, null, "TextMuted"));
        if (tip != null) head.Children.Add(Help(tip));
        return Tile(new Stack(4, head, value), 14, 12);
    }

    static void SaveSettings()
    {
        Settings.Save();
        LanScanner.Start();
    }

    void SetFilter(string f)
    {
        _filter = f;
        Render();
    }

    protected override async void OnShown()
    {
        if (!_loaded)
        {
            _loaded = true;
            await Task.Run(LanScanner.EnsureNetwork);
        }
        Render();
        // abre já com a lista atualizada (a varredura leva uns 15 s e não trava nada)
        if (!LanScanner.Scanning && (LanScanner.LastScan is not DateTime t || DateTime.Now - t > TimeSpan.FromMinutes(2)))
            _ = ScanAsync();
    }

    async Task ScanAsync()
    {
        if (LanScanner.Scanning) return;
        _scan.IsEnabled = false;
        _bar.Value = 0;
        _bar.Visibility = _status.Visibility = Visibility.Visible;
        var r = await LanScanner.ScanAsync();
        _scan.IsEnabled = true;
        _bar.Visibility = Visibility.Collapsed;
        _status.Text = r == null ? "" : r.NewDevices.Count > 0
            ? Loc.T($"{r.Online} aparelhos online · {r.NewDevices.Count} novo(s)", $"{r.Online} devices online · {r.NewDevices.Count} new")
            : Loc.T($"{r.Online} aparelhos online", $"{r.Online} devices online");
        _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    void OnChanged()
    {
        _scan.IsEnabled = !LanScanner.Scanning;
        if (!Shown || _list.IsKeyboardFocusWithin) { _dirty = true; return; }
        Render();
    }

    // ------------------------------------------------------------------ lista

    void Render()
    {
        _dirty = false;
        var net = LanScanner.Net;
        _eyebrow.Text = net != null
            ? Loc.T($"{net.AdapterKind} · rede {net.Cidr} · roteador {net.Gateway}", $"{net.AdapterKind} · network {net.Cidr} · router {net.Gateway}")
            : Loc.T("Procurando a rede…", "Looking for the network…");

        var all = LanScanner.Devices();
        bool scanned = LanScanner.LastScan != null;
        _online.Text = scanned ? Fmt.N(all.Count(d => d.Online)) : Fmt.Dash;
        _total.Text = scanned ? Fmt.N(all.Count) : Fmt.Dash;
        int nNew = all.Count(d => !d.Known);
        _new.Text = scanned ? Fmt.N(nNew) : Fmt.Dash;
        _new.SetResourceReference(TextBlock.ForegroundProperty, nNew > 0 ? "Warn" : "Text");
        _last.Text = LanScanner.LastScan is DateTime t ? Ago(t) : Fmt.Dash;

        _banners.Children.Clear();
        if (LanScanner.Error is string err) _banners.Children.Add(Banner(err, Health.Critical, false));
        if (nNew > 0)
            _banners.Children.Add(Banner(nNew == 1
                ? Loc.T("1 aparelho novo entrou na rede. Se você reconhece, dê um nome a ele ou clique aqui para marcar como conhecido.", "1 new device joined the network. If you recognize it, name it or click here to mark it as known.")
                : Loc.T($"{nNew} aparelhos novos entraram na rede. Se você reconhece todos, clique aqui para marcar como conhecidos.", $"{nNew} new devices joined the network. If you recognize them all, click here to mark them as known."),
                Health.Warning, false, LanScanner.AcknowledgeAll));

        var list = all.Where(d => _filter switch
        {
            "online" => d.Online,
            "offline" => !d.Online,
            "new" => !d.Known,
            _ => true,
        })
        .OrderByDescending(d => d.Online)
        .ThenByDescending(d => d.IsGateway)
        .ThenByDescending(d => d.IsSelf)
        .ThenBy(d => d.Online ? IpOrder(d.Ip) : 0)
        .ThenByDescending(d => d.LastSeen)
        .ToList();

        _list.Children.Clear();
        bool first = true;
        foreach (var d in list)
        {
            _list.Children.Add(DeviceRow(d, first));
            first = false;
        }
        if (list.Count == 0)
            _list.Children.Add(Text(!scanned ? Loc.T("Procurando aparelhos pela primeira vez…", "Looking for devices for the first time…")
                : Loc.T("Nenhum aparelho neste filtro.", "No devices in this filter."), 13, null, "TextMuted"));
    }

    static long IpOrder(string? ip) => System.Net.IPAddress.TryParse(ip, out var a) && a.GetAddressBytes() is { Length: 4 } b ? (long)b[0] << 24 | (long)b[1] << 16 | (long)b[2] << 8 | b[3] : long.MaxValue;

    UIElement DeviceRow(LanDevice d, bool first)
    {
        bool open = _open.Contains(d.Mac);
        var icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Child = TypeIcon(d.EffectiveType, d.Online ? "Accent" : "TextMuted", 22), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center }.Res(Border.BackgroundProperty, "Well");

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var name = Text(d.DisplayName, 13.5, FontWeights.SemiBold, d.Online ? "Text" : "Text3");
        title.Children.Add(name);
        if (!d.Known) title.Children.Add(Chip(Loc.T("NOVO", "NEW"), "Warn"));
        if (d.IsSelf) title.Children.Add(Chip(Loc.T("ESTE PC", "THIS PC"), "Accent"));

        var parts = new List<string?> { d.Ip ?? Loc.T("sem IP", "no IP") };
        // quando o nome mostrado é o do usuário, mostra também o nome que o aparelho anuncia
        if (!string.IsNullOrWhiteSpace(d.Name)) parts.Add(d.Friendly ?? d.Hostname);
        parts.Add(d.RandomMac ? Loc.T("MAC privado", "Private MAC") : d.Vendor);
        parts.Add(LanTypes.Label(d.EffectiveType));
        var sub = Text(string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))), 12, null, "TextMuted");
        var info = new Stack(3, title, sub) { VerticalAlignment = VerticalAlignment.Center };

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (d.Online && d.LatencyMs is double ms && !d.IsSelf)
        {
            var lat = Num(Fmt.N(ms) + " ms", 12.5, ms > 100 ? "Warn" : "Text2", FontWeights.SemiBold);
            lat.Margin = new Thickness(0, 0, 18, 0);
            lat.VerticalAlignment = VerticalAlignment.Center;
            lat.ToolTip = Loc.T("Tempo de resposta (ping). Celular em repouso costuma responder devagar.", "Response time (ping). Sleeping phones usually respond slowly.");
            right.Children.Add(lat);
        }
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center }.Res(Border.BackgroundProperty, d.Online ? "Good" : "TextMuted");
        right.Children.Add(dot);
        var st = Text(d.Online ? Loc.T("Online", "Online") : Ago(d.LastSeen), 12.5, FontWeights.SemiBold, d.Online ? "Good" : "TextMuted");
        st.MinWidth = 70;
        st.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(st);
        var chev = new TextBlock { Text = open ? "" : "", FontSize = 11, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        chev.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        chev.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        right.Children.Add(chev);

        var head = new DockPanel { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(icon);
        head.Children.Add(right);
        head.Children.Add(info);
        head.PointerReleased += (_, _) =>
        {
            if (!_open.Add(d.Mac)) _open.Remove(d.Mac);
            Render();
        };

        var body = new Stack(0, head);
        if (open) body.Children.Add(Details(d));
        return new Border { Child = body, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 11, 0, 11) }.Res(Border.BorderBrushProperty, "Divider");
    }

    /// <summary>Ícone desenhado do tipo de aparelho (mesmo traço dos ícones do menu).</summary>
    public static Viewbox TypeIcon(string type, string brush, double size)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(LanTypes.Icon(type)), Width = 24, Height = 24, StrokeThickness = 1.7,
            StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round,
        };
        path.SetResourceReference(Avalonia.Controls.Shapes.Shape.StrokeProperty, brush);
        return new Viewbox { Width = size, Height = size, Child = path, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    static Border Chip(string text, string brush)
    {
        var t = Text(text, 10, FontWeights.Bold, brush);
        return new Border { Child = t, CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) }
            .Res(Border.BorderBrushProperty, brush);
    }

    UIElement Details(LanDevice d)
    {
        // nome e tipo
        var nameBox = new TextBox { Text = d.Name ?? "", MinWidth = 240, MaxLength = 60 };
        var type = new ComboBox { MinWidth = 210 };
        type.Items.Add(new ComboBoxItem { Content = Loc.T($"Automático ({LanTypes.Label(d.DetectedType)})", $"Automatic ({LanTypes.Label(d.DetectedType)})"), Tag = null });
        foreach (var (id, pt, en, _) in LanTypes.All)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = TypeIcon(id, "Text2", 16);
            ic.Margin = new Thickness(0, 0, 8, 0);
            item.Children.Add(ic);
            item.Children.Add(new TextBlock { Text = Loc.T(pt, en), VerticalAlignment = VerticalAlignment.Center });
            type.Items.Add(new ComboBoxItem { Content = item, Tag = id });
        }
        type.SelectedIndex = d.Type == null ? 0 : Math.Max(0, Array.FindIndex(LanTypes.All, t => t.id == d.Type) + 1);
        void Save()
        {
            LanScanner.Update(d, nameBox.Text, (type.SelectedItem as ComboBoxItem)?.Tag as string);
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            Render();
        }
        nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Save(); };
        var save = Ui.Button(Loc.T("Salvar", "Save"), Save, "Primary");
        save.Padding = new Thickness(16, 8, 16, 8);
        var edit = new WrapPanel();
        foreach (var (label, ctl) in new (string, FrameworkElement)[] { (Loc.T("Nome", "Name"), nameBox), (Loc.T("Tipo", "Type"), type) })
            edit.Children.Add(new Stack(5, Label(label), ctl) { Margin = new Thickness(0, 0, 12, 8) });
        edit.Children.Add(new Stack(5, Label(" "), save) { Margin = new Thickness(0, 0, 12, 8) });

        // informações
        string announced = string.Join(" · ", new[] { d.Friendly, d.Model }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        var left = new Stack(0,
            Row(Loc.T("Endereço IP", "IP address"), d.Ip ?? Fmt.Dash, Loc.T("Pode mudar com o tempo: o roteador distribui os endereços.", "May change over time: the router hands out addresses.")),
            Row(Loc.T("Endereço MAC", "MAC address"), d.Mac + (d.RandomMac ? Loc.T(" (privado)", " (private)") : ""),
                d.RandomMac ? Loc.T("Endereço aleatório que celulares e tablets usam para não serem rastreados. Não indica o fabricante, mas costuma ser fixo para cada rede.", "Random address phones and tablets use to avoid tracking. It doesn't reveal the maker, but usually stays the same per network.")
                            : Loc.T("Identificação única da placa de rede. É por ele que o Pulso reconhece o aparelho.", "The network card's unique ID. It's how Pulso recognizes the device.")),
            Row(Loc.T("Fabricante", "Manufacturer"), d.Vendor ?? (d.RandomMac ? Loc.T("Oculto (MAC privado)", "Hidden (private MAC)") : Fmt.Dash), mono: false),
            Row(Loc.T("Nome na rede", "Network name"), d.Hostname ?? Fmt.Dash, Loc.T("Nome que o próprio aparelho informa (Bonjour, NetBIOS ou DNS do roteador).", "Name the device reports itself (Bonjour, NetBIOS or the router's DNS)."), mono: false));
        var right = new Stack(0,
            Row(Loc.T("Anuncia como", "Announces as"), announced.Length > 0 ? announced : Fmt.Dash, Loc.T("Nome e modelo que TVs, roteadores, consoles e impressoras divulgam pela rede (UPnP).", "Name and model that TVs, routers, consoles and printers broadcast on the network (UPnP)."), mono: false),
            Row(Loc.T("Visto pela primeira vez", "First seen"), d.FirstSeen.ToString("g", Fmt.Culture), mono: false),
            Row(Loc.T("Última vez online", "Last online"), d.Online ? Loc.T("Agora", "Now") : d.LastSeen.ToString("g", Fmt.Culture) + " · " + Ago(d.LastSeen), mono: false),
            Row(Loc.T("Latência", "Latency"), d.Online && d.LatencyMs is double ms ? Fmt.N(ms) + " ms" : Fmt.Dash, Loc.T("Tempo de resposta ao ping. Alguns aparelhos ignoram ping, então pode ficar vazio mesmo online.", "Ping response time. Some devices ignore ping, so it may be empty even when online.")));
        var infoGrid = Cols(28, new[] { 1.0, 1.0 }, left, right);

        // presença nas últimas 24 h
        var strip = new Grid { Height = 18 };
        var now = DateTime.Now;
        for (int i = 0; i < 48; i++)
        {
            strip.ColumnDefinitions.Add(new ColumnDefinition());
            var from = now.AddHours(-24).AddMinutes(i * 30);
            var to = from.AddMinutes(30);
            bool on = d.Sessions.Any(s => s.End >= from && s.Start < to);
            var cell = new Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(1, 0, 1, 0), ToolTip = $"{from:HH:mm}–{to:HH:mm}" }.Res(Border.BackgroundProperty, on ? "AccentFill" : "Well");
            Grid.SetColumn(cell, i);
            strip.Children.Add(cell);
        }
        var axis = new DockPanel();
        var l = Text(Loc.T("24 h atrás", "24 h ago"), 11, null, "TextMuted");
        var r = Text(Loc.T("agora", "now"), 11, null, "TextMuted");
        DockPanel.SetDock(r, Dock.Right);
        axis.Children.Add(r); axis.Children.Add(l);
        var onFor = d.OnlineWithin(TimeSpan.FromHours(24));
        var presenceHead = new StackPanel { Orientation = Orientation.Horizontal };
        presenceHead.Children.Add(Text(Loc.T("Presença nas últimas 24 horas", "Presence in the last 24 hours"), 12.5, FontWeights.SemiBold, "Text2"));
        presenceHead.Children.Add(Help(Loc.T("Registrada pelas varreduras do Pulso. Com o monitoramento ligado e o Pulso aberto (mesmo na bandeja), o histórico fica completo.",
                                              "Recorded by Pulso's scans. With monitoring on and Pulso open (even in the tray), the history is complete.")));
        presenceHead.Children.Add(Text("  " + Loc.T($"online por {Fmt.Duration(onFor)}", $"online for {Fmt.Duration(onFor)}"), 12, null, "TextMuted"));
        var presence = new Stack(6, presenceHead, strip, axis);

        // ações
        var actions = new WrapPanel();
        if (!d.Known)
        {
            var ack = Ui.Button(Loc.T("Marcar como conhecido", "Mark as known"), () => { LanScanner.Acknowledge(d); Render(); });
            ack.Padding = new Thickness(14, 7, 14, 7);
            ack.Margin = new Thickness(0, 0, 18, 0);
            actions.Children.Add(ack);
        }
        if (d.IsGateway && d.Ip != null)
            actions.Children.Add(Link(Loc.T("Abrir a página do roteador", "Open the router's page"), () => OpenUrl("http://" + d.Ip)));
        if (!d.Online && !d.IsSelf)
            actions.Children.Add(Link(Loc.T("Esquecer este aparelho", "Forget this device"), () => { _open.Remove(d.Mac); LanScanner.Forget(d); Render(); }));

        return new Border
        {
            Margin = new Thickness(52, 12, 0, 4), Padding = new Thickness(18, 16, 18, 16), CornerRadius = new CornerRadius(14),
            Child = new Stack(16, edit, infoGrid, presence, actions.Children.Count > 0 ? actions : null),
        }.Res(Border.BackgroundProperty, "Tile");
    }

    static Button Link(string text, Action act)
    {
        var b = Ui.Button(text, act, "LinkButton");
        b.Margin = new Thickness(0, 0, 18, 0);
        b.VerticalAlignment = VerticalAlignment.Center;
        return b;
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Paths.LogError("Lan.OpenUrl", ex); }
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return Loc.T("agora", "now");
        if (d.TotalMinutes < 60) return Loc.T($"há {(int)d.TotalMinutes} min", $"{(int)d.TotalMinutes} min ago");
        if (d.TotalHours < 24) return Loc.T($"há {(int)d.TotalHours} h", $"{(int)d.TotalHours} h ago");
        int days = (int)d.TotalDays;
        return days == 1 ? Loc.T("há 1 dia", "1 day ago") : Loc.T($"há {days} dias", $"{days} days ago");
    }
}
