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
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

/// <summary>Atualizações pelo winget (uma a uma ou todas) e lista de programas instalados com tamanho e desinstalação.</summary>
public sealed class ProgramsPage : PageBase
{
    readonly TextBlock _eyebrow = Label("");
    readonly TextBlock _nUpd = Num("—", 20), _nInst = Num("—", 20), _size = Num("—", 20), _nIgn = Num("—", 20);
    readonly Button _check, _updateAll, _cancel;
    readonly TextBlock _updStatus = Text("", 12.5, null, "TextMuted", wrap: true);
    readonly Stack _banners = new(10), _updList = new(0), _ignList = new(0), _instList = new(0);
    readonly Border _ignBox;
    readonly TextBox _search = new() { MinWidth = 260 };
    readonly Dictionary<string, (TextBlock status, Button btn)> _rows = new();
    List<Programs.Update> _updates = new();
    List<Programs.Installed> _installed = new();
    string _sort = "name";
    bool _loaded, _checking, _running;
    CancellationTokenSource? _cts;

    static List<string> Ignored => Settings.Current.IgnoredUpdates;

    public ProgramsPage()
    {
        _check = Ui.Button(Loc.T("Procurar atualizações", "Check for updates"), () => { _ = LoadInstalledAsync(); _ = CheckAsync(); }, "PrimaryPill");
        _updateAll = Ui.Button("", () => _ = UpdateAllAsync(), "Primary");
        _updateAll.Padding = new Thickness(16, 9, 16, 9);
        _cancel = Ui.Button(Loc.T("Parar depois deste", "Stop after this one"), () => _cts?.Cancel());
        _cancel.Padding = new Thickness(14, 9, 14, 9);
        _cancel.Visibility = Visibility.Collapsed;

        var tiles = Wrap(4, 12, new UIElement[]
        {
            StatTile(Loc.T("Atualizações disponíveis", "Updates available"), _nUpd, null),
            StatTile(Loc.T("Programas instalados", "Installed programs"), _nInst, null),
            StatTile(Loc.T("Espaço ocupado", "Space used"), _size, Loc.T("Soma do tamanho informado pelos próprios instaladores. Alguns não informam, então o total real pode ser maior.", "Sum of the sizes reported by the installers. Some don't report it, so the real total may be higher.")),
            StatTile(Loc.T("Ignorados", "Ignored"), _nIgn, Loc.T("Programas que você pediu para o Pulso não atualizar.", "Programs you asked Pulso not to update.")),
        });

        var updActions = new StackPanel { Orientation = Orientation.Horizontal };
        updActions.Children.Add(_updateAll);
        _cancel.Margin = new Thickness(10, 0, 0, 0);
        updActions.Children.Add(_cancel);
        var updHow = Text(Loc.T("As atualizações rodam em silêncio pelo winget, o instalador oficial do Windows: baixa a versão nova do site de cada fabricante e instala por cima, mantendo suas configurações. Feche o programa antes de atualizá-lo.",
                                "Updates run silently through winget, Windows' official installer: it downloads the new version from each publisher and installs over it, keeping your settings. Close a program before updating it."), 12.5, null, "Text3", wrap: true);
        _ignBox = new Border { Child = new Stack(8, Caps(Loc.T("Ignorados", "Ignored")), _ignList), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        var updCard = Card(new Stack(14, new DockPanel { Children = { updActions } }, _updStatus, updHow, _updList, _ignBox), 20, 18);

        var sorts = Segments("progsort", new (string, bool, Action)[]
        {
            (Loc.T("Nome", "Name"), true, () => { _sort = "name"; RenderInstalled(); }),
            (Loc.T("Tamanho", "Size"), false, () => { _sort = "size"; RenderInstalled(); }),
            (Loc.T("Instalação", "Install date"), false, () => { _sort = "date"; RenderInstalled(); }),
        });
        _search.TextChanged += (_, _) => RenderInstalled();
        var searchRow = new DockPanel();
        DockPanel.SetDock(sorts, Dock.Right);
        searchRow.Children.Add(sorts);
        var searchBox = new Stack(5, Label(Loc.T("Buscar", "Search")), _search) { HorizontalAlignment = HorizontalAlignment.Left };
        searchRow.Children.Add(searchBox);
        sorts.VerticalAlignment = VerticalAlignment.Bottom;
        var instCard = Card(new Stack(14, searchRow, _instList), 20, 18);

        Content = Scroll(new Stack(18,
            Header(_eyebrow, Loc.T("Programas", "Programs"), _check),
            _banners, tiles,
            new Stack(10, Caps(Loc.T("Atualizações", "Updates")), updCard),
            new Stack(10, Caps(Loc.T("Instalados", "Installed")), instCard)));
        _updStatus.Visibility = Visibility.Collapsed;
        RenderUpdates();
    }

    static Border StatTile(string label, TextBlock value, string? tip)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Text(label, 11.5, null, "TextMuted"));
        if (tip != null) head.Children.Add(Help(tip));
        return Tile(new Stack(4, head, value), 14, 12);
    }

    protected override void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadInstalledAsync();
        _ = CheckAsync();
    }

    async Task LoadInstalledAsync()
    {
        _installed = await Task.Run(Programs.ReadInstalled);
        _nInst.Text = Fmt.N(_installed.Count);
        var total = _installed.Sum(p => p.SizeBytes ?? 0);
        _size.Text = total > 0 ? Fmt.Capacity(total) : Fmt.Dash;
        RenderInstalled();
    }

    // ------------------------------------------------------------------ atualizações

    async Task CheckAsync()
    {
        if (_checking || _running) return;
        _banners.Children.Clear();
        if (Programs.WingetPath() == null)
        {
            _banners.Children.Add(Banner(Loc.T("O winget não está instalado neste PC. Instale o \"Instalador de Aplicativo\" pela Microsoft Store para o Pulso poder atualizar os programas.",
                                               "winget isn't installed on this PC. Install \"App Installer\" from the Microsoft Store so Pulso can update programs."), Health.Warning, false));
            _eyebrow.Text = Loc.T("Sem winget", "No winget");
            return;
        }
        _checking = true;
        _check.IsEnabled = false;
        _eyebrow.Text = Loc.T("Procurando atualizações… (leva alguns segundos)", "Checking for updates… (takes a few seconds)");
        try
        {
            _updates = await Programs.CheckUpdatesAsync();
            _eyebrow.Text = Loc.T($"Verificado às {DateTime.Now:HH:mm}", $"Checked at {DateTime.Now:HH:mm}");
        }
        catch (Exception ex)
        {
            Paths.LogError("Programs.Check", ex);
            _banners.Children.Add(Banner(Loc.T("Não foi possível consultar as atualizações: ", "Couldn't check for updates: ") + ex.Message, Health.Warning, false));
            _eyebrow.Text = "";
        }
        _checking = false;
        _check.IsEnabled = true;
        RenderUpdates();
    }

    void RenderUpdates()
    {
        var pending = _updates.Where(u => !Ignored.Contains(u.Id)).ToList();
        var ignored = _updates.Where(u => Ignored.Contains(u.Id)).ToList();
        _nUpd.Text = _loaded && !_checking ? Fmt.N(pending.Count) : Fmt.Dash;
        _nUpd.SetResourceReference(TextBlock.ForegroundProperty, pending.Count > 0 ? "Warn" : "Text");
        _nIgn.Text = Fmt.N(Ignored.Count);
        _updateAll.Content = Loc.T($"Atualizar todos ({pending.Count})", $"Update all ({pending.Count})");
        _updateAll.IsEnabled = pending.Count > 0 && !_running;

        _rows.Clear();
        _updList.Children.Clear();
        bool first = true;
        foreach (var u in pending) { _updList.Children.Add(UpdateRow(u, first, false)); first = false; }
        if (pending.Count == 0)
            _updList.Children.Add(Text(_checking || !_loaded ? Loc.T("Procurando…", "Checking…") : Loc.T("Tudo atualizado.", "Everything is up to date."), 13, null, "TextMuted"));

        _ignList.Children.Clear();
        first = true;
        foreach (var u in ignored) { _ignList.Children.Add(UpdateRow(u, first, true)); first = false; }
        _ignBox.Visibility = ignored.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    UIElement UpdateRow(Programs.Update u, bool first, bool ignored)
    {
        var ver = new StackPanel { Orientation = Orientation.Horizontal };
        ver.Children.Add(Num(u.Version.Length > 0 ? u.Version : "?", 12, "TextMuted", FontWeights.Normal));
        ver.Children.Add(Text("  →  ", 12, null, "TextMuted"));
        ver.Children.Add(Num(u.Available, 12, ignored ? "TextMuted" : "Accent", FontWeights.SemiBold));
        var info = new Stack(3, Text(u.Name, 13.5, FontWeights.SemiBold, ignored ? "Text3" : "Text"), ver) { VerticalAlignment = VerticalAlignment.Center };
        info.ToolTip = u.Id;

        var status = Text("", 12, null, "TextMuted");
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(0, 0, 14, 0);
        status.MaxWidth = 320;
        status.TextWrapping = TextWrapping.Wrap;
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(status);
        var toggleIgnore = Ui.Button(ignored ? Loc.T("Voltar a avisar", "Notify again") : Loc.T("Ignorar", "Ignore"), () =>
        {
            if (ignored) Ignored.Remove(u.Id); else if (!Ignored.Contains(u.Id)) Ignored.Add(u.Id);
            Settings.Save();
            RenderUpdates();
        }, "LinkButton");
        toggleIgnore.VerticalAlignment = VerticalAlignment.Center;
        toggleIgnore.Margin = new Thickness(0, 0, 16, 0);
        toggleIgnore.IsEnabled = !_running;
        right.Children.Add(toggleIgnore);
        var btn = Ui.Button(Loc.T("Atualizar", "Update"), () => _ = UpdateOneAsync(u));
        btn.Padding = new Thickness(14, 7, 14, 7);
        btn.IsEnabled = !_running;
        if (!ignored) right.Children.Add(btn);
        _rows[u.Id] = (status, btn);

        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(info);
        return new Border { Child = dock, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 11, 0, 11) }.Res(Border.BorderBrushProperty, "Divider");
    }

    Task UpdateOneAsync(Programs.Update u) => RunUpdates(new List<Programs.Update> { u });

    Task UpdateAllAsync() => RunUpdates(_updates.Where(u => !Ignored.Contains(u.Id)).ToList());

    async Task RunUpdates(List<Programs.Update> list)
    {
        if (_running || list.Count == 0) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _check.IsEnabled = _updateAll.IsEnabled = false;
        foreach (var (_, b) in _rows.Values) b.IsEnabled = false;
        _cancel.Visibility = list.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _updStatus.Visibility = Visibility.Visible;
        _updStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        int ok = 0, fail = 0, i = 0;
        var done = new List<Programs.Update>();
        foreach (var u in list)
        {
            if (_cts.IsCancellationRequested) break;
            i++;
            _updStatus.Text = Loc.T($"Atualizando {i} de {list.Count}: {u.Name}…", $"Updating {i} of {list.Count}: {u.Name}…");
            _rows.TryGetValue(u.Id, out var row);
            if (row.status != null)
            {
                row.status.Text = Loc.T("Atualizando…", "Updating…");
                row.status.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
            }
            // não cancela no meio de uma instalação: só impede a próxima
            var (success, msg) = await Programs.UpgradeAsync(u, line => Dispatcher.UIThread.Post(() =>
            {
                if (row.status != null && line.Length < 120) row.status.Text = line;
            }));
            if (success) { ok++; done.Add(u); } else fail++;
            if (row.status != null)
            {
                row.status.Text = msg;
                row.status.SetResourceReference(TextBlock.ForegroundProperty, success ? "Good" : "Crit");
            }
        }

        _running = false;
        _cancel.Visibility = Visibility.Collapsed;
        _check.IsEnabled = true;
        _updStatus.Text = fail == 0
            ? Loc.T($"{ok} programa(s) atualizado(s).", $"{ok} program(s) updated.")
            : Loc.T($"{ok} atualizado(s), {fail} com problema (veja o motivo em cada linha).", $"{ok} updated, {fail} failed (see the reason on each line).");
        _updStatus.SetResourceReference(TextBlock.ForegroundProperty, fail == 0 ? "Good" : "Warn");
        // tira da lista os que deram certo, mas mantém o resultado dos que falharam visível
        _updates.RemoveAll(done.Contains);
        var keep = _rows.Where(r => !done.Any(d => d.Id == r.Key)).ToDictionary(r => r.Key, r => (r.Value.status.Text, r.Value.status.Foreground));
        RenderUpdates();
        foreach (var (id, (text, brush)) in keep)
            if (_rows.TryGetValue(id, out var r) && text.Length > 0) { r.status.Text = text; r.status.Foreground = brush; }
        _ = LoadInstalledAsync();
    }

    // ------------------------------------------------------------------ instalados

    void RenderInstalled()
    {
        var q = _search.Text.Trim();
        IEnumerable<Programs.Installed> list = _installed;
        if (q.Length > 0) list = list.Where(p => p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        list = _sort switch
        {
            "size" => list.OrderByDescending(p => p.SizeBytes ?? -1),
            "date" => list.OrderByDescending(p => p.InstalledOn ?? DateTime.MinValue),
            _ => list,
        };
        _instList.Children.Clear();
        bool first = true;
        foreach (var p in list)
        {
            _instList.Children.Add(InstalledRow(p, first));
            first = false;
        }
        if (first) _instList.Children.Add(Text(_installed.Count == 0 ? Loc.T("Lendo…", "Reading…") : Loc.T("Nada encontrado.", "Nothing found."), 13, null, "TextMuted"));
    }

    UIElement InstalledRow(Programs.Installed p, bool first)
    {
        var parts = new List<string>();
        if (p.Publisher.Length > 0) parts.Add(p.Publisher);
        if (p.Version.Length > 0) parts.Add(p.Version);
        if (p.InstalledOn is DateTime d) parts.Add(Loc.T("instalado em ", "installed ") + d.ToString("d", Fmt.Culture));
        var info = new Stack(3, Text(p.Name, 13.5, FontWeights.SemiBold), Text(string.Join(" · ", parts), 12, null, "TextMuted")) { VerticalAlignment = VerticalAlignment.Center };

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var size = Num(p.SizeBytes is double s ? Fmt.Capacity(s) : "", 12.5, "Text2", FontWeights.SemiBold);
        size.VerticalAlignment = VerticalAlignment.Center;
        size.MinWidth = 80;
        size.TextAlignment = TextAlignment.Right;
        size.Margin = new Thickness(0, 0, 18, 0);
        right.Children.Add(size);
        if (p.Uninstall != null)
        {
            var un = Ui.Button(Loc.T("Desinstalar", "Uninstall"), () =>
            {
                if (!Programs.StartUninstall(p))
                    _banners.Children.Add(Banner(Loc.T($"Não foi possível abrir o desinstalador de {p.Name}.", $"Couldn't open {p.Name}'s uninstaller."), Health.Warning, false));
            }, "LinkButton");
            un.ToolTip = Loc.T("Abre o desinstalador do próprio programa. Depois de terminar, clique em Procurar atualizações para atualizar a lista.", "Opens the program's own uninstaller. When done, click Check for updates to refresh the list.");
            un.VerticalAlignment = VerticalAlignment.Center;
            right.Children.Add(un);
        }

        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(info);
        return new Border { Child = dock, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 10, 0, 10) }.Res(Border.BorderBrushProperty, "Divider");
    }
}
