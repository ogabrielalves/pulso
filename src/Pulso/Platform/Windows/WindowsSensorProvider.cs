using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Platform.Windows;

/// <summary>Sensores no Windows: LibreHardwareMonitor (+ PawnIO), NVML e contadores de desempenho.</summary>
public sealed class WindowsSensorProvider : ISensorProvider
{
    Computer? _pc;
    public Computer? Pc => _pc;
    readonly NetSampler _net = new();
    public bool CpuIsIntel { get; private set; }
    public bool PawnIoInstalled { get; private set; }

    public void Open()
    {
        try
        {
            PawnIoInstalled = SafeGet(() => LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled);
            _pc = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
                IsBatteryEnabled = false,
                IsPsuEnabled = false,
            };
            try { LibreHardwareMonitor.Hardware.Storage.StorageDevice.ThrottleInterval = TimeSpan.FromSeconds(5); } catch { }
            _pc.Open();
        }
        catch (Exception ex)
        {
            Paths.LogError("WindowsSensors.Open", ex);
            InitLayout();
            throw;
        }
        InitLayout();
    }

    public void Dispose()
    {
        try { _intelMsr?.Close(); } catch { }
        try { _pc?.Close(); } catch { }
    }

    static T SafeGet<T>(Func<T> f) { try { return f(); } catch { return default!; } }

    // ------------------------------------------------------------------ layout

    IHardware? _cpu;
    readonly List<IHardware> _gpus = new();
    IHardware? _ram;
    readonly List<IHardware> _storages = new();
    IHardware? _board;
    PerformanceCounter? _cpuPerf;
    double _cpuBaseMhz;
    PerfDisks? _perfDisks;
    LibreHardwareMonitor.PawnIo.IntelMsr? _intelMsr;
    bool _msrFailed;
    List<Nvml.Device> _nvml = new();
    readonly Dictionary<int, Nvml.Device> _nvmlByGpu = new();

    void InitLayout()
    {
        if (_pc != null)
        {
            foreach (var h in _pc.Hardware)
            {
                switch (h.HardwareType)
                {
                    case HardwareType.Cpu: _cpu ??= h; break;
                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel: _gpus.Add(h); break;
                    case HardwareType.Memory when h.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) || h.Identifier.ToString() == "/ram": _ram = h; break;
                    case HardwareType.Storage: _storages.Add(h); break;
                    case HardwareType.Motherboard: _board = h; break;
                }
            }
        }

        // Dedicadas primeiro: a "GPU principal" é a que roda os jogos.
        _gpus.Sort((a, b) => IsIntegrated(a).CompareTo(IsIntegrated(b)));
        CpuIsIntel = _cpu?.HardwareType == HardwareType.Cpu && (_cpu.Identifier.ToString().Contains("intel", StringComparison.OrdinalIgnoreCase));

        try
        {
            _cpuBaseMhz = Info.CpuInfo.BaseClockMhz();
            _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
            _cpuPerf.NextValue();
        }
        catch { _cpuPerf = null; }

        if (_storages.Count == 0)
            try { _perfDisks = new PerfDisks(); } catch { }

        try
        {
            if (_gpus.Any(g => g.HardwareType == HardwareType.GpuNvidia))
            {
                _nvml = Nvml.Devices();
                for (int i = 0; i < _gpus.Count; i++)
                {
                    if (_gpus[i].HardwareType != HardwareType.GpuNvidia) continue;
                    var m = _nvml.FirstOrDefault(d => Norm(d.Name) == Norm(_gpus[i].Name) && !_nvmlByGpu.ContainsValue(d))
                            ?? _nvml.FirstOrDefault(d => !_nvmlByGpu.ContainsValue(d));
                    if (m != null) _nvmlByGpu[i] = m;
                }
            }
        }
        catch { }

    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace("nvidia", "");

    public static bool IsIntegrated(IHardware h)
    {
        string n = h.Name;
        if (h.HardwareType == HardwareType.GpuIntel)
            return !System.Text.RegularExpressions.Regex.IsMatch(n, @"Arc.*\b[AB]\d{3}");
        if (h.HardwareType == HardwareType.GpuAmd)
            return System.Text.RegularExpressions.Regex.IsMatch(n, @"Radeon(\(TM\))? (Graphics|Vega)|Vega \d+ Graphics|Radeon \d{3}M|Radeon\(TM\) \d{3}M", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return false;
    }

    // ------------------------------------------------------------------ leitura

    public Snapshot Read(long tick)
    {
        if (_pc == null && _perfDisks == null) return BuildSnapshot();

        _cpu?.Update();
        foreach (var g in _gpus) g.Update();
        _ram?.Update();
        // Placa-mãe e discos mudam devagar: leitura a cada 2 ciclos.
        if (tick % 2 == 0 && _board != null)
        {
            _board.Update();
            foreach (var sh in _board.SubHardware) sh.Update();
        }
        foreach (var s in _storages) s.Update();

        return BuildSnapshot();
    }

    static double? V(ISensor? s) => s?.Value is float f && !float.IsNaN(f) ? f : null;

    static ISensor? Find(IHardware h, SensorType t, params string[] names)
    {
        foreach (var n in names)
            foreach (var s in h.Sensors)
                if (s.SensorType == t && s.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && s.Value != null)
                    return s;
        return null;
    }

    Snapshot BuildSnapshot()
    {
        var s = new Snapshot();
        if (_cpu != null) ReadCpu(_cpu, s.Cpu);
        if (s.Cpu.ClockAvg == null && _cpuPerf != null && _cpuBaseMhz > 0)
        {
            try { s.Cpu.ClockAvg = _cpuPerf.NextValue() / 100.0 * _cpuBaseMhz; } catch { }
        }

        for (int i = 0; i < _gpus.Count; i++)
        {
            var g = ReadGpu(_gpus[i]);
            g.Index = i;
            if (_nvmlByGpu.TryGetValue(i, out var nd)) ReadNvml(nd, g);
            s.Gpus.Add(g);
        }

        ReadMemory(s.Mem);

        if (_storages.Count > 0)
        {
            for (int i = 0; i < _storages.Count; i++)
            {
                var h = _storages[i];
                s.Disks.Add(new DiskState
                {
                    Index = i,
                    Name = h.Name,
                    Activity = V(Find(h, SensorType.Load, "Total Activity")),
                    ReadBps = V(Find(h, SensorType.Throughput, "Read Rate")),
                    WriteBps = V(Find(h, SensorType.Throughput, "Write Rate")),
                    Temp = V(h.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value != null)),
                });
            }
        }
        else _perfDisks?.Read(s.Disks);

        _net.Read(s.Net);

        if (_board != null)
        {
            foreach (var sh in _board.SubHardware)
                foreach (var se in sh.Sensors)
                {
                    if (se.Value is not float f || float.IsNaN(f)) continue;
                    if (se.SensorType == SensorType.Fan) s.Fans.Add(new FanReading { Name = se.Name, Rpm = f });
                    else if (se.SensorType == SensorType.Temperature && f > 0 && f < 150) s.BoardTemps.Add((se.Name, f));
                }
        }

        return s;
    }

    void ReadCpu(IHardware h, CpuState c)
    {
        c.Name = h.Name;
        c.Load = V(Find(h, SensorType.Load, "CPU Total"));
        c.MaxThreadLoad = V(Find(h, SensorType.Load, "CPU Core Max"));

        var threads = new List<double>();
        var clocks = new List<double>();
        var temps = new List<double?>();
        double? distance = null;
        foreach (var se in h.Sensors)
        {
            string n = se.Name;
            switch (se.SensorType)
            {
                case SensorType.Load when n.StartsWith("CPU Core #", StringComparison.Ordinal):
                    threads.Add(V(se) ?? 0);
                    break;
                case SensorType.Clock when n.Contains("Core #", StringComparison.Ordinal) && !n.Contains("Effective", StringComparison.OrdinalIgnoreCase):
                    if (V(se) is double ck && ck > 0) clocks.Add(ck);
                    break;
                case SensorType.Temperature when n.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase):
                    distance ??= V(se);
                    break;
                case SensorType.Temperature when (n.Contains("Core #", StringComparison.Ordinal) || n.StartsWith("CCD", StringComparison.Ordinal)):
                    temps.Add(V(se));
                    break;
            }
        }
        c.ThreadLoads = threads.ToArray();
        if (c.MaxThreadLoad == null && threads.Count > 0) c.MaxThreadLoad = threads.Max();
        c.CoreClocks = clocks.ToArray();
        if (clocks.Count > 0) { c.ClockAvg = clocks.Average(); c.ClockMax = clocks.Max(); }
        c.CoreTemps = temps.ToArray();

        c.Temp = V(Find(h, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "Package", "Core Max", "Core Average", "CPU Cores"));
        if (c.Temp != null && distance != null) c.TjMax = c.Temp + distance;
        c.Power = V(Find(h, SensorType.Power, "CPU Package", "Package", "CPU Cores"));
        if (c.Power is double p && p <= 0) c.Power = null;
        c.Voltage = V(h.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Voltage && x.Value is > 0.3f and < 2.5f
                        && (x.Name.StartsWith("CPU Core", StringComparison.Ordinal) || x.Name.StartsWith("Core (SVI", StringComparison.Ordinal) || x.Name == "Core" || x.Name == "Vcore")
                        && !x.Name.Contains('#')));

        if (CpuIsIntel && PawnIoInstalled && !_msrFailed)
        {
            try
            {
                _intelMsr ??= new LibreHardwareMonitor.PawnIo.IntelMsr();
                // IA32_PACKAGE_THERM_STATUS (0x1B1): bit 0 = limitando por temperatura agora; bit 2 = PROCHOT.
                if (_intelMsr.ReadMsr(0x1B1, out ulong v)) c.ThermalThrottle = (v & 0b101) != 0;
                else _msrFailed = true;
            }
            catch { _msrFailed = true; }
        }
    }

    static GpuState ReadGpu(IHardware h)
    {
        var g = new GpuState
        {
            Name = h.Name,
            Vendor = h.HardwareType switch { HardwareType.GpuNvidia => "NVIDIA", HardwareType.GpuAmd => "AMD", _ => "Intel" },
            Integrated = IsIntegrated(h),
            Load = V(Find(h, SensorType.Load, "GPU Core", "D3D 3D", "GPU")),
            Temp = V(Find(h, SensorType.Temperature, "GPU Core", "GPU", "GPU Temperature")),
            Hotspot = V(Find(h, SensorType.Temperature, "GPU Hot Spot", "GPU Hotspot")),
            MemTemp = V(Find(h, SensorType.Temperature, "GPU Memory Junction", "GPU Memory")),
            CoreClock = V(Find(h, SensorType.Clock, "GPU Core", "GPU Graphics")),
            MemClock = V(Find(h, SensorType.Clock, "GPU Memory")),
            Power = V(Find(h, SensorType.Power, "GPU Package", "GPU Power", "GPU Board Power", "GPU Core", "GPU Total")),
            FanRpm = V(h.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Fan && x.Value != null)),
            FanPct = V(h.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Control && x.Value != null)),
            EncodeLoad = V(Find(h, SensorType.Load, "D3D Video Encode", "GPU Video Engine")),
            DecodeLoad = V(Find(h, SensorType.Load, "D3D Video Decode")),
        };
        if (g.Integrated)
        {
            g.VramUsedMB = V(Find(h, SensorType.SmallData, "D3D Shared Memory Used"));
            g.VramTotalMB = V(Find(h, SensorType.SmallData, "D3D Shared Memory Total"));
        }
        else
        {
            g.VramUsedMB = V(Find(h, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"));
            g.VramTotalMB = V(Find(h, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total"));
        }
        // O LHM às vezes não expõe "GPU Core" para Intel/AMD integradas; usamos o maior motor D3D.
        if (g.Load == null)
        {
            var d3d = h.Sensors.Where(x => x.SensorType == SensorType.Load && x.Name.StartsWith("D3D", StringComparison.Ordinal) && x.Value != null).Select(x => (double)x.Value!.Value).DefaultIfEmpty().Max();
            g.Load = d3d;
        }
        if (g.Power is double p && p <= 0) g.Power = null;
        return g;
    }

    static void ReadNvml(Nvml.Device d, GpuState g)
    {
        g.EncodeLoad ??= Nvml.EncoderUtil(d);
        g.DecodeLoad ??= Nvml.DecoderUtil(d);
        if (Nvml.ThrottleReasons(d) is ulong r)
        {
            var (thr, reason) = Nvml.DescribeThrottle(r);
            g.Throttling = thr;
            g.ThrottleReason = reason;
        }
    }

    void ReadMemory(MemState m)
    {
        try
        {
            var pi = new Native.PERFORMANCE_INFORMATION { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>() };
            if (Native.GetPerformanceInfo(ref pi, pi.cb))
            {
                double page = (double)pi.PageSize;
                double gb = 1024.0 * 1024 * 1024;
                m.TotalGB = pi.PhysicalTotal * page / gb;
                m.AvailableGB = pi.PhysicalAvailable * page / gb;
                m.UsedGB = m.TotalGB - m.AvailableGB;
                m.Load = m.UsedGB / m.TotalGB * 100;
                m.CachedGB = pi.SystemCache * page / gb;
                m.CommitPct = pi.CommitLimit > 0 ? (double)pi.CommitTotal / pi.CommitLimit * 100 : null;
            }
        }
        catch { }
        if (m.Load == null && _ram != null)
        {
            m.UsedGB = V(Find(_ram, SensorType.Data, "Memory Used"));
            m.AvailableGB = V(Find(_ram, SensorType.Data, "Memory Available"));
            m.Load = V(Find(_ram, SensorType.Load, "Memory"));
            if (m.UsedGB != null && m.AvailableGB != null) m.TotalGB = m.UsedGB + m.AvailableGB;
        }
    }
}

/// <summary>Atividade de disco via contadores do Windows (usado sem privilégio de administrador).</summary>
sealed class PerfDisks
{
    readonly List<(string name, PerformanceCounter idle, PerformanceCounter rd, PerformanceCounter wr)> _c = new();

    public PerfDisks()
    {
        var cat = new PerformanceCounterCategory("PhysicalDisk");
        foreach (var inst in cat.GetInstanceNames().Where(i => i != "_Total").OrderBy(i => i))
        {
            var idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", inst, true);
            var rd = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst, true);
            var wr = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst, true);
            idle.NextValue(); rd.NextValue(); wr.NextValue();
            _c.Add((inst, idle, rd, wr));
        }
    }

    public void Read(List<DiskState> disks)
    {
        for (int i = 0; i < _c.Count; i++)
        {
            var (name, idle, rd, wr) = _c[i];
            try
            {
                disks.Add(new DiskState
                {
                    Index = i,
                    Name = Loc.T("Disco ", "Disk ") + name,
                    Activity = Math.Clamp(100 - idle.NextValue(), 0, 100),
                    ReadBps = rd.NextValue(),
                    WriteBps = wr.NextValue(),
                });
            }
            catch { }
        }
    }
}
