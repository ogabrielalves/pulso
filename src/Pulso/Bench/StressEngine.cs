using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Bench;

public sealed class StressReport
{
    public DateTime Date { get; set; } = DateTime.Now;
    public string Components { get; set; } = "";
    public TimeSpan Planned { get; set; }
    public TimeSpan Elapsed { get; set; }
    public double? MaxCpuTemp { get; set; }
    public double? MaxGpuTemp { get; set; }
    public double? AvgCpuClock { get; set; }
    public double? AvgGpuClock { get; set; }
    public double? MaxCpuPower { get; set; }
    public double? MaxGpuPower { get; set; }
    public bool Throttling { get; set; }
    public string? ThrottleDetail { get; set; }
    public long Errors { get; set; }
    public string? StopReason { get; set; }
    public bool SafetyStop { get; set; }
    public bool Stable => Errors == 0 && !SafetyStop;

    public string Verdict => SafetyStop ? Loc.T("Interrompido", "Stopped")
        : Errors > 0 ? Loc.T("Instável", "Unstable")
        : Elapsed < Planned - TimeSpan.FromSeconds(5) ? Loc.T("Estável até a parada", "Stable until stopped")
        : Loc.T("Estável", "Stable");
}

/// <summary>Executa a carga máxima e acompanha os sensores (limite de segurança, throttling, erros).</summary>
public sealed class StressEngine
{
    public bool Cpu, Gpu, Ram;
    public TimeSpan Duration = TimeSpan.FromMinutes(15);
    public double CpuLimit = 95, GpuLimit = 90;

    public bool Running { get; private set; }
    public DateTime Started { get; private set; }
    public TimeSpan Elapsed => Running ? DateTime.Now - Started : _final;
    public StressReport Report { get; private set; } = new();
    public double GpuFps { get; private set; }

    /// <summary>Atualização (thread de sensores): relatório parcial.</summary>
    public event Action? Tick;
    public event Action<StressReport>? Finished;

    CancellationTokenSource? _cts;
    readonly List<Thread> _threads = new();
    long _errors;
    TimeSpan _final;
    IDisposable? _demand;
    double _cpuClockSum, _gpuClockSum;
    int _cpuClockN, _gpuClockN;
    double _cpuRefClock, _gpuRefClock;  // clock "de cruzeiro" medido no início
    readonly List<double> _earlyCpuClocks = new(), _earlyGpuClocks = new();
    Task? _gpuTask;

    public static string ReportFile => Path.Combine(Paths.History, "stress.json");

    public void Start()
    {
        if (Running || !(Cpu || Gpu || Ram)) return;
        Running = true;
        Started = DateTime.Now;
        _errors = 0;
        _cpuClockSum = _gpuClockSum = 0; _cpuClockN = _gpuClockN = 0;
        _cpuRefClock = _gpuRefClock = 0;
        _earlyCpuClocks.Clear(); _earlyGpuClocks.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Report = new StressReport
        {
            Planned = Duration,
            Components = string.Join(" + ", new[] { Cpu ? "CPU" : null, Gpu ? "GPU" : null, Ram ? "RAM" : null }.Where(x => x != null)),
        };

        int logical = Environment.ProcessorCount;
        int ramThreads = Ram ? (Cpu ? Math.Max(1, logical / 4) : Math.Max(1, logical / 2)) : 0;
        int cpuThreads = Cpu ? Math.Max(1, logical - ramThreads - (Gpu ? 1 : 0)) : 0;

        for (int i = 0; i < cpuThreads; i++) StartThread(() => CpuWorker(ct), "Pulso.Stress.CPU");
        if (Ram)
        {
            long availMb = (long)((SensorHub.Instance.Last?.Mem.AvailableGB ?? 4) * 1024);
            long perThread = Math.Clamp(availMb * 6 / 10 / ramThreads, 64, 4096);
            for (int i = 0; i < ramThreads; i++) { int id = i; StartThread(() => RamWorker(perThread, id, ct), "Pulso.Stress.RAM"); }
        }
        if (Gpu)
            _gpuTask = GpuRenderer.StressAsync((fps, _) => GpuFps = fps, ct).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) Interlocked.Add(ref _errors, t.Result.Errors);
                else if (t.Exception != null) { Paths.LogError("Stress.GPU", t.Exception); Interlocked.Increment(ref _errors); }
                // Janela da GPU fechada pelo usuário = parar o teste.
                if (Running && !ct.IsCancellationRequested) Task.Run(() => Stop(Loc.T("Janela do teste de GPU fechada", "GPU test window closed")));
            });

        _demand = SensorHub.Instance.Demand();
        SensorHub.Instance.Updated += OnSensors;
    }

    void StartThread(ThreadStart f, string name)
    {
        var t = new Thread(() =>
        {
            try { f(); }
            catch (OutOfMemoryException) { }
            catch (Exception ex) { Paths.LogError(name, ex); Interlocked.Increment(ref _errors); }
        }) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        _threads.Add(t);
        t.Start();
    }

    /// <summary>
    /// Carga de ponto flutuante vetorial (FMA/AVX quando disponível) com resultado conhecido:
    /// se a CPU errar uma conta (instabilidade por overclock/undervolt/temperatura), o resultado diverge.
    /// </summary>
    void CpuWorker(CancellationToken ct)
    {
        int n = Vector<double>.Count;
        var a = new Vector<double>[64];
        var b = new Vector<double>(1.0000001);
        var c = new Vector<double>(0.9999999);
        double Run()
        {
            for (int i = 0; i < a.Length; i++) a[i] = new Vector<double>(1.0 + i * 1e-3);
            for (int k = 0; k < 20000; k++)
                for (int i = 0; i < a.Length; i++)
                    a[i] = a[i] * b + c * a[(i + 1) & 63] * 1e-7;
            double sum = 0;
            for (int i = 0; i < a.Length; i++) sum += Vector.Sum(a[i]);
            return sum;
        }
        double reference = Run();
        while (!ct.IsCancellationRequested)
        {
            double r = Run();
            if (BitConverter.DoubleToInt64Bits(r) != BitConverter.DoubleToInt64Bits(reference)) Interlocked.Increment(ref _errors);
        }
    }

    /// <summary>Grava e confere padrões na RAM continuamente.</summary>
    unsafe void RamWorker(long megabytes, int id, CancellationToken ct)
    {
        long bytes = megabytes << 20;
        ulong* p = (ulong*)NativeMemory.Alloc((nuint)bytes);
        try
        {
            long words = bytes / 8;
            ulong seed = 0x9E3779B97F4A7C15UL * (ulong)(id + 1);
            while (!ct.IsCancellationRequested)
            {
                ulong s = seed;
                for (long i = 0; i < words; i++)
                {
                    s ^= s << 13; s ^= s >> 7; s ^= s << 17;
                    p[i] = s;
                    if ((i & 0xFFFFF) == 0 && ct.IsCancellationRequested) return;
                }
                s = seed;
                long err = 0;
                for (long i = 0; i < words; i++)
                {
                    s ^= s << 13; s ^= s >> 7; s ^= s << 17;
                    if (p[i] != s) err++;
                    if ((i & 0xFFFFF) == 0 && ct.IsCancellationRequested) return;
                }
                if (err > 0) Interlocked.Add(ref _errors, err);
                seed = seed * 6364136223846793005UL + 1442695040888963407UL;
            }
        }
        finally { NativeMemory.Free(p); }
    }

    void OnSensors(Snapshot s)
    {
        if (!Running) return;
        var r = Report;
        var el = DateTime.Now - Started;
        r.Elapsed = el;
        r.Errors = Interlocked.Read(ref _errors);

        var c = s.Cpu;
        if (c.Temp is double ct) r.MaxCpuTemp = Math.Max(r.MaxCpuTemp ?? 0, ct);
        if (c.Power is double cp) r.MaxCpuPower = Math.Max(r.MaxCpuPower ?? 0, cp);
        var g = s.Gpu;
        if (g?.Temp is double gt) r.MaxGpuTemp = Math.Max(r.MaxGpuTemp ?? 0, gt);
        if (g?.Power is double gp) r.MaxGpuPower = Math.Max(r.MaxGpuPower ?? 0, gp);

        // Clocks: os primeiros 20–40 s definem a referência (ainda frio); quedas fortes depois = throttling.
        if ((Cpu || Ram) && c.ClockAvg is double cc && el.TotalSeconds > 5)
        {
            _cpuClockSum += cc; _cpuClockN++;
            r.AvgCpuClock = _cpuClockSum / _cpuClockN;
            if (el.TotalSeconds < 40) _earlyCpuClocks.Add(cc);
            else if (_cpuRefClock == 0 && _earlyCpuClocks.Count > 3) _cpuRefClock = _earlyCpuClocks.OrderBy(x => x).ElementAt(_earlyCpuClocks.Count / 2);
            double tj = c.TjMax ?? 100;
            bool hot = c.Temp is double t && t >= Math.Min(tj - 3, CpuLimitHot());
            if (c.ThermalThrottle == true)
                Flag(Loc.T($"CPU limitou o clock por temperatura ({Fmt.Value(c.Temp, Unit.Temp)})", $"CPU throttled due to temperature ({Fmt.Value(c.Temp, Unit.Temp)})"));
            else if (_cpuRefClock > 0 && hot && cc < _cpuRefClock * 0.9)
                Flag(Loc.T($"Clock da CPU caiu {Fmt.N((1 - cc / _cpuRefClock) * 100)}% com {Fmt.Value(c.Temp, Unit.Temp)}", $"CPU clock dropped {Fmt.N((1 - cc / _cpuRefClock) * 100)}% at {Fmt.Value(c.Temp, Unit.Temp)}"));
        }
        if (Gpu && g?.CoreClock is double gc && el.TotalSeconds > 5)
        {
            _gpuClockSum += gc; _gpuClockN++;
            r.AvgGpuClock = _gpuClockSum / _gpuClockN;
            if (el.TotalSeconds < 40) _earlyGpuClocks.Add(gc);
            else if (_gpuRefClock == 0 && _earlyGpuClocks.Count > 3) _gpuRefClock = _earlyGpuClocks.OrderBy(x => x).ElementAt(_earlyGpuClocks.Count / 2);
            if (g.Throttling == true && g.ThrottleReason != null && g.ThrottleReason.Contains(Loc.T("temperatura", "temperature")))
                Flag(Loc.T($"GPU limitou o clock: {g.ThrottleReason}", $"GPU limited its clock: {g.ThrottleReason}"));
            else if (_gpuRefClock > 0 && g.Temp is double t2 && t2 >= 83 && gc < _gpuRefClock * 0.88)
                Flag(Loc.T($"Clock da GPU caiu {Fmt.N((1 - gc / _gpuRefClock) * 100)}% com {Fmt.Value(t2, Unit.Temp)}", $"GPU clock dropped {Fmt.N((1 - gc / _gpuRefClock) * 100)}% at {Fmt.Value(t2, Unit.Temp)}"));
        }

        // Parada de segurança
        if (c.Temp is double t3 && t3 >= CpuLimit) { r.SafetyStop = true; Stop(Loc.T($"Temperatura da CPU chegou a {Fmt.Value(t3, Unit.Temp)} (limite {Fmt.Value(CpuLimit, Unit.Temp)})", $"CPU temperature reached {Fmt.Value(t3, Unit.Temp)} (limit {Fmt.Value(CpuLimit, Unit.Temp)})")); return; }
        if (g?.Temp is double t4 && t4 >= GpuLimit) { r.SafetyStop = true; Stop(Loc.T($"Temperatura da GPU chegou a {Fmt.Value(t4, Unit.Temp)} (limite {Fmt.Value(GpuLimit, Unit.Temp)})", $"GPU temperature reached {Fmt.Value(t4, Unit.Temp)} (limit {Fmt.Value(GpuLimit, Unit.Temp)})")); return; }

        if (el >= Duration) { Stop(null); return; }
        Tick?.Invoke();
    }

    static double CpuLimitHot() => 95;

    void Flag(string detail)
    {
        if (Report.Throttling) return;
        Report.Throttling = true;
        Report.ThrottleDetail = detail;
    }

    public void Stop(string? reason)
    {
        if (!Running) return;
        Running = false;
        SensorHub.Instance.Updated -= OnSensors;
        _cts?.Cancel();
        _final = DateTime.Now - Started;
        Report.Elapsed = _final;
        foreach (var t in _threads) t.Join(2000);
        _threads.Clear();
        try { _gpuTask?.Wait(3000); } catch { }
        Report.Errors = Interlocked.Read(ref _errors);
        Report.StopReason = reason;
        _demand?.Dispose();
        _demand = null;
        try
        {
            var list = Json.Load<List<StressReport>>(ReportFile);
            list.Insert(0, Report);
            Json.Save(ReportFile, list.Take(50).ToList());
        }
        catch { }
        Finished?.Invoke(Report);
    }

    public static StressReport? LastReport() => Json.Load<List<StressReport>>(ReportFile).FirstOrDefault();
}
