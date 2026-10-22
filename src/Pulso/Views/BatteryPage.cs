using System.Windows;
using System.Windows.Controls;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Hardware;
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class BatteryPage : PageBase
{
    readonly TextBlock _eyebrow = Label("");
    readonly TextBlock _health = Num("—", 40, "Text", FontWeights.ExtraBold), _healthText = Text("", 13, null, "Text3", wrap: true);
    readonly Bar _healthBar = new(10);
    readonly Border _healthPill = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock _healthPillText = Text("", 12, FontWeights.SemiBold);
    readonly TextBlock _charge = Num("—", 20), _state = Text("—", 15, FontWeights.Bold), _power = Num("—", 20), _left = Num("—", 20), _cycles = Num("—", 20), _volt = Num("—", 20);
    readonly Bar _chargeBar = new(6);
    readonly Stack _details = new(0), _history = new(0);
    readonly Stack _banners = new(10);
    Battery.Info? _bat;

    public BatteryPage()
    {
        _healthPill.Child = _healthPillText;
        var head = new DockPanel();
        DockPanel.SetDock(_healthPill, Dock.Right);
        head.Children.Add(_healthPill);
        var hl = new StackPanel { Orientation = Orientation.Horizontal };
        hl.Children.Add(Label(Loc.T("Saúde da bateria", "Battery health")));
        hl.Children.Add(Help(Loc.T("Quanto da capacidade original de fábrica a bateria ainda consegue guardar. Toda bateria perde capacidade com o uso; abaixo de 80% a autonomia já cai bastante, abaixo de 60% vale pensar em trocar.",
                                    "How much of its original factory capacity the battery can still hold. Every battery loses capacity with use; below 80% runtime drops noticeably, below 60% consider replacing it.")));
        head.Children.Add(hl);
        var healthCard = Card(new Stack(12, head, _health, _healthBar, _healthText));

        var tiles = Wrap(3, 12, new UIElement[]
        {
            TileOf(Loc.T("Carga", "Charge"), new Stack(8, _charge, _chargeBar), null),
            TileOf(Loc.T("Estado", "State"), _state, null),
            TileOf(Loc.T("Consumo agora", "Power draw now"), _power, Loc.T("Quanto o notebook está puxando da bateria (ou recebendo, ao carregar). Na tomada com a bateria cheia fica em 0.", "How much the laptop is drawing from the battery (or receiving while charging). On AC with a full battery it reads 0.")),
            TileOf(Loc.T("Tempo estimado", "Estimated time"), _left, Loc.T("Na bateria: quanto falta para acabar no ritmo atual. Carregando: quanto falta para encher.", "On battery: time left at the current pace. Charging: time until full.")),
            TileOf(Loc.T("Ciclos de carga", "Charge cycles"), _cycles, Loc.T("Um ciclo = 100% de carga usada (somando várias cargas parciais). Baterias de notebook costumam durar de 300 a 1000 ciclos.", "One cycle = 100% of charge used (adding up partial charges). Laptop batteries usually last 300 to 1000 cycles.")),
            TileOf(Loc.T("Voltagem", "Voltage"), _volt, null),
        });

        var detailsCard = Card(new Stack(0, Spaced(Title(Loc.T("Detalhes", "Details")), 6), _details), 20, 18);
        var histCard = Card(new Stack(0, Spaced(Title(Loc.T("Histórico de capacidade", "Capacity history")), 6), _history), 20, 18);

        var tips = Card(new Stack(10,
            Title(Loc.T("Para a bateria durar mais", "To make the battery last longer")),
            Tip(Loc.T("Se o notebook fica quase sempre na tomada, ative o limite de carga (80%) no app do fabricante: Dell Power Manager/Optimizer (\"Uso principalmente na tomada\"), Lenovo Vantage (\"Modo de conservação\"), MyASUS (\"Carga para saúde da bateria\"), HP (BIOS → \"Adaptive Battery Optimizer\").",
                      "If the laptop is plugged in most of the time, turn on the charge limit (80%) in the maker's app: Dell Power Manager/Optimizer (\"Primarily AC use\"), Lenovo Vantage (\"Conservation mode\"), MyASUS (\"Battery Health Charging\"), HP (BIOS → \"Adaptive Battery Optimizer\").")),
            Tip(Loc.T("Calor é o que mais desgasta: evite usar no colo, na cama ou com as saídas de ar tapadas, principalmente jogando.", "Heat is what wears it the most: avoid using it on your lap, on the bed or with the vents blocked, especially while gaming.")),
            Tip(Loc.T("Não precisa descarregar até 0%. Baterias de lítio preferem ficar entre 20% e 80%.", "No need to drain it to 0%. Lithium batteries prefer staying between 20% and 80%."))), 20, 18);

        Content = Scroll(new Stack(18,
            Header(_eyebrow, Loc.T("Bateria", "Battery")),
            _banners,
            Cols(18, new[] { 1.0, 1.5 }, healthCard, tiles),
            Cols(18, new[] { 1.0, 1.0 }, detailsCard, histCard),
            tips));
    }

    static FrameworkElement Spaced(FrameworkElement e, double b) { e.Margin = new Thickness(0, 0, 0, b); return e; }

    static Border TileOf(string label, UIElement value, string? tip)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Text(label, 11.5, null, "TextMuted"));
        if (tip != null) head.Children.Add(Help(tip));
        return Tile(new Stack(6, head, value), 14, 12);
    }

    static UIElement Tip(string text)
    {
        var dot = Text("•", 13, FontWeights.Bold, "Accent");
        dot.Margin = new Thickness(0, 0, 8, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(dot, Dock.Left);
        dock.Children.Add(dot);
        dock.Children.Add(Text(text, 12.5, null, "Text3", wrap: true));
        return dock;
    }

    protected override async void OnShown()
    {
        if (_bat == null)
        {
            var list = await Task.Run(() => { Battery.RecordDaily(); return Battery.ReadAll(); });
            _bat = list.FirstOrDefault();
            RenderStatic(list.Count);
        }
        if (Shown) RefreshStatus();
    }

    public override void OnSnapshot(Snapshot s) => RefreshStatus();

    static string Wh(uint mwh) => Fmt.N(mwh / 1000.0, 1) + " Wh";

    void RenderStatic(int count)
    {
        _banners.Children.Clear();
        if (_bat == null)
        {
            _eyebrow.Text = Loc.T("Nenhuma bateria encontrada", "No battery found");
            _healthText.Text = Loc.T("Este computador não tem bateria (ou o Windows não a reconhece).", "This computer has no battery (or Windows does not detect it).");
            return;
        }
        var b = _bat;
        _eyebrow.Text = string.Join(" · ", new[] { b.Manufacturer, b.Name, count > 1 ? Loc.T($"{count} baterias (mostrando a 1ª)", $"{count} batteries (showing the 1st)") : null }.Where(x => !string.IsNullOrEmpty(x)));

        if (b.WearPct is double wear)
        {
            double health = 100 - wear;
            var h = health >= 80 ? Health.Good : health >= 60 ? Health.Warning : Health.Critical;
            _health.Text = Fmt.N(health) + "%";
            _health.SetResourceReference(TextBlock.ForegroundProperty, HealthKey(h));
            _healthBar.Value = health;
            _healthBar.FillKey = h == Health.Good ? "AccentFill" : HealthKey(h);
            _healthText.Text = Loc.T($"Guarda {Wh(b.Full)} dos {Wh(b.Design)} originais ({Fmt.N(wear, 1)}% de desgaste).", $"Holds {Wh(b.Full)} of the original {Wh(b.Design)} ({Fmt.N(wear, 1)}% wear).");
            _healthPillText.Text = "● " + (h == Health.Good ? Loc.T("Boa", "Good") : h == Health.Warning ? Loc.T("Desgastada", "Worn") : Loc.T("Trocar em breve", "Replace soon"));
            _healthPill.SetResourceReference(Border.BorderBrushProperty, HealthKey(h));
            _healthPillText.SetResourceReference(TextBlock.ForegroundProperty, HealthKey(h));
            if (h == Health.Critical)
                _banners.Children.Add(Banner(Loc.T("A bateria guarda menos de 60% da capacidade original. A autonomia já está bem menor e o notebook pode desligar sozinho com carga sobrando: considere trocar.",
                                                   "The battery holds less than 60% of its original capacity. Runtime is much shorter and the laptop may shut down with charge left: consider replacing it."), Health.Critical));
        }
        else
        {
            _health.Text = "—";
            _healthText.Text = Loc.T("Esta bateria não informa a capacidade em Wh, então o desgaste não pode ser calculado.", "This battery does not report capacity in Wh, so wear cannot be calculated.");
        }
        _cycles.Text = b.Cycles is uint c ? Fmt.N(c) : Fmt.Dash;
        _cycles.ToolTip = b.Cycles == null ? Loc.T("O fabricante não informa os ciclos ao Windows.", "The manufacturer does not report cycles to Windows.") : null;

        _details.Children.Clear();
        bool first = true;
        void Add(string k, string? v) { if (string.IsNullOrEmpty(v)) return; var r = Row(k, v, padV: 8); if (first) r.BorderThickness = new Thickness(0); first = false; _details.Children.Add(r); }
        Add(Loc.T("Fabricante", "Manufacturer"), b.Manufacturer);
        Add(Loc.T("Modelo", "Model"), b.Name);
        Add(Loc.T("Número de série", "Serial number"), b.Serial);
        Add(Loc.T("Química", "Chemistry"), b.Chemistry);
        Add(Loc.T("Data de fabricação", "Manufacture date"), b.Manufactured?.ToString("d", Fmt.Culture));
        if (!b.Relative)
        {
            Add(Loc.T("Capacidade original", "Design capacity"), Wh(b.Design));
            Add(Loc.T("Capacidade atual (carga total)", "Current full-charge capacity"), Wh(b.Full));
        }
        Add(Loc.T("Temperatura", "Temperature"), b.TempC is double t ? Fmt.Value(t, Unit.Temp) : null);
        Add(Loc.T("Tipo de equipamento", "Device type"), CpuInfo.IsLaptop() ? Loc.T("Notebook", "Laptop") : null);

        RenderHistory();
    }

    void RenderHistory()
    {
        _history.Children.Clear();
        var pts = Battery.History();
        if (pts.Count < 2)
        {
            _history.Children.Add(Text(Loc.T("O Pulso guarda a capacidade da bateria uma vez por dia (sempre que é aberto). Com o tempo, aqui aparece quanto ela vem perdendo mês a mês.",
                                             "Pulso records the battery capacity once a day (whenever it runs). Over time, this shows how much it loses month by month."), 12.5, null, "TextMuted", wrap: true));
            return;
        }
        var first = pts[0];
        var last = pts[^1];
        if (first.Full > last.Full)
        {
            var s = Text(Loc.T($"Desde {first.Date.ToString("d", Fmt.Culture)}: perdeu {Wh(first.Full - last.Full)} ({Fmt.N((first.Full - last.Full) * 100.0 / Math.Max(1, first.Design), 1)} pontos de saúde).",
                               $"Since {first.Date.ToString("d", Fmt.Culture)}: lost {Wh(first.Full - last.Full)} ({Fmt.N((first.Full - last.Full) * 100.0 / Math.Max(1, first.Design), 1)} health points)."), 12.5, null, "Text3", wrap: true);
            s.Margin = new Thickness(0, 0, 0, 8);
            _history.Children.Add(s);
        }
        // um ponto por mês (o último de cada mês), mais recentes primeiro
        var months = pts.GroupBy(p => new DateTime(p.Date.Year, p.Date.Month, 1)).Select(g => g.Last()).OrderByDescending(p => p.Date).Take(12);
        foreach (var p in months)
        {
            double pct = p.Design > 0 ? p.Full * 100.0 / p.Design : 0;
            var label = Text(p.Date.ToString("MMM yyyy", Fmt.Culture), 12.5, null, "Text3");
            label.Width = 80;
            var val = Num($"{Wh(p.Full)} · {Fmt.N(pct)}%", 12.5, "Text", FontWeights.Normal);
            val.Width = 120;
            val.TextAlignment = TextAlignment.Right;
            val.Margin = new Thickness(12, 0, 0, 0);
            var bar = new Bar(6) { Value = pct, VerticalAlignment = VerticalAlignment.Center };
            var dock = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
            DockPanel.SetDock(label, Dock.Left); DockPanel.SetDock(val, Dock.Right);
            dock.Children.Add(label); dock.Children.Add(val); dock.Children.Add(bar);
            _history.Children.Add(dock);
        }
    }

    void RefreshStatus()
    {
        if (_bat == null) return;
        var s = Battery.ReadStatus(_bat);
        if (s == null) return;
        _charge.Text = s.ChargePct is double c ? Fmt.N(c) + "%" : Fmt.Dash;
        _chargeBar.Value = s.ChargePct ?? 0;
        _chargeBar.FillKey = s.ChargePct is < 15 ? "Crit" : s.ChargePct is < 30 ? "Warn" : "AccentFill";
        _state.Text = s.Charging ? Loc.T("Carregando", "Charging")
            : s.Discharging ? Loc.T("Na bateria", "On battery")
            : s.OnAc ? (s.ChargePct >= 95 ? Loc.T("Na tomada · carregada", "Plugged in · full") : Loc.T("Na tomada · sem carregar", "Plugged in · not charging"))
            : Fmt.Dash;
        _state.ToolTip = s.OnAc && !s.Charging && s.ChargePct < 95
            ? Loc.T("Na tomada, mas sem carregar: normalmente é o limite de carga do fabricante protegendo a bateria.", "Plugged in but not charging: usually the maker's charge limit protecting the battery.")
            : null;
        _power.Text = s.RateMw is int r && r != 0 ? (r > 0 ? "+" : "") + Fmt.N(r / 1000.0, 1) + " W" : "0 W";
        _power.SetResourceReference(TextBlock.ForegroundProperty, s.RateMw is < -25000 ? "Warn" : "Text");
        _left.Text = s.TimeLeft is TimeSpan t && t.TotalHours < 48 ? (t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min") : Fmt.Dash;
        _volt.Text = s.VoltageMv is uint v ? Fmt.N(v / 1000.0, 2) + " V" : Fmt.Dash;
    }
}
