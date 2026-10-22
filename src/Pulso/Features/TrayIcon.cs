using System.Drawing;
using System.Windows.Forms;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Features;

/// <summary>Ícone na bandeja do sistema (área de notificação).</summary>
public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _overlay;
    DateTime _lastTip;

    public TrayIcon()
    {
        // Lê o .ico embutido no tamanho certo para a bandeja (o ícone extraído do .exe vem em 32px e fica borrado).
        Icon? ico = null;
        try
        {
            using var s = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/pulso.ico")).Stream;
            ico = new Icon(s, SystemInformation.SmallIconSize);
        }
        catch { }
        if (ico == null) try { ico = Icon.ExtractAssociatedIcon(Paths.ExePath); } catch { }
        _icon = new NotifyIcon { Icon = ico ?? SystemIcons.Application, Text = "Pulso", Visible = true };

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(Loc.T("Abrir o Pulso", "Open Pulso"), null, (_, _) => App.Current.ShowMain()).Font = new Font(menu.Font, System.Drawing.FontStyle.Bold);
        _overlay = new ToolStripMenuItem(Loc.T("Overlay no jogo", "In-game overlay")) { CheckOnClick = true, Checked = Settings.Current.Overlay.Enabled };
        _overlay.Click += (_, _) => OverlayController.Instance.SetEnabled(_overlay.Checked);
        menu.Items.Add(_overlay);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("Sair", "Exit"), null, (_, _) => App.Current.Exit());
        menu.Opening += (_, _) => _overlay.Checked = Settings.Current.Overlay.Enabled;
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) App.Current.ShowMain(); };

        SensorHub.Instance.Updated += OnUpdate;
    }

    void OnUpdate(Snapshot s)
    {
        if (DateTime.Now - _lastTip < TimeSpan.FromSeconds(5)) return;
        _lastTip = DateTime.Now;
        string text = "Pulso";
        if (s.Cpu.Temp != null || s.Cpu.Load != null)
            text += $"\nCPU {Fmt.Value(s.Cpu.Load, Unit.Percent)} · {Fmt.Value(s.Cpu.Temp, Unit.Temp)}";
        if (s.Gpu is GpuState g)
            text += $"\nGPU {Fmt.Value(g.Load, Unit.Percent)} · {Fmt.Value(g.Temp, Unit.Temp)}";
        if (text.Length > 127) text = text[..127];
        App.Current?.Dispatcher.BeginInvoke(() => { try { _icon.Text = text; } catch { } });
    }

    public void Notify(string title, string text, bool warning = true)
    {
        try { _icon.ShowBalloonTip(6000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info); }
        catch { }
    }

    public void Dispose()
    {
        SensorHub.Instance.Updated -= OnUpdate;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
