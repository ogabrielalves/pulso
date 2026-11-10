using System.Runtime.InteropServices;
using Pulso.Core;
using Pulso.Features;

namespace Pulso.Platform.Linux;

/// <summary>Chamadas mínimas ao X11 (libX11/libXext) para o overlay: janela que ignora o mouse e atalho global.</summary>
static class X11
{
    const string LibX11 = "libX11.so.6", LibXext = "libXext.so.6";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int XErrorHandler(nint display, nint errorEvent);

    [DllImport(LibX11)] public static extern nint XOpenDisplay(nint name);
    [DllImport(LibX11)] public static extern int XCloseDisplay(nint display);
    [DllImport(LibX11)] public static extern nint XDefaultRootWindow(nint display);
    [DllImport(LibX11)] public static extern byte XKeysymToKeycode(nint display, nuint keysym);
    [DllImport(LibX11)] public static extern int XGrabKey(nint display, int keycode, uint modifiers, nint window, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(LibX11)] public static extern int XUngrabKey(nint display, int keycode, uint modifiers, nint window);
    [DllImport(LibX11)] public static extern int XPending(nint display);
    [DllImport(LibX11)] public static extern int XNextEvent(nint display, byte[] ev);
    [DllImport(LibX11)] public static extern int XSync(nint display, int discard);
    [DllImport(LibX11)] public static extern int XFlush(nint display);
    [DllImport(LibX11)] public static extern nint XSetErrorHandler(XErrorHandler handler);
    [DllImport(LibX11, EntryPoint = "XSetErrorHandler")] public static extern nint RestoreErrorHandler(nint handler);
    [DllImport(LibXext)] static extern void XShapeCombineRectangles(nint display, nint dest, int destKind, int xOff, int yOff, nint rects, int n, int op, int ordering);

    public static bool Available
    {
        get
        {
            try { var d = XOpenDisplay(0); if (d == 0) return false; XCloseDisplay(d); return true; }
            catch { return false; }
        }
    }

    /// <summary>Região de entrada vazia: todos os cliques atravessam a janela.</summary>
    public static void MakeClickThrough(nint xid)
    {
        if (xid == 0) return;
        try
        {
            var d = XOpenDisplay(0);
            if (d == 0) return;
            XShapeCombineRectangles(d, xid, 2 /*ShapeInput*/, 0, 0, 0, 0, 0 /*ShapeSet*/, 0);
            XFlush(d);
            XCloseDisplay(d);
        }
        catch (Exception ex) { Paths.LogError("X11.ClickThrough", ex); }
    }

    /// <summary>Código virtual do Windows (usado nas configurações) → keysym do X11.</summary>
    public static nuint? Keysym(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z') return vk + 32;
        if (vk >= '0' && vk <= '9') return vk;
        if (vk >= 0x70 && vk <= 0x87) return 0xFFBE + (vk - 0x70);
        if (vk >= 0x60 && vk <= 0x69) return 0xFFB0 + (vk - 0x60);
        return vk switch
        {
            0x20 => 0x20, 0x09 => 0xFF09, 0x0D => 0xFF0D, 0x1B => 0xFF1B, 0x08 => 0xFF08,
            0x2D => 0xFF63, 0x2E => 0xFFFF, 0x24 => 0xFF50, 0x23 => 0xFF57, 0x21 => 0xFF55, 0x22 => 0xFF56,
            0x25 => 0xFF51, 0x26 => 0xFF52, 0x27 => 0xFF53, 0x28 => 0xFF54, 0x13 => 0xFF13, 0x2C => 0xFF61,
            0xC0 => 0x60, 0xBD => 0x2D, 0xBB => 0x3D, 0xBC => 0x2C, 0xBE => 0x2E,
            _ => null,
        };
    }
}

/// <summary>Atalho global no X11 (XGrabKey) numa thread própria, com a mesma interface do atalho do Windows.</summary>
sealed class X11Hotkey : IHotkey
{
    readonly Action _onHotkey;
    readonly Thread _thread;
    readonly object _lock = new();
    readonly ManualResetEventSlim _done = new();
    volatile bool _stop, _pending;
    uint _mods, _vk;
    bool _lastOk, _grabFailed;
    static X11.XErrorHandler? _handler;

    public X11Hotkey(Action onHotkey)
    {
        _onHotkey = onHotkey;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Pulso.Hotkey" };
        _thread.Start();
    }

    public bool Register(uint winMods, uint vk)
    {
        lock (_lock)
        {
            _mods = winMods; _vk = vk; _done.Reset(); _pending = true;
            _done.Wait(1500);
            return _lastOk;
        }
    }

    public void Unregister() => Register(0, 0);

    public void Dispose() => _stop = true;

    static uint ToX(uint m) => ((m & 4) != 0 ? 1u : 0) | ((m & 2) != 0 ? 4u : 0) | ((m & 1) != 0 ? 8u : 0) | ((m & 8) != 0 ? 0x40u : 0);

    void Loop()
    {
        nint d;
        try { d = X11.XOpenDisplay(0); } catch { _lastOk = false; _done.Set(); return; }
        if (d == 0) { _lastOk = false; _done.Set(); return; }
        nint root = X11.XDefaultRootWindow(d);
        int code = 0; uint mods = 0;
        // Combinações com NumLock (Mod2) e CapsLock (Lock) também disparam o atalho.
        uint[] locks = { 0, 2, 0x10, 0x12 };
        var ev = new byte[192];
        try
        {
            while (!_stop)
            {
                if (_pending)
                {
                    _pending = false;
                    if (code != 0) { foreach (var l in locks) X11.XUngrabKey(d, code, mods | l, root); code = 0; }
                    _lastOk = false;
                    if (_vk != 0 && X11.Keysym(_vk) is nuint ks && X11.XKeysymToKeycode(d, ks) is byte kc and not 0)
                    {
                        _grabFailed = false;
                        _handler ??= (_, _) => { _grabFailed = true; return 0; };
                        var prev = X11.XSetErrorHandler(_handler);
                        uint xm = ToX(_mods);
                        foreach (var l in locks) X11.XGrabKey(d, kc, xm | l, root, 0, 1, 1);
                        X11.XSync(d, 0);
                        X11.RestoreErrorHandler(prev); // devolve o tratador anterior (Avalonia)
                        _lastOk = !_grabFailed;
                        if (_lastOk) { code = kc; mods = xm; }
                        else foreach (var l in locks) X11.XUngrabKey(d, kc, xm | l, root);
                    }
                    _done.Set();
                }
                bool any = false;
                while (X11.XPending(d) > 0)
                {
                    X11.XNextEvent(d, ev);
                    any = true;
                    if (ev[0] == 2 /*KeyPress*/) _onHotkey();
                }
                if (!any) Thread.Sleep(40);
            }
        }
        catch (Exception ex) { Paths.LogError("X11Hotkey", ex); }
        finally { try { X11.XCloseDisplay(d); } catch { } }
    }
}
