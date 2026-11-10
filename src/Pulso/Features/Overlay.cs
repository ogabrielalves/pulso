using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
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

    /// <summary>FPS, frametime e 1% usam eventos de quadros do Windows; não existem no Linux.</summary>
    public static bool Available(string id) => OperatingSystem.IsWindows() || id is not ("fps" or "ft" or "low1");

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
        All.Where(a => Available(a.id) && Settings.Current.Overlay.Items.Contains(a.id)).Select(a => Row(a.id, s)).Where(r => r != null).Select(r => r!.Value);
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
        try { valueColor = Color.Parse(o.TextColor); } catch { valueColor = Colors.White; }
        var keyBrush = new SolidColorBrush(Color.FromRgb(0xC5, 0xF3, 0x6A));
        var valBrush = new SolidColorBrush(valueColor);
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

/// <summary>Janela transparente sempre no topo que ignora o mouse e nunca recebe foco (Windows).</summary>
public sealed class OverlayWindow : Window
{
    public readonly OverlayPanel Panel = new();

    public OverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = Panel;
        Title = "Pulso Overlay";
        Opened += (_, _) =>
        {
            var h = Handle;
            if (h == 0) return;
            if (!OperatingSystem.IsWindows()) { Platform.Linux.X11.MakeClickThrough(h); return; }
            nint ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST);
        };
    }

    public nint Handle => TryGetPlatformHandle()?.Handle ?? 0;

    /// <summary>Encosta no canto escolhido do monitor onde está o jogo.</summary>
    public void Place(nint gameWindow)
    {
        var o = Settings.Current.Overlay;
        if (!OperatingSystem.IsWindows()) { PlaceLinux(o); return; }
        nint mon = Native.MonitorFromWindow(gameWindow != 0 ? gameWindow : Handle, 2 /*NEAREST*/);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return;
        double scale = RenderScaling;
        int m = (int)Math.Round(16 * scale);
        int w = (int)Math.Round(Bounds.Width * scale), h = (int)Math.Round(Bounds.Height * scale);
        int x = o.Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? mi.rcMonitor.Left + m : mi.rcMonitor.Right - w - m;
        int y = o.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? mi.rcMonitor.Top + m : mi.rcMonitor.Bottom - h - m;
        Position = new PixelPoint(x, y);
    }

    void PlaceLinux(OverlaySettings o)
    {
        var scr = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (scr == null) return;
        var b = scr.Bounds;
        double scale = RenderScaling;
        int m = (int)Math.Round(16 * scale);
        int w = (int)Math.Round(Bounds.Width * scale), h = (int)Math.Round(Bounds.Height * scale);
        int x = o.Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? b.X + m : b.Right - w - m;
        int y = o.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? b.Y + m : b.Bottom - h - m;
        Position = new PixelPoint(x, y);
    }

    public void KeepOnTop()
    {
        if (!OperatingSystem.IsWindows()) return; // no X11 o Topmost da janela já cuida disso
        var h = Handle;
        if (h != 0) Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_NOSENDCHANGING);
    }
}

/// <summary>
/// Sem injetar nada no jogo, o overlay só aparece por cima da tela cheia quando o Windows compõe o jogo
/// (otimizações de tela cheia). Aqui detectamos quando elas estão desligadas para o jogo ou no sistema todo.
/// </summary>
public static class FullscreenOptimizations
{
    const string LayersPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    const string GameConfigPath = @"System\GameConfigStore";
    const string Flag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>Verdadeiro se a tela cheia exclusiva deste jogo vai esconder janelas por cima.</summary>
    public static bool Blocked(string exePath)
    {
        try
        {
            foreach (var root in new[] { Registry.CurrentUser, Hklm })
            {
                using var k = root.OpenSubKey(LayersPath);
                if (k?.GetValue(exePath) is string v && v.Contains(Flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return GlobalOff();
        }
        catch { return false; }
    }

    /// <summary>Religa as otimizações de tela cheia (vale na próxima vez que o jogo abrir).</summary>
    public static bool Fix(string exePath)
    {
        bool ok = true;
        foreach (var root in new[] { Registry.CurrentUser, Hklm })
        {
            try
            {
                using var k = root.OpenSubKey(LayersPath, true);
                if (k?.GetValue(exePath) is not string v) continue;
                var rest = v.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !t.Equals(Flag, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rest.All(t => t is "~" or "$")) k.DeleteValue(exePath, false);
                else k.SetValue(exePath, string.Join(" ", rest));
            }
            catch (Exception ex) { Paths.LogError("Overlay.FixFso", ex); ok = false; }
        }
        try
        {
            // volta ao padrão do Windows (o próprio Windows decide, com otimizações ligadas)
            if (GlobalOff())
            {
                using var g = Registry.CurrentUser.OpenSubKey(GameConfigPath, true);
                g?.SetValue("GameDVR_HonorUserFSEBehaviorMode", 0, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex) { Paths.LogError("Overlay.FixFso", ex); ok = false; }
        return ok;
    }

    /// <summary>Ajuste "desativar otimizações de tela cheia" aplicado no sistema todo (comum em guias de otimização).</summary>
    static bool GlobalOff()
    {
        using var g = Registry.CurrentUser.OpenSubKey(GameConfigPath);
        return g?.GetValue("GameDVR_HonorUserFSEBehaviorMode") is int honor && honor == 1
            && g.GetValue("GameDVR_FSEBehaviorMode") is int mode && mode == 2;
    }
}

/// <summary>Liga/desliga o overlay, detecta o jogo em primeiro plano e trata o atalho global.</summary>
public sealed class OverlayController
{
    public static OverlayController Instance { get; } = new();

    OverlayWindow? _win;
    DispatcherTimer? _timer, _zTimer;
    IDisposable? _fps, _demand;
    IHotkey? _hotkey;
    nint _fgHook, _restoreHook;
    Native.WinEventProc? _fgProc; // referência mantida para o GC não coletar o callback
    bool _userHidden;
    uint _checkedPid;
    string? _checkedPath;
    bool _checkedBlocked;
    const int HotkeyId = 0x5A51;

    public string? CurrentGame { get; private set; }
    public double? CurrentFps { get; private set; }
    /// <summary>Jogo atual com otimizações de tela cheia desligadas (o overlay não consegue ficar por cima).</summary>
    public string? BlockedGamePath { get; private set; }
    /// <summary>Jogo atual está em tela cheia exclusiva.</summary>
    public bool ExclusiveFullscreen { get; private set; }
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
        if (OperatingSystem.IsWindows()) _fps ??= FpsMonitor.Instance.Acquire();
        _demand ??= SensorHub.Instance.Demand();
        _win ??= new OverlayWindow();
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(Math.Clamp(Settings.Current.Overlay.UpdateMs, 250, 5000)) };
            _timer.Tick += (_, _) => Update();
            _timer.Start();
        }
        // Jogos em tela cheia se colocam no topo ao ganhar foco; reafirmamos nossa posição com frequência (custo desprezível).
        if (_zTimer == null)
        {
            _zTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
            _zTimer.Tick += (_, _) => { if (_win is { IsVisible: true }) _win.KeepOnTop(); };
            _zTimer.Start();
        }
        // Ao trocar de janela (alt+tab, jogo voltando do minimizado) reage na hora, sem esperar o próximo ciclo.
        if (_fgHook == 0 && OperatingSystem.IsWindows())
        {
            _fgProc = (_, _, _, _, _, _, _) => Update();
            _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0, _fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            _restoreHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZEEND, Native.EVENT_SYSTEM_MINIMIZEEND, 0, _fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        }
        Update();
    }

    void Deactivate()
    {
        if (_fgHook != 0) { Native.UnhookWinEvent(_fgHook); _fgHook = 0; }
        if (_restoreHook != 0) { Native.UnhookWinEvent(_restoreHook); _restoreHook = 0; }
        _zTimer?.Stop(); _zTimer = null;
        _timer?.Stop(); _timer = null;
        _win?.Close(); _win = null;
        _fps?.Dispose(); _fps = null;
        _demand?.Dispose(); _demand = null;
        CurrentGame = null;
        BlockedGamePath = null;
        ExclusiveFullscreen = false;
    }

    public void Stop()
    {
        Deactivate();
        _hotkey?.Dispose();
        _hotkey = null;
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
        bool win = OperatingSystem.IsWindows();
        var game = win ? FpsMonitor.Instance.Foreground() : null;
        nint fg = win ? Native.GetForegroundWindow() : 0;
        bool isGame = game is { } gv && gv.fps >= 5 && CoversMonitor(fg);
        CurrentGame = isGame ? game!.Value.name : null;
        CurrentFps = isGame ? game!.Value.fps : null;
        // Mantém o último diagnóstico ao sair do jogo, para o aviso continuar visível na tela do overlay.
        if (isGame)
        {
            ExclusiveFullscreen = Native.SHQueryUserNotificationState(out int qs) == 0 && qs == 3;
            BlockedGamePath = CheckBlocked((uint)game!.Value.pid);
        }
        StateChanged?.Invoke();

        bool show = !_userHidden && (!o.OnlyInGames || isGame || !win); // sem detecção de jogo no Linux: fica sempre visível
        if (!show)
        {
            if (_win.IsVisible) _win.Hide();
            return;
        }
        if (SensorHub.Instance.Last is Snapshot s) _win.Panel.Update(OverlayItems.Rows(s));
        if (!_win.IsVisible) _win.Show();
        _win.UpdateLayout();
        _win.Place(fg);
        _win.KeepOnTop();
    }

    /// <summary>Caminho do jogo se ele estiver com otimizações de tela cheia desligadas (verificado uma vez por processo).</summary>
    string? CheckBlocked(uint pid)
    {
        if (pid != _checkedPid)
        {
            _checkedPid = pid;
            _checkedPath = Native.ProcessPath(pid);
            _checkedBlocked = _checkedPath != null && FullscreenOptimizations.Blocked(_checkedPath);
        }
        return _checkedBlocked ? _checkedPath : null;
    }

    /// <summary>Religa as otimizações de tela cheia do jogo atual; o jogo precisa ser reiniciado.</summary>
    public bool FixBlocked()
    {
        if (BlockedGamePath is not string path) return false;
        bool ok = FullscreenOptimizations.Fix(path);
        _checkedBlocked = FullscreenOptimizations.Blocked(path);
        BlockedGamePath = _checkedBlocked ? path : null;
        StateChanged?.Invoke();
        return ok && !_checkedBlocked;
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
                    if (Enum.TryParse<Key>(raw, true, out var k) && VirtualKey(k) is uint v) vk = v;
                    else if (raw.Length == 1) vk = char.ToUpperInvariant(raw[0]);
                    break;
            }
        }
        return vk != 0;
    }

    /// <summary>Tecla do Avalonia → código virtual do Windows (os valores usados em atalhos).</summary>
    public static uint? VirtualKey(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return (uint)('A' + (k - Key.A));
        if (k >= Key.D0 && k <= Key.D9) return (uint)('0' + (k - Key.D0));
        if (k >= Key.F1 && k <= Key.F24) return (uint)(0x70 + (k - Key.F1));
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return (uint)(0x60 + (k - Key.NumPad0));
        return k switch
        {
            Key.Space => 0x20, Key.Tab => 0x09, Key.Return => 0x0D, Key.Escape => 0x1B, Key.Back => 0x08,
            Key.Insert => 0x2D, Key.Delete => 0x2E, Key.Home => 0x24, Key.End => 0x23, Key.PageUp => 0x21, Key.PageDown => 0x22,
            Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28, Key.Pause => 0x13, Key.PrintScreen => 0x2C,
            Key.OemTilde => 0xC0, Key.OemMinus => 0xBD, Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemPeriod => 0xBE,
            _ => null,
        };
    }

    public bool RegisterHotkey()
    {
        try
        {
            _hotkey ??= OperatingSystem.IsWindows()
                ? new HotkeyThread(HotkeyId, () => Dispatcher.UIThread.Post(OnHotkey))
                : new Platform.Linux.X11Hotkey(() => Dispatcher.UIThread.Post(OnHotkey));
            if (!TryParseHotkey(Settings.Current.Overlay.Hotkey, out uint mods, out uint vk)) { _hotkey.Unregister(); return false; }
            return _hotkey.Register(mods | 0x4000 /*NOREPEAT*/, vk);
        }
        catch (Exception ex) { Paths.LogError("Overlay.Hotkey", ex); return false; }
    }

    void OnHotkey()
    {
        if (!Enabled) { SetEnabled(true); return; }
        _userHidden = !_userHidden;
        Update();
    }
}

/// <summary>
/// Atalho global do Windows: uma thread própria registra o atalho (RegisterHotKey sem janela) e espera
/// WM_HOTKEY na fila de mensagens dela. Não depende de nenhuma janela da interface.
/// </summary>
public interface IHotkey : IDisposable
{
    bool Register(uint mods, uint vk);
    void Unregister();
}

public sealed class HotkeyThread : IHotkey
{
    const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012, WM_APP_REGISTER = 0x8001, WM_APP_UNREGISTER = 0x8002;
    readonly int _id;
    readonly Action _onHotkey;
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly object _lock = new();
    uint _tid, _mods, _vk;
    bool _lastOk;
    readonly ManualResetEventSlim _done = new();

    public HotkeyThread(int id, Action onHotkey)
    {
        _id = id; _onHotkey = onHotkey;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Pulso.Hotkey" };
        _thread.Start();
        _ready.Wait(2000);
    }

    void Loop()
    {
        _tid = Native.GetCurrentThreadId();
        Native.PeekMessage(out _, 0, 0x0400, 0x0400, 0); // força a criação da fila de mensagens da thread
        _ready.Set();
        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_HOTKEY && (int)msg.wParam == _id) _onHotkey();
            else if (msg.message == WM_APP_REGISTER)
            {
                Native.UnregisterHotKey(0, _id);
                _lastOk = Native.RegisterHotKey(0, _id, _mods, _vk);
                _done.Set();
            }
            else if (msg.message == WM_APP_UNREGISTER) { Native.UnregisterHotKey(0, _id); _done.Set(); }
        }
        Native.UnregisterHotKey(0, _id);
    }

    public bool Register(uint mods, uint vk)
    {
        lock (_lock)
        {
            _mods = mods; _vk = vk; _done.Reset();
            if (!Native.PostThreadMessage(_tid, WM_APP_REGISTER, 0, 0)) return false;
            _done.Wait(1000);
            return _lastOk;
        }
    }

    public void Unregister()
    {
        lock (_lock)
        {
            _done.Reset();
            if (Native.PostThreadMessage(_tid, WM_APP_UNREGISTER, 0, 0)) _done.Wait(1000);
        }
    }

    public void Dispose() => Native.PostThreadMessage(_tid, WM_QUIT, 0, 0);
}
