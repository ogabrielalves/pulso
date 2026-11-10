using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Platform.Linux;

/// <summary>Sensores no Linux: /proc, /sys (hwmon, drm, powercap) e nvidia-smi quando existir.</summary>
public sealed class LinuxSensorProvider : ISensorProvider
{
    readonly NetSampler _net = new();
    public bool CpuIsIntel { get; private set; }
    public bool PawnIoInstalled => false;

    // ---- CPU
    string _cpuName = "";
    (ulong busy, ulong total)? _prevTotal;
    (ulong busy, ulong total)[] _prevCores = Array.Empty<(ulong, ulong)>();
    string? _cpuHwmon;
    string? _raplEnergy;
    ulong _raplPrev;
    long _raplStamp;
    ulong _raplMax;
    string? _throttleFile;
    ulong _throttlePrev;
    readonly List<string> _freqFiles = new();

    // ---- discos
    sealed class DiskRef { public string Dev = "", Name = "", Path = ""; public string? TempFile; public ulong Rd, Wr, Ticks; public long Stamp; }
    readonly List<DiskRef> _disks = new();

    // ---- GPUs
    sealed class GpuRef { public string Card = "", Dir = "", Vendor = "", Name = ""; public bool Integrated; public string? Hwmon; public int NvIndex = -1; }
    readonly List<GpuRef> _gpus = new();
    bool _nvidiaSmi;

    // ---- placa-mãe
    readonly List<(string dir, string chip)> _boardHwmons = new();

    static readonly string[] CpuChips = { "coretemp", "k10temp", "zenpower", "cpu_thermal", "x86_pkg_temp" };
    static readonly string[] SkipChips = { "coretemp", "k10temp", "zenpower", "nvme", "amdgpu", "radeon", "nouveau", "nvidia", "iwlwifi", "BAT0", "BAT1", "AC", "ACAD", "drivetemp", "i915", "xe" };

    // ------------------------------------------------------------------ util

    internal static string? Read(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return null; }
    }

    internal static double? ReadNum(string path)
    {
        var t = Read(path);
        return t != null && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static IEnumerable<string> Dirs(string path, string pattern)
    {
        try { return Directory.EnumerateDirectories(path, pattern).OrderBy(x => x, StringComparer.Ordinal).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    static IEnumerable<string> HwmonDirs() => Dirs("/sys/class/hwmon", "hwmon*");

    // ------------------------------------------------------------------ abertura

    public void Open()
    {
        var info = Read("/proc/cpuinfo") ?? "";
        var m = Regex.Match(info, @"model name\s*:\s*(.+)");
        _cpuName = m.Success ? Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ") : "CPU";
        CpuIsIntel = info.Contains("GenuineIntel", StringComparison.Ordinal);

        foreach (var d in Dirs("/sys/devices/system/cpu", "cpu*").Where(x => Regex.IsMatch(Path.GetFileName(x), @"^cpu\d+$")))
        {
            var f = Path.Combine(d, "cpufreq", "scaling_cur_freq");
            if (File.Exists(f)) _freqFiles.Add(f);
        }

        foreach (var h in HwmonDirs())
        {
            string chip = Read(Path.Combine(h, "name")) ?? "";
            if (_cpuHwmon == null && CpuChips.Contains(chip)) _cpuHwmon = h;
            else if (!SkipChips.Contains(chip) && !chip.StartsWith("ucsi", StringComparison.Ordinal)) _boardHwmons.Add((h, chip));
        }

        // RAPL (consumo do pacote). Em kernels recentes só root lê.
        foreach (var d in Dirs("/sys/class/powercap", "intel-rapl:*"))
        {
            if (Path.GetFileName(d).Count(c => c == ':') != 1) continue;
            var e = Path.Combine(d, "energy_uj");
            if (ReadNum(e) != null)
            {
                _raplEnergy = e;
                _raplMax = (ulong)(ReadNum(Path.Combine(d, "max_energy_range_uj")) ?? 0);
                break;
            }
        }

        var thr = "/sys/devices/system/cpu/cpu0/thermal_throttle/package_throttle_count";
        if (ReadNum(thr) != null) { _throttleFile = thr; _throttlePrev = (ulong)ReadNum(thr)!.Value; }

        OpenDisks();
        OpenGpus();
    }

    void OpenDisks()
    {
        foreach (var dir in Dirs("/sys/block", "*"))
        {
            string dev = Path.GetFileName(dir);
            if (Regex.IsMatch(dev, @"^(loop|ram|zram|dm-|md|sr|fd)")) continue;
            if (!File.Exists(Path.Combine(dir, "stat"))) continue;
            string model = Read(Path.Combine(dir, "device", "model")) ?? "";
            var disk = new DiskRef { Dev = dev, Path = dir, Name = string.IsNullOrWhiteSpace(model) ? dev : $"{model} ({dev})" };
            foreach (var hw in Dirs(Path.Combine(dir, "device"), "hwmon*").Concat(Dirs(Path.Combine(dir, "device", "hwmon"), "hwmon*")))
            {
                var t = Path.Combine(hw, "temp1_input");
                if (File.Exists(t)) { disk.TempFile = t; break; }
            }
            _disks.Add(disk);
        }
    }

    void OpenGpus()
    {
        _nvidiaSmi = Nvidia.Available;
        var nv = _nvidiaSmi ? Nvidia.Query() : new List<Nvidia.Row>();
        int nvSeen = 0;
        foreach (var pci in LinuxInfo.PciGpus())
        {
            string vendor = pci.VendorId switch { 0x10DE => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", _ => "" };
            if (vendor == "") continue;
            var dev = pci.Dev;
            var g = new GpuRef { Card = pci.Card ?? "", Dir = dev, Vendor = vendor, Name = GpuName(dev, vendor) };
            g.Hwmon = Dirs(Path.Combine(dev, "hwmon"), "hwmon*").FirstOrDefault();
            bool hasVram = File.Exists(Path.Combine(dev, "mem_info_vram_total"));
            g.Integrated = vendor == "Intel" ? !hasVram : vendor == "AMD" && ReadNum(Path.Combine(dev, "mem_info_vram_total")) is double v && v < 600_000_000;
            if (vendor == "NVIDIA")
            {
                g.NvIndex = nvSeen < nv.Count ? nvSeen : -1;
                if (g.NvIndex >= 0) g.Name = nv[g.NvIndex].Name;
                nvSeen++;
            }
            _gpus.Add(g);
        }
        // Dedicadas primeiro, como no Windows.
        var ordered = _gpus.OrderBy(x => x.Integrated).ToList();
        _gpus.Clear();
        _gpus.AddRange(ordered);
    }

    static string GpuName(string dev, string vendor)
    {
        string slot = Path.GetFileName(Path.GetFullPath(dev));
        var name = Tool.Run("lspci", $"-s {slot} -mm")?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var parts = Regex.Matches(name, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
            if (parts.Count >= 3)
            {
                string d = parts[2];
                var br = Regex.Match(d, @"\[([^\]]+)\]");
                return $"{vendor} {(br.Success ? br.Groups[1].Value : d)}";
            }
        }
        return vendor + " GPU";
    }

    // ------------------------------------------------------------------ leitura

    public Snapshot Read(long tick)
    {
        var s = new Snapshot();
        ReadCpu(s.Cpu);
        ReadGpus(s);
        ReadMemory(s.Mem);
        ReadDisks(s.Disks);
        _net.Read(s.Net);
        if (tick % 2 == 0 || _boardCache == null) ReadBoard();
        if (_boardCache != null) { s.Fans.AddRange(_boardCache.Value.fans); s.BoardTemps.AddRange(_boardCache.Value.temps); }
        return s;
    }

    static (ulong busy, ulong total) Parse(string line)
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ulong total = 0, idle = 0;
        for (int i = 1; i < p.Length && i <= 8; i++)
        {
            ulong v = ulong.TryParse(p[i], out var x) ? x : 0;
            total += v;
            if (i == 4 || i == 5) idle += v; // idle + iowait
        }
        return (total - idle, total);
    }

    static double Pct((ulong busy, ulong total) now, (ulong busy, ulong total) prev)
    {
        ulong dt = now.total - prev.total;
        if (dt == 0) return 0;
        return Math.Clamp((now.busy - prev.busy) * 100.0 / dt, 0, 100);
    }

    void ReadCpu(CpuState c)
    {
        c.Name = _cpuName;
        try
        {
            var lines = File.ReadAllLines("/proc/stat").Where(l => l.StartsWith("cpu", StringComparison.Ordinal)).ToArray();
            var total = Parse(lines[0]);
            var cores = lines.Skip(1).Select(Parse).ToArray();
            if (_prevTotal is { } pt)
            {
                c.Load = Pct(total, pt);
                if (_prevCores.Length == cores.Length)
                    c.ThreadLoads = cores.Select((x, i) => Pct(x, _prevCores[i])).ToArray();
                if (c.ThreadLoads.Length > 0) c.MaxThreadLoad = c.ThreadLoads.Max();
            }
            _prevTotal = total;
            _prevCores = cores;
        }
        catch { }

        var clocks = new List<double>();
        foreach (var f in _freqFiles)
            if (ReadNum(f) is double khz && khz > 0) clocks.Add(khz / 1000);
        if (clocks.Count == 0)
        {
            foreach (Match m in Regex.Matches(Read("/proc/cpuinfo") ?? "", @"cpu MHz\s*:\s*([\d.]+)"))
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)) clocks.Add(mhz);
        }
        c.CoreClocks = clocks.ToArray();
        if (clocks.Count > 0) { c.ClockAvg = clocks.Average(); c.ClockMax = clocks.Max(); }

        if (_cpuHwmon != null)
        {
            var temps = new List<double?>();
            foreach (var f in Directory.EnumerateFiles(_cpuHwmon, "temp*_input").OrderBy(x => x, StringComparer.Ordinal))
            {
                string idx = Path.GetFileName(f).Replace("_input", "");
                string label = Read(Path.Combine(_cpuHwmon, idx + "_label")) ?? "";
                double? v = ReadNum(f) / 1000;
                if (v is not double d || d <= 0 || d > 150) continue;
                if (label.StartsWith("Package", StringComparison.OrdinalIgnoreCase) || label is "Tctl" or "Tdie")
                {
                    if (label != "Tctl" || c.Temp == null) c.Temp = d;
                    if (ReadNum(Path.Combine(_cpuHwmon, idx + "_crit")) is double crit && crit > 0) c.TjMax = crit / 1000;
                }
                else if (label.StartsWith("Core", StringComparison.OrdinalIgnoreCase) || label.StartsWith("Tccd", StringComparison.OrdinalIgnoreCase)) temps.Add(d);
                else c.Temp ??= d;
            }
            c.CoreTemps = temps.ToArray();
            if (c.Temp == null && temps.Count > 0) c.Temp = temps.Max();
        }

        if (_raplEnergy != null && ReadNum(_raplEnergy) is double uj)
        {
            long now = Stopwatch.GetTimestamp();
            ulong cur = (ulong)uj;
            if (_raplStamp != 0)
            {
                double dt = (now - _raplStamp) / (double)Stopwatch.Frequency;
                ulong delta = cur >= _raplPrev ? cur - _raplPrev : (_raplMax > _raplPrev ? _raplMax - _raplPrev + cur : 0);
                if (dt > 0.05) c.Power = delta / 1e6 / dt;
            }
            _raplPrev = cur; _raplStamp = now;
        }

        if (_throttleFile != null && ReadNum(_throttleFile) is double tc)
        {
            ulong cur = (ulong)tc;
            c.ThermalThrottle = cur > _throttlePrev;
            _throttlePrev = cur;
        }
    }

    static void ReadMemory(MemState m)
    {
        try
        {
            var d = new Dictionary<string, double>();
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                int i = line.IndexOf(':');
                if (i < 0) continue;
                var parts = line[(i + 1)..].Trim().Split(' ');
                if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var kb)) d[line[..i]] = kb;
            }
            const double gb = 1024.0 * 1024;
            double total = d.GetValueOrDefault("MemTotal");
            double avail = d.GetValueOrDefault("MemAvailable", d.GetValueOrDefault("MemFree"));
            m.TotalGB = total / gb;
            m.AvailableGB = avail / gb;
            m.UsedGB = (total - avail) / gb;
            m.Load = total > 0 ? (total - avail) / total * 100 : null;
            m.CachedGB = (d.GetValueOrDefault("Cached") + d.GetValueOrDefault("Buffers") + d.GetValueOrDefault("SReclaimable")) / gb;
            double limit = d.GetValueOrDefault("CommitLimit");
            m.CommitPct = limit > 0 ? d.GetValueOrDefault("Committed_AS") / limit * 100 : null;
        }
        catch { }
    }

    void ReadDisks(List<DiskState> list)
    {
        for (int i = 0; i < _disks.Count; i++)
        {
            var d = _disks[i];
            try
            {
                var p = (Read(Path.Combine(d.Path, "stat")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 11) continue;
                ulong rd = ulong.Parse(p[2]) * 512, wr = ulong.Parse(p[6]) * 512, ticks = ulong.Parse(p[9]);
                long now = Stopwatch.GetTimestamp();
                var st = new DiskState { Index = i, Name = d.Name };
                if (d.Stamp != 0)
                {
                    double dt = (now - d.Stamp) / (double)Stopwatch.Frequency;
                    if (dt > 0)
                    {
                        st.ReadBps = Math.Max(0, (rd - d.Rd) / dt);
                        st.WriteBps = Math.Max(0, (wr - d.Wr) / dt);
                        st.Activity = Math.Clamp((ticks - d.Ticks) / (dt * 1000) * 100, 0, 100);
                    }
                }
                d.Rd = rd; d.Wr = wr; d.Ticks = ticks; d.Stamp = now;
                if (d.TempFile != null && ReadNum(d.TempFile) is double t && t > 0) st.Temp = t / 1000;
                list.Add(st);
            }
            catch { }
        }
    }

    (List<FanReading> fans, List<(string, double)> temps)? _boardCache;

    void ReadBoard()
    {
        var fans = new List<FanReading>();
        var temps = new List<(string, double)>();
        foreach (var (dir, chip) in _boardHwmons)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "fan*_input").OrderBy(x => x, StringComparer.Ordinal))
                {
                    string idx = Path.GetFileName(f).Replace("_input", "");
                    if (ReadNum(f) is double rpm && rpm >= 0)
                        fans.Add(new FanReading { Name = Read(Path.Combine(dir, idx + "_label")) ?? $"{chip} {idx}", Rpm = rpm });
                }
                foreach (var f in Directory.EnumerateFiles(dir, "temp*_input").OrderBy(x => x, StringComparer.Ordinal))
                {
                    string idx = Path.GetFileName(f).Replace("_input", "");
                    if (ReadNum(f) is double t && t / 1000 is > 0 and < 150)
                        temps.Add((Read(Path.Combine(dir, idx + "_label")) ?? $"{chip} {idx}", t / 1000));
                }
            }
            catch { }
        }
        _boardCache = (fans, temps);
    }

    void ReadGpus(Snapshot s)
    {
        List<Nvidia.Row>? nv = null;
        for (int i = 0; i < _gpus.Count; i++)
        {
            var r = _gpus[i];
            var g = new GpuState { Index = i, Name = r.Name, Vendor = r.Vendor, Integrated = r.Integrated };
            try
            {
                switch (r.Vendor)
                {
                    case "NVIDIA" when r.NvIndex >= 0:
                        nv ??= Nvidia.Query();
                        if (r.NvIndex < nv.Count) nv[r.NvIndex].Fill(g);
                        break;
                    case "AMD": ReadAmd(r, g); break;
                    case "Intel": ReadIntel(r, g); break;
                }
            }
            catch { }
            s.Gpus.Add(g);
        }
    }

    static void ReadAmd(GpuRef r, GpuState g)
    {
        g.Load = ReadNum(Path.Combine(r.Dir, "gpu_busy_percent"));
        if (ReadNum(Path.Combine(r.Dir, "mem_info_vram_used")) is double u) g.VramUsedMB = u / 1048576;
        if (ReadNum(Path.Combine(r.Dir, "mem_info_vram_total")) is double t) g.VramTotalMB = t / 1048576;
        if (r.Hwmon == null) return;
        string h = r.Hwmon;
        g.Temp = ReadNum(Path.Combine(h, "temp1_input")) / 1000;
        g.Hotspot = ReadNum(Path.Combine(h, "temp2_input")) / 1000;
        g.MemTemp = ReadNum(Path.Combine(h, "temp3_input")) / 1000;
        double? p = ReadNum(Path.Combine(h, "power1_average")) ?? ReadNum(Path.Combine(h, "power1_input"));
        if (p is > 0) g.Power = p / 1e6;
        g.FanRpm = ReadNum(Path.Combine(h, "fan1_input"));
        if (ReadNum(Path.Combine(h, "pwm1")) is double pwm) g.FanPct = pwm / 255 * 100;
        if (ReadNum(Path.Combine(h, "freq1_input")) is double f1) g.CoreClock = f1 / 1e6;
        if (ReadNum(Path.Combine(h, "freq2_input")) is double f2) g.MemClock = f2 / 1e6;
    }

    static void ReadIntel(GpuRef r, GpuState g)
    {
        if (r.Card != "")
        foreach (var f in new[] { "gt_cur_freq_mhz", "gt/gt0/rps_cur_freq_mhz", "device/tile0/gt0/freq0/cur_freq" })
            if (g.CoreClock == null && ReadNum(Path.Combine(r.Card, f)) is double mhz && mhz > 0) g.CoreClock = mhz;
        if (r.Hwmon != null)
        {
            g.Temp = ReadNum(Path.Combine(r.Hwmon, "temp1_input")) / 1000;
            if (ReadNum(Path.Combine(r.Hwmon, "power1_input")) is double p && p > 0) g.Power = p / 1e6;
        }
        if (ReadNum(Path.Combine(r.Dir, "mem_info_vram_used")) is double u) g.VramUsedMB = u / 1048576;
        if (ReadNum(Path.Combine(r.Dir, "mem_info_vram_total")) is double t) g.VramTotalMB = t / 1048576;
    }

    public void Dispose() { }
}

/// <summary>Executa um utilitário externo com tempo limite curto.</summary>
internal static class Tool
{
    public static string? Run(string file, string args, int timeoutMs = 2500)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return null;
            var task = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            return task.Result;
        }
        catch { return null; }
    }

    public static bool Exists(string file)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (File.Exists(Path.Combine(d, file))) return true;
        return false;
    }
}

/// <summary>NVIDIA no Linux via nvidia-smi (vem com o driver proprietário).</summary>
internal static class Nvidia
{
    public static bool Available { get; } = Tool.Exists("nvidia-smi");

    public sealed class Row
    {
        public string Name = "";
        public double? Load, Temp, Power, FanPct, MemUsedMB, MemTotalMB, CoreClock, MemClock, Enc, Dec;
        public string? Throttle;

        public void Fill(GpuState g)
        {
            g.Name = Name;
            g.Load = Load; g.Temp = Temp; g.Power = Power; g.FanPct = FanPct;
            g.VramUsedMB = MemUsedMB; g.VramTotalMB = MemTotalMB;
            g.CoreClock = CoreClock; g.MemClock = MemClock;
            g.EncodeLoad = Enc; g.DecodeLoad = Dec;
            if (Throttle != null)
            {
                g.Throttling = true;
                g.ThrottleReason = Throttle;
            }
        }
    }

    const string Query_ = "name,utilization.gpu,temperature.gpu,power.draw,fan.speed,memory.used,memory.total,clocks.gr,clocks.mem,utilization.encoder,utilization.decoder,clocks_throttle_reasons.hw_thermal_slowdown,clocks_throttle_reasons.sw_thermal_slowdown,clocks_throttle_reasons.sw_power_cap";

    static double? D(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static List<Row> Query()
    {
        var rows = new List<Row>();
        var text = Tool.Run("nvidia-smi", $"--query-gpu={Query_} --format=csv,noheader,nounits");
        if (text == null) return rows;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = line.Split(',');
            if (c.Length < 14) continue;
            var r = new Row
            {
                Name = c[0].Trim().Replace("NVIDIA ", ""), Load = D(c[1]), Temp = D(c[2]), Power = D(c[3]), FanPct = D(c[4]),
                MemUsedMB = D(c[5]), MemTotalMB = D(c[6]), CoreClock = D(c[7]), MemClock = D(c[8]), Enc = D(c[9]), Dec = D(c[10]),
            };
            if (c[11].Contains("Active")) r.Throttle = Loc.T("Limite térmico (hardware)", "Hardware thermal slowdown");
            else if (c[12].Contains("Active")) r.Throttle = Loc.T("Limite térmico (software)", "Software thermal slowdown");
            else if (c[13].Contains("Active")) r.Throttle = Loc.T("Limite de energia", "Power cap");
            rows.Add(r);
        }
        return rows;
    }
}
