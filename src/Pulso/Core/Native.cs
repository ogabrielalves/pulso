using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Pulso.Core;

internal static partial class Native
{
    public static bool IsAdmin { get; } = CheckAdmin();

    static bool CheckAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    // ---------- Janela ----------
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x8;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200, SWP_NOSENDCHANGING = 0x400;

    // ---------- Eventos de janela (troca de primeiro plano) ----------
    public const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_SYSTEM_MINIMIZEEND = 0x17, WINEVENT_OUTOFCONTEXT = 0;

    public delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmod, WinEventProc proc, uint pid, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    /// <summary>3 = QUNS_RUNNING_D3D_FULL_SCREEN (jogo em tela cheia exclusiva).</summary>
    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint h, uint flags, char* buf, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    /// <summary>Caminho do executável (funciona também em processos protegidos por anti-cheat).</summary>
    public static unsafe string? ProcessPath(uint pid)
    {
        nint h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            char* buf = stackalloc char[1024];
            int size = 1024;
            return QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(nint hWnd, [Out] char[] buf, int max);

    public static string ClassName(nint hwnd)
    {
        var buf = new char[256];
        int n = GetClassName(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    /// <summary>Barra de título escura/clara nativa do Windows 10/11.</summary>
    public static void SetDarkTitleBar(nint hwnd, bool dark)
    {
        int v = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref v, 4);
    }

    // ---------- Memória ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public int cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, int size);

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyWorkingSet(nint hProcess);

    /// <summary>Devolve ao Windows a memória que o app não está usando (chamado ao ir para a bandeja).</summary>
    public static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch { }
    }

    [LibraryImport("kernel32.dll")]
    public static partial ulong GetTickCount64();

    // ---------- Processadores lógicos (núcleos P/E) ----------
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint length);

    /// <summary>Retorna a classe de eficiência de cada núcleo físico (maior = núcleo de performance).</summary>
    public static List<(byte efficiencyClass, int threads)> GetCores()
    {
        var list = new List<(byte, int)>();
        uint len = 0;
        GetLogicalProcessorInformationEx(0 /*RelationProcessorCore*/, 0, ref len);
        if (len == 0) return list;
        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return list;
            long off = 0;
            while (off < len)
            {
                nint p = buf + (nint)off;
                int size = Marshal.ReadInt32(p, 4);
                byte flags = Marshal.ReadByte(p, 8);
                byte eff = Marshal.ReadByte(p, 9);
                ushort groupCount = (ushort)Marshal.ReadInt16(p, 30);
                int threads = 0;
                for (int g = 0; g < Math.Max(1, (int)groupCount); g++)
                {
                    ulong mask = (ulong)Marshal.ReadInt64(p, 32 + g * 16);
                    threads += BitOperations.PopCount(mask);
                }
                list.Add((eff, threads == 0 ? (flags == 1 ? 2 : 1) : threads));
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }
}

internal static class BitOperations
{
    public static int PopCount(ulong v) => System.Numerics.BitOperations.PopCount(v);
}
