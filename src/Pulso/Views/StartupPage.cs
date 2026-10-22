using System.Windows;
using System.Windows.Controls;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class StartupPage : PageBase
{
    readonly TextBlock _eyebrow = Label("");
    readonly TextBlock _on = Num("—", 20), _off = Num("—", 20), _slow = Num("—", 20), _boot = Num("—", 20);
    readonly Stack _list = new(0);
    readonly Stack _banners = new(10);
    readonly Button _refresh;
    bool _loaded, _busy;
    int _nOn, _nOff;

    public StartupPage()
    {
        _refresh = Ui.Button(Loc.T("Atualizar", "Refresh"), () => _ = LoadAsync());
        var tiles = Wrap(4, 12, new UIElement[]
        {
            StatTile(Loc.T("Abrem com o Windows", "Start with Windows"), _on, null),
            StatTile(Loc.T("Desativados", "Disabled"), _off, null),
            StatTile(Loc.T("Atrasaram o boot", "Slowed down boot"), _slow, Loc.T("Programas que o próprio Windows registrou como lentos para iniciar nos últimos 30 dias.", "Programs Windows itself logged as slow to start in the last 30 days.")),
            StatTile(Loc.T("Último boot", "Last boot"), _boot, Loc.T("Tempo do botão ligar até a área de trabalho pronta para uso, segundo o Windows. Precisa de administrador.", "Time from power button to a usable desktop, according to Windows. Requires administrator.")),
        });
        var how = Text(Loc.T("Desativar não desinstala nem apaga nada: o programa só deixa de abrir sozinho, e você pode reativar quando quiser. É o mesmo que o Gerenciador de Tarefas faz.",
                             "Disabling doesn't uninstall or delete anything: the program just stops opening by itself, and you can re-enable it anytime. It's the same thing Task Manager does."), 12.5, null, "Text3", wrap: true);
        Content = Scroll(new Stack(18,
            Header(_eyebrow, Loc.T("Inicialização", "Startup apps"), _refresh),
            _banners, tiles,
            Card(new Stack(12, how, _list), 20, 18)));
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
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        _refresh.IsEnabled = false;
        _eyebrow.Text = Loc.T("Lendo…", "Reading…");
        var (items, boot) = await Task.Run(() => (StartupApps.Read(), StartupApps.LastBoot()));
        _busy = false;
        _refresh.IsEnabled = true;

        _nOn = items.Count(i => i.Enabled);
        _nOff = items.Count - _nOn;
        _on.Text = Fmt.N(_nOn);
        _off.Text = Fmt.N(_nOff);
        int slow = items.Count(i => i.Enabled && i.SlowBootSec != null);
        _slow.Text = Native.IsAdmin ? Fmt.N(slow) : Fmt.Dash;
        _slow.SetResourceReference(TextBlock.ForegroundProperty, slow > 0 ? "Warn" : "Text");
        _boot.Text = boot.total is TimeSpan t ? Fmt.N(t.TotalSeconds) + " s" : Fmt.Dash;
        _boot.SetResourceReference(TextBlock.ForegroundProperty, boot.total is TimeSpan bt && bt.TotalSeconds > 60 ? "Warn" : "Text");
        _eyebrow.Text = boot.when is DateTime w ? Loc.T($"Último boot em {w.ToString("g", Fmt.Culture)}", $"Last boot at {w.ToString("g", Fmt.Culture)}") : Loc.T("Programas, atalhos e tarefas que abrem ao entrar no Windows", "Programs, shortcuts and tasks that open when you sign in");

        _banners.Children.Clear();
        if (!Native.IsAdmin)
            _banners.Children.Add(Banner(Loc.T("Sem administrador só dá para alterar os itens do seu usuário. Os de \"todos os usuários\" e as tarefas agendadas ficam bloqueados.", "Without administrator rights only your user's items can be changed. \"All users\" items and scheduled tasks are locked."), Health.Warning, false));

        _list.Children.Clear();
        bool first = true;
        foreach (var it in items)
        {
            _list.Children.Add(ItemRow(it, first));
            first = false;
        }
        if (items.Count == 0) _list.Children.Add(Text(Loc.T("Nenhum programa abre com o Windows.", "No programs start with Windows."), 13, null, "TextMuted"));
    }

    UIElement ItemRow(StartupApps.Item it, bool first)
    {
        var parts = new List<string> { it.Publisher.Length > 0 ? it.Publisher : Loc.T("Fornecedor desconhecido", "Unknown publisher"), it.Where };
        var sub = Text(string.Join(" · ", parts), 12, null, "TextMuted");
        var info = new Stack(3, Text(it.DisplayName, 13.5, FontWeights.SemiBold), sub) { VerticalAlignment = VerticalAlignment.Center };
        info.ToolTip = it.Command;
        if (it.SlowBootSec is double s)
            info.Children.Add(Text(Loc.T($"O Windows registrou que ele atrasou o boot em {Fmt.N(s, 1)} s", $"Windows logged it slowing boot by {Fmt.N(s, 1)} s"), 12, FontWeights.SemiBold, "Warn"));
        if (it.Missing)
            info.Children.Add(Text(Loc.T("O arquivo do programa não existe mais: entrada esquecida, pode desativar sem medo", "The program file no longer exists: a leftover entry, safe to disable"), 12, FontWeights.SemiBold, "Warn"));

        var status = Text("", 12, null, "TextMuted");
        status.VerticalAlignment = VerticalAlignment.Center;
        status.Margin = new Thickness(0, 0, 12, 0);
        CheckBox? toggle = null;
        toggle = Toggle(it.Enabled, async on =>
        {
            if (on == it.Enabled) return;
            toggle!.IsEnabled = false;
            var (ok, err) = await Task.Run(() => StartupApps.SetEnabled(it, on));
            toggle.IsEnabled = true;
            if (!ok)
            {
                toggle.IsChecked = it.Enabled;
                status.Text = Loc.T("Não foi possível: ", "Failed: ") + err;
                status.SetResourceReference(TextBlock.ForegroundProperty, "Crit");
                return;
            }
            status.Text = on ? Loc.T("Ativado", "Enabled") : Loc.T("Desativado · vale no próximo boot", "Disabled · takes effect next boot");
            status.SetResourceReference(TextBlock.ForegroundProperty, "Good");
            _nOn += on ? 1 : -1;
            _nOff += on ? -1 : 1;
            _on.Text = Fmt.N(_nOn);
            _off.Text = Fmt.N(_nOff);
        });
        if (it.NeedsAdmin && !Native.IsAdmin)
        {
            toggle.IsEnabled = false;
            toggle.ToolTip = Loc.T("Precisa de administrador", "Requires administrator");
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(status);
        if (!it.Missing && it.ExePath.Length > 0)
        {
            var open = Ui.Button(Loc.T("Local", "Folder"), () => StartupApps.ShowInExplorer(it.ExePath), "LinkButton");
            open.ToolTip = Loc.T("Abrir a pasta do programa", "Open the program's folder");
            open.VerticalAlignment = VerticalAlignment.Center;
            open.Margin = new Thickness(0, 0, 16, 0);
            right.Children.Add(open);
        }
        right.Children.Add(toggle);
        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(info);
        return new Border { Child = dock, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 11, 0, 11) }.Res(Border.BorderBrushProperty, "Divider");
    }
}
