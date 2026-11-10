using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using System.Text;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

/// <summary>Manutenção e atualizações no Linux. Duas telas com o mesmo esqueleto: "reparo" e "programas".</summary>
public sealed class LinuxMaintenancePage : PageBase
{
    public enum Mode { Maintenance, Updates }

    readonly Mode _mode;
    readonly List<Button> _buttons = new();
    readonly TextBlock _status = Text("", 12.5, null, "TextMuted", wrap: true);
    readonly TextBox _log = new()
    {
        IsReadOnly = true, AcceptsReturn = true, Height = 240, FontFamily = MonoFont, FontSize = 11.5,
        TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
    };
    readonly StringBuilder _logText = new();
    readonly Stack _cleanList = new(8);
    readonly TextBlock _cleanTotal = Text("", 12.5, Avalonia.Media.FontWeight.SemiBold, "Text3");
    readonly Dictionary<LinuxMaintenance.CleanItem, (CheckBox box, TextBlock size)> _cleanRows = new();
    readonly List<(Button btn, Func<bool> missing)> _installBtns = new();
    List<LinuxMaintenance.CleanItem> _items = new();
    bool _busy, _loaded;

    public LinuxMaintenancePage(Mode mode)
    {
        _mode = mode;
        _log.Classes.Add("mono");
        _status.Visibility = Visibility.Collapsed;
        var body = mode == Mode.Maintenance ? BuildMaintenance() : BuildUpdates();
        var title = mode == Mode.Maintenance
            ? Header(Loc.T("Verificação e limpeza com as ferramentas da sua distro", "Check and cleanup with your distro's own tools"), Loc.T("Reparo e limpeza", "Repair & cleanup"))
            : Header(Loc.T($"Atualizações via {LinuxMaintenance.ManagerName}, Flatpak, Snap e firmware", $"Updates via {LinuxMaintenance.ManagerName}, Flatpak, Snap and firmware"), Loc.T("Programas", "Programs"));

        var logCard = Card(new Stack(10, Title(Loc.T("Saída", "Output")), _log), 20, 18);
        Content = Scroll(new Stack(18,
            title,
            LinuxMaintenance.CanElevate ? null : Banner(Loc.T("O pkexec (polkit) não foi encontrado e você não está como root: ações de administrador vão falhar. Rode o Pulso com sudo.",
                                                              "pkexec (polkit) was not found and you are not root: administrator actions will fail. Run Pulso with sudo.")),
            Pulso.Platform.OS.Current.IsElevated || !LinuxMaintenance.CanElevate ? null : Banner(Loc.T("Ações que precisam de administrador abrem uma janela pedindo a sua senha.", "Actions that need administrator open a window asking for your password."), Health.Unknown, false),
            _status,
            body,
            logCard));
    }

    // ------------------------------------------------------------- montagem

    static Border Section(string title, string desc, UIElement body) =>
        Card(new Stack(12, Title(title), Text(desc, 12.5, null, "Text3", wrap: true), body), 20, 18);

    Button Act(string label, Func<Action<string>, Task<LinuxMaintenance.Outcome>> work, string? style = null, string? confirm = null, bool available = true)
    {
        var b = Ui.Button(label, () => Run(label, work, confirm), style);
        b.Padding = new Thickness(16, 8, 16, 8);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.IsEnabled = available;
        if (!available) ToolTip.SetTip(b, Loc.T("Ferramenta não instalada neste sistema.", "Tool not installed on this system."));
        _buttons.Add(b);
        return b;
    }

    /// <summary>Botão "Instalar X": só aparece enquanto a ferramenta não existir.</summary>
    Button Install(string package, string tool)
    {
        var b = Act(Loc.T($"Instalar {package}", $"Install {package}"), log => LinuxMaintenance.InstallPackageAsync(package, log),
            confirm: Loc.T($"Instalar o pacote {package} pelo gerenciador da distro? Pede a senha de administrador.", $"Install the {package} package with the distro's package manager? Asks for the administrator password."));
        _installBtns.Add((b, () => !LinuxMaintenance.Has(tool)));
        b.IsVisible = !LinuxMaintenance.Has(tool);
        return b;
    }

    UIElement BuildMaintenance()
    {
        var integrity = Section(Loc.T("Integridade dos pacotes", "Package integrity"),
            Loc.T($"Compara os arquivos instalados com os pacotes originais ({LinuxMaintenance.ManagerName}), o equivalente ao SFC. Se algo difere, reinstale o pacote indicado. Mudanças em arquivos de configuração são ignoradas.",
                  $"Compares installed files against the original packages ({LinuxMaintenance.ManagerName}), the equivalent of SFC. If something differs, reinstall the listed package. Changes to configuration files are ignored."),
            new Stack(10, Act(Loc.T("Verificar pacotes", "Check packages"), LinuxMaintenance.CheckPackagesAsync, "Primary", available: LinuxMaintenance.Manager != LinuxMaintenance.Pm.None),
                          LinuxMaintenance.Manager == LinuxMaintenance.Pm.Apt ? Install("debsums", "debsums") : null));

        var disks = Section(Loc.T("Discos", "Drives"),
            Loc.T("Saúde SMART de cada disco (smartctl) e TRIM nos SSDs (fstrim), o equivalente a verificar e otimizar unidades. Para checar o sistema de arquivos (fsck), a partição precisa estar desmontada: faça por um pendrive de recuperação.",
                  "SMART health of each drive (smartctl) and TRIM on SSDs (fstrim), the equivalent of checking and optimizing drives. Checking the file system (fsck) needs the partition unmounted: do it from a recovery USB stick."),
            new Stack(10, Act(Loc.T("Verificar saúde dos discos", "Check drive health"), LinuxMaintenance.SmartAsync, available: LinuxMaintenance.Has("lsblk")),
                          Install("smartmontools", "smartctl"),
                          Act(Loc.T("Otimizar (TRIM)", "Optimize (TRIM)"), LinuxMaintenance.TrimAsync, available: LinuxMaintenance.Has("fstrim"))));

        var clean = Section(Loc.T("Limpeza", "Cleanup"),
            Loc.T("Veja quanto cada item ocupa antes de apagar. Nada pessoal é tocado (documentos, fotos, downloads).", "See how much each item takes before deleting. Nothing personal is touched (documents, photos, downloads)."),
            new Stack(12, _cleanList, _cleanTotal, Act(Loc.T("Limpar selecionados", "Clean selected"), RunClean, "Primary")));

        var net = Section(Loc.T("Rede", "Network"),
            Loc.T("Limpar o DNS ajuda quando um site não abre ou abre o endereço antigo. Reiniciar o serviço de rede derruba a conexão por alguns segundos.",
                  "Clearing DNS helps when a site won't open or opens an old address. Restarting the network service drops the connection for a few seconds."),
            new Stack(10, Act(Loc.T("Limpar cache de DNS", "Clear DNS cache"), LinuxMaintenance.FlushDnsAsync),
                          Act(Loc.T("Reiniciar serviço de rede", "Restart network service"), LinuxMaintenance.RestartNetworkAsync,
                              confirm: Loc.T("A conexão cai por alguns segundos. Continuar?", "The connection drops for a few seconds. Continue?"), available: LinuxMaintenance.Has("systemctl"))));

        var memtest = Ui.Button(Loc.T("MemTest86 (pendrive) ↗", "MemTest86 (USB stick) ↗"), () => Pulso.Platform.Shell.Open("https://www.memtest86.com/download.htm"), "LinkButton");
        memtest.HorizontalAlignment = HorizontalAlignment.Left;
        var ram = Section(Loc.T("Memória RAM", "Memory (RAM)"),
            Loc.T("O teste de erros do Pulso só alcança a memória livre. Para testar toda a RAM, o teste precisa rodar antes do sistema carregar: grave o MemTest86 (ou o Memtest86+ do menu do GRUB, quando houver) num pendrive.",
                  "Pulso's error test only reaches free memory. To test all RAM the test must run before the system loads: write MemTest86 (or Memtest86+ from the GRUB menu, when available) to a USB stick."),
            memtest);

        return Cols(18, new[] { 1.0, 1.0 }, new Stack(18, integrity, disks, net), new Stack(18, clean, ram));
    }

    UIElement BuildUpdates()
    {
        var all = Act(Loc.T("Atualizar tudo", "Update everything"), UpdateAll, "Primary");

        var sys = Section(Loc.T($"Pacotes do sistema ({LinuxMaintenance.ManagerName})", $"System packages ({LinuxMaintenance.ManagerName})"),
            Loc.T("Atualiza os programas e bibliotecas instalados pela distro, o equivalente ao winget. Pede a senha de administrador.",
                  "Updates the programs and libraries installed by the distro, the equivalent of winget. Asks for the administrator password."),
            Act(Loc.T("Atualizar pacotes", "Update packages"), LinuxMaintenance.UpdateSystemAsync, available: LinuxMaintenance.Manager != LinuxMaintenance.Pm.None));

        var apps = Section(Loc.T("Flatpak e Snap", "Flatpak and Snap"),
            Loc.T("Aplicativos instalados por Flatpak (sem senha) e por Snap (pede senha).", "Apps installed through Flatpak (no password) and Snap (asks for the password)."),
            new Stack(10, Act("Flatpak", LinuxMaintenance.UpdateFlatpakAsync, available: LinuxMaintenance.Has("flatpak")),
                          Act("Snap", LinuxMaintenance.UpdateSnapAsync, available: LinuxMaintenance.Has("snap"))));

        var fw = Section(Loc.T("Firmware (BIOS, SSD, periféricos)", "Firmware (BIOS, SSD, peripherals)"),
            Loc.T("Usa o fwupd/LVFS para checar e instalar firmware dos fabricantes que participam. Algumas atualizações aplicam na próxima reinicialização.",
                  "Uses fwupd/LVFS to check and install firmware from participating vendors. Some updates apply on the next restart."),
            new Stack(10, Act(Loc.T("Verificar firmware", "Check firmware"), LinuxMaintenance.FirmwareCheckAsync, available: LinuxMaintenance.Has("fwupdmgr")),
                          Act(Loc.T("Instalar firmware", "Install firmware"), LinuxMaintenance.FirmwareInstallAsync, available: LinuxMaintenance.Has("fwupdmgr"),
                              confirm: Loc.T("Atualizar firmware pode demorar e não deve ser interrompido. Mantenha o notebook na tomada. Continuar?", "Updating firmware can take a while and must not be interrupted. Keep a laptop plugged in. Continue?"))));

        var content = new List<UIElement> { sys, apps, fw };
        if (LinuxMaintenance.Has("ubuntu-drivers"))
            content.Add(Section(Loc.T("Drivers de vídeo e dispositivos", "Graphics and device drivers"),
                Loc.T("No Linux, a maior parte dos drivers vem no kernel e é atualizada com o sistema. Para a placa de vídeo NVIDIA e outros drivers proprietários, o ubuntu-drivers indica e instala o recomendado.",
                      "On Linux most drivers ship in the kernel and update with the system. For NVIDIA graphics and other proprietary drivers, ubuntu-drivers suggests and installs the recommended one."),
                new Stack(10, Act(Loc.T("Ver drivers disponíveis", "Show available drivers"), LinuxMaintenance.GpuDriversListAsync),
                              Act(Loc.T("Instalar o recomendado", "Install the recommended"), LinuxMaintenance.GpuDriversInstallAsync,
                                  confirm: Loc.T("Vai instalar o driver proprietário recomendado; é preciso reiniciar depois. Continuar?", "This installs the recommended proprietary driver; a restart is needed afterwards. Continue?")))));

        return new Stack(18, new[] { (UIElement)all }.Concat(content).ToArray());
    }

    // ------------------------------------------------------------- limpeza

    protected override void OnShown()
    {
        if (_loaded || _mode != Mode.Maintenance) return;
        _loaded = true;
        _ = LoadCleanAsync();
    }

    async Task LoadCleanAsync()
    {
        _cleanTotal.Text = Loc.T("Calculando tamanhos…", "Calculating sizes…");
        _items = LinuxMaintenance.CleanItems();
        RenderClean();
        await Task.Run(() => LinuxMaintenance.MeasureAll(_items));
        RefreshSizes();
    }

    void RenderClean()
    {
        _cleanList.Children.Clear();
        _cleanRows.Clear();
        foreach (var it in _items)
        {
            var size = Num("…", 12.5, "Text", Avalonia.Media.FontWeight.SemiBold);
            size.VerticalAlignment = VerticalAlignment.Center;
            size.Margin = new Thickness(12, 0, 0, 0);
            var content = new Stack(2, Text(it.Name, 13, Avalonia.Media.FontWeight.SemiBold), Text(it.Desc, 11.5, null, "TextMuted", wrap: true)) { MaxWidth = 420 };
            var box = new CheckBox { Content = content, IsChecked = it.Default, HorizontalAlignment = HorizontalAlignment.Stretch };
            box.Classes.Add("tick");
            box.IsCheckedChanged += (_, _) => UpdateTotal();
            var dock = new DockPanel();
            DockPanel.SetDock(size, Dock.Right);
            dock.Children.Add(size);
            dock.Children.Add(box);
            _cleanRows[it] = (box, size);
            _cleanList.Children.Add(dock);
        }
    }

    void RefreshSizes()
    {
        foreach (var (it, (_, size)) in _cleanRows)
            size.Text = it.Size is long s ? (s < 1024 * 1024 ? "0" : Fmt.Bytes(s)) : "—";
        UpdateTotal();
    }

    void UpdateTotal()
    {
        long total = _cleanRows.Where(r => r.Value.box.IsChecked == true).Sum(r => r.Key.Size ?? 0);
        _cleanTotal.Text = Loc.T($"Selecionado: {Fmt.Bytes(total)}", $"Selected: {Fmt.Bytes(total)}");
    }

    async Task<LinuxMaintenance.Outcome> RunClean(Action<string> log)
    {
        var sel = _cleanRows.Where(r => r.Value.box.IsChecked == true).Select(r => r.Key).ToList();
        if (sel.Count == 0) return new LinuxMaintenance.Outcome(false, Loc.T("Nada selecionado.", "Nothing selected."), "");
        if (sel.Any(i => i.Id == "trash") && MessageBox.Show(Window.GetWindow(this)!, Loc.T("A lixeira será esvaziada e não dá para recuperar o que está nela. Continuar?", "The trash will be emptied and its contents cannot be recovered. Continue?"),
                "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return new LinuxMaintenance.Outcome(false, Loc.T("Cancelado", "Cancelled"), "");
        var o = await LinuxMaintenance.CleanAsync(sel, log);
        _ = Task.Run(() => LinuxMaintenance.MeasureAll(_items)).ContinueWith(_ => Dispatcher.UIThread.Post(RefreshSizes));
        return o;
    }

    // ------------------------------------------------------------- execução

    async Task<LinuxMaintenance.Outcome> UpdateAll(Action<string> log)
    {
        var parts = new List<LinuxMaintenance.Outcome>();
        if (LinuxMaintenance.Manager != LinuxMaintenance.Pm.None) { log("== " + LinuxMaintenance.ManagerName); parts.Add(await LinuxMaintenance.UpdateSystemAsync(log)); }
        if (LinuxMaintenance.Has("flatpak")) { log("== Flatpak"); parts.Add(await LinuxMaintenance.UpdateFlatpakAsync(log)); }
        if (LinuxMaintenance.Has("snap")) { log("== Snap"); parts.Add(await LinuxMaintenance.UpdateSnapAsync(log)); }
        if (parts.Count == 0) return new LinuxMaintenance.Outcome(false, Loc.T("Nenhum gerenciador de pacotes encontrado.", "No package manager found."), "");
        bool ok = parts.All(p => p.Ok);
        return new LinuxMaintenance.Outcome(ok, ok ? Loc.T("Tudo atualizado ✓", "Everything updated ✓") : string.Join("\n", parts.Where(p => !p.Ok).Select(p => p.Summary)), string.Join("\n", parts.Select(p => p.Log)));
    }

    void AppendLog(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logText.AppendLine(line);
            if (_logText.Length > 200_000) _logText.Remove(0, _logText.Length - 150_000);
            _log.Text = _logText.ToString();
            _log.CaretIndex = _log.Text.Length;
        });
    }

    async void Run(string name, Func<Action<string>, Task<LinuxMaintenance.Outcome>> work, string? confirm)
    {
        if (_busy) return;
        if (confirm != null && MessageBox.Show(Window.GetWindow(this)!, confirm, "Pulso", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _busy = true;
        foreach (var b in _buttons) b.IsEnabled = false;
        _status.Visibility = Visibility.Visible;
        _status.Text = Loc.T($"Executando: {name}…", $"Running: {name}…");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _logText.Clear();
        _log.Text = "";
        AppendLog($"$ {name} · {DateTime.Now:g}");

        LinuxMaintenance.Outcome o;
        try { o = await work(AppendLog); }
        catch (Exception ex) { Paths.LogError("LinuxMaintenance." + name, ex); o = new LinuxMaintenance.Outcome(false, ex.Message, ex.ToString()); }

        _status.Text = o.Summary;
        _status.SetResourceReference(TextBlock.ForegroundProperty, !o.Ok ? "Crit" : o.Warning ? "Warn" : "Good");
        AppendLog("→ " + o.Summary);
        try { LinuxMaintenance.SaveReport($"Pulso · {name} · {DateTime.Now:g}\n{o.Summary}\n\n{o.Log}"); }
        catch (Exception ex) { Paths.LogError("LinuxMaintenance.Save", ex); }
        _busy = false;
        foreach (var b in _buttons) b.IsEnabled = true;
        // volta a desabilitar o que a distro não oferece
        foreach (var b in _buttons.Where(b => ToolTip.GetTip(b) != null)) b.IsEnabled = false;
        foreach (var (btn, missing) in _installBtns) btn.IsVisible = missing();
    }
}
