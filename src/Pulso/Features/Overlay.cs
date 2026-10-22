using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Features;

/// <summary>Itens disponíveis no overlay e como formatá-los.</summary>
public static class OverlayItems
{
    public static readonly (string id, string pt, string en)[] All =
    {
        ("fps", "FPS", "FPS"), ("cpu", "CPU", "CPU"), ("gpu", "GPU", "GPU"), ("ram", "RAM", "RAM"),
        ("temp", "Temperaturas", "Temperatures"), ("ft", "Frametime", "Frametime"), ("low1", "1% mínimo", "1% low"),
        ("vram", "VRAM", "VRAM"), ("clk", "Clock", "Clock"),
    };

    public static (string key, string value)? Row(string id, Snapshot s)
    {
        var g = s.Gpu;
        string Temp(double? v) => v is double x ? Fmt.N(Fmt.ToDisplayTemp(x)) + "°" : "-";
        return id switch
        {
            "fps" => ("FPS", s.Fps is double f ? Fmt.N(f) : "-"),
            "ft" => ("FT", s.FrameTimeMs is double ft ? Fmt.N(ft, 1) + " ms" : "-"),
            "low1" => ("1%", s.Fps1Low is double l ? Fmt.N(l) : "-"),
            "cpu" => ("CPU", s.Cpu.Load is double c ? Fmt.N(c) + "%" : "-"),
            "gpu" => ("GPU", g?.Load is double gl ? Fmt.N(gl) + "%" : "-"),
            "ram" => ("RAM", s.Mem.UsedGB is double r ? Fmt.N(r, 1) + " GB" : "-"),
            "temp" => ("TEMP", Temp(s.Cpu.Temp) + " · " + Temp(g?.Temp)),
            "vram" => ("VRAM", g?.VramUsedMB is double v ? Fmt.N(v / 1024, 1) + " GB" : "-"),
            "clk" => ("CLK", s.Cpu.ClockAvg is double ck ? Fmt.N(ck / 1000, 2) + " GHz" : "-"),
            _ => null,
        };
    }

    public static IEnumerable<(string key, string value)> Rows(Snapshot s) =>
        All.Where(a => Settings.Current.Overlay.Items.Contains(a.id)).Select(a => Row(a.id, s)).Where(r => r != null).Select(r => r!.Value);
}

/// <summary>Bloco visual do overlay (usado na janela real e na pré-visualização).</summary>
public sealed class OverlayPanel : Border
{
    readonly Grid _grid = new();

    public OverlayPanel()
    {
        Child = _grid;
        CornerRadius = new CornerRadius(8);
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ApplyStyle();
    }

    public void ApplyStyle()
    {
        var o = Settings.Current.Overlay;
        Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(o.BackgroundOpacity, 0, 1) * 255), 10, 12, 11));
        Padding = new Thickness(o.FontSize * 0.9, o.FontSize * 0.65, o.FontSize * 0.9, o.FontSize * 0.65);
    }

    public void Update(IEnumerable<(string key, string value)> rows)
    {
        var o = Settings.Current.Overlay;
        var list = rows.ToList();
        Color valueColor;
        try { valueColor = (Color)ColorConverter.ConvertFromString(o.TextColor); } catch { valueColor = Colors.White; }
        var keyBrush = new SolidColorBrush(Color.FromRgb(0xC5, 0xF3, 0x6A));
        var valBrush = new SolidColorBrush(valueColor);
        keyBrush.Freeze(); valBrush.Freeze();
        while (_grid.RowDefinitions.Count < list.Count) _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (_grid.RowDefinitions.Count > list.Count) _grid.RowDefinitions.RemoveAt(_grid.RowDefinitions.Count - 1);
        while (_grid.Children.Count < list.Count * 2)
        {
            var t = new TextBlock { FontFamily = Ui.MonoFont };
            _grid.Children.Add(t);
        }
        while (_grid.Children.Count > list.Count * 2) _grid.Children.RemoveAt(_grid.Children.Count - 1);
        for (int i = 0; i < list.Count; i++)
        {
            var k = (TextBlock)_grid.Children[i * 2];
            var v = (TextBlock)_grid.Children[i * 2 + 1];
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            k.Text = list[i].key; v.Text = list[i].value;
            k.FontSize = v.FontSize = o.FontSize;
            k.FontWeight = FontWeights.SemiBold;
            k.Foreground = keyBrush; v.Foreground = valBrush;
            v.TextAlignment = TextAlignment.Right;
            v.Margin = new Thickness(o.FontSize * 1.4, 0, 0, 0);
            v.MinWidth = o.FontSize * 4;
        }
    }
}

/// <summary>Janela transparente sempre no topo que ignora o mouse e nunca recebe foco.</summary>
public sealed class OverlayWindow : Window
{
    public readonly OverlayPanel Panel = new();

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = Panel;
        Title = "Pulso Overlay";
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            nint ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST);
        };
    }

    public nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Encosta no canto escolhido do monitor onde está o jogo.</summary>
    public void Place(nint gameWindow)
    {
        var o = Settings.Current.Overlay;
        nint mon = Native.MonitorFromWindow(gameWindow != 0 ? gameWindow : Handle, 2 /*NEAREST*/);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        double l = mi.rcMonitor.Left / sx, t = mi.rcMonitor.Top / sy, r = mi.rcMonitor.Right / sx, b = mi.rcMonitor.Bottom / sy;
        const double m = 16;
        double w = ActualWidth, h = ActualHeight;
        Left = o.Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? l + m : r - w - m;
        Top = o.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? t + m : b - h - m;
    }

    public void KeepOnTop()
    {
        var h = Handle;
        if (h != 0) Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}

/// <summary>Liga/desliga o overlay, detecta o jogo em primeiro plano e trata o atalho global.</summary>
public sealed class OverlayController
{
    public static OverlayController Instance { get; } = new();

    OverlayWindow? _win;
    DispatcherTimer? _timer;
    IDisposable? _fps, _demand;
    HwndSource? _hotkeyWnd;
    bool _userHidden;
    int _tick;
    const int HotkeyId = 0x5A51;

    public string? CurrentGame { get; private set; }
    public double? CurrentFps { get; private set; }
    public event Action? StateChanged;

    public bool Enabled => Settings.Current.Overlay.Enabled;

    public void Start()
    {
        RegisterHotkey();
        if (Enabled) Activate();
    }

    public void SetEnabled(bool on)
    {
        Settings.Current.Overlay.Enabled = on;
        Settings.Save();
        if (on) Activate(); else Deactivate();
        StateChanged?.Invoke();
    }

    void Activate()
    {
        _userHidden = false;
        _fps ??= FpsMonitor.Instance.Acquire();
        _demand ??= SensorHub.Instance.Demand();
        _win ??= new OverlayWindow();
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.Current.Overlay.UpdateMs, 250, 5000)) };
            _timer.Tick += (_, _) => Update();
            _timer.Start();
        }
        Update();
    }

    void Deactivate()
    {
        _timer?.Stop(); _timer = null;
        _win?.Close(); _win = null;
        _fps?.Dispose(); _fps = null;
        _demand?.Dispose(); _demand = null;
        CurrentGame = null;
    }

    public void Stop()
    {
        Deactivate();
        if (_hotkeyWnd != null)
        {
            Native.UnregisterHotKey(_hotkeyWnd.Handle, HotkeyId);
            _hotkeyWnd.Dispose();
            _hotkeyWnd = null;
        }
    }

    /// <summary>Aplica mudanças de aparência imediatamente.</summary>
    public void Refresh()
    {
        _win?.Panel.ApplyStyle();
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.Current.Overlay.UpdateMs, 250, 5000));
        Update();
    }

    void Update()
    {
        if (_win == null) return;
        var o = Settings.Current.Overlay;
        var game = FpsMonitor.Instance.Foreground();
        nint fg = Native.GetForegroundWindow();
        bool isGame = game is { } gv && gv.fps >= 5 && CoversMonitor(fg);
        CurrentGame = isGame ? game!.Value.name : null;
        CurrentFps = isGame ? game!.Value.fps : null;
        StateChanged?.Invoke();

        bool show = !_userHidden && (!o.OnlyInGames || isGame);
        if (!show)
        {
            if (_win.IsVisible) _win.Hide();
            return;
        }
        if (SensorHub.Instance.Last is Snapshot s) _win.Panel.Update(OverlayItems.Rows(s));
        if (!_win.IsVisible) _win.Show();
        _win.UpdateLayout();
        _win.Place(fg);
        if (++_tick % 2 == 0) _win.KeepOnTop();
    }

    /// <summary>Janela ocupa (quase) o monitor inteiro: tela cheia ou janela sem bordas.</summary>
    static bool CoversMonitor(nint hwnd)
    {
        if (hwnd == 0 || !Native.GetWindowRect(hwnd, out var r)) return false;
        string cls = Native.ClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        nint mon = Native.MonitorFromWindow(hwnd, 2);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        int mw = mi.rcMonitor.Right - mi.rcMonitor.Left, mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        int ww = Math.Min(r.Right, mi.rcMonitor.Right) - Math.Max(r.Left, mi.rcMonitor.Left);
        int wh = Math.Min(r.Bottom, mi.rcMonitor.Bottom) - Math.Max(r.Top, mi.rcMonitor.Top);
        // Jogos em janela grande também contam (≥ 85% da tela).
        return ww >= mw * 0.85 && wh >= mh * 0.85;
    }

    // ------------------------------------------------------------------ atalho global

    public static bool TryParseHotkey(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 0x2; break;
                case "shift": mods |= 0x4; break;
                case "alt": mods |= 0x1; break;
                case "win": mods |= 0x8; break;
                default:
                    if (Enum.TryParse<Key>(raw, true, out var k)) vk = (uint)KeyInterop.VirtualKeyFromKey(k);
                    else if (raw.Length == 1) vk = char.ToUpperInvariant(raw[0]);
                    break;
            }
        }
        return vk != 0;
    }

    public bool RegisterHotkey()
    {
        try
        {
            if (_hotkeyWnd == null)
            {
                var p = new HwndSourceParameters("PulsoHotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
                _hotkeyWnd = new HwndSource(p);
                _hotkeyWnd.AddHook(WndProc);
            }
            Native.UnregisterHotKey(_hotkeyWnd.Handle, HotkeyId);
            if (!TryParseHotkey(Settings.Current.Overlay.Hotkey, out uint mods, out uint vk)) return false;
            return Native.RegisterHotKey(_hotkeyWnd.Handle, HotkeyId, mods | 0x4000 /*NOREPEAT*/, vk);
        }
        catch (Exception ex) { Paths.LogError("Overlay.Hotkey", ex); return false; }
    }

    nint WndProc(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x0312 && w == HotkeyId)
        {
            handled = true;
            if (!Enabled) { SetEnabled(true); return 0; }
            _userHidden = !_userHidden;
            Update();
        }
        return 0;
    }
}
