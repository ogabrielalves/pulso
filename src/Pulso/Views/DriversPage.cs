using System.Windows;
using System.Windows.Controls;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class DriversPage : PageBase
{
    readonly Stack _banners = new(10);
    readonly TextBlock _total = Num("—", 20), _third = Num("—", 20), _problems = Num("—", 20), _updates = Num("—", 20);
    readonly Button _searchBtn, _installBtn;
    readonly TextBlock _updStatus = Text("", 12.5, null, "TextMuted", wrap: true);
    readonly Stack _updList = new(8);
    readonly TextBlock _hiddenNote = Text("", 12, null, "TextMuted", wrap: true);
    readonly Stack _probList = new(0), _gpuList = new(0), _histList = new(0);
    readonly Grid _table = new();
    readonly CheckBox _showMs;
    readonly Dictionary<Drivers.Update, (CheckBox box, TextBlock status)> _rows = new();
    List<Drivers.Installed> _installed = new();
    bool _loaded, _busy, _rebootPending;

    public DriversPage()
    {
        _searchBtn = Ui.Button(Loc.T("Procurar atualizações", "Check for updates"), Search, "Primary");
        _searchBtn.Padding = new Thickness(16, 9, 16, 9);
        _installBtn = Ui.Button(Loc.T("Instalar selecionados", "Install selected"), InstallSelected, "Primary");
        _installBtn.Padding = new Thickness(16, 9, 16, 9);
        _installBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _installBtn.Visibility = Visibility.Collapsed;

        var tiles = Wrap(4, 12, new UIElement[]
        {
            StatTile(Loc.T("Drivers instalados", "Installed drivers"), _total, null),
            StatTile(Loc.T("De terceiros", "Third-party"), _third, Loc.T("Drivers que não são da Microsoft: placa de vídeo, chipset, áudio, rede… São os que costumam receber atualizações.", "Drivers not made by Microsoft: graphics, chipset, audio, network… These are the ones that usually get updates.")),
            StatTile(Loc.T("Dispositivos com problema", "Devices with problems"), _problems, Loc.T("Dispositivos que o Windows marca com erro (o triângulo amarelo do Gerenciador de Dispositivos).", "Devices Windows flags with an error (the yellow triangle in Device Manager).")),
            StatTile(Loc.T("Atualizações", "Updates"), _updates, Loc.T("Drivers mais novos disponíveis no Windows Update para o seu hardware.", "Newer drivers available on Windows Update for your hardware.")),
        });

        var how = Text(Loc.T("Os drivers vêm só do Windows Update: são assinados e enviados pelos próprios fabricantes. Antes de instalar, o Pulso cria um ponto de restauração e guarda uma cópia do driver atual. Versões mais antigas que a instalada nunca são oferecidas.",
                             "Drivers come only from Windows Update: they are signed and submitted by the manufacturers themselves. Before installing, Pulso creates a restore point and keeps a copy of the current driver. Versions older than the installed one are never offered."), 12.5, null, "Text3", wrap: true);
        var updCard = Card(new Stack(12, Title(Loc.T("Atualizações disponíveis", "Available updates")), how, _updStatus, _updList, _hiddenNote, _installBtn));

        var probCard = Card(new Stack(0, Spaced(Title(Loc.T("Dispositivos com problema", "Devices with problems")), 10), _probList), 20, 18);
        var gpuCard = Card(new Stack(0, Spaced(Title(Loc.T("Placa de vídeo", "Graphics card")), 4),
            Spaced(Text(Loc.T("O Windows Update costuma atrasar semanas os drivers de vídeo. Para jogos, baixe direto do fabricante.", "Windows Update usually lags weeks behind on graphics drivers. For gaming, download from the manufacturer."), 12, null, "TextMuted", wrap: true), 8),
            _gpuList), 20, 18);
        var histCard = Card(new Stack(0, Spaced(Title(Loc.T("Histórico e reversão", "History and rollback")), 10), _histList), 20, 18);

        _showMs = Toggle(false, _ => RenderTable(), Loc.T("Mostrar drivers da Microsoft", "Show Microsoft drivers"));
        var tableHead = new DockPanel();
        DockPanel.SetDock(_showMs, Dock.Right);
        tableHead.Children.Add(_showMs);
        tableHead.Children.Add(Title(Loc.T("Drivers instalados", "Installed drivers")));
        var tableCard = Card(new Stack(10, tableHead, _table), 20, 18);

        Content = Scroll(new Stack(18,
            Header(Loc.T("Atualização segura via Windows Update", "Safe updates via Windows Update"), "Drivers", _searchBtn),
            _banners, tiles, updCard, Cols(18, new[] { 1.0, 1.0 }, probCard, gpuCard), histCard, tableCard));
        _updStatus.Text = Loc.T("Clique em \"Procurar atualizações\". A busca no Windows Update pode levar alguns minutos.", "Click \"Check for updates\". Searching Windows Update can take a few minutes.");
    }

    static FrameworkElement Spaced(FrameworkElement e, double b) { e.Margin = new Thickness(0, 0, 0, b); return e; }

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
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        _total.Text = _third.Text = _problems.Text = "…";
        var (inst, probs, warns, laptop) = await Task.Run(() => (Drivers.ReadInstalled(), Drivers.ReadProblems(), Drivers.Warnings(), CpuInfo.IsLaptop()));
        _installed = inst;
        _total.Text = Fmt.N(inst.Count);
        _third.Text = Fmt.N(inst.Count(d => !d.IsMicrosoft));
        _problems.Text = Fmt.N(probs.Count(p => p.Code != 22));
        _problems.SetResourceReference(TextBlock.ForegroundProperty, probs.Any(p => p.Code != 22) ? "Warn" : "Good");

        _banners.Children.Clear();
        if (!Native.IsAdmin)
            _banners.Children.Add(Banner(Loc.T("Sem administrador o Pulso consegue procurar atualizações, mas não instalar nem reverter drivers. Reabra o app e aceite o pedido do Windows.", "Without administrator rights Pulso can check for updates but cannot install or roll back drivers. Reopen the app and accept the Windows prompt.")));
        if (laptop)
            _banners.Children.Add(Banner(Loc.T("Notebook detectado: drivers de áudio, touchpad, teclas de atalho e gerenciamento de energia costumam ser personalizados pelo fabricante. Para esses, prefira o site ou app do fabricante do notebook.", "Laptop detected: audio, touchpad, hotkey and power management drivers are often customized by the manufacturer. For those, prefer the laptop maker's website or app."), Health.Warning, false));
        foreach (var w in warns) _banners.Children.Add(Banner(w, Health.Warning, false));
        if (_rebootPending) _banners.Children.Add(Banner(Loc.T("Reinicie o Windows para concluir a instalação dos drivers.", "Restart Windows to finish installing the drivers.")));

        RenderProblems(probs);
        RenderGpu();
        RenderHistory();
        RenderTable();
    }

    // ------------------------------------------------------------- busca

    async void Search()
    {
        if (_busy) return;
        _busy = true;
        _searchBtn.IsEnabled = _installBtn.IsEnabled = false;
        _searchBtn.Content = Loc.T("Procurando…", "Searching…");
        _updStatus.Text = Loc.T("Consultando o Windows Update…", "Querying Windows Update…");
        _updList.Children.Clear(); _rows.Clear();
        _hiddenNote.Text = "";
        _installBtn.Visibility = Visibility.Collapsed;
        try
        {
            if (_installed.Count == 0) _installed = await Task.Run(Drivers.ReadInstalled);
            var inst = _installed;
            var all = await Task.Run(() => Drivers.Search(inst));
            var offer = all.Where(u => u.Offer == Drivers.Offer.Newer).ToList();
            int older = all.Count(u => u.Offer == Drivers.Offer.Older), same = all.Count(u => u.Offer == Drivers.Offer.Same);
            _updates.Text = Fmt.N(offer.Count);
            _updates.SetResourceReference(TextBlock.ForegroundProperty, offer.Count > 0 ? "Accent" : "Good");
            _updStatus.Text = offer.Count == 0
                ? Loc.T("Nenhuma atualização de driver disponível no Windows Update. Seus drivers estão em dia ✓", "No driver updates available on Windows Update. Your drivers are up to date ✓")
                : Loc.T($"{offer.Count} atualização(ões) encontrada(s). Marque as que quiser instalar.", $"{offer.Count} update(s) found. Tick the ones you want to install.");
            foreach (var u in offer) _updList.Children.Add(UpdateRow(u));
            if (older + same > 0)
                _hiddenNote.Text = Loc.T($"{older + same} oferta(s) do Windows Update foram ocultadas porque são da mesma versão ou mais antigas que o driver instalado.",
                                         $"{older + same} Windows Update offer(s) were hidden because they are the same version or older than the installed driver.");
            _installBtn.Visibility = offer.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _updStatus.Text = Loc.T("Não foi possível consultar o Windows Update: ", "Could not query Windows Update: ") + Drivers.Hr(ex.HResult);
            _updStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            Paths.LogError("Drivers.Search", ex);
        }
        catch (Exception ex)
        {
            _updStatus.Text = Loc.T("Não foi possível consultar o Windows Update: ", "Could not query Windows Update: ") + ex.Message;
            _updStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            Paths.LogError("Drivers.Search", ex);
        }
        _searchBtn.Content = Loc.T("Procurar novamente", "Check again");
        _searchBtn.IsEnabled = _installBtn.IsEnabled = true;
        _busy = false;
    }

    UIElement UpdateRow(Drivers.Update u)
    {
        string cur = u.Current?.Version is { Length: > 0 } v ? v : "—";
        string details = string.Join(" · ", new[]
        {
            u.Class, u.Provider,
            $"{cur} → {u.Version ?? "?"}",
            u.Date?.ToString("d", Fmt.Culture),
            u.Size > 0 ? Fmt.Bytes(u.Size) : null,
        }.Where(x => !string.IsNullOrEmpty(x)));
        var content = new Stack(3, Text(u.DisplayName, 13.5, FontWeights.SemiBold), Text(details, 12, null, "TextMuted"));
        var box = new CheckBox { Content = content, IsChecked = true, IsEnabled = Native.IsAdmin, ToolTip = u.Title };
        box.SetResourceReference(StyleProperty, "Tick");
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        var status = Text("", 12, FontWeights.SemiBold, "TextMuted");
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(12, 0, 0, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(status, Dock.Right);
        dock.Children.Add(status);
        dock.Children.Add(box);
        _rows[u] = (box, status);
        return dock;
    }

    // ------------------------------------------------------------- instalação

    async void InstallSelected()
    {
        if (_busy) return;
        var sel = _rows.Where(r => r.Value.box.IsChecked == true && r.Value.box.IsEnabled).Select(r => r.Key).ToList();
        if (sel.Count == 0) return;
        var ok = MessageBox.Show(Window.GetWindow(this)!,
            Loc.T($"Instalar {sel.Count} driver(s) do Windows Update?\n\nAntes, o Pulso vai:\n• criar um ponto de restauração do Windows\n• guardar uma cópia do driver atual de cada dispositivo\n\nA tela pode piscar durante a instalação de drivers de vídeo, e talvez seja preciso reiniciar.",
                  $"Install {sel.Count} driver(s) from Windows Update?\n\nFirst, Pulso will:\n• create a Windows restore point\n• keep a copy of each device's current driver\n\nThe screen may flicker while graphics drivers install, and a restart may be needed."),
            "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;

        _busy = true;
        _searchBtn.IsEnabled = _installBtn.IsEnabled = false;
        foreach (var (_, (box, _)) in _rows) box.IsEnabled = false;
        _installBtn.Content = Loc.T("Instalando…", "Installing…");

        _updStatus.Text = Loc.T("Criando ponto de restauração (pode levar até um minuto)…", "Creating restore point (may take up to a minute)…");
        var (rp, rpDetail) = await Task.Run(() => Drivers.CreateRestorePoint("Pulso: atualização de drivers"));
        bool go = true;
        if (rp == Drivers.RestoreResult.Failed)
        {
            go = MessageBox.Show(Window.GetWindow(this)!,
                Loc.T($"Não foi possível criar o ponto de restauração ({rpDetail}).\n\nA cópia dos drivers atuais ainda será feita e você poderá revertê-los por aqui. Continuar mesmo assim?",
                      $"Could not create the restore point ({rpDetail}).\n\nThe current drivers will still be backed up and you can roll them back from here. Continue anyway?"),
                "Pulso", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        int done = 0, failed = 0;
        bool reboot = false;
        if (go)
        {
            foreach (var u in sel)
            {
                var status = _rows[u].status;
                status.Text = Loc.T("Copiando driver atual…", "Backing up…");
                status.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                _updStatus.Text = Loc.T($"Instalando {done + failed + 1} de {sel.Count}: {u.DisplayName}", $"Installing {done + failed + 1} of {sel.Count}: {u.DisplayName}");
                var backup = await Task.Run(() => Drivers.Backup(u.Current));
                status.Text = Loc.T("Baixando e instalando…", "Downloading and installing…");
                var (iok, ireboot, err) = await Task.Run(() => Drivers.Install(u));
                reboot |= ireboot;
                if (iok) done++; else failed++;
                status.Text = iok ? (ireboot ? Loc.T("Instalado · reiniciar", "Installed · restart") : Loc.T("Instalado ✓", "Installed ✓")) : Loc.T("Falhou: ", "Failed: ") + err;
                status.SetResourceReference(TextBlock.ForegroundProperty, iok ? "Good" : "Crit");
                Drivers.AddHistory(new Drivers.HistoryEntry
                {
                    Date = DateTime.Now, Device = u.DisplayName, DeviceId = u.Current?.DeviceId ?? "", HardwareId = u.HardwareId,
                    OldVersion = u.Current?.Version ?? "", NewVersion = u.Version ?? u.Title, BackupDir = backup, Ok = iok, Reboot = ireboot, Error = err,
                });
            }
        }

        _rebootPending |= reboot;
        _updStatus.Text = !go ? Loc.T("Instalação cancelada.", "Installation cancelled.")
            : Loc.T($"{done} instalado(s), {failed} com falha. {rpDetail.Substring(0, 1).ToUpper() + rpDetail[1..]}.", $"{done} installed, {failed} failed. {rpDetail.Substring(0, 1).ToUpper() + rpDetail[1..]}.")
              + (reboot ? Loc.T(" Reinicie o Windows para concluir.", " Restart Windows to finish.") : "");
        _installBtn.Content = Loc.T("Instalar selecionados", "Install selected");
        _searchBtn.IsEnabled = true;
        _busy = false;
        _installed.Clear();
        await LoadAsync();
    }

    // ------------------------------------------------------------- listas

    void RenderProblems(List<Drivers.Problem> probs)
    {
        _probList.Children.Clear();
        if (probs.Count == 0)
        {
            _probList.Children.Add(Text(Loc.T("Nenhum dispositivo com problema ✓", "No devices with problems ✓"), 13, null, "Good"));
            return;
        }
        foreach (var p in probs.OrderBy(p => p.Code == 22))
            _probList.Children.Add(Row(p.Name, p.Description, p.DeviceId, p.Code == 22 ? "TextMuted" : "Warn", mono: false));
        _probList.Children.Add(Spaced(Text(Loc.T("Dica: muitos desses problemas se resolvem procurando atualizações acima ou com o driver do site do fabricante.", "Tip: many of these are fixed by checking for updates above or with the manufacturer's driver."), 12, null, "TextMuted", wrap: true), 0));
        ((FrameworkElement)_probList.Children[^1]).Margin = new Thickness(0, 10, 0, 0);
    }

    void RenderGpu()
    {
        _gpuList.Children.Clear();
        var gpus = _installed.Where(d => d.Class.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)).ToList();
        if (gpus.Count == 0) { _gpuList.Children.Add(Text(Fmt.NA, 13, null, "TextFaint")); return; }
        bool first = true;
        foreach (var g in gpus)
        {
            bool basic = g.Name.Contains("Basic", StringComparison.OrdinalIgnoreCase) && g.IsMicrosoft;
            int ageDays = g.Date is DateTime d ? (int)(DateTime.Now - d).TotalDays : 0;
            string age = g.Date is DateTime dd ? dd.ToString("d", Fmt.Culture) + (ageDays > 60 ? Loc.T($" · há {ageDays / 30} meses", $" · {ageDays / 30} months ago") : "") : "";
            var info = new Stack(3,
                Text(g.Name, 13.5, FontWeights.SemiBold),
                Text(basic ? Loc.T("Sem driver do fabricante: usando o driver básico do Windows", "No manufacturer driver: using the basic Windows driver")
                           : $"{g.Version} · {age}", 12, null, basic ? "Crit" : ageDays > 180 ? "Warn" : "TextMuted"));
            var dock = new DockPanel();
            if (Drivers.GpuVendorUrl(g.Provider, g.Name) is string url)
            {
                var link = Ui.Button(Loc.T("Site oficial ↗", "Official site ↗"), () => Drivers.OpenUrl(url), "LinkButton");
                link.VerticalAlignment = VerticalAlignment.Center;
                link.Margin = new Thickness(12, 0, 0, 0);
                DockPanel.SetDock(link, Dock.Right);
                dock.Children.Add(link);
            }
            dock.Children.Add(info);
            _gpuList.Children.Add(new Border { Child = dock, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 9, 0, 9) }.Res(Border.BorderBrushProperty, "Divider"));
            first = false;
        }
    }

    void RenderHistory()
    {
        _histList.Children.Clear();
        var hist = Drivers.History();
        if (hist.Count == 0)
        {
            _histList.Children.Add(Text(Loc.T("Nenhum driver instalado pelo Pulso ainda. Depois de instalar, você pode reverter por aqui.", "No drivers installed by Pulso yet. After installing, you can roll back from here."), 13, null, "TextMuted", wrap: true));
            return;
        }
        bool first = true;
        foreach (var h in hist.Take(15))
        {
            var info = new Stack(3,
                Text(h.Device, 13.5, FontWeights.SemiBold),
                Text(string.Join(" · ", new[]
                {
                    h.Date.ToString("g", Fmt.Culture),
                    $"{(h.OldVersion == "" ? "—" : h.OldVersion)} → {h.NewVersion}",
                    h.Ok ? Loc.T("instalado", "installed") : Loc.T("falhou: ", "failed: ") + h.Error,
                    h.BackupDir != null ? Loc.T("com backup", "backed up") : null,
                }.Where(x => x != null)), 12, null, h.Ok ? "TextMuted" : "Crit"));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (h.Ok && h.DeviceId != "")
            {
                var rb = Ui.Button(Loc.T("Reverter", "Roll back"), () => Revert(h, false), "LinkButton");
                rb.ToolTip = Loc.T("Volta ao driver anterior guardado pelo Windows (igual ao Gerenciador de Dispositivos).", "Returns to the previous driver kept by Windows (same as Device Manager).");
                actions.Children.Add(rb);
            }
            if (h.Ok && h.BackupDir != null)
            {
                var bb = Ui.Button(Loc.T("Restaurar backup", "Restore backup"), () => Revert(h, true), "LinkButton");
                bb.ToolTip = Loc.T("Reinstala a cópia do driver feita pelo Pulso antes da atualização.", "Reinstalls the driver copy Pulso made before the update.");
                bb.Margin = new Thickness(16, 0, 0, 0);
                actions.Children.Add(bb);
            }
            var dock = new DockPanel();
            DockPanel.SetDock(actions, Dock.Right);
            dock.Children.Add(actions);
            dock.Children.Add(info);
            _histList.Children.Add(new Border { Child = dock, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 9, 0, 9) }.Res(Border.BorderBrushProperty, "Divider"));
            first = false;
        }
    }

    async void Revert(Drivers.HistoryEntry h, bool backup)
    {
        if (_busy) return;
        if (!Native.IsAdmin)
        {
            MessageBox.Show(Window.GetWindow(this)!, Loc.T("Reverter drivers precisa de administrador. Reabra o Pulso e aceite o pedido do Windows.", "Rolling back drivers requires administrator rights. Reopen Pulso and accept the Windows prompt."), "Pulso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string what = backup ? Loc.T("reinstalar a cópia do driver anterior", "reinstall the previous driver copy") : Loc.T("voltar ao driver anterior", "roll back to the previous driver");
        if (MessageBox.Show(Window.GetWindow(this)!, Loc.T($"{h.Device}\n\nDeseja {what} ({h.OldVersion})?", $"{h.Device}\n\nDo you want to {what} ({h.OldVersion})?"), "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _busy = true;
        var (ok, reboot, err) = await Task.Run(() => backup ? Drivers.RestoreBackup(h) : Drivers.Rollback(h.DeviceId));
        _busy = false;
        _rebootPending |= reboot;
        MessageBox.Show(Window.GetWindow(this)!,
            ok ? Loc.T("Driver revertido.", "Driver rolled back.") + (reboot ? Loc.T(" Reinicie o Windows para concluir.", " Restart Windows to finish.") : "")
               : Loc.T("Não foi possível reverter: ", "Could not roll back: ") + err,
            "Pulso", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        await LoadAsync();
    }

    void RenderTable()
    {
        _table.Children.Clear(); _table.ColumnDefinitions.Clear(); _table.RowDefinitions.Clear();
        double[] w = { 2.4, 1.1, 1.4, 1.2, 0.9 };
        foreach (var x in w) _table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(x, GridUnitType.Star) });
        string[] head = { Loc.T("Dispositivo", "Device"), Loc.T("Classe", "Class"), Loc.T("Fornecedor", "Provider"), Loc.T("Versão", "Version"), Loc.T("Data", "Date") };
        _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < head.Length; c++)
        {
            var t = Text(head[c], 12, FontWeights.SemiBold, "Accent");
            t.Margin = new Thickness(0, 8, 8, 8);
            Grid.SetColumn(t, c); _table.Children.Add(t);
        }
        bool ms = _showMs.IsChecked == true;
        int r = 1;
        foreach (var d in _installed.Where(d => ms || !d.IsMicrosoft))
        {
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0) }.Res(Border.BorderBrushProperty, "Divider");
            Grid.SetRow(line, r); Grid.SetColumnSpan(line, w.Length); _table.Children.Add(line);
            string[] cells = { d.Name, d.Class, d.Provider, d.Version, d.Date?.ToString("d", Fmt.Culture) ?? "—" };
            for (int c = 0; c < cells.Length; c++)
            {
                var t = c >= 3 ? Num(cells[c], 12.5, "Text", FontWeights.Normal) : Text(cells[c], 12.5, null, c == 0 ? "Text" : "Text3");
                t.Margin = new Thickness(0, 8, 8, 8);
                t.ToolTip = c == 0 ? $"{d.DeviceId}\n{d.Inf}{(d.Signed ? "" : Loc.T(" · não assinado", " · unsigned"))}" : null;
                Grid.SetRow(t, r); Grid.SetColumn(t, c); _table.Children.Add(t);
            }
            r++;
        }
        if (r == 1)
        {
            var empty = Text(_loaded ? Loc.T("Carregando…", "Loading…") : "", 13, null, "TextMuted");
            empty.Margin = new Thickness(0, 10, 0, 4);
            _table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(empty, 1); Grid.SetColumnSpan(empty, w.Length); _table.Children.Add(empty);
        }
    }

    public override string? CopyText() =>
        string.Join("\r\n", _installed.Where(d => !d.IsMicrosoft).Select(d => $"{d.Name}\t{d.Class}\t{d.Provider}\t{d.Version}\t{d.Date?.ToString("d", Fmt.Culture)}"));
}
