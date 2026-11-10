using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using ManagedNativeWifi;
using Microsoft.Win32;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Info;

/// <summary>Páginas de Processador, Placa-mãe, Rede e Sistema Operacional.</summary>
public static class InfoPages
{
    static string L(LocText pt, LocText en) => Loc.T(pt, en);

    // ======================================================== PROCESSADOR
    static double? _tdp;
    static bool _tdpRead;

    public static async Task<InfoPage> CpuAsync()
    {
        var c = await Task.Run(CpuInfo.Get);
        if (!_tdpRead) { _tdpRead = true; _tdp = await CpuInfo.ReadIntelTdpAsync(); }
        var s = SensorHub.Instance.Last?.Cpu;
        var p = new InfoPage { Title = L("Processador", "Processor") };

        var id = p.Group(L("Identificação", "Identification"));
        id.Add(L("Modelo", "Model"), c.Brand);
        id.Add(L("Fabricante", "Manufacturer"), c.Vendor switch { "GenuineIntel" => "Intel", "AuthenticAMD" => "AMD", var v => v });
        id.Add(L("Codinome", "Codename"), c.Codename, L("Nome interno da geração do processador.", "Internal name of the processor generation."));
        id.Add(L("Arquitetura", "Architecture"), c.Architecture);
        id.Add(L("Litografia", "Process node"), c.Process, L("Tamanho do processo de fabricação. Menor costuma significar mais eficiência.", "Manufacturing process size. Smaller usually means more efficiency."));
        id.Add("Socket", c.Socket, L("Encaixe do processador na placa-mãe. Define quais processadores são compatíveis.", "CPU socket on the motherboard. Defines compatible processors."));
        id.Add(L("Família / Modelo / Revisão", "Family / Model / Stepping"), $"{c.Family:X} / {c.Model:X} / {c.Stepping:X}");

        var cores = p.Group(L("Núcleos", "Cores"));
        cores.Add(L("Núcleos físicos", "Physical cores"), c.Cores.ToString(), L("Unidades de processamento reais.", "Real processing units."));
        cores.Add(L("Threads (núcleos lógicos)", "Threads (logical)"), c.Threads.ToString(), L("Com Hyper-Threading/SMT, cada núcleo executa duas tarefas ao mesmo tempo.", "With Hyper-Threading/SMT, each core runs two tasks at once."));
        if (c.PCores > 0)
        {
            cores.Add(L("Núcleos de performance (P)", "Performance cores (P)"), c.PCores.ToString(), L("Núcleos rápidos, usados por jogos e tarefas pesadas.", "Fast cores used by games and heavy tasks."));
            cores.Add(L("Núcleos de eficiência (E)", "Efficiency cores (E)"), c.ECores.ToString(), L("Núcleos econômicos para tarefas leves e de fundo.", "Low-power cores for light and background tasks."));
        }

        var clk = p.Group(L("Clocks", "Clocks"));
        clk.Add(L("Clock base", "Base clock"), c.BaseMhz > 0 ? Fmt.Value(c.BaseMhz, Unit.MHz) : null, L("Frequência garantida em carga total dentro do TDP.", "Guaranteed frequency at full load within TDP."));
        double? boost = c.BoostMhz > c.BaseMhz ? c.BoostMhz : null;
        clk.Add(L("Clock boost (máximo)", "Boost clock (max)"), boost is double b ? Fmt.Value(b, Unit.MHz) : SensorHub.Instance.GetStats("cpu.clockmax")?.MaxV is double mx ? Fmt.Value(mx, Unit.MHz) + L(" (maior visto)", " (highest seen)") : null,
            L("Frequência máxima que o processador atinge automaticamente quando há folga térmica.", "Maximum frequency reached automatically with thermal headroom."));
        clk.Add(L("Clock atual (média)", "Current clock (avg)"), Fmt.Or(s?.ClockAvg, Unit.MHz));
        if (s?.CoreClocks.Length > 0)
        {
            var tb = new InfoTable { Columns = new[] { L("Núcleo", "Core"), "Clock", L("Temperatura", "Temperature") } };
            for (int i = 0; i < s.CoreClocks.Length; i++)
                tb.Add(null, Health.Unknown, $"#{i + 1}", Fmt.Value(s.CoreClocks[i], Unit.MHz), i < s.CoreTemps.Length ? Fmt.Value(s.CoreTemps[i], Unit.Temp) : Fmt.Dash);
            clk.Tables.Add(tb);
        }

        var cache = p.Group("Cache");
        cache.Add("L1", c.L1, L("Memória ultrarrápida dentro de cada núcleo.", "Ultra-fast memory inside each core."));
        cache.Add("L2", c.L2, L("Cache intermediário, geralmente por núcleo.", "Mid-level cache, usually per core."));
        cache.Add("L3", c.L3, L("Cache compartilhado entre núcleos. Muito importante para jogos.", "Cache shared by cores. Very important for games."));

        var ins = p.Group(L("Instruções suportadas", "Supported instructions"));
        ins.Add(L("Conjuntos", "Sets"), string.Join(", ", c.Instructions), L("Extensões que aceleram cálculos (multimídia, criptografia, IA). Alguns jogos exigem AVX/AVX2.", "Extensions that speed up math (media, crypto, AI). Some games require AVX/AVX2."));
        ins.Add(L("Virtualização", "Virtualization"), Fmt.YesNo(c.Virtualization), L("Permite máquinas virtuais, WSL2 e emuladores Android.", "Enables virtual machines, WSL2 and Android emulators."));

        var pw = p.Group(L("Energia e temperatura", "Power and temperature"));
        pw.Add("TDP", _tdp is double tdp ? Fmt.Value(tdp, Unit.Watt) : null, L("Potência térmica de projeto: quanto calor o cooler precisa dissipar em uso contínuo.", "Thermal design power: heat the cooler must dissipate under sustained load."));
        pw.Add(L("Consumo atual", "Current power"), Fmt.Or(s?.Power, Unit.Watt));
        pw.Add(L("Voltagem do núcleo", "Core voltage"), Fmt.Or(s?.Voltage, Unit.Volt));
        pw.Add(L("Temperatura do pacote", "Package temperature"), Fmt.Or(s?.Temp, Unit.Temp), L("Temperatura geral do processador.", "Overall processor temperature."));
        if (s?.TjMax is double tj) pw.Add("TjMax", Fmt.Value(tj, Unit.Temp), L("Temperatura máxima antes de o processador reduzir o clock para se proteger.", "Maximum temperature before the CPU throttles to protect itself."));
        pw.Add(L("Uso total", "Total usage"), Fmt.Or(s?.Load, Unit.Percent));
        if (s?.ThreadLoads.Length > 0)
        {
            var tb = new InfoTable { Columns = new[] { "Thread", L("Uso", "Usage") } };
            for (int i = 0; i < s.ThreadLoads.Length; i++) tb.Add(null, Health.Unknown, $"#{i + 1}", Fmt.Value(s.ThreadLoads[i], Unit.Percent));
            pw.Tables.Add(tb);
        }
        if (s?.Temp == null && !SensorHub.Instance.PawnIoInstalled)
            pw.Notes.Add((Health.Warning, L("Temperatura, consumo e voltagem do processador precisam do driver gratuito PawnIO. Instale em Configurações → Sensores.", "CPU temperature, power and voltage require the free PawnIO driver. Install it in Settings → Sensors.")));
        return p;
    }

    // ======================================================== PLACA-MÃE
    sealed record Dmi(string? BoardMaker, string? BoardProduct, string? BoardVersion, string? SysMaker, string? SysProduct, string? BiosVendor, string? BiosVersion, DateTime? BiosDate);

    static async Task<Dmi> ReadDmiAsync() => OperatingSystem.IsLinux() ? LinuxDmi() : await WindowsDmiAsync();

    static Dmi LinuxDmi()
    {
        static string? D(string n) { var v = Platform.Linux.LinuxInfo.Dmi(n); return string.IsNullOrWhiteSpace(v) ? null : v; }
        DateTime? date = DateTime.TryParse(D("bios_date"), System.Globalization.CultureInfo.GetCultureInfo("en-US"), System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
        return new Dmi(D("board_vendor"), D("board_name"), D("board_version"), D("sys_vendor"), D("product_name"), D("bios_vendor"), D("bios_version"), date);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static async Task<Dmi> WindowsDmiAsync()
    {
        var smb = await SensorHub.Instance.InvokeAsync(pc => pc?.SMBios ?? new SMBios());
        return new Dmi(smb.Board?.ManufacturerName, smb.Board?.ProductName, smb.Board?.Version, smb.System?.ManufacturerName, smb.System?.ProductName, smb.Bios?.Vendor, smb.Bios?.Version, smb.Bios?.Date);
    }

    public static async Task<InfoPage> BoardAsync()
    {
        var smb = await ReadDmiAsync();
        var snap = SensorHub.Instance.Last;
        _chipset ??= await Task.Run(Chipset);
        string chipset = _chipset;
        var p = new InfoPage { Title = L("Placa-mãe", "Motherboard") };
        var g = p.Group(L("Identificação", "Identification"));
        g.Add(L("Fabricante", "Manufacturer"), smb.BoardMaker);
        g.Add(L("Modelo", "Model"), smb.BoardProduct);
        g.Add(L("Revisão", "Revision"), smb.BoardVersion);
        g.Add("Chipset", chipset, L("Conjunto de chips que conecta processador, USB, SATA e PCIe. Define recursos como overclock e quantidade de portas.", "Chip set linking CPU, USB, SATA and PCIe. Defines features like overclocking and port count."));
        if (!string.IsNullOrWhiteSpace(smb.SysProduct) && smb.SysProduct != smb.BoardProduct)
            g.Add(L("Sistema", "System"), $"{smb.SysMaker} {smb.SysProduct}".Trim());

        var b = p.Group("BIOS / UEFI");
        b.Add(L("Fabricante", "Vendor"), smb.BiosVendor);
        b.Add(L("Versão", "Version"), smb.BiosVersion, L("Atualizar a BIOS pode trazer compatibilidade com novos processadores e corrigir bugs.", "Updating BIOS can add support for new CPUs and fix bugs."));
        b.Add(L("Data", "Date"), smb.BiosDate is DateTime dt ? dt.ToString("d", Fmt.Culture) : null);
        if (OperatingSystem.IsLinux())
        {
            if (LinuxSecureBoot() is bool sb) b.Add("Secure Boot", sb ? L("Ativado", "Enabled") : L("Desativado", "Disabled"));
        }
        else
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
                if (k?.GetValue("UEFISecureBootEnabled") is int sb) b.Add("Secure Boot", sb == 1 ? L("Ativado", "Enabled") : L("Desativado", "Disabled"));
            }
            catch { }

        var t = p.Group(L("Sensores", "Sensors"));
        if (snap != null && (snap.BoardTemps.Count > 0 || snap.Fans.Count > 0))
        {
            foreach (var (name, v) in snap.BoardTemps) t.Add(name, Fmt.Value(v, Unit.Temp));
            foreach (var f in snap.Fans) t.Add(f.Name, f.Rpm is > 0 ? Fmt.Value(f.Rpm, Unit.Rpm) : L("parada / não conectada", "stopped / not connected"));
        }
        else
        {
            t.Add(L("Temperaturas e ventoinhas", "Temperatures and fans"), null);
            t.Notes.Add((Health.Unknown, SensorHub.Instance.PawnIoInstalled
                ? L("O chip de sensores desta placa-mãe não foi reconhecido (comum em notebooks e PCs de marca).", "This board's sensor chip was not recognized (common on laptops and OEM PCs).")
                : L("Os sensores da placa-mãe precisam do driver PawnIO (Configurações → Sensores).", "Motherboard sensors require the PawnIO driver (Settings → Sensors).")));
        }
        return p;
    }

    static string? _chipset;

    static bool? LinuxSecureBoot()
    {
        try
        {
            var f = System.IO.Directory.EnumerateFiles("/sys/firmware/efi/efivars", "SecureBoot-*").FirstOrDefault();
            if (f == null) return null;
            var b = System.IO.File.ReadAllBytes(f);
            return b.Length > 4 ? b[4] == 1 : null;
        }
        catch { return null; }
    }

    static string LinuxChipset()
    {
        // O chipset aparece no lspci como a ponte LPC/eSPI (Intel) ou ponte ISA/FCH (AMD).
        var o = Platform.Linux.Tool.Run("lspci", "-mm") ?? "";
        foreach (var line in o.Split('\n'))
        {
            var parts = Regex.Matches(line, "\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
            if (parts.Count < 3) continue;
            if (!Regex.IsMatch(parts[0], "ISA bridge|LPC|eSPI|Host bridge", RegexOptions.IgnoreCase) || !Regex.IsMatch(parts[0], "ISA bridge|LPC|eSPI", RegexOptions.IgnoreCase)) continue;
            string dev = parts[2];
            var br = Regex.Match(dev, @"\[([^\]]+)\]");
            var m2 = Regex.Match(dev, @"\b([BHQXZW]\d{3}[A-Z]?)\b");
            if (parts[1].Contains("Intel") && m2.Success) return "Intel " + m2.Groups[1].Value;
            return (parts[1].Contains("Intel") ? "Intel " : parts[1].Contains("AMD") ? "AMD " : "") + (br.Success ? br.Groups[1].Value : dev);
        }
        return CpuInfo.IsLaptop() ? L("Integrado ao processador (SoC)", "Integrated in the processor (SoC)") : "";
    }

    static string Chipset()
    {
        if (OperatingSystem.IsLinux()) return LinuxChipset();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI\\\\VEN_8086%' OR PNPDeviceID LIKE 'PCI\\\\VEN_1022%'");
            var amd = new List<string>();
            foreach (ManagementObject o in s.Get())
            {
                string name = o["Name"] as string ?? "";
                string id = o["PNPDeviceID"] as string ?? "";
                var m = Regex.Match(name, @"Intel\(R\)\s+([A-Z]{1,2}\d{2,3}[A-Z]?)\s+(Chipset\s+)?(LPC|eSPI)", RegexOptions.IgnoreCase);
                if (m.Success) return "Intel " + m.Groups[1].Value;
                if (Regex.IsMatch(name, @"(LPC|eSPI) Controller", RegexOptions.IgnoreCase) && name.Contains("Intel"))
                {
                    var m2 = Regex.Match(name, @"\b([BHQXZW]\d{3}[A-Z]?)\b");
                    if (m2.Success) return "Intel " + m2.Groups[1].Value;
                }
                amd.Add(id.ToUpperInvariant());
            }
            if (amd.Any(i => i.Contains("DEV_43F4") || i.Contains("DEV_43F5"))) return L("AMD série 600/800 (B650/X670/A620/B850/X870)", "AMD 600/800 series (B650/X670/A620/B850/X870)");
            if (amd.Any(i => i.Contains("DEV_43EE") || i.Contains("DEV_43EB"))) return L("AMD série 500 (B550/A520)", "AMD 500 series (B550/A520)");
            if (amd.Any(i => i.Contains("DEV_57AD") || i.Contains("DEV_57A4"))) return "AMD X570";
            if (amd.Any(i => i.Contains("DEV_43C8") || i.Contains("DEV_43C6"))) return L("AMD série 400 (B450/X470)", "AMD 400 series (B450/X470)");
            if (amd.Any(i => i.Contains("DEV_43BB") || i.Contains("DEV_43BC") || i.Contains("DEV_43B9"))) return L("AMD série 300 (B350/X370/A320)", "AMD 300 series (B350/X370/A320)");
            if (CpuInfo.IsLaptop()) return L("Integrado ao processador (SoC)", "Integrated in the processor (SoC)");
        }
        catch { }
        return "";
    }

    // ======================================================== REDE
    public sealed class WifiInfo
    {
        public string Ssid = "", Phy = "", Band = "";
        public int Signal, Channel, Rssi, RxMbps, TxMbps;
        public string Description = "";
    }

    /// <summary>Wi-Fi no Linux via NetworkManager (nmcli).</summary>
    static List<WifiInfo> LinuxWifi()
    {
        var list = new List<WifiInfo>();
        var o = Platform.Linux.Tool.Run("nmcli", "-t -f IN-USE,SSID,SIGNAL,CHAN,FREQ,RATE,DEVICE device wifi list");
        foreach (var line in (o ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = Regex.Split(line, @"(?<!\\):").Select(x => x.Replace("\\:", ":")).ToArray();
            if (f.Length < 7 || f[0] != "*") continue;
            var w = new WifiInfo { Ssid = f[1], Description = f[6] };
            int.TryParse(f[2], out w.Signal);
            int.TryParse(f[3], out w.Channel);
            var mf = Regex.Match(f[4], @"(\d+)");
            if (mf.Success) w.Band = $"{int.Parse(mf.Groups[1].Value) / 1000.0:0.#} GHz";
            var mr = Regex.Match(f[5], @"(\d+)");
            if (mr.Success) { w.RxMbps = int.Parse(mr.Groups[1].Value); w.TxMbps = w.RxMbps; }
            w.Phy = w.Band.StartsWith("6") ? "Wi-Fi 6E/7" : w.Band.StartsWith("5") ? "Wi-Fi 5/6" : "Wi-Fi 4/6";
            // dBm real: /proc/net/wireless (coluna "level")
            try
            {
                foreach (var l in System.IO.File.ReadLines("/proc/net/wireless"))
                    if (l.TrimStart().StartsWith(w.Description + ":", StringComparison.Ordinal))
                    {
                        var cols = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (cols.Length > 3 && double.TryParse(cols[3].TrimEnd('.'), out var dbm)) w.Rssi = (int)dbm;
                    }
            }
            catch { }
            list.Add(w);
        }
        return list;
    }

    public static List<WifiInfo> Wifi()
    {
        if (OperatingSystem.IsLinux()) return LinuxWifi();
        return WindowsWifi();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static List<WifiInfo> WindowsWifi()
    {
        var list = new List<WifiInfo>();
        try
        {
            foreach (var itf in NativeWifi.EnumerateInterfaces())
            {
                var (res, cc) = NativeWifi.GetCurrentConnection(itf.Id);
                if (cc == null || cc.InterfaceState != InterfaceState.Connected) continue;
                var w = new WifiInfo
                {
                    Description = itf.Description,
                    Ssid = cc.Ssid?.ToString() ?? "",
                    Signal = cc.SignalQuality,
                    RxMbps = cc.RxRate / 1000,
                    TxMbps = cc.TxRate / 1000,
                    Phy = cc.PhyType switch
                    {
                        PhyType.Eht => "Wi-Fi 7 (802.11be)",
                        PhyType.He => "Wi-Fi 6/6E (802.11ax)",
                        PhyType.Vht => "Wi-Fi 5 (802.11ac)",
                        PhyType.Ht => "Wi-Fi 4 (802.11n)",
                        PhyType.Erp => "802.11g",
                        PhyType.Ofdm => "802.11a",
                        PhyType.HrDsss => "802.11b",
                        var x => x.ToString(),
                    },
                };
                try { var (_, rssi) = NativeWifi.GetRssi(itf.Id); w.Rssi = rssi; } catch { }
                try
                {
                    var (_, bss) = NativeWifi.EnumerateBssNetworks(itf.Id);
                    string bssid = cc.Bssid?.ToString() ?? "";
                    var me = bss?.FirstOrDefault(b => b.Bssid?.ToString() == bssid);
                    if (me != null)
                    {
                        w.Channel = me.Channel;
                        w.Band = me.Band > 0 ? $"{me.Band:0.#} GHz" : "";
                    }
                }
                catch { }
                list.Add(w);
            }
        }
        catch { }
        return list;
    }

    public static async Task<InfoPage> NetworkAsync()
    {
        var p = new InfoPage { Title = L("Rede", "Network") };
        var pub = await Http.GetPublicInfoAsync();
        var wifi = await Task.Run(Wifi);

        var gp = p.Group(L("Internet", "Internet"));
        gp.Add(L("IP público", "Public IP"), pub.Ipv4 ?? pub.Ip, L("Endereço pelo qual a internet enxerga sua conexão.", "Address the internet sees for your connection."));
        if (pub.Ipv6 != null) gp.Add(L("IPv6 público", "Public IPv6"), pub.Ipv6);
        gp.Add(L("Provedor", "Provider"), pub.Isp + (pub.Asn != null ? $" ({pub.Asn})" : ""));
        gp.Add(L("Localização aproximada", "Approximate location"), string.Join(", ", new[] { pub.City, pub.Region, pub.Country }.Where(x => !string.IsNullOrEmpty(x))));

        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT)
            .Where(n => !Regex.IsMatch(n.Description, "virtual|vmware|hyper-v|vpn|tap-|bluetooth|wan miniport|loopback|pseudo|wi-fi direct|filter", RegexOptions.IgnoreCase))
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up);
        foreach (var n in nics)
        {
            bool wl = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            var g = p.Group($"{(wl ? "Wi-Fi" : "Ethernet")} — {n.Name}");
            g.Add(L("Adaptador", "Adapter"), n.Description);
            bool up = n.OperationalStatus == OperationalStatus.Up;
            g.Add(L("Status", "Status"), up ? L("Conectado", "Connected") : L("Desconectado", "Disconnected"), null, up ? Health.Good : Health.Unknown);
            if (!up) continue;
            g.Add(L("Velocidade do link", "Link speed"), n.Speed > 0 ? Fmt.Mbps(n.Speed) : null, L("Velocidade negociada entre o PC e o roteador/rede — não é a velocidade da internet.", "Speed negotiated between PC and router — not your internet speed."));
            g.Add(L("Endereço MAC", "MAC address"), string.Join(":", n.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))));
            var ip = n.GetIPProperties();
            g.Add(L("IP local (IPv4)", "Local IP (IPv4)"), string.Join(", ", ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => $"{a.Address}/{a.PrefixLength}")));
            var v6 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString()).ToList();
            if (v6.Count > 0) g.Add(L("IP local (IPv6)", "Local IP (IPv6)"), string.Join(", ", v6.Take(2)));
            g.Add("Gateway", string.Join(", ", ip.GatewayAddresses.Select(a => a.Address.ToString()).Where(a => a != "0.0.0.0")), L("Endereço do roteador.", "Router address."));
            g.Add("DNS", string.Join(", ", ip.DnsAddresses.Select(a => a.ToString()).Take(4)), L("Servidores que traduzem nomes de sites em endereços IP.", "Servers that translate site names into IP addresses."));
            try { g.Add("DHCP", Fmt.YesNo(ip.GetIPv4Properties()?.IsDhcpEnabled ?? false)); } catch { }
            if (wl)
            {
                var w = wifi.FirstOrDefault(x => x.Description == n.Description) ?? wifi.FirstOrDefault();
                if (w != null)
                {
                    g.Add(L("Rede (SSID)", "Network (SSID)"), w.Ssid);
                    g.Add(L("Sinal", "Signal"), $"{w.Signal}%" + (w.Rssi != 0 ? $" ({w.Rssi} dBm)" : ""), L("Acima de -60 dBm é ótimo; abaixo de -75 dBm a conexão fica instável.", "Above -60 dBm is great; below -75 dBm the connection gets unstable."),
                        w.Signal >= 60 ? Health.Good : w.Signal >= 35 ? Health.Warning : Health.Critical);
                    g.Add(L("Padrão", "Standard"), w.Phy);
                    if (w.Band != "") g.Add(L("Faixa / canal", "Band / channel"), $"{w.Band} / {w.Channel}", L("5 GHz e 6 GHz são mais rápidos; 2,4 GHz alcança mais longe.", "5 and 6 GHz are faster; 2.4 GHz reaches farther."));
                    if (w.RxMbps > 0) g.Add(L("Taxa de recepção / envio", "Rx / Tx rate"), $"{w.RxMbps} / {w.TxMbps} Mbps");
                }
            }
        }
        return p;
    }

    // ======================================================== SISTEMA OPERACIONAL
    static InfoPage LinuxOs()
    {
        var p = new InfoPage { Title = L("Sistema operacional", "Operating system") };
        var g = p.Group("Linux");
        string R(string k) => Platform.Linux.LinuxPlatform.ReadOsRelease(k) ?? "";
        g.Add(L("Distribuição", "Distribution"), R("PRETTY_NAME"));
        g.Add(L("Versão", "Version"), R("VERSION"));
        g.Add("Kernel", Platform.Linux.LinuxPlatform.KernelVersion());
        g.Add(L("Arquitetura", "Architecture"), System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString());
        g.Add(L("Ambiente gráfico", "Desktop"), Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "");
        g.Add(L("Servidor de janelas", "Display server"), Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "");
        g.Add(L("Nome do computador", "Computer name"), Environment.MachineName);
        g.Add(L("Usuário", "User"), Environment.UserName);
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        try { up = TimeSpan.FromSeconds(double.Parse((Platform.Linux.LinuxSensorProvider.Read("/proc/uptime") ?? "0").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)); } catch { }
        g.Add(L("Tempo ligado (uptime)", "Uptime"), Fmt.Duration(up));
        g.Add(L("Ligado desde", "Up since"), (DateTime.Now - up).ToString("g", Fmt.Culture));
        g.Add(L("Idioma do sistema", "System language"), Environment.GetEnvironmentVariable("LANG") ?? System.Globalization.CultureInfo.InstalledUICulture.DisplayName);
        g.Add(L("Pulso executando como root", "Pulso running as root"), Fmt.YesNo(Platform.OS.Current.IsElevated));
        return p;
    }

    public static Task<InfoPage> OsAsync() => OperatingSystem.IsLinux() ? Task.Run(LinuxOs) : Task.Run(WindowsOs);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static InfoPage WindowsOs()
    {
        var p = new InfoPage { Title = L("Sistema operacional", "Operating system") };
        var g = p.Group("Windows");
        string product = "", display = "", build = "", ubr = "", edition = "";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            product = k?.GetValue("ProductName") as string ?? "";
            display = k?.GetValue("DisplayVersion") as string ?? k?.GetValue("ReleaseId") as string ?? "";
            build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            ubr = k?.GetValue("UBR")?.ToString() ?? "";
            edition = k?.GetValue("EditionID") as string ?? "";
        }
        catch { }
        // O registro continua dizendo "Windows 10" no Windows 11; o número do build é o que vale.
        if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        g.Add(L("Versão", "Version"), $"{product} {display}".Trim());
        g.Add(L("Edição", "Edition"), edition);
        g.Add("Build", ubr != "" ? $"{build}.{ubr}" : build);
        g.Add(L("Arquitetura", "Architecture"), Environment.Is64BitOperatingSystem ? "64 bits" : "32 bits");
        g.Add(L("Nome do computador", "Computer name"), Environment.MachineName);
        g.Add(L("Usuário", "User"), Environment.UserName);
        var up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
        g.Add(L("Tempo ligado (uptime)", "Uptime"), Fmt.Duration(up), L("Com a Inicialização Rápida ativa, \"Desligar\" não zera este tempo — só \"Reiniciar\".", "With Fast Startup enabled, \"Shut down\" does not reset this — only \"Restart\" does."));
        g.Add(L("Ligado desde", "Up since"), (DateTime.Now - up).ToString("g", Fmt.Culture));
        try
        {
            using var s = new ManagementObjectSearcher("SELECT InstallDate FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
                if (o["InstallDate"] is string d) g.Add(L("Instalado em", "Installed on"), ManagementDateTimeConverter.ToDateTime(d).ToString("d", Fmt.Culture));
        }
        catch { }
        g.Add(L("Idioma do sistema", "System language"), System.Globalization.CultureInfo.InstalledUICulture.DisplayName);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore");
            using var k2 = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            if (k2?.GetValue("AutoGameModeEnabled") is int gm) g.Add(L("Modo de Jogo", "Game Mode"), gm == 1 ? L("Ativado", "Enabled") : L("Desativado", "Disabled"));
            else g.Add(L("Modo de Jogo", "Game Mode"), L("Ativado (padrão)", "Enabled (default)"));
        }
        catch { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (k?.GetValue("HwSchMode") is int hags) g.Add(L("Agendamento de GPU acelerado por hardware", "Hardware-accelerated GPU scheduling"), hags == 2 ? L("Ativado", "Enabled") : L("Desativado", "Disabled"));
        }
        catch { }
        g.Add(L("Pulso executando como administrador", "Pulso running as administrator"), Fmt.YesNo(Native.IsAdmin));
        return p;
    }
}
