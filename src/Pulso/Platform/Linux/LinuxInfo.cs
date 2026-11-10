using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pulso.Core;
using Pulso.Info;

namespace Pulso.Platform.Linux;

/// <summary>Informações estáticas de hardware no Linux (CPU, memória, discos, GPU). Tudo opcional: o que não puder ser lido fica vazio.</summary>
public static class LinuxInfo
{
    static string? R(string p) => LinuxSensorProvider.Read(p);
    static double? N(string p) => LinuxSensorProvider.ReadNum(p);

    // ================================================================== CPU

    public static void FillCpu(CpuInfo.Data r)
    {
        var info = R("/proc/cpuinfo") ?? "";
        if (string.IsNullOrEmpty(r.Brand))
        {
            var m = Regex.Match(info, @"model name\s*:\s*(.+)");
            if (m.Success) r.Brand = Regex.Replace(m.Groups[1].Value.Trim(), @"\s+", " ");
        }

        // núcleos físicos = pares (physical id, core id) distintos
        var cores = new HashSet<string>();
        string phys = "0";
        foreach (var block in info.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var pid = Regex.Match(block, @"physical id\s*:\s*(\d+)");
            var cid = Regex.Match(block, @"core id\s*:\s*(\d+)");
            if (pid.Success) phys = pid.Groups[1].Value;
            if (cid.Success) cores.Add(phys + ":" + cid.Groups[1].Value);
        }
        r.Threads = Environment.ProcessorCount;
        r.Cores = cores.Count > 0 ? cores.Count : r.Threads;

        // clocks: base (intel_pstate) e máximo (turbo)
        double? max = N("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq");
        double? bas = N("/sys/devices/system/cpu/cpu0/cpufreq/base_frequency");
        if (max is > 0) r.BoostMhz = max.Value / 1000;
        if (bas is > 0) r.BaseMhz = bas.Value / 1000;
        else if (r.BaseMhz <= 0 && max is > 0) r.BaseMhz = max.Value / 1000;

        // híbrido Intel: P-cores x E-cores
        int Count(string list)
        {
            var t = R(list);
            if (string.IsNullOrWhiteSpace(t)) return 0;
            int n = 0;
            foreach (var part in t.Split(','))
            {
                var ab = part.Split('-');
                n += ab.Length == 2 ? int.Parse(ab[1]) - int.Parse(ab[0]) + 1 : 1;
            }
            return n;
        }
        int pThreads = Count("/sys/devices/cpu_core/cpus"), eThreads = Count("/sys/devices/cpu_atom/cpus");
        if (pThreads > 0 && eThreads > 0)
        {
            int pc = Math.Max(1, pThreads / Math.Max(1, Count("/sys/devices/system/cpu/cpu0/topology/thread_siblings_list")));
            r.PCores = pc;
            r.ECores = eThreads;
            r.Cores = r.PCores + r.ECores;
        }

        ReadCaches(r);
    }

    static void ReadCaches(CpuInfo.Data r)
    {
        var sizes = new Dictionary<(int level, string type), List<long>>();
        var seen = new HashSet<string>();
        foreach (var cpu in Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*").Where(x => Regex.IsMatch(Path.GetFileName(x), @"^cpu\d+$")))
        {
            string cdir = Path.Combine(cpu, "cache");
            if (!Directory.Exists(cdir)) continue;
            foreach (var idx in Directory.EnumerateDirectories(cdir, "index*"))
            {
                string shared = R(Path.Combine(idx, "shared_cpu_list")) ?? Path.GetFileName(cpu);
                int level = (int)(N(Path.Combine(idx, "level")) ?? 0);
                string type = R(Path.Combine(idx, "type")) ?? "Unified";
                string key = $"{level}:{type}:{shared}";
                if (!seen.Add(key)) continue;
                string sz = R(Path.Combine(idx, "size")) ?? "";
                long bytes = sz.EndsWith("K") ? long.Parse(sz[..^1]) * 1024 : sz.EndsWith("M") ? long.Parse(sz[..^1]) * 1048576 : long.TryParse(sz, out var b) ? b : 0;
                if (bytes <= 0) continue;
                if (!sizes.TryGetValue((level, type), out var l)) sizes[(level, type)] = l = new();
                l.Add(bytes);
            }
        }

        string Desc(int level, string type, string? label)
        {
            if (!sizes.TryGetValue((level, type), out var l) || l.Count == 0) return "";
            var groups = l.GroupBy(x => x).Select(g => (g.Count() > 1 ? g.Count() + " × " : "") + Fmt.Bytes(g.Key, 0).Replace(",0", ""));
            string s = string.Join(" + ", groups);
            if (l.Count > 1) s += $" ({Loc.T("total", "total")} {Fmt.Bytes(l.Sum(), 1)})";
            return label is null ? s : $"{s} {label}";
        }

        r.L1 = string.Join("; ", new[] { Desc(1, "Data", Loc.T("dados", "data")), Desc(1, "Instruction", Loc.T("instruções", "instruction")) }.Where(x => x != ""));
        r.L2 = Desc(2, "Unified", null);
        r.L3 = Desc(3, "Unified", null);
    }

    public static bool IsLaptop()
    {
        if (N("/sys/class/dmi/id/chassis_type") is double t && (int)t is 8 or 9 or 10 or 11 or 14 or 30 or 31 or 32) return true;
        return Directory.Exists("/sys/class/power_supply") && Directory.EnumerateDirectories("/sys/class/power_supply", "BAT*").Any()
               && !(N("/sys/class/dmi/id/chassis_type") is double c && (int)c is 3 or 4 or 5 or 6 or 7 or 13 or 15 or 16 or 17 or 23 or 24);
    }

    // ================================================================== placa-mãe / SO

    public static string? Dmi(string name) => R("/sys/class/dmi/id/" + name);

    // ================================================================== GPU

    public sealed record PciGpu(string Slot, string Dev, string? Card, uint VendorId, uint DeviceId);

    /// <summary>Placas de vídeo pelo barramento PCI (classe 0x03xx). A NVIDIA aparece mesmo sem driver carregado (sem "card" no DRM).</summary>
    public static List<PciGpu> PciGpus()
    {
        var res = new List<PciGpu>();
        try
        {
            foreach (var dev in Directory.EnumerateDirectories("/sys/bus/pci/devices").OrderBy(x => x, StringComparer.Ordinal))
            {
                string cls = (R(Path.Combine(dev, "class")) ?? "").ToLowerInvariant();
                if (!cls.StartsWith("0x03", StringComparison.Ordinal)) continue;
                if (!uint.TryParse((R(Path.Combine(dev, "vendor")) ?? "").Replace("0x", ""), NumberStyles.HexNumber, null, out var v)) continue;
                if (!uint.TryParse((R(Path.Combine(dev, "device")) ?? "").Replace("0x", ""), NumberStyles.HexNumber, null, out var d)) continue;
                string? card = null;
                string drm = Path.Combine(dev, "drm");
                if (Directory.Exists(drm))
                    card = Directory.EnumerateDirectories(drm).FirstOrDefault(x => Regex.IsMatch(Path.GetFileName(x), @"^card\d+$"));
                res.Add(new PciGpu(Path.GetFileName(dev), dev, card is null ? null : Path.Combine("/sys/class/drm", Path.GetFileName(card)), v, d));
            }
        }
        catch { }
        return res;
    }

    public static List<GpuInfo.Static> Gpus()
    {
        var list = new List<GpuInfo.Static>();
        var smi = Nvidia.Available ? NvidiaInfo() : new List<(string name, double memMB, string driver)>();
        int nvIdx = 0;
        foreach (var pci in PciGpus())
        {
            string dev = pci.Dev, slot = pci.Slot;
            uint v = pci.VendorId, d = pci.DeviceId;
            string vid = $"0x{v:x4}", did = $"0x{d:x4}";

            var g = new GpuInfo.Static { VendorId = v, DeviceId = d, PnpId = slot };
            g.Name = LspciName(slot) ?? $"GPU {vid}:{did}";
            if (v == 0x10DE && nvIdx < smi.Count)
            {
                g.Name = smi[nvIdx].name;
                g.DedicatedMB = smi[nvIdx].memMB;
                g.DriverVersion = smi[nvIdx].driver;
                nvIdx++;
            }
            if (N(Path.Combine(dev, "mem_info_vram_total")) is double vram) g.DedicatedMB = vram / 1048576;
            if (N(Path.Combine(dev, "mem_info_gtt_total")) is double gtt) g.SharedMB = gtt / 1048576;
            if (g.DriverVersion == "")
            {
                string drv = Path.GetFileName(Path.GetFullPath(Path.Combine(dev, "driver")));
                g.DriverVersion = R($"/sys/module/{drv}/version") ?? drv;
            }
            g.BoardMaker = GpuInfo.BoardVendor(uint.TryParse((R(Path.Combine(dev, "subsystem_vendor")) ?? "").Replace("0x", ""), NumberStyles.HexNumber, null, out var sv) ? sv : 0);
            g.Link = Pcie(dev);
            list.Add(g);
        }
        return list;
    }

    static Pcie.Link? Pcie(string dev)
    {
        static uint Gen(string? s)
        {
            var m = Regex.Match(s ?? "", @"([\d.]+)\s*GT/s");
            if (!m.Success) return 0;
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) switch { <= 2.5 => 1u, <= 5 => 2u, <= 8 => 3u, <= 16 => 4u, <= 32 => 5u, _ => 6u };
        }
        uint cur = Gen(R(Path.Combine(dev, "current_link_speed"))), max = Gen(R(Path.Combine(dev, "max_link_speed")));
        if (cur == 0 || max == 0) return null;
        uint cw = (uint)(N(Path.Combine(dev, "current_link_width")) ?? 0), mw = (uint)(N(Path.Combine(dev, "max_link_width")) ?? 0);
        return new Pcie.Link(cur, cw, max, mw);
    }

    static string? LspciName(string slot)
    {
        var o = Tool.Run("lspci", $"-s {slot} -mm");
        if (string.IsNullOrWhiteSpace(o)) return null;
        var parts = Regex.Matches(o, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        if (parts.Count < 3) return null;
        string vendor = parts[1].Replace("Corporation", "").Replace("[AMD/ATI]", "AMD").Replace("Advanced Micro Devices, Inc.", "AMD").Replace("Intel", "Intel").Trim();
        var br = Regex.Match(parts[2], @"\[([^\]]+)\]");
        string dev = br.Success ? br.Groups[1].Value : parts[2];
        return $"{vendor} {dev}".Replace("NVIDIA NVIDIA", "NVIDIA").Trim();
    }

    static List<(string name, double memMB, string driver)> NvidiaInfo()
    {
        var res = new List<(string, double, string)>();
        var o = Tool.Run("nvidia-smi", "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits");
        foreach (var line in (o ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = line.Split(',');
            if (c.Length >= 3) res.Add((c[0].Trim(), double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 0, c[2].Trim()));
        }
        return res;
    }

    // ================================================================== memória (SMBIOS)

    public static MemoryInfo.Data Memory()
    {
        var d = new MemoryInfo.Data();
        byte[]? table = null;
        try { table = File.ReadAllBytes("/sys/firmware/dmi/tables/DMI"); } catch { }
        if (table == null) return d;
        try
        {
            int pos = 0;
            while (pos + 4 <= table.Length)
            {
                byte type = table[pos], len = table[pos + 1];
                if (len < 4) break;
                int strStart = pos + len;
                var strings = new List<string>();
                int p = strStart;
                while (p < table.Length - 1 && !(table[p] == 0 && table[p + 1] == 0))
                {
                    int e = Array.IndexOf(table, (byte)0, p);
                    if (e < 0) break;
                    strings.Add(Encoding.ASCII.GetString(table, p, e - p));
                    p = e + 1;
                }
                int next = p + 2;
                string S(int idx) => idx >= 1 && idx <= strings.Count ? strings[idx - 1].Trim() : "";
                ushort W(int off) => (ushort)(table[pos + off] | table[pos + off + 1] << 8);

                if (type == 16 && len >= 0x0F) d.TotalSlots += W(0x0D);
                else if (type == 17 && len >= 0x15)
                {
                    int sizeRaw = W(0x0C);
                    long sizeMb = sizeRaw == 0x7FFF && len >= 0x20 ? BitConverter.ToUInt32(table, pos + 0x1C) : (sizeRaw & 0x8000) != 0 ? (sizeRaw & 0x7FFF) / 1024 : sizeRaw;
                    string slot = S(table[pos + 0x10]);
                    if (sizeRaw == 0)
                        d.Slots.Add((slot, null));
                    else
                    {
                        var m = new MemoryInfo.Module
                        {
                            Slot = slot,
                            Bank = S(table[pos + 0x11]),
                            SizeGB = sizeMb / 1024.0,
                            Speed = len >= 0x17 ? W(0x15) : 0,
                            ConfiguredSpeed = len >= 0x22 ? W(0x20) : 0,
                            VoltageMv = len >= 0x26 ? W(0x26) : 0,
                            Manufacturer = len >= 0x18 ? S(table[pos + 0x17]) : "",
                            Serial = len >= 0x19 ? S(table[pos + 0x18]) : "",
                            PartNumber = len >= 0x1B ? S(table[pos + 0x1A]) : "",
                            Type = MemType(table[pos + 0x12]),
                        };
                        d.Modules.Add(m);
                        d.Slots.Add((slot, m));
                    }
                }
                else if (type == 127) break;
                pos = next;
            }
        }
        catch (Exception ex) { Paths.LogError("LinuxInfo.smbios", ex); }
        if (d.TotalSlots < d.Modules.Count) d.TotalSlots = d.Modules.Count;
        return d;
    }

    static string MemType(byte t) => t switch { 0x18 => "DDR3", 0x1A => "DDR4", 0x22 => "DDR5", 0x1B => "LPDDR", 0x1D => "LPDDR3", 0x1E => "LPDDR4", 0x23 => "LPDDR5", 0x14 => "DDR", 0x13 => "DDR2", _ => "" };

    // ================================================================== discos

    public static List<StorageInfo.Disk> Disks()
    {
        var list = new List<StorageInfo.Disk>();
        var mounts = Mounts();
        int number = 0;
        foreach (var dir in Directory.EnumerateDirectories("/sys/block").OrderBy(x => x, StringComparer.Ordinal))
        {
            string dev = Path.GetFileName(dir);
            if (Regex.IsMatch(dev, @"^(loop|ram|zram|dm-|md|sr|fd|nbd)")) continue;
            ulong sectors = (ulong)(N(Path.Combine(dir, "size")) ?? 0);
            if (sectors == 0) continue;
            bool nvme = dev.StartsWith("nvme", StringComparison.Ordinal);
            bool rota = N(Path.Combine(dir, "queue", "rotational")) == 1;
            string devLink = "";
            try { devLink = Path.GetFullPath(Path.Combine(dir, "device")); } catch { }
            bool usb = devLink.Contains("/usb", StringComparison.Ordinal);
            string model = R(Path.Combine(dir, "device", "model")) ?? "";
            var d = new StorageInfo.Disk
            {
                Number = number++,
                Model = string.IsNullOrWhiteSpace(model) ? dev : model,
                Vendor = (R(Path.Combine(dir, "device", "vendor")) ?? "").Trim(),
                Serial = R(Path.Combine(dir, "device", "serial")) ?? "",
                Firmware = R(Path.Combine(dir, "device", "firmware_rev")) ?? R(Path.Combine(dir, "device", "rev")) ?? "",
                Size = sectors * 512,
                Kind = usb ? Loc.T("Disco USB", "USB drive") : nvme ? "NVMe SSD" : rota ? "HD" : "SATA SSD",
                Bus = nvme ? "NVMe" : usb ? "USB" : devLink.Contains("/ata", StringComparison.Ordinal) ? "SATA" : "",
                PnpId = "/dev/" + dev,
            };
            d.Interface = d.Bus;
            d.Trim = (N(Path.Combine(dir, "queue", "discard_max_bytes")) ?? 0) > 0;

            // temperatura via hwmon (nvme/drivetemp), sem precisar de root
            foreach (var h in new[] { Path.Combine(dir, "device"), Path.Combine(dir, "device", "hwmon"), Path.Combine(dir, "device", "nvme", dev) })
            {
                if (!Directory.Exists(h)) continue;
                foreach (var hw in Directory.EnumerateDirectories(h, "hwmon*"))
                    if (N(Path.Combine(hw, "temp1_input")) is double t && t > 0) { d.Temp = (int)(t / 1000); break; }
                if (d.Temp != null) break;
            }

            // partições montadas
            foreach (var part in Directory.EnumerateDirectories(dir, dev + "*"))
            {
                string pn = Path.GetFileName(part);
                foreach (var (mdev, mp, fs) in mounts.Where(m => Resolve(m.dev) == pn))
                {
                    try
                    {
                        var di = new DriveInfo(mp);
                        if (!di.IsReady) continue;
                        if (d.Partitions.Any(x => x.Letter == mp)) continue;
                        d.Partitions.Add(new StorageInfo.Part { Letter = mp, Label = di.VolumeLabel ?? "", FileSystem = fs, Size = di.TotalSize, Free = di.AvailableFreeSpace });
                    }
                    catch { }
                }
            }
            // raiz em LVM/LUKS: o dispositivo montado é dm-*, cujo "slave" é a partição
            foreach (var (mdev, mp, fs) in mounts)
            {
                string r = Resolve(mdev);
                if (!r.StartsWith("dm-", StringComparison.Ordinal)) continue;
                if (!Slaves(r).Any(s => s.StartsWith(dev, StringComparison.Ordinal))) continue;
                try
                {
                    var di = new DriveInfo(mp);
                    if (di.IsReady && d.Partitions.All(x => x.Letter != mp))
                        d.Partitions.Add(new StorageInfo.Part { Letter = mp, FileSystem = fs, Size = di.TotalSize, Free = di.AvailableFreeSpace });
                }
                catch { }
            }
            Smart(d, dev, nvme);
            list.Add(d);
        }
        return list;
    }

    static List<(string dev, string mp, string fs)> Mounts()
    {
        var res = new List<(string, string, string)>();
        try
        {
            foreach (var l in File.ReadLines("/proc/self/mounts"))
            {
                var p = l.Split(' ');
                if (p.Length < 3 || !p[0].StartsWith("/dev/", StringComparison.Ordinal)) continue;
                string mp = p[1].Replace("\\040", " ");
                if (mp.StartsWith("/snap", StringComparison.Ordinal) || mp.StartsWith("/var/lib/docker", StringComparison.Ordinal)) continue;
                res.Add((p[0], mp, p[2]));
            }
        }
        catch { }
        return res;
    }

    /// <summary>/dev/mapper/xyz e /dev/sda1 → nome real em /sys/class/block (ex.: dm-0, sda1).</summary>
    static string Resolve(string dev)
    {
        try
        {
            var fi = new FileInfo(dev);
            string target = fi.LinkTarget != null ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dev)!, fi.LinkTarget)) : dev;
            return Path.GetFileName(target);
        }
        catch { return Path.GetFileName(dev); }
    }

    static IEnumerable<string> Slaves(string dm)
    {
        string dir = $"/sys/class/block/{dm}/slaves";
        if (!Directory.Exists(dir)) yield break;
        foreach (var s in Directory.EnumerateDirectories(dir))
        {
            string n = Path.GetFileName(s);
            yield return n;
            if (n.StartsWith("dm-", StringComparison.Ordinal)) foreach (var x in Slaves(n)) yield return x;
        }
    }

    /// <summary>SMART via smartctl (precisa de root e do pacote smartmontools). Sem isso a saúde fica "desconhecida".</summary>
    static void Smart(StorageInfo.Disk d, string dev, bool nvme)
    {
        if (!Tool.Exists("smartctl")) return;
        var json = Tool.Run("smartctl", $"-a -j /dev/{dev}", 6000);
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("model_name", out var mn) && mn.GetString() is { Length: > 0 } mname) d.Model = mname;
            if (root.TryGetProperty("serial_number", out var sn)) d.Serial = sn.GetString() ?? d.Serial;
            if (root.TryGetProperty("firmware_version", out var fw)) d.Firmware = fw.GetString() ?? d.Firmware;
            if (root.TryGetProperty("smart_status", out var ss) && ss.TryGetProperty("passed", out var pass))
            {
                d.SmartAvailable = true;
                d.Status = pass.GetBoolean() ? Health.Good : Health.Critical;
            }
            if (root.TryGetProperty("temperature", out var tp) && tp.TryGetProperty("current", out var tc)) d.Temp = tc.GetInt32();
            if (root.TryGetProperty("power_on_time", out var po) && po.TryGetProperty("hours", out var ph)) d.PowerOnHours = ph.GetUInt64();
            if (root.TryGetProperty("power_cycle_count", out var pc)) d.PowerOnCount = pc.GetUInt64();

            if (nvme && root.TryGetProperty("nvme_smart_health_information_log", out var log))
            {
                d.SmartAvailable = true;
                if (log.TryGetProperty("percentage_used", out var pu)) d.Life = Math.Clamp(100 - pu.GetInt32(), 0, 100);
                if (log.TryGetProperty("data_units_written", out var dw)) d.HostWritesGB = (ulong)(dw.GetDouble() * 512000 / 1e9);
                if (log.TryGetProperty("data_units_read", out var dr)) d.HostReadsGB = (ulong)(dr.GetDouble() * 512000 / 1e9);
                if (log.TryGetProperty("critical_warning", out var cw) && cw.GetInt32() != 0) d.Status = Health.Warning;
                if (log.TryGetProperty("temperature", out var lt)) d.Temp = lt.GetInt32();
            }
            else if (root.TryGetProperty("ata_smart_attributes", out var attrs) && attrs.TryGetProperty("table", out var table))
            {
                d.SmartAvailable = true;
                foreach (var a in table.EnumerateArray())
                {
                    byte id = (byte)a.GetProperty("id").GetInt32();
                    d.Attributes.Add(new StorageInfo.Attr
                    {
                        Id = id,
                        Name = a.GetProperty("name").GetString() ?? "",
                        Current = a.GetProperty("value").GetInt32(),
                        Worst = a.GetProperty("worst").GetInt32(),
                        Threshold = a.GetProperty("thresh").GetInt32(),
                        Raw = a.GetProperty("raw").GetProperty("value").GetUInt64(),
                    });
                    if (id is 177 or 231 or 233) d.Life ??= Math.Clamp(a.GetProperty("value").GetInt32(), 0, 100);
                }
            }
        }
        catch (Exception ex) { Paths.LogError("LinuxInfo.smart", ex); }
    }
}
