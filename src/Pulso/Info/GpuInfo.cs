using System.Management;
using System.Text.RegularExpressions;
using Pulso.Core;
using Pulso.Hardware;
using Vortice.DXGI;

namespace Pulso.Info;

public static class GpuInfo
{
    public sealed class Static
    {
        public string Name = "", Vendor = "", BoardMaker = "", PnpId = "", DriverVersion = "", DriverDate = "", Chip = "", MemType = "", Arch = "", Vbios = "";
        public uint VendorId, DeviceId;
        public double DedicatedMB, SharedMB;
        public int? BusWidth;
        public bool Integrated;
        public double? MaxCoreClock, MaxMemClock, TdpW;
        public uint? Cores;
        public Pcie.Link? Link;
        public Nvml.Device? Nvml;
    }

    static List<Static>? _cache;

    public static List<Static> Get() => _cache ??= Read();

    static List<Static> Read()
    {
        var list = new List<Static>();
        if (OperatingSystem.IsLinux())
        {
            try { list = Platform.Linux.LinuxInfo.Gpus(); } catch (Exception ex) { Paths.LogError("GpuInfo.linux", ex); }
            foreach (var g in list)
            {
                g.Vendor = g.VendorId switch { 0x10DE => "NVIDIA", 0x1002 or 0x1022 => "AMD", 0x8086 => "Intel", _ => $"0x{g.VendorId:X4}" };
                g.Integrated = g.VendorId == 0x8086 ? !Regex.IsMatch(g.Name, @"Arc.*\b[AB]\d{3}") : g.DedicatedMB < 1024 && g.VendorId != 0x10DE;
                var spec = Spec(g.Name);
                if (spec != null) { g.Chip = spec.Value.chip; g.MemType = spec.Value.mem; g.BusWidth = spec.Value.bus; }
                if (g.Integrated) g.MemType = Loc.T("Compartilhada com a RAM do sistema", "Shared with system RAM");
            }
            return list.OrderBy(g => g.Integrated).ToList();
        }
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++)
            {
                using (a)
                {
                    var d = a.Description1;
                    if ((d.Flags & AdapterFlags.Software) != 0 || d.VendorId == 0x1414) continue; // adaptador básico/remoto da Microsoft
                    if (list.Any(x => x.VendorId == d.VendorId && x.DeviceId == d.DeviceId && x.DedicatedMB == d.DedicatedVideoMemory / 1048576.0)) continue;
                    list.Add(new Static
                    {
                        Name = d.Description.Trim(),
                        VendorId = d.VendorId,
                        DeviceId = d.DeviceId,
                        DedicatedMB = d.DedicatedVideoMemory / 1048576.0,
                        SharedMB = d.SharedSystemMemory / 1048576.0,
                    });
                }
            }
        }
        catch (Exception ex) { Paths.LogError("GpuInfo.dxgi", ex); }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, DriverVersion, DriverDate FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                string pnp = o["PNPDeviceID"] as string ?? "";
                var ven = Regex.Match(pnp, @"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                var dev = Regex.Match(pnp, @"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                if (!ven.Success || !dev.Success) continue;
                uint v = Convert.ToUInt32(ven.Groups[1].Value, 16), dv = Convert.ToUInt32(dev.Groups[1].Value, 16);
                var g = list.FirstOrDefault(x => x.VendorId == v && x.DeviceId == dv && x.PnpId == "");
                if (g == null) continue;
                g.PnpId = pnp;
                g.DriverVersion = o["DriverVersion"] as string ?? "";
                if (o["DriverDate"] is string dd && dd.Length >= 8)
                    try { g.DriverDate = ManagementDateTimeConverter.ToDateTime(dd).ToString("d", Fmt.Culture); } catch { }
                var sub = Regex.Match(pnp, @"SUBSYS_[0-9A-F]{4}([0-9A-F]{4})", RegexOptions.IgnoreCase);
                if (sub.Success) g.BoardMaker = BoardVendor(Convert.ToUInt32(sub.Groups[1].Value, 16));
            }
        }
        catch (Exception ex) { Paths.LogError("GpuInfo.wmi", ex); }

        var nv = Hardware.Nvml.Devices();
        foreach (var g in list)
        {
            g.Vendor = g.VendorId switch { 0x10DE => "NVIDIA", 0x1002 or 0x1022 => "AMD", 0x8086 => "Intel", 0x5143 => "Qualcomm", _ => $"0x{g.VendorId:X4}" };
            g.Integrated = g.VendorId == 0x8086 ? !Regex.IsMatch(g.Name, @"Arc.*\b[AB]\d{3}") : g.DedicatedMB < 1024 && g.VendorId != 0x10DE;
            if (!string.IsNullOrEmpty(g.PnpId)) g.Link = Pcie.Read(g.PnpId);
            var spec = Spec(g.Name);
            if (spec != null) { g.Chip = spec.Value.chip; g.MemType = spec.Value.mem; g.BusWidth = spec.Value.bus; }
            if (g.Integrated) g.MemType = Loc.T("Compartilhada com a RAM do sistema", "Shared with system RAM");

            if (g.VendorId == 0x10DE && nv.Count > 0)
            {
                var d = nv.FirstOrDefault(x => Normal(x.Name) == Normal(g.Name)) ?? nv[0];
                nv.Remove(d);
                g.Nvml = d;
                g.MaxCoreClock = Hardware.Nvml.MaxClock(d, 0);
                g.MaxMemClock = Hardware.Nvml.MaxClock(d, 2);
                if (Hardware.Nvml.BusWidth(d) is uint bw && bw > 0) g.BusWidth = (int)bw;
                if (Hardware.Nvml.DefaultPowerLimitMw(d) is uint mw && mw > 0) g.TdpW = mw / 1000.0;
                g.Cores = Hardware.Nvml.Cores(d);
                if (Hardware.Nvml.Architecture(d) is uint ar) g.Arch = Hardware.Nvml.ArchName(ar);
                g.Vbios = Hardware.Nvml.Vbios(d) ?? "";
                if (Hardware.Nvml.DriverVersion() is string dv && dv.Length > 0) g.DriverVersion = $"{dv} ({g.DriverVersion})";
                if (Hardware.Nvml.PcieCurGen(d) is uint cg && Hardware.Nvml.PcieMaxGen(d) is uint mg)
                    g.Link = new Pcie.Link(cg, Hardware.Nvml.PcieCurWidth(d), mg, Hardware.Nvml.PcieMaxWidth(d));
            }
        }

        // Dedicadas primeiro, mesma ordem do monitor de sensores.
        return list.OrderBy(g => g.Integrated).ToList();
    }

    static string Normal(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "").Replace("nvidia", "");

    public static string BoardVendor(uint id) => id switch
    {
        0x1043 => "ASUS", 0x1462 => "MSI", 0x1458 => "Gigabyte", 0x3842 => "EVGA", 0x19DA => "Zotac",
        0x1569 => "Palit", 0x10B0 => "Gainward", 0x1DA2 => "Sapphire", 0x148C => "PowerColor", 0x1682 => "XFX",
        0x196E => "PNY", 0x1B4C => "Galax / KFA2", 0x7377 => "Colorful", 0x1849 => "ASRock", 0x10DE => "NVIDIA (Founders Edition)",
        0x1002 => Loc.T("AMD (referência)", "AMD (reference)"), 0x8086 => "Intel", 0x1028 => "Dell", 0x103C => "HP", 0x17AA => "Lenovo",
        0x1025 => "Acer", 0x144D => "Samsung", 0x1558 => "Clevo", 0x1D05 => "Tongfang", 0x152D => "Quanta", 0x1179 => "Toshiba",
        0x104D => "Sony", 0x1BB1 => "Seagate", 0x1A58 => "Razer", 0x8A86 => "Inno3D", 0x1D17 => "Zhaoxin", 0x1414 => "Microsoft",
        0x16F3 => "Jetway", 0x1A03 => "ASPEED", 0x1545 => "VisionTek", 0x174B => "Sapphire", 0x1787 => "HIS", 0x1EAE => "XFX",
        0x1D7B => "Maxsun", 0x1F5C => "Yeston", 0x1F64 => "Peladn", 0x1E49 => "Sparkle",
        _ => $"0x{id:X4}",
    };

    /// <summary>Chip, tipo de memória e largura do barramento das placas mais comuns.</summary>
    static (string chip, string mem, int bus)? Spec(string name)
    {
        string n = name.ToUpperInvariant();
        bool laptop = n.Contains("LAPTOP") || n.Contains("MOBILE") || Regex.IsMatch(n, @"\b\d{4}M\b");
        (string pattern, string chip, string mem, int bus, bool? lap)[] t =
        {
            (@"RTX 5090", "GB202", "GDDR7", 512, false), (@"RTX 5080", "GB203", "GDDR7", 256, false), (@"RTX 5070 TI", "GB203", "GDDR7", 256, false),
            (@"RTX 5070", "GB205", "GDDR7", 192, false), (@"RTX 5060 TI", "GB206", "GDDR7", 128, false), (@"RTX 5060", "GB206", "GDDR7", 128, false), (@"RTX 5050", "GB207", "GDDR6", 128, false),
            (@"RTX 4090", "AD103", "GDDR6", 256, true), (@"RTX 4080", "AD104", "GDDR6", 192, true), (@"RTX 4070", "AD106", "GDDR6", 128, true), (@"RTX 4060", "AD107", "GDDR6", 128, true), (@"RTX 4050", "AD107", "GDDR6", 96, true),
            (@"RTX 4090", "AD102", "GDDR6X", 384, false), (@"RTX 4080", "AD103", "GDDR6X", 256, false), (@"RTX 4070 TI SUPER", "AD103", "GDDR6X", 256, false),
            (@"RTX 4070 TI", "AD104", "GDDR6X", 192, false), (@"RTX 4070", "AD104", "GDDR6X", 192, false), (@"RTX 4060 TI", "AD106", "GDDR6", 128, false), (@"RTX 4060", "AD107", "GDDR6", 128, false),
            (@"RTX 3080 TI", "GA103", "GDDR6", 256, true), (@"RTX 3080", "GA104", "GDDR6", 256, true), (@"RTX 3070 TI", "GA104", "GDDR6", 256, true), (@"RTX 3070", "GA104", "GDDR6", 256, true), (@"RTX 3060", "GA106", "GDDR6", 192, true), (@"RTX 3050", "GA107", "GDDR6", 128, true),
            (@"RTX 3090", "GA102", "GDDR6X", 384, false), (@"RTX 3080 TI", "GA102", "GDDR6X", 384, false), (@"RTX 3080", "GA102", "GDDR6X", 320, false), (@"RTX 3070 TI", "GA104", "GDDR6X", 256, false),
            (@"RTX 3070", "GA104", "GDDR6", 256, false), (@"RTX 3060 TI", "GA104", "GDDR6", 256, false), (@"RTX 3060", "GA106", "GDDR6", 192, false), (@"RTX 3050", "GA106", "GDDR6", 128, false),
            (@"RTX 2080 TI", "TU102", "GDDR6", 352, null), (@"RTX 2080", "TU104", "GDDR6", 256, null), (@"RTX 2070", "TU106", "GDDR6", 256, null), (@"RTX 2060", "TU106", "GDDR6", 192, null),
            (@"GTX 1660 TI", "TU116", "GDDR6", 192, null), (@"GTX 1660 SUPER", "TU116", "GDDR6", 192, null), (@"GTX 1660", "TU116", "GDDR5", 192, null), (@"GTX 1650 SUPER", "TU116", "GDDR6", 128, null), (@"GTX 1650", "TU117", "GDDR5/GDDR6", 128, null),
            (@"GTX 1080 TI", "GP102", "GDDR5X", 352, null), (@"GTX 1080", "GP104", "GDDR5X", 256, null), (@"GTX 1070", "GP104", "GDDR5", 256, null), (@"GTX 1060", "GP106", "GDDR5", 192, null), (@"GTX 1050", "GP107", "GDDR5", 128, null), (@"GT 1030", "GP108", "GDDR5", 64, null),
            (@"GTX 980 TI", "GM200", "GDDR5", 384, null), (@"GTX 980", "GM204", "GDDR5", 256, null), (@"GTX 970", "GM204", "GDDR5", 256, null), (@"GTX 960", "GM206", "GDDR5", 128, null), (@"GTX 750", "GM107", "GDDR5", 128, null),

            (@"RX 9070", "Navi 48", "GDDR6", 256, null), (@"RX 9060 XT", "Navi 44", "GDDR6", 128, null),
            (@"RX 7900 XTX", "Navi 31", "GDDR6", 384, null), (@"RX 7900 XT\b", "Navi 31", "GDDR6", 320, null), (@"RX 7900 GRE", "Navi 31", "GDDR6", 256, null),
            (@"RX 7800 XT", "Navi 32", "GDDR6", 256, null), (@"RX 7700", "Navi 32", "GDDR6", 192, null), (@"RX 7600", "Navi 33", "GDDR6", 128, null),
            (@"RX 69[05]0 XT|RX 6800", "Navi 21", "GDDR6", 256, null), (@"RX 67[05]0", "Navi 22", "GDDR6", 192, null), (@"RX 66[05]0|RX 6600", "Navi 23", "GDDR6", 128, null),
            (@"RX 6500|RX 6400", "Navi 24", "GDDR6", 64, null), (@"RX 5700", "Navi 10", "GDDR6", 256, null), (@"RX 5600", "Navi 10", "GDDR6", 192, null), (@"RX 5500", "Navi 14", "GDDR6", 128, null),
            (@"RX 590", "Polaris 30", "GDDR5", 256, null), (@"RX 580|RX 570|RX 480|RX 470", "Polaris 20/10", "GDDR5", 256, null), (@"RX 560|RX 460", "Polaris 21/11", "GDDR5", 128, null), (@"RX 550", "Lexa", "GDDR5", 64, null),
            (@"RADEON VII", "Vega 20", "HBM2", 4096, null), (@"VEGA 64|VEGA 56", "Vega 10", "HBM2", 2048, null),

            (@"ARC.*A770|ARC.*A750|ARC.*A580", "ACM-G10", "GDDR6", 256, null), (@"ARC.*A380", "ACM-G11", "GDDR6", 96, null), (@"ARC.*A310", "ACM-G11", "GDDR6", 64, null),
            (@"ARC.*B580", "BMG-G21", "GDDR6", 192, null), (@"ARC.*B570", "BMG-G21", "GDDR6", 160, null),
        };
        foreach (var (p, chip, mem, bus, lap) in t)
        {
            if (lap is bool l && l != laptop) continue;
            if (Regex.IsMatch(n, p)) return (chip, mem, bus);
        }
        return null;
    }

    public static string Upscalers(Static g)
    {
        var u = new List<string>();
        string n = g.Name.ToUpperInvariant();
        if (n.Contains("RTX"))
        {
            string dlss = "DLSS (Super Resolution)";
            if (Regex.IsMatch(n, @"RTX (PRO )?50\d0")) dlss += " + Multi Frame Generation";
            else if (Regex.IsMatch(n, @"RTX 40\d0")) dlss += " + Frame Generation";
            u.Add(dlss);
        }
        if (Regex.IsMatch(n, @"RX 90\d0")) u.Add("FSR 4");
        u.Add(g.Integrated ? "FSR 1/2/3" : Loc.T("FSR 1/2/3 (compatível com qualquer GPU moderna)", "FSR 1/2/3 (works on any modern GPU)"));
        u.Add(g.VendorId == 0x8086 && !g.Integrated ? "XeSS (XMX)" : "XeSS (DP4a)");
        return string.Join(" · ", u);
    }

    public static string PcieText(Pcie.Link? l)
    {
        if (l == null) return Fmt.NA;
        string Gen(uint? x) => x is uint v && v > 0 ? $"PCIe {v}.0" : "?";
        string W(uint? x) => x is uint v && v > 0 ? $"x{v}" : "";
        return $"{Gen(l.CurGen)} {W(l.CurWidth)} " + Loc.T("(máx. ", "(max ") + $"{Gen(l.MaxGen)} {W(l.MaxWidth)})";
    }

    static string DxText(GpuApis.AdapterApi? a)
    {
        if (a?.D3D12FeatureLevel is not string fl) return a == null ? Fmt.NA : "DirectX 11";
        string flPretty = fl.Replace('_', '.');
        string name = fl.StartsWith("12_2") ? "DirectX 12 Ultimate" : fl.StartsWith("12") ? "DirectX 12" : "DirectX 12 (FL 11)";
        return $"{name} (Feature Level {flPretty}" + (a.ShaderModel != null ? $", Shader Model {a.ShaderModel})" : ")");
    }

    public static async Task<List<InfoPage>> BuildPagesAsync()
    {
        var statics = await Task.Run(Get);
        var apis = await GpuApis.GetAsync();
        var snap = SensorHub.Instance.Last;
        var pages = new List<InfoPage>();
        var L = Loc.T;
        int idx = 0;
        foreach (var g in statics)
        {
            var api = apis.Adapters.FirstOrDefault(a => a.VendorId == g.VendorId && a.DeviceId == g.DeviceId);
            var live = snap?.Gpus.FirstOrDefault(x => Normal(x.Name) == Normal(g.Name)) ?? (idx < (snap?.Gpus.Count ?? 0) ? snap!.Gpus[idx] : null);
            idx++;

            var p = new InfoPage { Title = (statics.Count > 1 ? $"GPU {idx}: " : "") + g.Name };
            var id = p.Group(L("Identificação", "Identification"));
            id.Add(L("Modelo", "Model"), g.Name);
            id.Add(L("Fabricante do chip", "Chip vendor"), g.Vendor);
            id.Add(L("Fabricante da placa", "Board partner"), g.Integrated ? L("— (integrada ao processador)", "— (integrated in CPU)") : g.BoardMaker,
                L("Empresa que montou a placa de vídeo (ASUS, MSI, Gigabyte…), lida do ID de subsistema PCI.", "Company that built the card, read from the PCI subsystem ID."));
            id.Add(L("Tipo", "Type"), g.Integrated ? L("Integrada (iGPU)", "Integrated (iGPU)") : L("Dedicada", "Dedicated"),
                L("Integrada: faz parte do processador e usa a RAM do sistema. Dedicada: placa separada, com memória própria (VRAM).", "Integrated: part of the CPU, uses system RAM. Dedicated: separate card with its own memory (VRAM)."));
            id.Add("Chip (GPU)", g.Chip, L("Nome do processador gráfico usado na placa.", "Graphics processor die used on the card."));
            if (g.Arch != "") id.Add(L("Arquitetura", "Architecture"), g.Arch);
            if (g.Cores is uint c && c > 0) id.Add(L("Núcleos de processamento", "Shader cores"), Fmt.N(c), L("Unidades de cálculo (CUDA cores na NVIDIA).", "Compute units (CUDA cores on NVIDIA)."));
            id.Add(L("ID do dispositivo", "Device ID"), $"{g.VendorId:X4}:{g.DeviceId:X4}");
            if (g.Vbios != "") id.Add("VBIOS", g.Vbios);

            var mem = p.Group(L("Memória de vídeo (VRAM)", "Video memory (VRAM)"));
            mem.Add(L("VRAM total", "Total VRAM"), g.Integrated ? L($"{Fmt.Value(g.DedicatedMB, Unit.MB)} reservada + até {Fmt.Value(g.SharedMB, Unit.MB)} compartilhada", $"{Fmt.Value(g.DedicatedMB, Unit.MB)} reserved + up to {Fmt.Value(g.SharedMB, Unit.MB)} shared") : Fmt.Value(g.DedicatedMB, Unit.MB),
                L("Memória própria da placa de vídeo, usada para texturas e quadros. Se encher, o jogo engasga.", "The card's own memory for textures and frames. If it fills up, games stutter."));
            mem.Add(L("Tipo de memória", "Memory type"), g.MemType, L("Geração da memória (GDDR6, GDDR6X, GDDR7…). Mais nova = mais rápida.", "Memory generation (GDDR6, GDDR6X, GDDR7...). Newer = faster."));
            mem.Add(L("Largura do barramento", "Bus width"), g.BusWidth is int bw ? $"{bw} bits" : null, L("Quantos bits a GPU troca com a VRAM por ciclo. Maior = mais banda.", "Bits transferred to VRAM per clock. Wider = more bandwidth."));
            mem.Add(L("VRAM em uso agora", "VRAM in use now"), live?.VramUsedMB is double vu ? $"{Fmt.Value(vu, Unit.MB)}" + (live.VramPct is double vp ? $" ({Fmt.N(vp)}%)" : "") : null);

            var clk = p.Group(L("Clocks", "Clocks"));
            clk.Add(L("Núcleo – atual", "Core – current"), Fmt.Or(live?.CoreClock, Unit.MHz), L("Frequência do processador gráfico agora. Sobe sob carga.", "Current GPU frequency. Goes up under load."));
            clk.Add(L("Núcleo – máximo (boost)", "Core – max (boost)"), g.MaxCoreClock is double mc ? Fmt.Value(mc, Unit.MHz) : MaxSeen($"gpu{idx - 1}.clock"));
            clk.Add(L("Memória – atual", "Memory – current"), Fmt.Or(live?.MemClock, Unit.MHz));
            clk.Add(L("Memória – máximo", "Memory – max"), g.MaxMemClock is double mm ? Fmt.Value(mm, Unit.MHz) : MaxSeen($"gpu{idx - 1}.memclock"));

            var drv = p.Group(L("Driver e APIs", "Driver and APIs"));
            drv.Add(L("Versão do driver", "Driver version"), g.DriverVersion);
            drv.Add(L("Data do driver", "Driver date"), g.DriverDate, L("Drivers muito antigos podem causar travamentos e menor desempenho em jogos novos.", "Very old drivers can cause crashes and lower performance in new games."));
            drv.Add("DirectX", DxText(api), L("Versão do DirectX suportada. \"12 Ultimate\" inclui ray tracing, mesh shaders e VRS.", "Supported DirectX version. \"12 Ultimate\" includes ray tracing, mesh shaders and VRS."));
            drv.Add("Vulkan", api?.VulkanVersion, L("API gráfica alternativa ao DirectX, usada por vários jogos e emuladores.", "Graphics API alternative to DirectX, used by many games and emulators."));
            drv.Add("OpenGL", apis.OpenGlVersion != null && (apis.OpenGlRenderer ?? "").Contains(g.Name.Split(' ').Last(), StringComparison.OrdinalIgnoreCase) ? apis.OpenGlVersion : apis.OpenGlVersion != null && idx == 1 ? apis.OpenGlVersion : null);
            drv.Add("Ray Tracing", api?.RaytracingTier is string rt ? L($"Sim (DXR tier {rt})", $"Yes (DXR tier {rt})") : api?.D3D12FeatureLevel != null ? L("Não", "No") : null,
                L("Iluminação realista por traçado de raios em tempo real. Exige hardware específico.", "Realistic real-time ray-traced lighting. Requires dedicated hardware."));
            drv.Add("Mesh Shaders", api?.MeshShaderTier != null ? L("Sim", "Yes") : api?.D3D12FeatureLevel != null ? L("Não", "No") : null);
            drv.Add(L("Upscaling (DLSS/FSR/XeSS)", "Upscaling (DLSS/FSR/XeSS)"), Upscalers(g),
                L("Técnicas que renderizam em resolução menor e reconstroem a imagem, aumentando o FPS.", "Render at lower resolution and reconstruct the image to raise FPS."));

            var bus = p.Group(L("Interface", "Interface"));
            bus.Add("PCI Express", g.Integrated ? L("— (interna ao processador)", "— (internal to CPU)") : PcieText(g.Link),
                L("Conexão entre a placa e a placa-mãe. Em repouso a GPU pode reduzir a versão para economizar energia — rode um teste para ver o valor real.", "Link between card and motherboard. At idle the GPU may downshift to save power — run a test to see the real value."));

            var sens = p.Group(L("Sensores (agora)", "Sensors (now)"));
            sens.Add(L("Uso do núcleo", "Core usage"), Fmt.Or(live?.Load, Unit.Percent));
            sens.Add(L("Temperatura do núcleo", "Core temperature"), Fmt.Or(live?.Temp, Unit.Temp));
            sens.Add(L("Temperatura hotspot", "Hotspot temperature"), Fmt.Or(live?.Hotspot, Unit.Temp), L("Ponto mais quente do chip. Costuma ficar 10–20 °C acima do núcleo.", "Hottest point of the die. Usually 10–20 °C above core temp."));
            if (live?.MemTemp != null) sens.Add(L("Temperatura da memória", "Memory temperature"), Fmt.Value(live.MemTemp, Unit.Temp));
            sens.Add(L("Ventoinha", "Fan"), live?.FanRpm is double rpm ? Fmt.Value(rpm, Unit.Rpm) + (live.FanPct is double fp ? $" ({Fmt.N(fp)}%)" : "") : live?.FanPct is double fp2 ? Fmt.Value(fp2, Unit.Percent) : null);
            sens.Add(L("Consumo", "Power draw"), Fmt.Or(live?.Power, Unit.Watt));
            if (g.TdpW is double tdp) sens.Add(L("Limite de energia padrão (TGP)", "Default power limit (TGP)"), Fmt.Value(tdp, Unit.Watt));
            sens.Add(L("Encoder de vídeo", "Video encoder"), Fmt.Or(live?.EncodeLoad, Unit.Percent), L("Usado para gravar/transmitir (NVENC, AMF, QuickSync).", "Used for recording/streaming (NVENC, AMF, QuickSync)."));
            sens.Add(L("Decoder de vídeo", "Video decoder"), Fmt.Or(live?.DecodeLoad, Unit.Percent), L("Usado para assistir vídeos.", "Used for video playback."));
            if (live?.ThrottleReason != null) sens.Add(L("Limitação de clock", "Clock limit"), live.ThrottleReason, status: live.Throttling == true ? Health.Warning : Health.Unknown);
            pages.Add(p);
        }
        if (pages.Count == 0)
        {
            var p = new InfoPage { Title = "GPU" };
            p.Group(L("Placa de vídeo", "Graphics card")).Add(L("Status", "Status"), L("Nenhuma placa de vídeo detectada", "No graphics card detected"));
            pages.Add(p);
        }
        return pages;
    }

    static string? MaxSeen(string metricId)
    {
        var st = SensorHub.Instance.GetStats(metricId);
        return st?.MaxV is double v ? Fmt.Value(v, Unit.MHz) + Loc.T(" (maior visto)", " (highest seen)") : null;
    }
}
