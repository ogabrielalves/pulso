using Pulso.Core;

namespace Pulso.Hardware;

/// <summary>Uma grandeza monitorável (usada por gráficos, overlay, alertas, CSV e estatísticas).</summary>
public sealed class Metric
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Label { get; init; }
    public required Unit Unit { get; init; }
    public required Func<Snapshot, double?> Get { get; init; }
    /// <summary>Rótulo curto para o overlay.</summary>
    public string Short { get; init; } = "";

    public string FullLabel => Group + " · " + Label;
    public override string ToString() => FullLabel;
}

public static class Metrics
{
    public static List<Metric> Build(Snapshot s)
    {
        var L = Loc.T;
        var list = new List<Metric>
        {
            new() { Id = "fps", Group = L("Jogo", "Game"), Label = "FPS", Short = "FPS", Unit = Unit.Fps, Get = x => x.Fps },
            new() { Id = "frametime", Group = L("Jogo", "Game"), Label = "Frametime", Short = "FT", Unit = Unit.Ms, Get = x => x.FrameTimeMs },
            new() { Id = "fps.low1", Group = L("Jogo", "Game"), Label = L("1% mínimo", "1% low"), Short = "1%", Unit = Unit.Fps, Get = x => x.Fps1Low },

            new() { Id = "cpu.load", Group = "CPU", Label = L("Uso", "Usage"), Short = "CPU", Unit = Unit.Percent, Get = x => x.Cpu.Load },
            new() { Id = "cpu.maxthread", Group = "CPU", Label = L("Thread mais carregada", "Busiest thread"), Short = "CPU↑", Unit = Unit.Percent, Get = x => x.Cpu.MaxThreadLoad },
            new() { Id = "cpu.temp", Group = "CPU", Label = L("Temperatura", "Temperature"), Short = "CPU", Unit = Unit.Temp, Get = x => x.Cpu.Temp },
            new() { Id = "cpu.clock", Group = "CPU", Label = L("Clock médio", "Average clock"), Short = "CPU", Unit = Unit.MHz, Get = x => x.Cpu.ClockAvg },
            new() { Id = "cpu.clockmax", Group = "CPU", Label = L("Clock máximo", "Max clock"), Short = "CPU", Unit = Unit.MHz, Get = x => x.Cpu.ClockMax },
            new() { Id = "cpu.power", Group = "CPU", Label = L("Consumo", "Power"), Short = "CPU", Unit = Unit.Watt, Get = x => x.Cpu.Power },
            new() { Id = "cpu.voltage", Group = "CPU", Label = L("Voltagem", "Voltage"), Short = "CPU", Unit = Unit.Volt, Get = x => x.Cpu.Voltage },
        };

        foreach (var g in s.Gpus)
        {
            int i = g.Index;
            string grp = s.Gpus.Count > 1 ? $"GPU {i + 1}" : "GPU";
            string sh = s.Gpus.Count > 1 ? $"GPU{i + 1}" : "GPU";
            GpuState? G(Snapshot x) => i < x.Gpus.Count ? x.Gpus[i] : null;
            list.Add(new() { Id = $"gpu{i}.load", Group = grp, Label = L("Uso", "Usage"), Short = sh, Unit = Unit.Percent, Get = x => G(x)?.Load });
            list.Add(new() { Id = $"gpu{i}.temp", Group = grp, Label = L("Temperatura", "Temperature"), Short = sh, Unit = Unit.Temp, Get = x => G(x)?.Temp });
            list.Add(new() { Id = $"gpu{i}.hotspot", Group = grp, Label = L("Temperatura hotspot", "Hotspot temperature"), Short = "HS", Unit = Unit.Temp, Get = x => G(x)?.Hotspot });
            list.Add(new() { Id = $"gpu{i}.clock", Group = grp, Label = L("Clock do núcleo", "Core clock"), Short = sh, Unit = Unit.MHz, Get = x => G(x)?.CoreClock });
            list.Add(new() { Id = $"gpu{i}.memclock", Group = grp, Label = L("Clock da memória", "Memory clock"), Short = "MEM", Unit = Unit.MHz, Get = x => G(x)?.MemClock });
            list.Add(new() { Id = $"gpu{i}.power", Group = grp, Label = L("Consumo", "Power"), Short = sh, Unit = Unit.Watt, Get = x => G(x)?.Power });
            list.Add(new() { Id = $"gpu{i}.vram", Group = grp, Label = L("VRAM em uso", "VRAM used"), Short = "VRAM", Unit = Unit.MB, Get = x => G(x)?.VramUsedMB });
            list.Add(new() { Id = $"gpu{i}.vrampct", Group = grp, Label = L("VRAM em uso (%)", "VRAM used (%)"), Short = "VRAM", Unit = Unit.Percent, Get = x => G(x)?.VramPct });
            list.Add(new() { Id = $"gpu{i}.fan", Group = grp, Label = L("Ventoinha", "Fan"), Short = "FAN", Unit = Unit.Rpm, Get = x => G(x)?.FanRpm });
            list.Add(new() { Id = $"gpu{i}.enc", Group = grp, Label = L("Encoder de vídeo", "Video encoder"), Short = "ENC", Unit = Unit.Percent, Get = x => G(x)?.EncodeLoad });
            list.Add(new() { Id = $"gpu{i}.dec", Group = grp, Label = L("Decoder de vídeo", "Video decoder"), Short = "DEC", Unit = Unit.Percent, Get = x => G(x)?.DecodeLoad });
        }

        list.Add(new() { Id = "ram.load", Group = "RAM", Label = L("Uso", "Usage"), Short = "RAM", Unit = Unit.Percent, Get = x => x.Mem.Load });
        list.Add(new() { Id = "ram.used", Group = "RAM", Label = L("Em uso", "Used"), Short = "RAM", Unit = Unit.GB, Get = x => x.Mem.UsedGB });

        foreach (var d in s.Disks)
        {
            int i = d.Index;
            string grp = L("Disco", "Disk") + " " + (i + 1);
            DiskState? D(Snapshot x) => i < x.Disks.Count ? x.Disks[i] : null;
            list.Add(new() { Id = $"disk{i}.activity", Group = grp, Label = L("Atividade", "Activity"), Short = "DSK", Unit = Unit.Percent, Get = x => D(x)?.Activity });
            list.Add(new() { Id = $"disk{i}.read", Group = grp, Label = L("Leitura", "Read"), Short = "R", Unit = Unit.BytesPerSec, Get = x => D(x)?.ReadBps });
            list.Add(new() { Id = $"disk{i}.write", Group = grp, Label = L("Escrita", "Write"), Short = "W", Unit = Unit.BytesPerSec, Get = x => D(x)?.WriteBps });
            list.Add(new() { Id = $"disk{i}.temp", Group = grp, Label = L("Temperatura", "Temperature"), Short = "DSK", Unit = Unit.Temp, Get = x => D(x)?.Temp });
        }

        list.Add(new() { Id = "net.down", Group = L("Rede", "Network"), Label = "Download", Short = "↓", Unit = Unit.BytesPerSec, Get = x => x.Net.DownBps });
        list.Add(new() { Id = "net.up", Group = L("Rede", "Network"), Label = "Upload", Short = "↑", Unit = Unit.BytesPerSec, Get = x => x.Net.UpBps });
        return list;
    }
}

/// <summary>Mínimo, máximo e média desde o início do monitoramento.</summary>
public sealed class RunningStats
{
    public double Min = double.MaxValue, Max = double.MinValue, Sum;
    public long Count;
    public double? Last;

    public void Add(double v)
    {
        if (v < Min) Min = v;
        if (v > Max) Max = v;
        Sum += v;
        Count++;
        Last = v;
    }

    public double? Avg => Count > 0 ? Sum / Count : null;
    public double? MinV => Count > 0 ? Min : null;
    public double? MaxV => Count > 0 ? Max : null;
}

/// <summary>Buffer circular de amostras para os gráficos.</summary>
public sealed class Series
{
    readonly float[] _data;
    int _start, _count;

    public Series(int capacity) => _data = new float[capacity];

    public int Count => _count;
    public int Capacity => _data.Length;

    public void Add(double? v)
    {
        float f = v is double d ? (float)d : float.NaN;
        if (_count < _data.Length) _data[(_start + _count++) % _data.Length] = f;
        else { _data[_start] = f; _start = (_start + 1) % _data.Length; }
    }

    public float this[int i] => _data[(_start + i) % _data.Length];

    public void Clear() { _start = 0; _count = 0; }
}
