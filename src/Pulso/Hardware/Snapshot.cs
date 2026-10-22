namespace Pulso.Hardware;

public sealed class CpuState
{
    public string Name = "";
    public double? Load;
    public double[] ThreadLoads = Array.Empty<double>();
    public double? MaxThreadLoad;
    public double? Temp;                // pacote / Tctl
    public double?[] CoreTemps = Array.Empty<double?>();
    public double? TjMax;
    public double[] CoreClocks = Array.Empty<double>();
    public double? ClockAvg;
    public double? ClockMax;
    public double? Power;
    public double? Voltage;
    public bool? ThermalThrottle;       // leitura direta do registrador (Intel) quando disponível
}

public sealed class GpuState
{
    public int Index;
    public string Name = "";
    public string Vendor = "";
    public bool Integrated;
    public double? Load;
    public double? Temp;
    public double? Hotspot;
    public double? MemTemp;
    public double? CoreClock;
    public double? MemClock;
    public double? Power;
    public double? FanRpm;
    public double? FanPct;
    public double? VramUsedMB;
    public double? VramTotalMB;
    public double? EncodeLoad;
    public double? DecodeLoad;
    public bool? Throttling;
    public string? ThrottleReason;

    public double? VramPct => VramUsedMB is double u && VramTotalMB is double t && t > 0 ? u / t * 100 : null;
}

public sealed class MemState
{
    public double? UsedGB;
    public double? AvailableGB;
    public double? TotalGB;
    public double? Load;
    public double? CachedGB;
    public double? CommitPct;
}

public sealed class DiskState
{
    public int Index;
    public string Name = "";
    public double? Activity;
    public double? ReadBps;
    public double? WriteBps;
    public double? Temp;
}

public sealed class NetState
{
    public double DownBps;
    public double UpBps;
}

public sealed class FanReading
{
    public string Name = "";
    public double? Rpm;
}

public sealed class Snapshot
{
    public DateTime Time = DateTime.Now;
    public CpuState Cpu = new();
    public List<GpuState> Gpus = new();
    public MemState Mem = new();
    public List<DiskState> Disks = new();
    public NetState Net = new();
    public List<FanReading> Fans = new();
    public List<(string Name, double Value)> BoardTemps = new();
    public double? Fps;
    public double? FrameTimeMs;
    public double? Fps1Low;
    public string? FpsProcess;

    public GpuState? Gpu => Gpus.Count > 0 ? Gpus[0] : null;
}
