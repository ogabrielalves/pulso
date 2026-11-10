using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Microsoft.Win32;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Info;

/// <summary>Informações estáticas do processador (CPUID + Windows + tabela de codinomes).</summary>
public static class CpuInfo
{
    public sealed class Data
    {
        public string Vendor = "", Brand = "", Codename = "", Process = "", Socket = "", Architecture = "";
        public int Family, Model, Stepping;
        public int Cores, Threads, PCores, ECores;
        public double BaseMhz, BoostMhz;
        public string L1 = "", L2 = "", L3 = "";
        public List<string> Instructions = new();
        public bool Virtualization;
        public double? TdpW;
    }

    static Data? _cache;

    public static Data Get() => _cache ??= Read();

    public static double BaseClockMhz() => Get().BaseMhz;

    static (uint a, uint b, uint c, uint d) Id(uint leaf, uint sub = 0)
    {
        if (!X86Base.IsSupported) return default;
        var (a, b, c, d) = X86Base.CpuId((int)leaf, (int)sub);
        return ((uint)a, (uint)b, (uint)c, (uint)d);
    }

    static bool Bit(uint v, int b) => (v >> b & 1) != 0;

    static Data Read()
    {
        var r = new Data();
        try
        {
            var l0 = Id(0);
            r.Vendor = Encoding.ASCII.GetString(BitConverter.GetBytes(l0.b).Concat(BitConverter.GetBytes(l0.d)).Concat(BitConverter.GetBytes(l0.c)).ToArray());
            uint maxLeaf = l0.a;
            uint maxExt = Id(0x80000000).a;

            var brand = new StringBuilder();
            if (maxExt >= 0x80000004)
                for (uint i = 0x80000002; i <= 0x80000004; i++)
                {
                    var x = Id(i);
                    foreach (var v in new[] { x.a, x.b, x.c, x.d }) brand.Append(Encoding.ASCII.GetString(BitConverter.GetBytes(v)));
                }
            r.Brand = System.Text.RegularExpressions.Regex.Replace(brand.ToString().Trim('\0', ' '), @"\s+", " ");

            var l1 = Id(1);
            int fam = (int)(l1.a >> 8 & 0xF), mod = (int)(l1.a >> 4 & 0xF);
            r.Stepping = (int)(l1.a & 0xF);
            r.Family = fam == 0xF ? fam + (int)(l1.a >> 20 & 0xFF) : fam;
            r.Model = fam is 6 or 0xF ? ((int)(l1.a >> 16 & 0xF) << 4) + mod : mod;

            var ins = r.Instructions;
            void A(bool on, string n) { if (on) ins.Add(n); }
            A(Bit(l1.d, 23), "MMX"); A(Bit(l1.d, 25), "SSE"); A(Bit(l1.d, 26), "SSE2"); A(Bit(l1.c, 0), "SSE3");
            A(Bit(l1.c, 9), "SSSE3"); A(Bit(l1.c, 19), "SSE4.1"); A(Bit(l1.c, 20), "SSE4.2");
            var e1 = maxExt >= 0x80000001 ? Id(0x80000001) : default;
            A(Bit(e1.c, 6), "SSE4A"); A(Bit(e1.d, 29), "x86-64"); A(Bit(l1.c, 25), "AES-NI"); A(Bit(l1.c, 1), "CLMUL");
            A(Bit(l1.c, 28), "AVX"); A(Bit(l1.c, 12), "FMA3"); A(Bit(e1.c, 16), "FMA4"); A(Bit(l1.c, 29), "F16C");
            if (maxLeaf >= 7)
            {
                var l7 = Id(7, 0);
                var l71 = Id(7, 1);
                A(Bit(l7.b, 5), "AVX2"); A(Bit(l71.a, 4), "AVX-VNNI");
                A(Bit(l7.b, 16), "AVX-512F"); A(Bit(l7.b, 17), "AVX-512DQ"); A(Bit(l7.b, 28), "AVX-512CD");
                A(Bit(l7.b, 30), "AVX-512BW"); A(Bit(l7.b, 31), "AVX-512VL"); A(Bit(l7.c, 11), "AVX-512VNNI");
                A(Bit(l7.c, 1), "AVX-512VBMI"); A(Bit(l71.a, 5), "AVX-512BF16"); A(Bit(l7.d, 23), "AVX-512FP16");
                A(Bit(l7.d, 24), "AMX"); A(Bit(l7.b, 3), "BMI1"); A(Bit(l7.b, 8), "BMI2"); A(Bit(l7.b, 29), "SHA");
                A(Bit(l7.c, 9), "VAES"); A(Bit(l7.c, 8), "GFNI"); A(Bit(l7.b, 18), "RDSEED");
            }
            A(Bit(l1.c, 30), "RDRAND"); A(Bit(l1.c, 23), "POPCNT"); A(Bit(e1.d, 20), "NX");
            r.Virtualization = Bit(l1.c, 5) || Bit(e1.c, 2);
            A(Bit(l1.c, 5), "VT-x"); A(Bit(e1.c, 2), "AMD-V");

            // Intel: folha 0x16 traz clock base e máximo em MHz.
            if (maxLeaf >= 0x16)
            {
                var l16 = Id(0x16);
                if (l16.a > 0) r.BaseMhz = l16.a;
                if (l16.b > 0) r.BoostMhz = l16.b;
            }
        }
        catch (Exception ex) { Paths.LogError("CpuInfo.cpuid", ex); }

        r.Architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86-64 (64 bits)",
            Architecture.Arm64 => "ARM64",
            Architecture.X86 => "x86 (32 bits)",
            var a => a.ToString(),
        };

        if (OperatingSystem.IsLinux())
        {
            try { Platform.Linux.LinuxInfo.FillCpu(r); } catch (Exception ex) { Paths.LogError("CpuInfo.linux", ex); }
            var lm = System.Text.RegularExpressions.Regex.Match(r.Brand, @"@\s*([\d.]+)\s*GHz", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (lm.Success && r.BaseMhz <= 0 && double.TryParse(lm.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lg)) r.BaseMhz = lg * 1000;
            ApplyTable(r);
            return r;
        }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, MaxClockSpeed, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            int cores = 0, threads = 0;
            foreach (ManagementObject o in s.Get())
            {
                cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                threads += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                if (r.BaseMhz <= 0) r.BaseMhz = Convert.ToDouble(o["MaxClockSpeed"] ?? 0);
                string sock = (o["SocketDesignation"] as string ?? "").Trim();
                if (LooksLikeSocket(sock)) r.Socket = sock;
                if (string.IsNullOrEmpty(r.Brand)) r.Brand = (o["Name"] as string ?? "").Trim();
            }
            r.Cores = cores; r.Threads = threads;
        }
        catch (Exception ex) { Paths.LogError("CpuInfo.wmi", ex); }

        // Clock base escrito no nome ("@ 2.40GHz") é mais confiável que o WMI em alguns notebooks.
        var m = System.Text.RegularExpressions.Regex.Match(r.Brand, @"@\s*([\d.]+)\s*GHz", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ghz))
            r.BaseMhz = ghz * 1000;

        if (r.Threads == 0) r.Threads = Environment.ProcessorCount;
        try
        {
            var coreList = Native.GetCores();
            if (coreList.Count > 0)
            {
                r.Cores = coreList.Count;
                var classes = coreList.Select(c => c.efficiencyClass).Distinct().ToList();
                if (classes.Count > 1)
                {
                    byte top = classes.Max();
                    r.PCores = coreList.Count(c => c.efficiencyClass == top);
                    r.ECores = coreList.Count - r.PCores;
                }
            }
        }
        catch { }

        ReadCaches(r);
        ApplyTable(r);
        return r;
    }

    static bool LooksLikeSocket(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, @"^(FC)?LGA|^AM\d|^sTR|^sWRX|^SP\d|^FM\d|^BGA|^FP\d|^Socket", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static void ReadCaches(Data r)
    {
        var sizes = new Dictionary<(int level, int type), List<long>>();
        uint len = 0;
        Native.GetLogicalProcessorInformationEx(2 /*RelationCache*/, 0, ref len);
        if (len == 0) return;
        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(2, buf, ref len)) return;
            long off = 0;
            while (off < len)
            {
                nint p = buf + (nint)off;
                int size = Marshal.ReadInt32(p, 4);
                int level = Marshal.ReadByte(p, 8);
                long cacheSize = (uint)Marshal.ReadInt32(p, 12);
                int type = Marshal.ReadInt32(p, 16);
                var key = (level, type);
                if (!sizes.TryGetValue(key, out var l)) sizes[key] = l = new List<long>();
                l.Add(cacheSize);
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }

        string Desc(int level, int type, string? label)
        {
            if (!sizes.TryGetValue((level, type), out var l) || l.Count == 0) return "";
            var groups = l.GroupBy(x => x).Select(g => (g.Count() > 1 ? g.Count() + " × " : "") + Fmt.Bytes(g.Key, 0).Replace(",0", ""));
            string s = string.Join(" + ", groups);
            long total = l.Sum();
            if (l.Count > 1) s += $" ({Loc.T("total", "total")} {Fmt.Bytes(total, 1)})";
            return label is null ? s : $"{s} {label}";
        }

        r.L1 = string.Join("; ", new[] { Desc(1, 2, Loc.T("dados", "data")), Desc(1, 1, Loc.T("instruções", "instruction")), Desc(1, 0, null) }.Where(x => x != ""));
        r.L2 = Desc(2, 0, null);
        r.L3 = Desc(3, 0, null);
    }

    // ----- tabela de codinomes (família/modelo do CPUID)
    record Cn(bool Intel, int Family, int[] Models, string Name, string Process, string? Socket = null);

    static readonly Cn[] Table =
    {
        new(true, 6, new[] { 0x2A }, "Sandy Bridge", "32 nm", "LGA1155"),
        new(true, 6, new[] { 0x2D }, "Sandy Bridge-E", "32 nm", "LGA2011"),
        new(true, 6, new[] { 0x3A }, "Ivy Bridge", "22 nm", "LGA1155"),
        new(true, 6, new[] { 0x3E }, "Ivy Bridge-E", "22 nm", "LGA2011"),
        new(true, 6, new[] { 0x3C, 0x45, 0x46 }, "Haswell", "22 nm", "LGA1150"),
        new(true, 6, new[] { 0x3F }, "Haswell-E", "22 nm", "LGA2011-3"),
        new(true, 6, new[] { 0x3D, 0x47 }, "Broadwell", "14 nm", "LGA1150"),
        new(true, 6, new[] { 0x4F, 0x56 }, "Broadwell-E", "14 nm", "LGA2011-3"),
        new(true, 6, new[] { 0x4E, 0x5E }, "Skylake", "14 nm", "LGA1151"),
        new(true, 6, new[] { 0x55 }, "Skylake-X / Cascade Lake", "14 nm", "LGA2066"),
        new(true, 6, new[] { 0x8E }, "Kaby Lake / Whiskey Lake / Amber Lake", "14 nm"),
        new(true, 6, new[] { 0x9E }, "Kaby Lake / Coffee Lake", "14 nm", "LGA1151"),
        new(true, 6, new[] { 0xA5, 0xA6 }, "Comet Lake", "14 nm", "LGA1200"),
        new(true, 6, new[] { 0x66 }, "Cannon Lake", "10 nm"),
        new(true, 6, new[] { 0x7D, 0x7E }, "Ice Lake", "10 nm"),
        new(true, 6, new[] { 0x6A, 0x6C }, "Ice Lake-SP", "10 nm", "LGA4189"),
        new(true, 6, new[] { 0x8C, 0x8D }, "Tiger Lake", "10 nm SuperFin"),
        new(true, 6, new[] { 0xA7 }, "Rocket Lake", "14 nm", "LGA1200"),
        new(true, 6, new[] { 0x97, 0x9A }, "Alder Lake", "Intel 7 (10 nm ESF)", "LGA1700"),
        new(true, 6, new[] { 0xBE }, "Alder Lake-N", "Intel 7 (10 nm ESF)"),
        new(true, 6, new[] { 0xB7, 0xBA, 0xBF }, "Raptor Lake", "Intel 7 (10 nm ESF)", "LGA1700"),
        new(true, 6, new[] { 0xAA, 0xAC }, "Meteor Lake", "Intel 4"),
        new(true, 6, new[] { 0xBD }, "Lunar Lake", "TSMC N3B"),
        new(true, 6, new[] { 0xC5, 0xC6 }, "Arrow Lake", "TSMC N3B", "LGA1851"),
        new(true, 6, new[] { 0xCC }, "Panther Lake", "Intel 18A"),
        new(true, 6, new[] { 0x8F }, "Sapphire Rapids", "Intel 7", "LGA4677"),
        new(true, 6, new[] { 0xCF }, "Emerald Rapids", "Intel 7", "LGA4677"),
        new(true, 6, new[] { 0x5C, 0x5F }, "Goldmont", "14 nm"),
        new(true, 6, new[] { 0x7A }, "Gemini Lake", "14 nm"),
        new(true, 6, new[] { 0x96, 0x9C }, "Elkhart / Jasper Lake", "10 nm"),

        new(false, 0x15, new[] { 0x01, 0x02, 0x10, 0x13, 0x30, 0x38, 0x60, 0x65, 0x70 }, "Bulldozer / Piledriver / Steamroller / Excavator", "32/28 nm"),
        new(false, 0x17, new[] { 0x01 }, "Summit Ridge (Zen)", "14 nm", "AM4"),
        new(false, 0x17, new[] { 0x11 }, "Raven Ridge (Zen)", "14 nm", "AM4"),
        new(false, 0x17, new[] { 0x08 }, "Pinnacle Ridge (Zen+)", "12 nm", "AM4"),
        new(false, 0x17, new[] { 0x18 }, "Picasso (Zen+)", "12 nm", "AM4"),
        new(false, 0x17, new[] { 0x20 }, "Dali (Zen)", "14 nm"),
        new(false, 0x17, new[] { 0x31 }, "Castle Peak / Rome (Zen 2)", "7 nm", "sTRX4"),
        new(false, 0x17, new[] { 0x60 }, "Renoir (Zen 2)", "7 nm", "AM4"),
        new(false, 0x17, new[] { 0x68 }, "Lucienne (Zen 2)", "7 nm"),
        new(false, 0x17, new[] { 0x71 }, "Matisse (Zen 2)", "7 nm", "AM4"),
        new(false, 0x17, new[] { 0x90, 0x91 }, "Van Gogh (Zen 2)", "7 nm"),
        new(false, 0x17, new[] { 0xA0 }, "Mendocino (Zen 2)", "6 nm"),
        new(false, 0x19, new[] { 0x01 }, "Milan (Zen 3)", "7 nm", "SP3"),
        new(false, 0x19, new[] { 0x08 }, "Chagall (Zen 3)", "7 nm", "sWRX8"),
        new(false, 0x19, new[] { 0x21 }, "Vermeer (Zen 3)", "7 nm", "AM4"),
        new(false, 0x19, new[] { 0x50 }, "Cezanne / Barcelo (Zen 3)", "7 nm", "AM4"),
        new(false, 0x19, new[] { 0x40, 0x44 }, "Rembrandt (Zen 3+)", "6 nm"),
        new(false, 0x19, new[] { 0x10, 0x11 }, "Genoa (Zen 4)", "5 nm", "SP5"),
        new(false, 0x19, new[] { 0x18 }, "Storm Peak (Zen 4)", "5 nm", "sTR5"),
        new(false, 0x19, new[] { 0x61 }, "Raphael (Zen 4)", "5 nm", "AM5"),
        new(false, 0x19, new[] { 0x74, 0x78 }, "Phoenix (Zen 4)", "4 nm"),
        new(false, 0x19, new[] { 0x75 }, "Hawk Point (Zen 4)", "4 nm"),
        new(false, 0x19, new[] { 0x7C }, "Phoenix 2 (Zen 4/4c)", "4 nm"),
        new(false, 0x19, new[] { 0xA0 }, "Bergamo (Zen 4c)", "5 nm", "SP5"),
        new(false, 0x1A, new[] { 0x02, 0x11 }, "Turin (Zen 5)", "4/3 nm", "SP5"),
        new(false, 0x1A, new[] { 0x24 }, "Strix Point (Zen 5)", "4 nm"),
        new(false, 0x1A, new[] { 0x44 }, "Granite Ridge (Zen 5)", "4 nm", "AM5"),
        new(false, 0x1A, new[] { 0x60, 0x68 }, "Krackan Point (Zen 5)", "4 nm"),
        new(false, 0x1A, new[] { 0x70 }, "Strix Halo (Zen 5)", "4 nm"),
    };

    static void ApplyTable(Data r)
    {
        bool intel = r.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        var cn = Table.FirstOrDefault(t => t.Intel == intel && t.Family == r.Family && t.Models.Contains(r.Model));
        bool mobile = IsMobileModel(r.Brand) || IsLaptop();
        if (cn != null)
        {
            r.Codename = cn.Name;
            r.Process = cn.Process;
            if (string.IsNullOrEmpty(r.Socket))
                r.Socket = mobile ? Loc.T("BGA (soldado na placa)", "BGA (soldered)") : (cn.Socket ?? "");
        }
        else if (string.IsNullOrEmpty(r.Socket) && mobile)
            r.Socket = Loc.T("BGA (soldado na placa)", "BGA (soldered)");
        // Família Coffee Lake (modelo 0x9E com stepping >= 10)
        if (intel && r.Model == 0x9E && r.Stepping >= 10) r.Codename = "Coffee Lake";
    }

    static bool IsMobileModel(string brand) =>
        System.Text.RegularExpressions.Regex.IsMatch(brand, @"\d{4,5}(U|H|HS|HX|HK|G\d|P|Y|M)\b|Core\(TM\) Ultra \d \d{3}[HUV]|Ryzen.*\d{4}(U|H|HS|HX)\b|Ryzen AI", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static bool? _laptop;
    public static bool IsLaptop()
    {
        if (_laptop is bool b) return b;
        if (OperatingSystem.IsLinux()) return (_laptop = Platform.Linux.LinuxInfo.IsLaptop()).Value;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject o in s.Get())
                if (o["ChassisTypes"] is ushort[] types && types.Any(t => t is 8 or 9 or 10 or 11 or 14 or 30 or 31 or 32))
                    return (_laptop = true).Value;
        }
        catch { }
        return (_laptop = false).Value;
    }

    /// <summary>TDP do Intel via MSR (precisa do driver PawnIO); executado na thread de sensores.</summary>
    public static Task<double?> ReadIntelTdpAsync()
    {
        if (!OperatingSystem.IsWindows() || !SensorHub.Instance.CpuIsIntel || !SensorHub.Instance.PawnIoInstalled) return Task.FromResult<double?>(null);
        return ReadIntelTdpWindowsAsync();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static async Task<double?> ReadIntelTdpWindowsAsync()
    {
        return await SensorHub.Instance.InvokeAsync<double?>(_ =>
        {
            try
            {
                var msr = new LibreHardwareMonitor.PawnIo.IntelMsr();
                try
                {
                    if (!msr.ReadMsr(0x606, out ulong unit) || !msr.ReadMsr(0x614, out ulong info)) return null;
                    double pu = 1.0 / (1 << (int)(unit & 0xF));
                    double tdp = (info & 0x7FFF) * pu;
                    return tdp is > 1 and < 1000 ? tdp : null;
                }
                finally { msr.Close(); }
            }
            catch { return null; }
        });
    }
}
