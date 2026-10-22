using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>
/// "O que está pesando agora": uso por aplicativo (processos com o mesmo executável somados).
/// CPU, memória e disco vêm de uma única chamada NtQuerySystemInformation (barata, sem abrir cada processo);
/// GPU pelos contadores "GPU Engine" do Windows (os mesmos do Gerenciador de Tarefas);
/// rede pelos eventos Kernel-Network (ETW, só como administrador e só enquanto a tela está aberta).
/// </summary>
public sealed unsafe partial class ProcessMonitor : IDisposable
{
    public sealed class AppUsage
    {
        public string Image = "", Name = "", Path = "";
        public List<int> Pids = new();
        public double Cpu, Gpu;          // %
        public double Ram;               // bytes (conjunto de trabalho privado, igual ao Gerenciador de Tarefas)
        public double Disk, Net;         // bytes/s
        public int Count => Pids.Count;
    }

    sealed class Prev { public long Cpu, Io; }

    /// <summary>Instância única: o Painel e a tela de processos nunca estão visíveis ao mesmo tempo.</summary>
    public static ProcessMonitor Instance { get; } = new();

    readonly Dictionary<int, Prev> _prev = new();
    readonly Dictionary<string, (string name, string path)> _names = new(StringComparer.OrdinalIgnoreCase);
    long _lastTicks;
    nint _buf;
    int _bufSize = 1 << 20;
    readonly object _lock = new();

    public bool Gpu { get; set; }
    public bool Network { get; set; }
    public string? NetError { get; private set; }

    public List<AppUsage> Apps { get; private set; } = new();

    // ------------------------------------------------------------- coleta

    /// <summary>Lê tudo e recalcula as taxas desde a chamada anterior (thread de fundo).</summary>
    public void Sample()
    {
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            double secs = _lastTicks == 0 ? 0 : (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;
            var gpu = Gpu ? _gpu.Read() : null;
            var net = Network ? _net.Drain() : null;
            int cpus = Environment.ProcessorCount;

            var apps = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<int>();
            foreach (var p in Snapshot())
            {
                if (p.Pid == 0) continue; // ociosidade do sistema
                seen.Add(p.Pid);
                double cpu = 0, io = 0;
                if (_prev.TryGetValue(p.Pid, out var pr) && secs > 0)
                {
                    cpu = Math.Max(0, (p.Cpu - pr.Cpu) / 1e7 / secs / cpus * 100);
                    io = Math.Max(0, (p.Io - pr.Io) / secs);
                    pr.Cpu = p.Cpu; pr.Io = p.Io;
                }
                else _prev[p.Pid] = new Prev { Cpu = p.Cpu, Io = p.Io };

                if (!apps.TryGetValue(p.Image, out var a))
                {
                    var (name, path) = Describe(p.Image, p.Pid);
                    apps[p.Image] = a = new AppUsage { Image = p.Image, Name = name, Path = path };
                }
                a.Pids.Add(p.Pid);
                a.Cpu += cpu;
                a.Disk += io;
                a.Ram += p.PrivateWs;
                if (gpu != null && gpu.TryGetValue(p.Pid, out var g)) a.Gpu = Math.Max(a.Gpu, g);
                if (net != null && net.TryGetValue(p.Pid, out var nb) && secs > 0) a.Net += nb / secs;
            }
            foreach (var pid in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(pid);
            foreach (var a in apps.Values) a.Cpu = Math.Min(100, a.Cpu);
            Apps = apps.Values.ToList();
        }
    }

    record struct ProcInfo(int Pid, string Image, long Cpu, long Io, double PrivateWs);

    /// <summary>SYSTEM_PROCESS_INFORMATION (x64): todos os processos numa chamada só.</summary>
    List<ProcInfo> Snapshot()
    {
        var list = new List<ProcInfo>(400);
        while (true)
        {
            if (_buf == 0) _buf = Marshal.AllocHGlobal(_bufSize);
            int st = NtQuerySystemInformation(5, _buf, _bufSize, out int need);
            if (st == unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
            {
                Marshal.FreeHGlobal(_buf); _buf = 0;
                _bufSize = Math.Max(need, _bufSize) + (1 << 16);
                continue;
            }
            if (st != 0) return list;
            break;
        }
        byte* p = (byte*)_buf;
        while (true)
        {
            int next = *(int*)p;
            long priv = *(long*)(p + 8);
            long user = *(long*)(p + 40), kern = *(long*)(p + 48);
            ushort nameLen = *(ushort*)(p + 56);
            char* name = *(char**)(p + 64);
            int pid = (int)*(nint*)(p + 80);
            long rd = *(long*)(p + 232), wr = *(long*)(p + 240);
            string image = pid == 4 ? "System" : name != null && nameLen > 0 ? new string(name, 0, nameLen / 2) : "?";
            list.Add(new ProcInfo(pid, image, user + kern, rd + wr, priv));
            if (next == 0) break;
            p += next;
        }
        return list;
    }

    (string name, string path) Describe(string image, int pid)
    {
        if (_names.TryGetValue(image, out var d)) return d;
        string path = "", name = System.IO.Path.GetFileNameWithoutExtension(image);
        try
        {
            nint h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h != 0)
            {
                try
                {
                    char* buf = stackalloc char[1024];
                    int size = 1024;
                    if (QueryFullProcessImageName(h, 0, buf, ref size)) path = new string(buf, 0, size);
                }
                finally { CloseHandle(h); }
            }
            if (path != "" && File.Exists(path))
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(desc) && desc.Length < 60) name = desc;
            }
        }
        catch { }
        // só guarda quando conseguiu o caminho: processos protegidos podem dar certo depois (ex.: virar admin)
        if (path != "" || pid == 4) _names[image] = (name, path);
        return (name, path);
    }

    // ------------------------------------------------------------- GPU (PDH)

    readonly GpuCounters _gpu = new();

    sealed class GpuCounters : IDisposable
    {
        nint _query, _counter;
        bool _failed;

        /// <summary>Uso de GPU por PID: maior motor (3D, cópia, vídeo…) somando as instâncias do processo naquele motor.</summary>
        public Dictionary<int, double>? Read()
        {
            if (_failed) return null;
            if (_query == 0)
            {
                if (PdhOpenQuery(0, 0, out _query) != 0 || PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _counter) != 0)
                { _failed = true; return null; }
                PdhCollectQueryData(_query);
                return null;
            }
            if (PdhCollectQueryData(_query) != 0) return null;
            uint size = 0, count = 0;
            PdhGetFormattedCounterArray(_counter, 0x200 /* PDH_FMT_DOUBLE */, ref size, ref count, 0);
            if (size == 0) return null;
            nint buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, 0x200, ref size, ref count, buf) != 0) return null;
                var perEngine = new Dictionary<(int pid, string eng), double>();
                for (int i = 0; i < count; i++)
                {
                    // PDH_FMT_COUNTERVALUE_ITEM_W: szName (ptr), CStatus (uint) + pad, doubleValue
                    nint item = buf + i * 24;
                    string inst = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    if (v <= 0) continue;
                    // "pid_1234_luid_0x00000000_0x0000D1A2_phys_0_eng_3_engtype_3D"
                    int a = inst.IndexOf("pid_", StringComparison.Ordinal), b = inst.IndexOf("_luid", StringComparison.Ordinal), e = inst.IndexOf("_engtype", StringComparison.Ordinal);
                    if (a < 0 || b < 0 || e < 0 || !int.TryParse(inst.AsSpan(a + 4, b - a - 4), out int pid)) continue;
                    var key = (pid, inst[b..e]);
                    perEngine[key] = perEngine.GetValueOrDefault(key) + v;
                }
                var res = new Dictionary<int, double>();
                foreach (var ((pid, _), v) in perEngine) res[pid] = Math.Min(100, Math.Max(res.GetValueOrDefault(pid), v));
                return res;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public void Dispose()
        {
            if (_query != 0) PdhCloseQuery(_query);
            _query = 0;
        }
    }

    // ------------------------------------------------------------- rede (ETW)

    readonly NetTracer _net = new();

    sealed class NetTracer : IDisposable
    {
        static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
        TraceEventSession? _session;
        Dictionary<int, long> _bytes = new();
        readonly object _lock = new();
        public string? Error;

        public void Start()
        {
            if (_session != null) return;
            if (!Native.IsAdmin) { Error = Loc.T("Uso de rede por app precisa de administrador.", "Per-app network usage requires administrator."); return; }
            try
            {
                _session = new TraceEventSession("Pulso-Net-Session") { StopOnDispose = true };
                // TCP/UDP enviados e recebidos, IPv4 e IPv6
                var ids = new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 10, 11, 26, 27, 42, 43, 58, 59 } };
                _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, 0x10 | 0x20, ids);
                _session.Source.AllEvents += OnEvent;
                var s = _session;
                new Thread(() =>
                {
                    try { s.Source.Process(); }
                    catch (Exception ex) { Paths.LogError("ProcessMonitor.Net", ex); }
                }) { IsBackground = true, Name = "Pulso.Net", Priority = ThreadPriority.BelowNormal }.Start();
                Error = null;
            }
            catch (Exception ex)
            {
                Error = Loc.T("Não foi possível medir a rede por app: ", "Could not measure per-app network: ") + ex.Message;
                Paths.LogError("ProcessMonitor.NetStart", ex);
                try { _session?.Dispose(); } catch { }
                _session = null;
            }
        }

        void OnEvent(TraceEvent e)
        {
            if (e.ProviderGuid != KernelNetwork || e.EventDataLength < 8) return;
            // Todos esses eventos começam com PID (uint32) e size (uint32); o PID do cabeçalho nem sempre é o do app.
            byte* d = (byte*)e.DataStart;
            int pid = *(int*)d;
            uint size = *(uint*)(d + 4);
            lock (_lock) _bytes[pid] = _bytes.GetValueOrDefault(pid) + size;
        }

        public Dictionary<int, long>? Drain()
        {
            if (_session == null) return null;
            lock (_lock)
            {
                var r = _bytes;
                _bytes = new Dictionary<int, long>();
                return r;
            }
        }

        public void Dispose()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            lock (_lock) _bytes.Clear();
        }
    }

    /// <summary>Liga a captura de rede (ETW) enquanto a tela estiver aberta.</summary>
    public void StartNetwork()
    {
        Network = true;
        _net.Start();
        NetError = _net.Error;
    }

    public void StopNetwork()
    {
        Network = false;
        _net.Dispose();
    }

    // ------------------------------------------------------------- finalizar

    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "svchost.exe",
        "dwm.exe", "fontdrvhost.exe", "LsaIso.exe", "MemCompression", "Secure System", "sihost.exe", "ctfmon.exe", "audiodg.exe", "spoolsv.exe",
    };

    public static bool CanKill(AppUsage a) =>
        !Critical.Contains(a.Image) && !a.Pids.Contains(Environment.ProcessId);

    public static (int killed, int failed) Kill(AppUsage a)
    {
        int ok = 0, fail = 0;
        foreach (var pid in a.Pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
                ok++;
            }
            catch { fail++; }
        }
        return (ok, fail);
    }

    public void Dispose()
    {
        _net.Dispose();
        _gpu.Dispose();
        lock (_lock)
        {
            if (_buf != 0) Marshal.FreeHGlobal(_buf);
            _buf = 0;
        }
    }

    // ------------------------------------------------------------- interop

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int infoClass, nint buffer, int length, out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint h, uint flags, char* buf, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW")]
    private static partial int PdhOpenQuery(nint dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial int PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial int PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, ref uint itemCount, nint items);

    [LibraryImport("pdh.dll")]
    private static partial int PdhCloseQuery(nint query);
}
