using System.Collections.Concurrent;
using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using Pulso.Platform;
using Pulso.Platform.Windows;
using Pulso.Core;

namespace Pulso.Hardware;

/// <summary>
/// Coração do monitoramento: uma única thread lê todos os sensores no intervalo configurado
/// e publica um <see cref="Snapshot"/>. Tudo o mais (abas, overlay, alertas, log) só consome.
/// </summary>
public sealed class SensorHub : IDisposable
{
    public static SensorHub Instance { get; } = new();

    public const int HistoryLength = 900;

    ISensorProvider? _provider;
    Thread? _thread;
    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    readonly ConcurrentQueue<Action> _work = new();
    int _demand;
    long _tick;

    public bool Ready { get; private set; }
    public string? InitError { get; private set; }
    public bool Minimized { get; set; }
    public Snapshot? Last { get; private set; }
    public List<Metric> MetricList { get; private set; } = new();
    public Dictionary<string, Metric> MetricById { get; private set; } = new();
    public DateTime StatsSince { get; private set; } = DateTime.Now;

    readonly Dictionary<string, RunningStats> _stats = new();
    readonly Dictionary<string, Series> _history = new();
    readonly object _lock = new();

    /// <summary>Disparado na thread de sensores após cada leitura.</summary>
    public event Action<Snapshot>? Updated;
    public event Action? ReadyChanged;

    /// <summary>Fonte externa de FPS (monitor ETW no Windows).</summary>
    public Func<(double? fps, double? frametime, double? low1, string? process)>? FpsSource { get; set; }

    public bool CpuIsIntel => _provider?.CpuIsIntel ?? false;
    public bool PawnIoInstalled => _provider?.PawnIoInstalled ?? false;
    public bool HasCpuTemp { get; private set; }

    public int EffectiveInterval
    {
        get
        {
            int baseMs = Settings.Current.IntervalMs;
            if (Minimized && Volatile.Read(ref _demand) == 0) return Math.Max(baseMs, Settings.Current.MinimizedIntervalMs);
            return baseMs;
        }
    }

    /// <summary>Pede leitura em ritmo normal mesmo com a janela minimizada (overlay, log, testes).</summary>
    public IDisposable Demand()
    {
        Interlocked.Increment(ref _demand);
        _wake.Set();
        return new Releaser(() => Interlocked.Decrement(ref _demand));
    }

    sealed class Releaser(Action a) : IDisposable
    {
        Action? _a = a;
        public void Dispose() { Interlocked.Exchange(ref _a, null)?.Invoke(); }
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Pulso.Sensors", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Poke() => _wake.Set();

    /// <summary>Executa algo na thread de sensores (evita acesso concorrente a drivers/barramentos).</summary>
    public Task<T> InvokeAsync<T>(Func<Computer?, T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { tcs.SetResult(f((_provider as WindowsSensorProvider)?.Pc)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _wake.Set();
        return tcs.Task;
    }

    public RunningStats? GetStats(string id) { lock (_lock) return _stats.TryGetValue(id, out var s) ? s : null; }

    public Series? GetSeries(string id) { lock (_lock) return _history.TryGetValue(id, out var s) ? s : null; }

    public object HistoryLock => _lock;

    public void ResetStats()
    {
        lock (_lock) { _stats.Clear(); }
        StatsSince = DateTime.Now;
    }

    void Loop()
    {
        try
        {
            _provider = OS.Current.CreateSensorProvider();
            _provider.Open();
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            Paths.LogError("SensorHub.Open", ex);
        }

        Snapshot first;
        try { first = _provider?.Read(0) ?? new Snapshot(); }
        catch (Exception ex) { Paths.LogError("SensorHub.First", ex); first = new Snapshot(); }
        HasCpuTemp = first.Cpu.Temp != null;
        var metrics = Metrics.Build(first);
        lock (_lock)
        {
            MetricList = metrics;
            MetricById = metrics.ToDictionary(m => m.Id);
            foreach (var m in metrics) _history[m.Id] = new Series(HistoryLength);
        }
        Last = first;
        Ready = true;
        ReadyChanged?.Invoke();

        var sw = new Stopwatch();
        while (!_stop)
        {
            sw.Restart();
            while (_work.TryDequeue(out var job)) job();
            try { Tick(); }
            catch (Exception ex) { Paths.LogError("SensorHub.Tick", ex); }
            int wait = EffectiveInterval - (int)sw.ElapsedMilliseconds;
            if (wait > 0) _wake.WaitOne(wait);
        }

        try { _provider?.Dispose(); } catch { }
    }

    void Tick()
    {
        _tick++;
        if (_provider == null) { Publish(new Snapshot()); return; }
        var s = _provider.Read(_tick);
        if (FpsSource != null)
        {
            try
            {
                var (fps, ft, low, proc) = FpsSource();
                s.Fps = fps; s.FrameTimeMs = ft; s.Fps1Low = low; s.FpsProcess = proc;
            }
            catch { }
        }
        Publish(s);
    }

    void Publish(Snapshot s)
    {
        Last = s;
        lock (_lock)
        {
            foreach (var m in MetricList)
            {
                double? v = m.Get(s);
                if (_history.TryGetValue(m.Id, out var ser)) ser.Add(v);
                if (v is double d && !double.IsNaN(d))
                {
                    if (!_stats.TryGetValue(m.Id, out var st)) _stats[m.Id] = st = new RunningStats();
                    st.Add(d);
                }
            }
        }
        try { Updated?.Invoke(s); }
        catch (Exception ex) { Paths.LogError("SensorHub.Updated", ex); }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(3000);
    }
}
