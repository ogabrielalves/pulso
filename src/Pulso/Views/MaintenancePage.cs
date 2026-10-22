using System.Text;
using System.Windows;
using System.Windows.Controls;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

/// <summary>Reparo e limpeza do Windows: blocos independentes + "Executar tudo" na ordem certa.</summary>
public sealed class MaintenancePage : PageBase
{
    /// <summary>Botão + barra + status de uma tarefa demorada.</summary>
    sealed class TaskBox
    {
        public readonly Button Button;
        public readonly Bar Bar = new(6) { Visibility = Visibility.Collapsed };
        public readonly TextBlock Status = Text("", 12.5, null, "TextMuted", wrap: true);
        readonly string _label;

        public TaskBox(string label, Action run, string? style = null)
        {
            _label = label;
            Button = Ui.Button(label, run, style);
            Button.Padding = new Thickness(16, 8, 16, 8);
            Button.HorizontalAlignment = HorizontalAlignment.Left;
            Status.Visibility = Visibility.Collapsed;
        }

        public void Start(string text)
        {
            Bar.Visibility = Status.Visibility = Visibility.Visible;
            Bar.Value = 0;
            Status.Text = text;
            Status.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        }

        public void Progress(double? pct, string text)
        {
            if (pct is double p) Bar.Value = p;
            Status.Text = text;
        }

        public void Done(Maintenance.Outcome o)
        {
            Bar.Visibility = Visibility.Collapsed;
            Status.Visibility = Visibility.Visible;
            Status.Text = o.Summary;
            Status.SetResourceReference(TextBlock.ForegroundProperty, !o.Ok ? "Crit" : o.Warning ? "Warn" : "Good");
            Button.Content = _label;
        }

        public void Fail(string text) => Done(new Maintenance.Outcome(false, text, ""));

        public UIElement View(params UIElement[] extra) => new Stack(10, new[] { (UIElement)Button }.Concat(extra).Append(Bar).Append(Status).ToArray());
    }

    readonly TaskBox _repair, _clean, _disk, _opt, _dns, _net;
    readonly Button _runAll, _reportLink;
    readonly TextBlock _runAllStatus = Text("", 12.5, null, "TextMuted", wrap: true);
    readonly Stack _cleanList = new(8), _diskList = new(0), _optList = new(0);
    readonly TextBlock _cleanTotal = Text("", 12.5, FontWeights.SemiBold, "Text3");
    readonly Dictionary<Maintenance.CleanItem, (CheckBox box, TextBlock size)> _cleanRows = new();
    readonly Dictionary<string, Button> _repairDiskBtns = new();
    List<Maintenance.CleanItem> _items = new();
    List<Maintenance.Volume> _vols = new();
    CancellationTokenSource? _cts;
    bool _loaded;
    string? _lastReport;

    public MaintenancePage()
    {
        _repair = new TaskBox(Loc.T("Verificar e reparar", "Check and repair"), RunRepair, "Primary");
        _clean = new TaskBox(Loc.T("Limpar selecionados", "Clean selected"), RunClean, "Primary");
        _disk = new TaskBox(Loc.T("Verificar discos", "Check drives"), RunDisk);
        _opt = new TaskBox(Loc.T("Otimizar unidades", "Optimize drives"), RunOptimize);
        _dns = new TaskBox(Loc.T("Limpar cache de DNS", "Clear DNS cache"), RunDns);
        _net = new TaskBox(Loc.T("Resetar rede (avançado)", "Reset network (advanced)"), RunNetReset);
        _runAll = Ui.Button(Loc.T("Executar tudo", "Run everything"), RunAll, "Primary");
        _runAll.Padding = new Thickness(16, 9, 16, 9);

        var repairCard = Section(Loc.T("Integridade do Windows", "Windows integrity"),
            Loc.T("Repara a imagem do Windows (DISM) e depois os arquivos do sistema (SFC), nessa ordem: o SFC usa a imagem como fonte para consertar. Leva de 10 a 30 minutos; não desligue o PC no meio.",
                  "Repairs the Windows image (DISM) and then the system files (SFC), in that order: SFC uses the image as its repair source. Takes 10 to 30 minutes; don't turn off the PC midway."),
            _repair.View());

        var cleanCard = Section(Loc.T("Limpeza", "Cleanup"),
            Loc.T("Veja quanto cada item ocupa antes de apagar. Nada pessoal é tocado (documentos, fotos, downloads).", "See how much each item takes before deleting. Nothing personal is touched (documents, photos, downloads)."),
            new Stack(12, _cleanList, _cleanTotal, _clean.View()));

        var diskCard = Section(Loc.T("Verificar disco", "Check drive"),
            Loc.T("Procura erros no sistema de arquivos com o Windows aberto, sem reiniciar (chkdsk /scan). Só pede reinicialização se achar algo que precise de reparo.", "Looks for file system errors with Windows running, no restart (chkdsk /scan). It only asks for a restart if it finds something that needs repair."),
            new Stack(10, _diskList, _disk.View()));

        var optCard = Section(Loc.T("Otimizar unidades", "Optimize drives"),
            Loc.T("O Windows escolhe sozinho: TRIM no SSD, desfragmentação no HD. Ele já faz isso toda semana; rode aqui se o agendamento estiver desligado.", "Windows picks automatically: TRIM on SSDs, defragmentation on HDDs. It already does this weekly; run it here if the schedule is turned off."),
            new Stack(10, _optList, _opt.View()));

        var dnsTip = Text(Loc.T("Resetar a rede (Winsock e TCP/IP) resolve internet que para sem motivo, mas exige reiniciar e apaga configurações manuais de IP.", "Resetting the network (Winsock and TCP/IP) fixes internet that stops for no reason, but requires a restart and clears manual IP settings."), 12, null, "TextMuted", wrap: true);
        var netCard = Section(Loc.T("Rede", "Network"),
            Loc.T("Limpar o DNS ajuda quando algum site não abre ou abre o endereço antigo.", "Clearing DNS helps when a site won't open or opens an old address."),
            new Stack(12, _dns.View(), dnsTip, _net.View()));

        var mdsched = Ui.Button(Loc.T("Agendar teste completo", "Schedule full test"), Maintenance.OpenMemoryDiagnostic);
        mdsched.Padding = new Thickness(16, 8, 16, 8);
        mdsched.HorizontalAlignment = HorizontalAlignment.Left;
        var memtest = Ui.Button(Loc.T("MemTest86 (pendrive) ↗", "MemTest86 (USB stick) ↗"), () => Drivers.OpenUrl("https://www.memtest86.com/download.htm"), "LinkButton");
        var ramCard = Section(Loc.T("Memória RAM", "Memory (RAM)"),
            Loc.T("O teste de erros do Pulso roda dentro do Windows e só alcança a memória livre. Para testar toda a RAM, o teste precisa rodar antes do Windows carregar: o Diagnóstico de Memória do Windows faz isso na próxima reinicialização; o MemTest86 é ainda mais completo.",
                  "Pulso's error test runs inside Windows and only reaches free memory. To test all RAM the test must run before Windows loads: Windows Memory Diagnostic does this on the next restart; MemTest86 is even more thorough."),
            new Stack(12, mdsched, memtest));

        _reportLink = Ui.Button(Loc.T("Abrir relatório ↗", "Open report ↗"), OpenReport, "LinkButton");
        _reportLink.HorizontalAlignment = HorizontalAlignment.Left;
        _reportLink.Visibility = Visibility.Collapsed;
        var headStatus = new Stack(6, _runAllStatus, _reportLink);
        Content = Scroll(new Stack(18,
            Header(Loc.T("Verificação, reparo e limpeza com as ferramentas do Windows", "Check, repair and cleanup with Windows' own tools"), Loc.T("Reparo e limpeza", "Repair & cleanup"), _runAll),
            Native.IsAdmin ? null : Banner(Loc.T("Tudo aqui precisa de administrador. Reabra o Pulso e aceite o pedido do Windows.", "Everything here requires administrator rights. Reopen Pulso and accept the Windows prompt.")),
            headStatus,
            Cols(18, new[] { 1.0, 1.0 },
                new Stack(18, repairCard, diskCard, optCard),
                new Stack(18, cleanCard, netCard, ramCard))));
        _runAllStatus.Visibility = Visibility.Collapsed;
        foreach (var b in new[] { _repair.Button, _clean.Button, _disk.Button, _opt.Button, _dns.Button, _net.Button, _runAll }) b.IsEnabled = Native.IsAdmin;
    }

    static Border Section(string title, string desc, UIElement body) =>
        Card(new Stack(12, Title(title), Text(desc, 12.5, null, "Text3", wrap: true), body), 20, 18);

    protected override void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        _cleanTotal.Text = Loc.T("Calculando tamanhos…", "Calculating sizes…");
        var items = Maintenance.CleanItems();
        RenderClean(items);
        var (vols, _) = await Task.Run(() => { Maintenance.MeasureAll(items); return (Maintenance.Volumes(), 0); });
        _items = items;
        _vols = vols;
        RefreshCleanSizes();
        RenderVolumes();
    }

    // ------------------------------------------------------------- limpeza

    void RenderClean(List<Maintenance.CleanItem> items)
    {
        _cleanList.Children.Clear();
        _cleanRows.Clear();
        foreach (var it in items)
        {
            var size = Num("…", 12.5, "Text", FontWeights.SemiBold);
            size.VerticalAlignment = VerticalAlignment.Center;
            size.Margin = new Thickness(12, 0, 0, 0);
            var content = new Stack(2, Text(it.Name, 13, FontWeights.SemiBold), Text(it.Desc, 11.5, null, "TextMuted", wrap: true)) { MaxWidth = 420 };
            var box = new CheckBox { Content = content, IsChecked = it.Default, HorizontalAlignment = HorizontalAlignment.Stretch };
            box.SetResourceReference(StyleProperty, "Tick");
            box.Checked += (_, _) => UpdateCleanTotal();
            box.Unchecked += (_, _) => UpdateCleanTotal();
            var dock = new DockPanel();
            DockPanel.SetDock(size, Dock.Right);
            dock.Children.Add(size);
            dock.Children.Add(box);
            _cleanRows[it] = (box, size);
            _cleanList.Children.Add(dock);
        }
    }

    void RefreshCleanSizes()
    {
        foreach (var (it, (_, size)) in _cleanRows)
        {
            size.Text = it.Size is long s ? (s < 1024 * 1024 ? "0" : Fmt.Bytes(s)) : Loc.T("—", "—");
            size.ToolTip = it.Size == null ? Loc.T("Não dá para medir antes; o espaço liberado aparece no fim.", "Can't be measured beforehand; freed space shows at the end.") : null;
        }
        UpdateCleanTotal();
    }

    void UpdateCleanTotal()
    {
        long total = _cleanRows.Where(r => r.Value.box.IsChecked == true).Sum(r => r.Key.Size ?? 0);
        _cleanTotal.Text = Loc.T($"Selecionado: {Fmt.Bytes(total)}", $"Selected: {Fmt.Bytes(total)}");
    }

    List<Maintenance.CleanItem> SelectedClean() => _cleanRows.Where(r => r.Value.box.IsChecked == true).Select(r => r.Key).ToList();

    // ------------------------------------------------------------- volumes

    void RenderVolumes()
    {
        _diskList.Children.Clear(); _optList.Children.Clear(); _repairDiskBtns.Clear();
        bool first = true;
        foreach (var v in _vols)
        {
            var fix = Ui.Button(Loc.T("Agendar reparo", "Schedule repair"), () => ScheduleRepair(v), "LinkButton");
            fix.Visibility = Visibility.Collapsed;
            fix.VerticalAlignment = VerticalAlignment.Center;
            _repairDiskBtns[v.Letter] = fix;
            _diskList.Children.Add(VolRow(v.Display, null, fix, first));

            string when = v.LastOptimized is DateTime d
                ? Loc.T($"Última otimização: {d.ToString("g", Fmt.Culture)} ({v.LastOptimizedOp})", $"Last optimized: {d.ToString("g", Fmt.Culture)} ({v.LastOptimizedOp})")
                : Loc.T("Sem registro de otimização recente", "No recent optimization on record");
            _optList.Children.Add(VolRow(v.Display, when, null, first, v.LastOptimized is DateTime dd && (DateTime.Now - dd).TotalDays > 35 ? "Warn" : "TextMuted"));
            first = false;
        }
    }

    static Border VolRow(string title, string? sub, UIElement? right, bool first, string subBrush = "TextMuted")
    {
        var dock = new DockPanel();
        if (right != null) { DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right); }
        dock.Children.Add(new Stack(2, Text(title, 13, FontWeights.SemiBold), sub == null ? null : Text(sub, 11.5, null, subBrush)));
        return new Border { Child = dock, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 8, 0, 8) }.Res(Border.BorderBrushProperty, "Divider");
    }

    // ------------------------------------------------------------- execução

    bool _busy;

    /// <summary>Uma tarefa por vez: o Windows não gosta de DISM, SFC e chkdsk rodando juntos.</summary>
    bool Begin(TaskBox box, string text)
    {
        if (_busy) return false;
        _busy = true;
        _cts = new CancellationTokenSource();
        foreach (var b in new[] { _repair, _clean, _disk, _opt, _dns, _net }) if (b != box) b.Button.IsEnabled = false;
        _runAll.IsEnabled = false;
        box.Start(text);
        return true;
    }

    void End()
    {
        _busy = false;
        _cts = null;
        foreach (var b in new[] { _repair, _clean, _disk, _opt, _dns, _net }) b.Button.IsEnabled = Native.IsAdmin;
        _runAll.IsEnabled = Native.IsAdmin;
    }

    Maintenance.Report Reporter(TaskBox box) => (p, t) => Dispatcher.BeginInvoke(() => box.Progress(p, t));

    async Task<Maintenance.Outcome> Guard(TaskBox box, Func<CancellationToken, Task<Maintenance.Outcome>> work, string ctx)
    {
        try { return await work(_cts!.Token); }
        catch (OperationCanceledException) { return new Maintenance.Outcome(false, Loc.T("Cancelado", "Cancelled"), ""); }
        catch (Exception ex) { Paths.LogError("Maintenance." + ctx, ex); return new Maintenance.Outcome(false, ex.Message, ex.ToString()); }
    }

    async void RunRepair()
    {
        if (_busy && _repair.Button.Content as string == Loc.T("Cancelar", "Cancel")) { CancelRepair(); return; }
        if (!Begin(_repair, Loc.T("Iniciando DISM…", "Starting DISM…"))) return;
        _repair.Button.Content = Loc.T("Cancelar", "Cancel");
        var o = await Guard(_repair, ct => Maintenance.RepairWindowsAsync(Reporter(_repair), ct), "Repair");
        _repair.Done(o);
        Save(o);
        End();
    }

    void CancelRepair()
    {
        if (MessageBox.Show(Window.GetWindow(this)!, Loc.T("Interromper o reparo no meio pode deixar uma operação pendente (ela termina na próxima reinicialização). Cancelar mesmo assim?", "Stopping the repair midway may leave a pending operation (it finishes on the next restart). Cancel anyway?"),
                "Pulso", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _cts?.Cancel();
    }

    async void RunClean()
    {
        var sel = SelectedClean();
        if (sel.Count == 0) return;
        if (sel.Any(i => i.Id == "recycle") && MessageBox.Show(Window.GetWindow(this)!, Loc.T("A lixeira será esvaziada e não dá para recuperar o que está nela. Continuar?", "The Recycle Bin will be emptied and its contents cannot be recovered. Continue?"),
                "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (!Begin(_clean, Loc.T("Limpando…", "Cleaning…"))) return;
        var o = await Guard(_clean, ct => Maintenance.CleanAsync(sel, Reporter(_clean), ct), "Clean");
        _clean.Done(o);
        Save(o);
        RefreshCleanSizes();
        End();
    }

    async void RunDisk()
    {
        if (_vols.Count == 0 || !Begin(_disk, Loc.T("Verificando…", "Checking…"))) return;
        var o = await Guard(_disk, ct => ScanAll(ct), "Disk");
        _disk.Done(o);
        Save(o);
        End();
    }

    async Task<Maintenance.Outcome> ScanAll(CancellationToken ct)
    {
        var parts = new List<Maintenance.Outcome>();
        foreach (var v in _vols)
        {
            var o = await Maintenance.ScanDiskAsync(v, Reporter(_disk), ct);
            parts.Add(o);
            if (!o.Ok && o.Summary.Contains(v.Letter) && _repairDiskBtns.TryGetValue(v.Letter, out var b))
                await Dispatcher.InvokeAsync(() => b.Visibility = Visibility.Visible);
        }
        return Merge(parts);
    }

    async void ScheduleRepair(Maintenance.Volume v)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, Loc.T($"Na próxima reinicialização o Windows vai verificar e corrigir {v.Letter} antes de abrir (pode levar de alguns minutos a mais de uma hora em HDs grandes). Agendar?",
                                                           $"On the next restart Windows will check and fix {v.Letter} before starting (from a few minutes to over an hour on large HDDs). Schedule it?"),
                "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var o = await Maintenance.ScheduleDiskRepairAsync(v);
        _disk.Done(o);
        if (o.Ok && _repairDiskBtns.TryGetValue(v.Letter, out var b)) b.Visibility = Visibility.Collapsed;
    }

    async void RunOptimize()
    {
        if (_busy && _opt.Button.Content as string == Loc.T("Cancelar", "Cancel")) { _cts?.Cancel(); return; }
        if (_vols.Count == 0 || !Begin(_opt, Loc.T("Otimizando…", "Optimizing…"))) return;
        _opt.Button.Content = Loc.T("Cancelar", "Cancel");
        var o = await Guard(_opt, async ct =>
        {
            var parts = new List<Maintenance.Outcome>();
            foreach (var v in _vols) parts.Add(await Maintenance.OptimizeAsync(v, Reporter(_opt), ct));
            return Merge(parts);
        }, "Optimize");
        _opt.Done(o);
        Save(o);
        End();
        _vols = await Task.Run(Maintenance.Volumes);
        RenderVolumes();
    }

    async void RunDns()
    {
        if (!Begin(_dns, Loc.T("Limpando…", "Clearing…"))) return;
        var o = await Guard(_dns, _ => Maintenance.FlushDnsAsync(), "Dns");
        _dns.Done(o);
        End();
    }

    async void RunNetReset()
    {
        if (MessageBox.Show(Window.GetWindow(this)!, Loc.T("Isso reseta o Winsock e o TCP/IP do Windows. Configurações manuais de IP e alguns programas de VPN podem precisar ser refeitos, e é preciso reiniciar. Continuar?",
                                                           "This resets Windows Winsock and TCP/IP. Manual IP settings and some VPN programs may need to be set up again, and a restart is required. Continue?"),
                "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (!Begin(_net, Loc.T("Resetando…", "Resetting…"))) return;
        var o = await Guard(_net, _ => Maintenance.ResetNetworkAsync(), "NetReset");
        _net.Done(o);
        Save(o);
        End();
    }

    /// <summary>Sequência segura do .bat, na ordem certa: DISM → SFC → chkdsk /scan → limpeza → otimização → DNS.</summary>
    async void RunAll()
    {
        if (_busy) { _cts?.Cancel(); return; }
        if (MessageBox.Show(Window.GetWindow(this)!,
                Loc.T("Vai rodar, em ordem:\n\n1. Reparo da imagem do Windows (DISM)\n2. Verificação dos arquivos do sistema (SFC)\n3. Verificação dos discos (sem reiniciar)\n4. Limpeza dos itens marcados\n5. Otimização das unidades\n6. Limpeza do cache de DNS\n\nLeva de 15 a 45 minutos. Dá para continuar usando o PC, mas evite desligá-lo. Começar?",
                      "It will run, in order:\n\n1. Windows image repair (DISM)\n2. System file check (SFC)\n3. Drive check (no restart)\n4. Cleanup of the ticked items\n5. Drive optimization\n6. DNS cache clear\n\nTakes 15 to 45 minutes. You can keep using the PC, but avoid turning it off. Start?"),
                "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _busy = true;
        _cts = new CancellationTokenSource();
        foreach (var b in new[] { _repair, _clean, _disk, _opt, _dns, _net }) b.Button.IsEnabled = false;
        _runAll.Content = Loc.T("Cancelar", "Cancel");
        _runAllStatus.Visibility = Visibility.Visible;
        _runAllStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var log = new StringBuilder($"Pulso · {Loc.T("Reparo e limpeza", "Repair & cleanup")} · {DateTime.Now:g}\r\n\r\n");
        var results = new List<(string step, Maintenance.Outcome o)>();
        var ct = _cts.Token;

        async Task Step(string name, TaskBox box, Func<CancellationToken, Task<Maintenance.Outcome>> work)
        {
            if (ct.IsCancellationRequested) return;
            _runAllStatus.Text = Loc.T($"Executando: {name}…", $"Running: {name}…");
            box.Start(name + "…");
            var o = await Guard(box, work, "RunAll." + name);
            box.Done(o);
            results.Add((name, o));
            log.AppendLine($"### {name}: {o.Summary}").AppendLine(o.Log).AppendLine();
        }

        await Step(Loc.T("Integridade do Windows", "Windows integrity"), _repair, c => Maintenance.RepairWindowsAsync(Reporter(_repair), c));
        await Step(Loc.T("Verificar discos", "Check drives"), _disk, ScanAll);
        var sel = SelectedClean().Where(i => i.Id != "recycle").ToList(); // a lixeira só com confirmação explícita
        if (sel.Count > 0) await Step(Loc.T("Limpeza", "Cleanup"), _clean, c => Maintenance.CleanAsync(sel, Reporter(_clean), c));
        await Step(Loc.T("Otimizar unidades", "Optimize drives"), _opt, async c =>
        {
            var parts = new List<Maintenance.Outcome>();
            foreach (var v in _vols) parts.Add(await Maintenance.OptimizeAsync(v, Reporter(_opt), c));
            return Merge(parts);
        });
        await Step("DNS", _dns, _ => Maintenance.FlushDnsAsync());

        bool cancelled = ct.IsCancellationRequested;
        int bad = results.Count(r => !r.o.Ok);
        _lastReport = Maintenance.SaveReport(log.ToString());
        _reportLink.Visibility = Visibility.Visible;
        _runAllStatus.Text = (cancelled ? Loc.T("Cancelado. ", "Cancelled. ") : bad == 0 ? Loc.T("Tudo concluído ✓ ", "All done ✓ ") : Loc.T($"Concluído com {bad} problema(s): veja cada bloco. ", $"Finished with {bad} problem(s): see each block. "))
            + Loc.T("Relatório salvo em Documentos\\Pulso\\Manutencao.", "Report saved to Documents\\Pulso\\Manutencao.");
        _runAllStatus.SetResourceReference(TextBlock.ForegroundProperty, cancelled || bad > 0 ? "Warn" : "Good");
        _runAll.Content = Loc.T("Executar tudo", "Run everything");
        RefreshCleanSizes();
        End();
        _vols = await Task.Run(Maintenance.Volumes);
        RenderVolumes();
    }

    static Maintenance.Outcome Merge(List<Maintenance.Outcome> parts) =>
        parts.Count == 1 ? parts[0] : new Maintenance.Outcome(parts.All(p => p.Ok), string.Join("\n", parts.Select(p => p.Summary)), string.Join("\r\n", parts.Select(p => p.Log)), parts.Any(p => p.Warning));

    void Save(Maintenance.Outcome o)
    {
        if (string.IsNullOrWhiteSpace(o.Log)) return;
        try { _lastReport = Maintenance.SaveReport($"Pulso · {DateTime.Now:g}\r\n{o.Summary}\r\n\r\n{o.Log}"); }
        catch (Exception ex) { Paths.LogError("Maintenance.Save", ex); }
        _reportLink.Visibility = Visibility.Visible;
    }

    void OpenReport()
    {
        if (_lastReport != null) StartupApps.ShowInExplorer(_lastReport);
    }
}
