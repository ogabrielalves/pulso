using System.Management;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Pulso.Core;
using Pulso.Hardware;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.SPD;

namespace Pulso.Info;

public static class MemoryInfo
{
    public sealed class Module
    {
        public string Slot = "", Bank = "", Manufacturer = "", PartNumber = "", Serial = "", Type = "";
        public double SizeGB;
        public int Speed, ConfiguredSpeed, VoltageMv;
        public int RatedSpeed;           // velocidade de fábrica estimada (part number)
        public string? Timings;          // CL-tRCD-tRP-tRAS JEDEC (SPD)
        public int? SpdSpeed;
        public double? SpdTemp;
        public string? DramMaker;
        public string? MfgDate;
    }

    public sealed class Data
    {
        public List<Module> Modules = new();
        /// <summary>Todos os slots (ocupados e vazios) na ordem do SMBIOS.</summary>
        public List<(string slot, Module? module)> Slots = new();
        public int TotalSlots;
        public string ChannelMode = "";
        public bool? XmpActive;
        public string? XmpText;
        public string? Warning;
    }

    public static async Task<Data> ReadAsync()
    {
        var d = new Data();
        var hub = SensorHub.Instance;
        try
        {
            var smb = await hub.InvokeAsync(pc => pc?.SMBios ?? new SMBios());
            foreach (var m in smb.MemoryDevices)
            {
                if (m.Size == 0)
                {
                    d.Slots.Add((m.DeviceLocator?.Trim() ?? "", null));
                    continue;
                }
                d.Modules.Add(new Module
                {
                    Slot = m.DeviceLocator?.Trim() ?? "",
                    Bank = m.BankLocator?.Trim() ?? "",
                    Manufacturer = Maker(m.ManufacturerName),
                    PartNumber = m.PartNumber?.Trim() ?? "",
                    Serial = m.SerialNumber?.Trim() ?? "",
                    SizeGB = m.Size / 1024.0,
                    Speed = m.Speed,
                    ConfiguredSpeed = m.ConfiguredSpeed,
                    VoltageMv = m.ConfiguredVoltage,
                    Type = m.Type.ToString().Replace("_", ""),
                });
                d.Slots.Add((d.Modules[^1].Slot, d.Modules[^1]));
            }
        }
        catch (Exception ex) { Paths.LogError("MemoryInfo.smbios", ex); }

        try
        {
            using var s = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            foreach (ManagementObject o in s.Get()) d.TotalSlots += Convert.ToInt32(o["MemoryDevices"] ?? 0);
        }
        catch { }
        if (d.TotalSlots < d.Modules.Count) d.TotalSlots = d.Modules.Count;

        // Se o SMBIOS falhar (raro), usa o WMI.
        if (d.Modules.Count == 0)
        {
            try
            {
                using var s = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory");
                foreach (ManagementObject o in s.Get())
                    d.Modules.Add(new Module
                    {
                        Slot = o["DeviceLocator"] as string ?? "",
                        Bank = o["BankLabel"] as string ?? "",
                        Manufacturer = Maker(o["Manufacturer"] as string),
                        PartNumber = (o["PartNumber"] as string ?? "").Trim(),
                        SizeGB = Convert.ToDouble(o["Capacity"] ?? 0) / 1073741824.0,
                        Speed = Convert.ToInt32(o["Speed"] ?? 0),
                        ConfiguredSpeed = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0),
                        VoltageMv = Convert.ToInt32(o["ConfiguredVoltage"] ?? 0),
                        Type = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", 35 => "LPDDR5", 30 => "LPDDR4", _ => "" },
                    });
            }
            catch { }
        }

        foreach (var m in d.Modules) m.RatedSpeed = Math.Max(m.Speed, PartNumberSpeed(m.PartNumber) ?? 0);

        await ReadSpdAsync(d);

        // Modo de canal pelos nomes dos slots.
        var channels = d.Modules.Select(m => ChannelOf(m.Bank + " " + m.Slot)).Where(c => c != null).Distinct().Count();
        int n = d.Modules.Count;
        string mode = channels switch
        {
            >= 4 => "Quad channel",
            3 => "Triple channel",
            2 => "Dual channel",
            1 when n == 1 => "Single channel",
            1 => Loc.T("Single channel (pentes no mesmo canal)", "Single channel (modules on the same channel)"),
            _ => n switch { 1 => "Single channel", 2 or 4 => Loc.T("Dual channel (provável)", "Dual channel (likely)"), 0 => "", _ => Loc.T("Desconhecido", "Unknown") },
        };
        if (mode.StartsWith("Single") && d.Modules.Any(m => m.Type.Contains("DDR5")))
            mode += Loc.T(" — cada pente DDR5 tem 2 subcanais de 32 bits", " — each DDR5 module has 2 × 32-bit subchannels");
        d.ChannelMode = mode;

        EvaluateXmp(d);
        return d;
    }

    static string? ChannelOf(string s)
    {
        var m = Regex.Match(s, @"CHANNEL\s*([A-H])|CHANNEL([A-H])|CH[\s_-]?([A-H])\b|DIMM[\s_-]?([A-H])\d?\b|\b([A-H])[0-2]\b|_([A-H])\d?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        for (int i = 1; i < m.Groups.Count; i++) if (m.Groups[i].Success) return m.Groups[i].Value.ToUpperInvariant();
        return null;
    }

    static string Maker(string? raw)
    {
        raw = raw?.Trim() ?? "";
        // Alguns BIOS gravam o código JEDEC em vez do nome.
        string hex = raw.Replace(" ", "").ToUpperInvariant();
        string[,] codes =
        {
            { "80CE", "Samsung" }, { "CE00", "Samsung" }, { "80AD", "SK hynix" }, { "AD00", "SK hynix" }, { "802C", "Micron" }, { "2C00", "Micron" },
            { "859B", "Crucial" }, { "9B05", "Crucial" }, { "04CB", "ADATA" }, { "CB04", "ADATA" }, { "0198", "Kingston" }, { "9801", "Kingston" },
            { "029E", "Corsair" }, { "9E02", "Corsair" }, { "04CD", "G.Skill" }, { "CD04", "G.Skill" }, { "04EF", "Team Group" }, { "EF04", "Team Group" },
            { "0194", "Smart Modular" }, { "02FE", "Elpida" }, { "8551", "Qimonda" }, { "8A76", "Lexar" }, { "8C14", "Netac" }, { "0B9B", "Patriot" }, { "8502", "Patriot" },
        };
        for (int i = 0; i < codes.GetLength(0); i++)
            if (hex.StartsWith(codes[i, 0]) || hex.Contains(codes[i, 0] + "0000")) return $"{codes[i, 1]}";
        return raw;
    }

    /// <summary>Velocidade de fábrica (com XMP/EXPO) codificada no part number dos principais fabricantes.</summary>
    public static int? PartNumberSpeed(string pn)
    {
        if (string.IsNullOrWhiteSpace(pn)) return null;
        pn = pn.ToUpperInvariant();
        Match m;
        if ((m = Regex.Match(pn, @"^KF[3-5](\d{2})")).Success) return int.Parse(m.Groups[1].Value) * 100;          // Kingston Fury
        if ((m = Regex.Match(pn, @"^KHX(\d{4})")).Success) return int.Parse(m.Groups[1].Value);                   // HyperX
        if ((m = Regex.Match(pn, @"^F[3-5]-(\d{4})")).Success) return int.Parse(m.Groups[1].Value);               // G.Skill
        if ((m = Regex.Match(pn, @"^CM\w+?M\d[A-Z](\d{4})")).Success) return int.Parse(m.Groups[1].Value);        // Corsair
        if ((m = Regex.Match(pn, @"^AX[45]U(\d{4})")).Success) return int.Parse(m.Groups[1].Value);              // ADATA XPG
        if ((m = Regex.Match(pn, @"^TF\w+?G(\d{4})")).Success) return int.Parse(m.Groups[1].Value);              // TeamGroup T-Force
        if ((m = Regex.Match(pn, @"^BL\w*?\d+G\d+D(\d{2})")).Success) return int.Parse(m.Groups[1].Value) * 100;  // Crucial Ballistix
        if ((m = Regex.Match(pn, @"^CT\d+G[45]\w*?(\d{2})[A-Z]?\.?\w*$")).Success && int.Parse(m.Groups[1].Value) is int c and >= 21 and <= 80) return c * 100;
        if ((m = Regex.Match(pn, @"(?<!\d)(2133|2400|2666|2667|2800|2933|3000|3200|3333|3466|3600|3733|3866|4000|4133|4266|4400|4800|5200|5600|6000|6200|6400|6600|6800|7000|7200|7600|8000|8200|8400)(?!\d)")).Success)
            return int.Parse(m.Groups[1].Value);
        return null;
    }

    static int JedecMax(string type) => type switch
    {
        var t when t.Contains("DDR5") => 6400,
        var t when t.Contains("DDR4") => 3200,
        var t when t.Contains("DDR3") => 1866,
        _ => int.MaxValue,
    };

    static void EvaluateXmp(Data d)
    {
        var mods = d.Modules;
        if (mods.Count == 0) return;
        int cfg = mods.Min(m => m.ConfiguredSpeed);
        int rated = mods.Max(m => m.RatedSpeed);
        string type = mods[0].Type;
        bool laptop = CpuInfo.IsLaptop();
        if (cfg <= 0) return;

        if (cfg > JedecMax(type) || (rated > 0 && cfg >= rated && rated > JedecMax(type)))
        {
            d.XmpActive = true;
            d.XmpText = Loc.T("Ativo — a memória está acima da velocidade padrão JEDEC", "Active — memory runs above JEDEC standard speed");
        }
        else if (rated > cfg + 50)
        {
            d.XmpActive = false;
            d.XmpText = Loc.T("Inativo", "Inactive");
            d.Warning = laptop
                ? Loc.T($"A memória suporta {rated} MT/s mas está rodando a {cfg} MT/s. Em notebooks isso normalmente é um limite da plataforma e não pode ser alterado.",
                        $"Memory is rated for {rated} MT/s but runs at {cfg} MT/s. On laptops this is usually a platform limit that cannot be changed.")
                : Loc.T($"A memória está rodando a {cfg} MT/s, abaixo da velocidade nominal de {rated} MT/s. Ative o perfil XMP (Intel) ou EXPO/DOCP (AMD) na BIOS para ganhar desempenho.",
                        $"Memory runs at {cfg} MT/s, below its rated {rated} MT/s. Enable the XMP (Intel) or EXPO/DOCP (AMD) profile in BIOS to gain performance.");
        }
        else
        {
            d.XmpActive = null;
            d.XmpText = rated > 0 && rated <= JedecMax(type)
                ? Loc.T("Não necessário — a memória já roda na velocidade nominal", "Not needed — memory already runs at rated speed")
                : Loc.T("Não foi possível determinar", "Could not be determined");
        }
    }

    static async Task ReadSpdAsync(Data d)
    {
        var hub = SensorHub.Instance;
        if (!hub.PawnIoInstalled) return;
        try
        {
            var spd = await hub.InvokeAsync(_ =>
            {
                var res = new List<(string pn, int? speed, string? timings, double? temp, string? dram, string? date, double cap)>();
                // O LHM já detecta os barramentos SMBus quando o driver PawnIO está presente.
                if (SMBusManager.RegisteredSMBuses.Count == 0) return res;
                foreach (var bus in SMBusManager.RegisteredSMBuses)
                {
                    for (byte addr = 0x50; addr <= 0x57; addr++)
                    {
                        try
                        {
                            var det = new SPDDetector(bus, addr);
                            if (!det.IsValid || det.Accessor is not SPDAccessor acc) continue;
                            string pn = (acc.ModulePartNumber() ?? "").Trim();
                            int? speed = null; string? tim = null; double? temp = null;
                            if (acc is DDR4Accessor d4)
                            {
                                var t = d4.SDRAMTimings;
                                (speed, tim) = Timings(t.MinimumCycleTime, t.MinimumCASLatencyTime, t.MinimumRASToCASDelayTime, t.MinimumRowPrechargeDelayTime, t.MinimumActiveToPrechargeDelayTime);
                                if (d4.HasThermalSensor && d4.UpdateTemperature()) temp = d4.Temperature;
                            }
                            else if (acc is DDR5Accessor d5)
                            {
                                var t = d5.SDRAMTimings;
                                (speed, tim) = Timings(t.MinimumCycleTime, t.MinimumCASLatencyTime, t.MinimumRASToCASDelayTime, t.MinimumRowPrechargeDelayTime, t.MinimumActiveToPrechargeDelayTime);
                                if (d5.HasThermalSensor && d5.UpdateTemperature()) temp = d5.Temperature;
                            }
                            string? date = acc.ModuleManufacturingDate() is DateTime dt ? dt.ToString("MM/yyyy") : null;
                            res.Add((pn, speed, tim, temp, acc.GetDRAMManufacturerString(), date, acc.GetCapacity()));
                        }
                        catch { }
                    }
                }
                return res;
            });

            // Associa por part number (ou ordem).
            var free = d.Modules.ToList();
            foreach (var s in spd)
            {
                var m = free.FirstOrDefault(x => x.PartNumber.Equals(s.pn, StringComparison.OrdinalIgnoreCase)) ?? free.FirstOrDefault();
                if (m == null) break;
                free.Remove(m);
                m.Timings = s.timings;
                m.SpdSpeed = s.speed;
                m.SpdTemp = s.temp is > 0 and < 120 ? s.temp : null;
                m.DramMaker = s.dram;
                m.MfgDate = s.date;
            }
        }
        catch (Exception ex) { Paths.LogError("MemoryInfo.spd", ex); }
    }

    static (int?, string?) Timings(decimal tck, decimal taa, decimal trcd, decimal trp, decimal tras)
    {
        if (tck <= 0) return (null, null);
        int C(decimal t) => (int)Math.Ceiling(t / tck - 0.025m);
        int speed = (int)Math.Round(2000m / tck);
        return (speed, $"{C(taa)}-{C(trcd)}-{C(trp)}-{C(tras)}");
    }

    public static async Task<InfoPage> BuildPageAsync()
    {
        var L = Loc.T;
        var d = (await SystemSummary.GetAsync()).Memory;
        var snap = SensorHub.Instance.Last;
        var p = new InfoPage { Title = L("Memória RAM", "Memory (RAM)") };

        var use = p.Group(L("Uso", "Usage"));
        use.Add(L("Capacidade total", "Total capacity"), snap?.Mem.TotalGB is double tg ? $"{Fmt.N(tg, 1)} GB" + (d.Modules.Count > 0 ? L($" ({Fmt.N(d.Modules.Sum(m => m.SizeGB), 0)} GB instalados)", $" ({Fmt.N(d.Modules.Sum(m => m.SizeGB), 0)} GB installed)") : "") : null,
            L("O Windows mostra um pouco menos que o instalado: parte é reservada para hardware (ex.: vídeo integrado).", "Windows shows slightly less than installed: some is reserved for hardware (e.g. integrated graphics)."));
        use.Add(L("Em uso", "In use"), snap?.Mem.UsedGB is double ug ? $"{Fmt.N(ug, 1)} GB ({Fmt.N(snap.Mem.Load ?? 0)}%)" : null);
        use.Add(L("Disponível", "Available"), snap?.Mem.AvailableGB is double ag ? $"{Fmt.N(ag, 1)} GB" : null, L("Memória livre + cache que pode ser liberado na hora.", "Free memory plus cache that can be released instantly."));
        use.Add(L("Em cache", "Cached"), snap?.Mem.CachedGB is double cg ? $"{Fmt.N(cg, 1)} GB" : null, L("Arquivos guardados na RAM para abrir mais rápido. É liberado quando um programa precisa.", "Files kept in RAM for faster access. Released when a program needs memory."));
        use.Add(L("Memória comprometida", "Committed memory"), snap?.Mem.CommitPct is double cp ? $"{Fmt.N(cp)}%" : null, L("Total reservado por programas, incluindo o arquivo de paginação.", "Total reserved by programs, including the page file."));

        var cfg = p.Group(L("Configuração", "Configuration"));
        cfg.Add(L("Pentes instalados", "Installed modules"), d.Modules.Count.ToString());
        cfg.Add(L("Slots", "Slots"), d.TotalSlots > 0 ? L($"{d.Modules.Count} ocupados de {d.TotalSlots} ({d.TotalSlots - d.Modules.Count} livres)", $"{d.Modules.Count} used of {d.TotalSlots} ({d.TotalSlots - d.Modules.Count} free)") : null);
        cfg.Add(L("Tipo", "Type"), d.Modules.FirstOrDefault()?.Type);
        cfg.Add(L("Modo de canal", "Channel mode"), d.ChannelMode, L("Dual channel usa dois canais em paralelo e quase dobra a banda de memória. Use pentes em pares nos slots indicados pela placa-mãe.", "Dual channel uses two channels in parallel, nearly doubling bandwidth. Install modules in pairs in the slots recommended by the motherboard."));
        var maxCfg = d.Modules.Count > 0 ? d.Modules.Max(m => m.ConfiguredSpeed) : 0;
        cfg.Add(L("Velocidade atual", "Current speed"), maxCfg > 0 ? $"{maxCfg} MT/s ({maxCfg / 2} MHz)" : null,
            L("Transferências por segundo. A frequência real do clock é metade (DDR = Double Data Rate).", "Transfers per second. The actual clock is half (DDR = Double Data Rate)."));
        cfg.Add(L("Perfil XMP / EXPO", "XMP / EXPO profile"), d.XmpText, L("Perfil de fábrica que faz a memória rodar na velocidade anunciada na caixa. Precisa ser ativado na BIOS.", "Factory profile that runs memory at the advertised speed. Must be enabled in BIOS."),
            d.XmpActive == false ? Health.Warning : d.XmpActive == true ? Health.Good : Health.Unknown);
        if (d.Warning != null) cfg.Notes.Add((Health.Warning, d.Warning));

        int i = 0;
        foreach (var m in d.Modules)
        {
            i++;
            var g = p.Group(L($"Pente {i}", $"Module {i}") + (m.Slot != "" ? $" — {m.Slot}" : ""));
            g.Add(L("Fabricante", "Manufacturer"), m.Manufacturer);
            g.Add(L("Modelo (part number)", "Model (part number)"), m.PartNumber);
            g.Add(L("Capacidade", "Capacity"), $"{Fmt.N(m.SizeGB, 0)} GB");
            g.Add(L("Tipo", "Type"), m.Type);
            g.Add(L("Frequência configurada", "Configured speed"), m.ConfiguredSpeed > 0 ? $"{m.ConfiguredSpeed} MT/s" : null);
            g.Add(L("Frequência máxima informada", "Rated speed"), m.RatedSpeed > 0 ? $"{m.RatedSpeed} MT/s" : null, L("Maior velocidade indicada pela BIOS ou pelo part number do fabricante.", "Highest speed reported by BIOS or encoded in the part number."));
            g.Add(L("Timings (JEDEC)", "Timings (JEDEC)"), m.Timings != null ? $"{m.Timings} @ {m.SpdSpeed} MT/s" : L("não disponível (requer o driver PawnIO)", "not available (requires PawnIO driver)"),
                L("Latências CL-tRCD-tRP-tRAS do perfil padrão gravado no chip SPD. Menor = resposta mais rápida.", "CL-tRCD-tRP-tRAS latencies of the default profile stored in the SPD chip. Lower = faster response."));
            g.Add(L("Voltagem", "Voltage"), m.VoltageMv > 0 ? $"{m.VoltageMv / 1000.0:0.00#} V" : null);
            if (m.DramMaker != null) g.Add(L("Fabricante dos chips", "DRAM chip maker"), m.DramMaker);
            if (m.MfgDate != null) g.Add(L("Fabricação", "Manufactured"), m.MfgDate);
            if (m.SpdTemp != null) g.Add(L("Temperatura", "Temperature"), Fmt.Value(m.SpdTemp, Unit.Temp));
            if (m.Serial != "" && !m.Serial.All(c => c == '0')) g.Add(L("Número de série", "Serial number"), m.Serial);
            if (m.Bank != "") g.Add(L("Banco", "Bank"), m.Bank);
        }
        return p;
    }
}
