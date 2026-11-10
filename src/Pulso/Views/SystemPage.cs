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

/// <summary>Aba Sistema: uma sub-aba por componente (escolhida na barra lateral).</summary>
public sealed class SystemPage : PageBase
{
    string _sub = "cpu";
    readonly TextBlock _title = Text("", 28, FontWeights.ExtraBold);
    readonly ContentControl _body = new();
    readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    List<InfoPage> _pages = new();
    readonly Dictionary<string, TextBlock> _values = new();
    string _signature = "";
    bool _loading;
    DateTime _lastLoad;

    // por núcleo (CPU)
    readonly List<(Border fill, RowDefinition top, RowDefinition bottom, TextBlock use, TextBlock clk, TextBlock temp)> _cores = new();

    public SystemPage()
    {
        var copy = Ui.Button(Loc.T("Copiar esta aba", "Copy this tab"), CopyTab);
        var export = new StackPanel { Orientation = Orientation.Horizontal };
        export.Children.Add(new TextBlock { Text = Loc.T("Exportar", "Export"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 9, 2, 9) }.Res(TextBlock.ForegroundProperty, "TextMuted"));
        foreach (var fmt in new[] { "TXT", "HTML", "PDF" })
        {
            string f = fmt;
            var b = Ui.Button(fmt, () => ReportExporter.Export(this, f, _pages, Loc.T("Sistema — ", "System — ") + _title.Text));
            b.Background = Brushes.Transparent;
            b.Padding = new Thickness(10, 9, f == "PDF" ? 14 : 10, 9);
            export.Children.Add(b);
        }
        var exportPill = new Border { Child = export, CornerRadius = new CornerRadius(10) }.Res(Border.BackgroundProperty, "Tile");
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Stack(4, Label(Loc.T("Sistema", "System")), _title) { VerticalAlignment = VerticalAlignment.Bottom });
        var actions = new Stack(8, copy, exportPill) { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        Content = Scroll(new Stack(18, header, _body));
        _timer.Tick += async (_, _) => await LoadAsync(false);
    }

    public void Select(string sub)
    {
        if (_sub == sub && _pages.Count > 0) return;
        _sub = sub;
        _title.Text = MainWindow.SystemSubs.FirstOrDefault(x => x.id == sub) is var t && t.id != null ? Loc.T(t.pt, t.en) : sub;
        _signature = "";
        _pages.Clear();
        _body.Content = Text(Loc.T("Lendo informações…", "Reading information…"), 13, null, "TextMuted");
        if (Shown) _ = LoadAsync(true);
    }

    protected override void OnShown()
    {
        if (_title.Text == "") Select(_sub);
        _ = LoadAsync(true);
        _timer.Start();
    }

    protected override void OnHidden() => _timer.Stop();

    async Task LoadAsync(bool force)
    {
        if (_loading) return;
        // Discos e rede mudam devagar e são caros de ler: atualização espaçada.
        var minGap = _sub switch { "disk" => TimeSpan.FromSeconds(30), "net" => TimeSpan.FromSeconds(10), _ => TimeSpan.Zero };
        if (!force && DateTime.Now - _lastLoad < minGap) return;
        _loading = true;
        string sub = _sub;
        try
        {
            List<InfoPage> pages = sub switch
            {
                "cpu" => new() { await InfoPages.CpuAsync() },
                "gpu" => await GpuInfo.BuildPagesAsync(),
                "ram" => new() { await MemoryInfo.BuildPageAsync() },
                "disk" => await StorageInfo.BuildPagesAsync(),
                "board" => new() { await InfoPages.BoardAsync() },
                "net" => new() { await InfoPages.NetworkAsync() },
                _ => new() { await InfoPages.OsAsync() },
            };
            _lastLoad = DateTime.Now;
            if (sub != _sub) return;
            _pages = pages;
            string sig = string.Join("|", pages.SelectMany(p => p.Groups.Select(g => g.Title + ":" + string.Join(",", g.Items.Select(i => i.Key)) + ":" + g.Tables.Sum(t => t.Rows.Count) + ":" + g.Notes.Count)));
            if (sig == _signature) UpdateValues(pages);
            else
            {
                _signature = sig;
                Render(pages);
            }
        }
        catch (Exception ex)
        {
            Paths.LogError("SystemPage." + sub, ex);
            _body.Content = Banner(Loc.T("Não foi possível ler estas informações: ", "Could not read this information: ") + ex.Message, Health.Critical);
        }
        finally { _loading = false; }
    }

    void UpdateValues(List<InfoPage> pages)
    {
        for (int p = 0; p < pages.Count; p++)
            for (int g = 0; g < pages[p].Groups.Count; g++)
                foreach (var it in pages[p].Groups[g].Items)
                    if (_values.TryGetValue($"{p}|{g}|{it.Key}", out var tb) && tb.Text != it.Value)
                    {
                        tb.Text = it.Value;
                        tb.SetResourceReference(TextBlock.ForegroundProperty, ValueBrush(it));
                    }
    }

    static string ValueBrush(InfoItem it) =>
        it.Value == Fmt.NA || it.Value.StartsWith(Fmt.NA) ? "TextFaint" : it.Status switch { Health.Warning => "Warn", Health.Critical => "Crit", Health.Good => "Good", _ => "Text" };

    void Render(List<InfoPage> pages)
    {
        _values.Clear();
        _cores.Clear();
        var root = new Stack(18);

        if (_sub == "cpu") root.Children.Add(CoresCard(pages[0]));
        if (_sub == "ram") RenderRamTop(root, pages[0]);

        for (int p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            if (pages.Count > 1)
                root.Children.Add(Text(page.Title, 18, FontWeights.ExtraBold));
            var cards = new List<UIElement>();
            var wide = new List<UIElement>();
            for (int g = 0; g < page.Groups.Count; g++)
            {
                var grp = page.Groups[g];
                // A tabela por núcleo/thread do processador já aparece no cartão "Por núcleo".
                bool skipTables = _sub == "cpu";
                if (_sub == "cpu" && grp.Items.Count == 1 && grp.Items[0].Key == Loc.T("Conjuntos", "Sets")) continue;
                var card = GroupCard(grp, p, g, skipTables);
                if (!skipTables && grp.Tables.Count > 0 && grp.Items.Count == 0) wide.Add(card);
                else cards.Add(card);
            }
            if (cards.Count > 0) root.Children.Add(Masonry(18, cards));
            foreach (var w in wide) root.Children.Add(w);
        }
        _body.Content = root;
        if (SensorHub.Instance.Last is Snapshot s) OnSnapshot(s);
    }

    Border GroupCard(InfoGroup grp, int p, int g, bool skipTables)
    {
        var st = new StackPanel();
        var title = Title(grp.Title);
        title.Margin = new Thickness(0, 0, 0, 10);
        st.Children.Add(title);
        foreach (var (lvl, text) in grp.Notes)
        {
            var b = Banner(text, lvl == Health.Critical ? Health.Critical : Health.Warning, bigIcon: false);
            b.Margin = new Thickness(0, 0, 0, 10);
            b.Padding = new Thickness(12);
            if (lvl == Health.Unknown) { b.SetResourceReference(Border.BackgroundProperty, "Tile"); ((TextBlock)((DockPanel)b.Child).Children[1]).SetResourceReference(TextBlock.ForegroundProperty, "Text3"); }
            st.Children.Add(b);
        }
        foreach (var it in grp.Items)
        {
            var row = Row(it.Key, it.Value, it.Tip, ValueBrush(it));
            _values[$"{p}|{g}|{it.Key}"] = (TextBlock)((DockPanel)row.Child).Children[0];
            st.Children.Add(row);
        }
        if (!skipTables)
            foreach (var t in grp.Tables) st.Children.Add(TableView(t));
        return Card(st, 20, 18);
    }

    static UIElement TableView(InfoTable t)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        int n = t.Columns.Length;
        for (int c = 0; c < n; c++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 1 && n > 3 ? new GridLength(2.2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < n; c++)
        {
            var h = Text(t.Columns[c], 12, FontWeights.SemiBold, "Accent");
            h.Margin = new Thickness(0, 8, 8, 8);
            if (c > 1 || n <= 2 && c == 1) h.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(h, c);
            g.Children.Add(h);
        }
        for (int r = 0; r < t.Rows.Count; r++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) }.Res(Border.BorderBrushProperty, "Divider");
            Grid.SetRow(line, r + 1);
            Grid.SetColumnSpan(line, n);
            g.Children.Add(line);
            string brush = t.RowStatus[r] switch { Health.Warning => "Warn", Health.Critical => "Crit", _ => "Text" };
            for (int c = 0; c < n && c < t.Rows[r].Length; c++)
            {
                bool nameCol = c == 1 && n > 3;
                var cell = nameCol ? Text(t.Rows[r][c], 13, FontWeights.SemiBold, brush) : Num(t.Rows[r][c], 13, c == 0 ? "Text3" : brush, FontWeights.Normal);
                cell.Margin = new Thickness(0, 10, 8, 10);
                if (c > 1 || n <= 2 && c == 1) cell.TextAlignment = TextAlignment.Right;
                if (t.RowTips[r] is string tip && nameCol)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = cell.Margin };
                    cell.Margin = new Thickness(0);
                    sp.Children.Add(cell);
                    sp.Children.Add(Help(tip));
                    Grid.SetRow(sp, r + 1); Grid.SetColumn(sp, c);
                    g.Children.Add(sp);
                    continue;
                }
                Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c);
                g.Children.Add(cell);
            }
        }
        return g;
    }

    // ---------------------------------------------------------------- CPU: por núcleo

    UIElement CoresCard(InfoPage page)
    {
        var s = SensorHub.Instance.Last?.Cpu;
        int n = Math.Max(s?.CoreClocks.Length ?? 0, s?.ThreadLoads.Length ?? 0);
        int cores = s?.CoreClocks.Length > 0 ? s.CoreClocks.Length : CpuInfo.Get().Cores;
        n = cores > 0 ? cores : n;
        var tiles = new List<UIElement>();
        for (int i = 0; i < n; i++)
        {
            var top = new RowDefinition { Height = new GridLength(100, GridUnitType.Star) };
            var bottom = new RowDefinition { Height = new GridLength(0, GridUnitType.Star) };
            var well = new Grid { Height = 54 };
            well.RowDefinitions.Add(top); well.RowDefinitions.Add(bottom);
            var wb = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = well }.Res(Border.BackgroundProperty, "Well");
            var fill = new Border { Opacity = 0.85 }.Res(Border.BackgroundProperty, "AccentFill");
            Grid.SetRow(fill, 1);
            well.Children.Add(fill);
            var use = Num("—", 14);
            var clk = Num("—", 11.5, "Text3", FontWeights.Normal);
            var temp = Num("—", 11.5, "Warn", FontWeights.Normal);
            tiles.Add(Tile(new Stack(8, Caps(Loc.T($"Núcleo {i}", $"Core {i}")), wb, use, clk, temp), 12, 12));
            _cores.Add((fill, top, bottom, use, clk, temp));
        }
        var head = new DockPanel();
        var hint = Label(Loc.T("Uso · clock · temperatura", "Usage · clock · temperature"));
        DockPanel.SetDock(hint, Dock.Right);
        head.Children.Add(hint);
        head.Children.Add(Title(Loc.T("Por núcleo", "Per core")));

        var isaItems = CpuInfo.Get().Instructions;
        var chips = new WrapPanel();
        foreach (var i in isaItems)
            chips.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 6), Child = Num(i, 12, "Text2", FontWeights.Normal) }.Res(Border.BackgroundProperty, "Tile"));
        var isaHead = new StackPanel { Orientation = Orientation.Horizontal };
        isaHead.Children.Add(Label(Loc.T("Instruções suportadas", "Supported instructions")));
        isaHead.Children.Add(Help(Loc.T("Conjuntos de instruções que o processador entende. Alguns jogos e programas exigem AVX/AVX2.", "Instruction sets the CPU understands. Some games and apps require AVX/AVX2.")));

        int cols = Math.Min(8, Math.Max(4, n));
        return Card(new Stack(14, head, Wrap(cols, 10, tiles), new Stack(8, isaHead, chips) { Margin = new Thickness(0, 6, 0, 0) }));
    }

    // ---------------------------------------------------------------- RAM: aviso + pentes

    void RenderRamTop(Stack root, InfoPage page)
    {
        var mem = SystemSummary.GetAsync().IsCompletedSuccessfully ? SystemSummary.GetAsync().Result.Memory : null;
        if (mem == null) return;
        if (mem.Warning != null) root.Children.Add(Banner(mem.Warning));
        var slots = mem.Slots.Count > 0 ? mem.Slots : mem.Modules.Select(m => (m.Slot, (MemoryInfo.Module?)m)).ToList();
        var tiles = new List<UIElement>();
        foreach (var (slot, m) in slots)
        {
            Border b;
            if (m == null)
            {
                b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14) };
                b.Child = new Stack(6, Caps(slot), Text(Loc.T("Vazio", "Empty"), 13.5, FontWeights.Bold), Text(Loc.T("Slot livre", "Free slot"), 12, null, "Text3"), Num("—", 12, "Text3", FontWeights.Normal));
                b = Dashed(b);
            }
            else
            {
                string d1 = $"{Fmt.N(m.SizeGB)} GB {m.Type}" + (m.RatedSpeed > 0 ? Loc.T($" · {m.RatedSpeed} MT/s nominal", $" · {m.RatedSpeed} MT/s rated") : "")
                            + (m.ConfiguredSpeed > 0 && m.ConfiguredSpeed + 50 < m.RatedSpeed ? Loc.T($" · rodando a {m.ConfiguredSpeed}", $" · running at {m.ConfiguredSpeed}") : m.ConfiguredSpeed > 0 && m.RatedSpeed <= 0 ? $" · {m.ConfiguredSpeed} MT/s" : "");
                string d2 = string.Join(" · ", new[] { m.Timings != null ? "CL" + m.Timings : null, m.VoltageMv > 0 ? $"{Fmt.N(m.VoltageMv / 1000.0, 2)} V" : null }.Where(x => x != null));
                b = Tile(new Stack(6, Caps(slot), Text($"{m.Manufacturer} {m.PartNumber}".Trim(), 13.5, FontWeights.Bold), Text(d1, 12, null, "Text3", wrap: true), Num(d2 == "" ? "—" : d2, 12, "Text3", FontWeights.Normal)), 14, 14, 14);
            }
            tiles.Add(b);
        }
        if (tiles.Count > 0) root.Children.Add(Wrap(Math.Min(4, Math.Max(2, tiles.Count)), 12, tiles));
    }

    static Border Dashed(Border inner)
    {
        // Borda tracejada como no design (slots vazios).
        var rect = new Avalonia.Controls.Shapes.Rectangle { RadiusX = 14, RadiusY = 14, StrokeThickness = 1, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 } };
        rect.SetResourceReference(Avalonia.Controls.Shapes.Shape.StrokeProperty, "BorderStrong");
        inner.BorderThickness = new Thickness(0);
        var g = new Grid();
        g.Children.Add(rect);
        g.Children.Add(inner);
        return new Border { Child = g };
    }

    public override void OnSnapshot(Snapshot s)
    {
        if (_sub != "cpu" || _cores.Count == 0) return;
        var c = s.Cpu;
        int threadsPerCore = c.ThreadLoads.Length > 0 && _cores.Count > 0 ? Math.Max(1, c.ThreadLoads.Length / _cores.Count) : 1;
        for (int i = 0; i < _cores.Count; i++)
        {
            var (fill, top, bottom, use, clk, temp) = _cores[i];
            double? load = null;
            if (c.ThreadLoads.Length > 0)
            {
                // Uso do núcleo = maior das suas threads.
                double mx = 0;
                for (int k = 0; k < threadsPerCore && i * threadsPerCore + k < c.ThreadLoads.Length; k++) mx = Math.Max(mx, c.ThreadLoads[i * threadsPerCore + k]);
                load = mx;
            }
            double l = Math.Clamp(load ?? 0, 0, 100);
            top.Height = new GridLength(100 - l, GridUnitType.Star);
            bottom.Height = new GridLength(l, GridUnitType.Star);
            use.Text = Fmt.Value(load, Unit.Percent);
            clk.Text = i < c.CoreClocks.Length ? Fmt.N(c.CoreClocks[i] / 1000, 2) + " GHz" : Fmt.Dash;
            temp.Text = i < c.CoreTemps.Length ? Fmt.Value(c.CoreTemps[i], Unit.Temp) : c.Temp is double pt ? Fmt.Value(pt, Unit.Temp) : Fmt.Dash;
        }
    }

    void CopyTab()
    {
        if (_pages.Count == 0) return;
        try
        {
            _ = Dialogs.CopyAsync(this, string.Join("\r\n\r\n", _pages.Select(p => p.ToText())));
            Notifier.Notify("Pulso", Loc.T("Informações copiadas para a área de transferência.", "Information copied to clipboard."), false);
        }
        catch { }
    }

    public override string? CopyText() => string.Join("\r\n\r\n", _pages.Select(p => p.ToText()));
}
