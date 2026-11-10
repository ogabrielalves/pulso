using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using System.IO;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Info;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

/// <summary>Histórico de travamentos: resumo, diagnóstico, programas que mais falham e linha do tempo.</summary>
public sealed class CrashesPage : PageBase
{
    readonly TextBlock _eyebrow = Label("");
    readonly TextBlock _bsod = Num("—", 20), _shut = Num("—", 20), _apps = Num("—", 20), _hw = Num("—", 20);
    readonly Stack _diag = new(10), _top = new(0), _timeline = new(0);
    readonly Border _topCard;
    readonly Button _refresh;
    List<Crashes.Event> _events = new();
    int _days = 30;
    string _filter = "all";
    bool _loaded, _busy;

    public CrashesPage()
    {
        _refresh = Ui.Button(Loc.T("Atualizar", "Refresh"), () => _ = LoadAsync());
        var tiles = Wrap(4, 12, new UIElement[]
        {
            StatTile(Loc.T("Telas azuis", "Blue screens"), _bsod, Loc.T("O Windows parou com um erro grave e reiniciou.", "Windows stopped with a critical error and restarted.")),
            StatTile(Loc.T("Desligamentos inesperados", "Unexpected shutdowns"), _shut, Loc.T("Desligou ou reiniciou de repente, sem tela azul (queda de energia, travamento total, fonte, temperatura).", "Shut down or restarted suddenly without a blue screen (power loss, full freeze, power supply, temperature).")),
            StatTile(Loc.T("Programas que fecharam", "Programs that crashed"), _apps, Loc.T("Fecharam sozinhos ou pararam de responder.", "Closed by themselves or stopped responding.")),
            StatTile(Loc.T("Vídeo e hardware", "Video and hardware"), _hw, Loc.T("Driver de vídeo que reiniciou e erros relatados pelo próprio processador (WHEA).", "Display driver resets and errors reported by the processor itself (WHEA).")),
        });

        var period = Segments("crashdays", new[] { 7, 30, 90 }.Select(d => (Loc.T($"{d} dias", $"{d} days"), d == _days, (Action)(() => { _days = d; _ = LoadAsync(); }))));
        var filters = Segments("crashfilter", new (string, bool, Action)[]
        {
            (Loc.T("Tudo", "All"), true, () => SetFilter("all")),
            (Loc.T("Telas azuis", "Blue screens"), false, () => SetFilter("bsod")),
            (Loc.T("Desligamentos", "Shutdowns"), false, () => SetFilter("shutdown")),
            (Loc.T("Programas", "Programs"), false, () => SetFilter("apps")),
            (Loc.T("Vídeo e hardware", "Video and hardware"), false, () => SetFilter("hw")),
        });
        _topCard = Card(new Stack(12, Title(Loc.T("Programas que mais fecharam", "Programs that crashed the most")), _top), 20, 18);
        var timelineCard = Card(new Stack(14, filters, _timeline), 20, 18);

        var headActions = new StackPanel { Orientation = Orientation.Horizontal };
        headActions.Children.Add(period);
        _refresh.Margin = new Thickness(10, 0, 0, 0);
        headActions.Children.Add(_refresh);

        Content = Scroll(new Stack(18,
            Header(_eyebrow, Loc.T("Histórico de travamentos", "Crash history"), headActions),
            tiles, _diag, _topCard,
            new Stack(10, Caps(Loc.T("Linha do tempo", "Timeline")), timelineCard)));
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

    void SetFilter(string f)
    {
        _filter = f;
        RenderTimeline();
    }

    async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        _refresh.IsEnabled = false;
        _eyebrow.Text = Loc.T("Lendo os registros do Windows…", "Reading Windows logs…");
        int days = _days;
        _events = await Task.Run(() => Crashes.Read(days));
        _busy = false;
        _refresh.IsEnabled = true;
        Render();
    }

    static bool IsApp(Crashes.Event e) => e.Kind is Crashes.Kind.AppCrash or Crashes.Kind.AppHang;
    static bool IsHw(Crashes.Event e) => e.Kind is Crashes.Kind.Gpu or Crashes.Kind.Hardware;

    void Render()
    {
        int bsod = _events.Count(e => e.Kind == Crashes.Kind.BlueScreen);
        int shut = _events.Count(e => e.Kind == Crashes.Kind.Shutdown);
        int apps = _events.Count(IsApp);
        int gpu = _events.Count(e => e.Kind == Crashes.Kind.Gpu);
        int whea = _events.Count(e => e.Kind == Crashes.Kind.Hardware);
        Set(_bsod, bsod, "Crit");
        Set(_shut, shut, "Warn");
        Set(_apps, apps, apps >= 10 ? "Warn" : "Text");
        Set(_hw, gpu + whea, "Warn");

        var lastSerious = _events.FirstOrDefault(e => e.Kind is Crashes.Kind.BlueScreen or Crashes.Kind.Shutdown);
        _eyebrow.Text = lastSerious == null
            ? Loc.T($"Nenhuma tela azul ou desligamento inesperado nos últimos {_days} dias", $"No blue screens or unexpected shutdowns in the last {_days} days")
            : Loc.T($"Último problema grave há {Days(lastSerious.When)}", $"Last serious problem {Days(lastSerious.When)} ago");

        // diagnóstico em linguagem simples
        _diag.Children.Clear();
        if (whea > 0)
            _diag.Children.Add(Banner(Loc.T($"O processador relatou {whea} erro(s) de hardware. Isso costuma indicar CPU ou memória instável: overclock, perfil XMP/EXPO da RAM, undervolt ou temperatura alta. Vale voltar tudo ao padrão e rodar o Teste de estresse.",
                                            $"The processor reported {whea} hardware error(s). This usually means an unstable CPU or memory: overclock, RAM XMP/EXPO, undervolt or high temperature. Revert to stock and run the Stress test."), Health.Critical, false));
        if (bsod > 0)
        {
            var common = _events.Where(e => e.Kind == Crashes.Kind.BlueScreen).GroupBy(e => e.Title).OrderByDescending(g => g.Count()).First();
            _diag.Children.Add(Banner(Loc.T($"{bsod} tela(s) azul(is). A mais frequente: {common.Key.Replace("Tela azul: ", "")} ({common.Count()}x). {common.First().Hint}",
                                            $"{bsod} blue screen(s). Most frequent: {common.Key.Replace("Blue screen: ", "")} ({common.Count()}x). {common.First().Hint}"), Health.Critical, false));
        }
        if (gpu >= 2)
            _diag.Children.Add(Banner(Loc.T($"O driver de vídeo reiniciou {gpu} vezes. Atualize o driver da placa de vídeo e confira a temperatura da GPU durante jogos.",
                                            $"The display driver reset {gpu} times. Update the graphics driver and check GPU temperature while gaming."), Health.Warning, false));
        if (shut >= 2)
            _diag.Children.Add(Banner(Loc.T($"{shut} desligamentos inesperados. Se não foi queda de energia, confira temperaturas e a fonte (no notebook, a bateria).",
                                            $"{shut} unexpected shutdowns. If it wasn't a power outage, check temperatures and the power supply (battery on a laptop)."), Health.Warning, false));
        if (Directory.Exists(Crashes.MinidumpFolder) && bsod > 0)
        {
            var open = Ui.Button(Loc.T("Abrir a pasta dos arquivos de tela azul (minidump)", "Open the blue screen dump folder (minidump)"), () =>
            {
                try { Process.Start(new ProcessStartInfo(Crashes.MinidumpFolder) { UseShellExecute = true }); } catch { }
            }, "LinkButton");
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.ToolTip = Loc.T("Esses arquivos podem ser analisados no WinDbg ou BlueScreenView para saber o driver exato.", "These files can be analyzed in WinDbg or BlueScreenView to find the exact driver.");
            _diag.Children.Add(open);
        }
        if (_diag.Children.Count == 0)
        {
            var ok = Text(apps == 0 ? Loc.T("Tudo estável neste período: nenhum travamento registrado.", "All stable in this period: no crashes recorded.")
                                    : Loc.T("Sistema estável: nenhuma tela azul, desligamento inesperado ou erro de hardware. Só alguns programas fecharam sozinhos (abaixo).", "System is stable: no blue screens, unexpected shutdowns or hardware errors. Only a few programs crashed (below)."), 13, FontWeights.SemiBold, "Good", wrap: true);
            _diag.Children.Add(Tile(ok, 16, 14));
        }

        // programas que mais falharam
        _top.Children.Clear();
        var groups = _events.Where(IsApp).GroupBy(e => e.App ?? "?", StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(8).ToList();
        bool first = true;
        foreach (var g in groups)
        {
            int crash = g.Count(e => e.Kind == Crashes.Kind.AppCrash), hang = g.Count() - crash;
            var parts = new List<string>();
            if (crash > 0) parts.Add(Loc.T($"{crash}x fechou", $"crashed {crash}x"));
            if (hang > 0) parts.Add(Loc.T($"{hang}x travou", $"froze {hang}x"));
            parts.Add(Loc.T("última há ", "last ") + Days(g.First().When) + Loc.T("", " ago"));
            var info = new Stack(3, Text(g.Key, 13.5, FontWeights.SemiBold), Text(string.Join(" · ", parts), 12, null, "TextMuted"));
            var count = Num(Fmt.N(g.Count()), 16, g.Count() >= 5 ? "Warn" : "Text2");
            count.VerticalAlignment = VerticalAlignment.Center;
            var dock = new DockPanel();
            DockPanel.SetDock(count, Dock.Right);
            dock.Children.Add(count);
            dock.Children.Add(info);
            _top.Children.Add(new Border { Child = dock, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 10, 0, 10) }.Res(Border.BorderBrushProperty, "Divider"));
            first = false;
        }
        _topCard.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderTimeline();
    }

    static void Set(TextBlock t, int n, string badBrush)
    {
        t.Text = Fmt.N(n);
        t.SetResourceReference(TextBlock.ForegroundProperty, n > 0 ? badBrush : "Good");
    }

    void RenderTimeline()
    {
        var list = _events.Where(e => _filter switch
        {
            "bsod" => e.Kind == Crashes.Kind.BlueScreen,
            "shutdown" => e.Kind == Crashes.Kind.Shutdown,
            "apps" => IsApp(e),
            "hw" => IsHw(e),
            _ => true,
        }).Take(300).ToList();

        _timeline.Children.Clear();
        DateTime? day = null;
        bool first = true;
        foreach (var e in list)
        {
            if (day != e.When.Date)
            {
                day = e.When.Date;
                var h = Caps(e.When.Date == DateTime.Today ? Loc.T("Hoje", "Today") : e.When.Date == DateTime.Today.AddDays(-1) ? Loc.T("Ontem", "Yesterday") : e.When.ToString("dddd, d MMM", Fmt.Culture));
                h.Margin = new Thickness(0, first ? 0 : 14, 0, 4);
                _timeline.Children.Add(h);
                first = true;
            }
            _timeline.Children.Add(EventRow(e, first));
            first = false;
        }
        if (list.Count == 0)
            _timeline.Children.Add(Text(_busy ? Loc.T("Lendo…", "Reading…") : Loc.T("Nada registrado neste período.", "Nothing recorded in this period."), 13, null, "TextMuted"));
    }

    static UIElement EventRow(Crashes.Event e, bool first)
    {
        string brush = e.Kind switch
        {
            Crashes.Kind.BlueScreen => "Crit",
            Crashes.Kind.Hardware when e.Title.Contains(Loc.T("grave", "Fatal")) => "Crit",
            Crashes.Kind.Shutdown or Crashes.Kind.Gpu or Crashes.Kind.Hardware => "Warn",
            _ => "TextMuted",
        };
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top }.Res(Border.BackgroundProperty, brush);
        var time = Num(e.When.ToString("HH:mm"), 12, "TextMuted", FontWeights.Normal);
        time.Width = 46;
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(0, 2, 0, 0);
        var body = new Stack(3, Text(e.Title, 13.5, FontWeights.SemiBold), Text(e.Detail, 12, null, "Text3", wrap: true));
        if (e.Hint != null) body.Children.Add(Text(e.Hint, 12, null, "TextMuted", wrap: true));
        var dock = new DockPanel();
        DockPanel.SetDock(time, Dock.Left);
        DockPanel.SetDock(dot, Dock.Left);
        dock.Children.Add(time);
        dock.Children.Add(dot);
        dock.Children.Add(body);
        return new Border { Child = dock, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 10, 0, 10) }.Res(Border.BorderBrushProperty, "Divider");
    }

    static string Days(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalHours < 1) return Loc.T($"{Math.Max(1, (int)d.TotalMinutes)} min", $"{Math.Max(1, (int)d.TotalMinutes)} min");
        if (d.TotalDays < 1) return Loc.T($"{(int)d.TotalHours} h", $"{(int)d.TotalHours} h");
        int n = (int)d.TotalDays;
        return n == 1 ? Loc.T("1 dia", "1 day") : Loc.T($"{n} dias", $"{n} days");
    }
}
