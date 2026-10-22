using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Features;

/// <summary>
/// Mede FPS e frametime de qualquer jogo pelos eventos de apresentação de quadros do Windows (ETW),
/// a mesma técnica do PresentMon. Não injeta nada no jogo (seguro para anti-cheat).
/// A sessão só fica ativa enquanto alguém precisa (overlay, log, detector de gargalo).
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    public static FpsMonitor Instance { get; } = new();

    static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-3CC3B6EF2D43");
    static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    TraceEventSession? _session;
    Thread? _thread;
    int _demand;
    readonly object _lock = new();
    DateTime _sessionStart;
    readonly ConcurrentDictionary<int, Proc> _procs = new();

    public string? Error { get; private set; }
    public bool Running => _session != null;

    sealed class Proc
    {
        public readonly Queue<double>[] Times = { new(), new(), new() }; // 0 = DXGI, 1 = D3D9, 2 = kernel (Vulkan/OpenGL)
        public readonly Queue<double> Intervals = new();                  // para 1% low
        public double LastFrame = -1;
        public int LastSource = -1;
    }

    public void Start()
    {
        SensorHub.Instance.FpsSource = Read;
    }

    /// <summary>Liga a captura enquanto o objeto retornado não for descartado.</summary>
    public IDisposable Acquire()
    {
        lock (_lock)
        {
            if (++_demand == 1) StartSession();
        }
        return new Releaser(this);
    }

    sealed class Releaser(FpsMonitor m) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (m._lock)
            {
                if (--m._demand == 0) m.StopSession();
            }
        }
    }

    void StartSession()
    {
        if (!Native.IsAdmin)
        {
            Error = Loc.T("O contador de FPS precisa que o Pulso rode como administrador.", "The FPS counter requires running Pulso as administrator.");
            return;
        }
        try
        {
            _session = new TraceEventSession("Pulso-FPS-Session") { StopOnDispose = true };
            var only42 = new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 42 } };
            var only1 = new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 1 } };
            var kern = new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 168, 184, 252 } };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue, only42);
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue, only1);
            _session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Informational, 0x1 | 0x8000000, kern);
            _sessionStart = _session.Source.SessionStartTime;
            _session.Source.AllEvents += OnEvent;
            var s = _session;
            _thread = new Thread(() =>
            {
                try { s.Source.Process(); }
                catch (Exception ex) { Paths.LogError("FpsMonitor.Process", ex); }
            }) { IsBackground = true, Name = "Pulso.FPS", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
            Error = null;
        }
        catch (Exception ex)
        {
            Error = Loc.T("Não foi possível iniciar o contador de FPS: ", "Could not start the FPS counter: ") + ex.Message;
            Paths.LogError("FpsMonitor.Start", ex);
            try { _session?.Dispose(); } catch { }
            _session = null;
        }
    }

    void StopSession()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        _thread = null;
        _procs.Clear();
    }

    void OnEvent(TraceEvent e)
    {
        int src;
        if (e.ProviderGuid == DxgiProvider) { if ((int)e.ID != 42) return; src = 0; }
        else if (e.ProviderGuid == D3D9Provider) { if ((int)e.ID != 1) return; src = 1; }
        else if (e.ProviderGuid == DxgKrnlProvider) src = 2;
        else return;

        int pid = e.ProcessID;
        if (pid <= 4) return;
        double t = e.TimeStampRelativeMSec;
        var p = _procs.GetOrAdd(pid, _ => new Proc());
        lock (p)
        {
            var q = p.Times[src];
            q.Enqueue(t);
            while (q.Count > 0 && t - q.Peek() > 2000) q.Dequeue();
            // Intervalos só da fonte de maior prioridade com frames recentes (evita contar duas vezes).
            int best = p.Times[0].Count > 0 ? 0 : p.Times[1].Count > 0 ? 1 : 2;
            if (src == best)
            {
                if (p.LastSource == src && p.LastFrame >= 0)
                {
                    double dt = t - p.LastFrame;
                    if (dt > 0 && dt < 1000)
                    {
                        p.Intervals.Enqueue(dt);
                        while (p.Intervals.Count > 3000) p.Intervals.Dequeue();
                    }
                }
                p.LastFrame = t;
                p.LastSource = src;
            }
        }
    }

    double Now => (DateTime.Now - _sessionStart).TotalMilliseconds;

    /// <summary>FPS de um processo específico (null se não estiver desenhando quadros).</summary>
    public (double fps, double frametime, double? low1)? ForProcess(int pid)
    {
        if (!_procs.TryGetValue(pid, out var p)) return null;
        lock (p)
        {
            for (int src = 0; src < 3; src++)
            {
                var q = p.Times[src];
                if (q.Count < 2) continue;
                double last = q.Last();
                if (Now - last > 2500) continue;
                // quadros no último segundo (pela linha do tempo do próprio processo)
                int n = 0; double first = last;
                foreach (var t in q) if (last - t <= 1000) { n++; if (t < first) first = t; }
                if (n < 2) continue;
                double span = last - first;
                double fps = span > 0 ? (n - 1) * 1000.0 / span : n;
                double? low = null;
                if (p.Intervals.Count >= 100)
                {
                    var arr = p.Intervals.ToArray();
                    Array.Sort(arr);
                    double p99 = arr[(int)(arr.Length * 0.99) - 1];
                    low = p99 > 0 ? 1000.0 / p99 : null;
                }
                return (fps, fps > 0 ? 1000.0 / fps : 0, low);
            }
        }
        return null;
    }

    static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "explorer", "Pulso", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "ApplicationFrameHost", "csrss",
        "chrome", "msedge", "firefox", "opera", "brave", "Discord", "Spotify", "Teams", "ms-teams", "Code", "devenv", "obs64", "steamwebhelper", "EpicWebHelper",
        "LockApp", "SystemSettings", "Widgets", "PhoneExperienceHost", "msedgewebview2", "Lively", "wallpaper64", "wallpaper32", "nvcontainer", "NVIDIA Overlay",
    };

    public static bool IsIgnored(string name) => Ignore.Contains(name);

    /// <summary>Processo em primeiro plano que está renderizando quadros (provável jogo).</summary>
    public (int pid, string name, double fps, double frametime, double? low1)? Foreground()
    {
        try
        {
            nint fg = Native.GetForegroundWindow();
            if (fg == 0) return null;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            if (ForProcess((int)pid) is not var (fps, ft, low)) return null;
            string name;
            try { using var pr = Process.GetProcessById((int)pid); name = pr.ProcessName; } catch { name = pid.ToString(); }
            if (IsIgnored(name)) return null;
            return ((int)pid, name, fps, ft, low);
        }
        catch { return null; }
    }

    string? _cachedName;
    int _cachedPid;

    (double? fps, double? frametime, double? low1, string? process) Read()
    {
        if (!Running) return (null, null, null, null);
        var fg = Foreground();
        if (fg is var (pid, name, fps, ft, low))
        {
            _cachedPid = pid; _cachedName = name;
            return (fps, ft, low, name);
        }
        // Sem jogo em primeiro plano: mantém o último jogo se ele ainda estiver rodando quadros.
        if (_cachedPid != 0 && ForProcess(_cachedPid) is var (f2, ft2, l2))
            return (f2, ft2, l2, _cachedName);
        return (null, null, null, null);
    }

    public void Dispose() => StopSession();
}
