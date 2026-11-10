using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>Período em que o aparelho foi visto online.</summary>
public sealed class LanSession
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
}

/// <summary>Aparelho visto na rede, identificado pelo MAC (guardado em rede.json).</summary>
public sealed class LanDevice
{
    public string Mac { get; set; } = "";
    /// <summary>MAC do roteador: separa aparelhos de redes diferentes (casa, trabalho…).</summary>
    public string Network { get; set; } = "";
    /// <summary>Nome dado pelo usuário.</summary>
    public string? Name { get; set; }
    /// <summary>Tipo escolhido pelo usuário (sobrepõe o detectado).</summary>
    public string? Type { get; set; }
    public string? Ip { get; set; }
    public string? Hostname { get; set; }
    public string? Vendor { get; set; }
    /// <summary>Nome que o próprio aparelho anuncia (UPnP), ex.: "[TV] Samsung 7 Series".</summary>
    public string? Friendly { get; set; }
    /// <summary>Fabricante + modelo anunciados (UPnP).</summary>
    public string? Model { get; set; }
    public bool IsGateway { get; set; }
    public bool IsSelf { get; set; }
    public bool Known { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public List<LanSession> Sessions { get; set; } = new();

    [JsonIgnore] public bool Online { get; set; }
    [JsonIgnore] public double? LatencyMs { get; set; }

    /// <summary>Celulares modernos usam um MAC aleatório por rede (bit "administrado localmente").</summary>
    [JsonIgnore] public bool RandomMac => Mac.Length >= 2 && (Convert.ToByte(Mac[..2], 16) & 0x02) != 0;
    [JsonIgnore] public string DetectedType => LanTypes.Guess(this);
    [JsonIgnore] public string EffectiveType => Type ?? DetectedType;

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name!
        : IsSelf ? Loc.T("Este computador", "This computer")
        : IsGateway ? Loc.T("Roteador", "Router") + (Vendor != null ? " " + Vendor : "")
        : Friendly ?? Hostname ?? (Vendor != null ? LanTypes.Label(EffectiveType) + " " + Vendor : null) ?? LanTypes.Label(EffectiveType);

    /// <summary>Tempo online nas últimas <paramref name="window"/> (pelas varreduras registradas).</summary>
    public TimeSpan OnlineWithin(TimeSpan window)
    {
        var from = DateTime.Now - window;
        var total = TimeSpan.Zero;
        foreach (var s in Sessions)
        {
            var a = s.Start < from ? from : s.Start;
            if (s.End > a) total += s.End - a;
        }
        return total;
    }
}

public sealed class LanData
{
    public List<LanDevice> Devices { get; set; } = new();
    /// <summary>Redes que já tiveram a primeira varredura (a partir daí, aparelho desconhecido = novo).</summary>
    public List<string> Baselines { get; set; } = new();
    public DateTime? LastScan { get; set; }
}

/// <summary>Rede local em que o PC está conectado agora.</summary>
public sealed record LanNet(string Adapter, string AdapterKind, IPAddress LocalIp, string LocalMac, IPAddress Gateway, int Prefix, IPAddress[] Hosts)
{
    public string Cidr
    {
        get
        {
            var b = LocalIp.GetAddressBytes();
            uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            uint mask = Prefix == 0 ? 0 : uint.MaxValue << (32 - Prefix);
            uint n = ip & mask;
            return $"{n >> 24}.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}/{Prefix}";
        }
    }
}

/// <summary>Tipos de aparelho: rótulo, ícone (traço 24×24 no estilo do menu) e detecção por nome/fabricante.</summary>
public static class LanTypes
{
    public static readonly (string id, string pt, string en, string icon)[] All =
    {
        ("router", "Roteador / rede", "Router / network", "M3 14h18v6H3zM7 17h.01M11 17h.01M7 14l-1.5-6M17 14l1.5-6M9.5 7.5a3.5 3.5 0 0 1 5 0M7.5 5a6.5 6.5 0 0 1 9 0"),
        ("desktop", "PC de mesa", "Desktop PC", "M3 4h18v12H3zM8 20h8M12 16v4"),
        ("notebook", "Notebook", "Laptop", "M5 5h14v10H5zM2.5 19h19l-2.5-4h-14z"),
        ("pc", "Computador", "Computer", "M3 4h18v12H3zM8 20h8M12 16v4"),
        ("iphone", "iPhone", "iPhone", "M12 7.4c-1.1-.6-2.6-.9-3.9 0-2 1.3-2.3 4.4-1.2 7.2.9 2.4 2.3 4.4 3.6 4.4.7 0 1-.4 1.5-.4s.8.4 1.5.4c1.4 0 2.7-2 3.5-4.1-1.4-.6-2.2-1.9-2.1-3.3.1-1.2.8-2.2 1.8-2.7-.8-1-2-1.5-3.1-1.4-.6.1-1.1.4-1.6.6zM12.2 6.6c-.1-1.6.9-3 2.4-3.4.2 1.6-.9 3.1-2.4 3.4z"),
        ("android", "Celular Android", "Android phone", "M5 18a7 7 0 0 1 14 0zM8.5 11.8L6.8 9M15.5 11.8L17.2 9M9.5 15h.01M14.5 15h.01"),
        ("phone", "Celular", "Phone", "M7 2.5h10v19H7zM11 18.5h2"),
        ("tablet", "Tablet", "Tablet", "M4.5 3h15v18h-15zM11 18h2"),
        ("tv", "TV / streaming", "TV / streaming", "M2.5 7h19v12h-19zM8 21.5h8M9 3l3 3.5 3-3.5"),
        ("console", "Videogame", "Game console", "M7 8h10a4.5 4.5 0 0 1 4.4 5.4l-.7 3.4a2.3 2.3 0 0 1-4 .9L14.5 15.5h-5l-2.2 2.2a2.3 2.3 0 0 1-4-.9l-.7-3.4A4.5 4.5 0 0 1 7 8zM8 10.5v3.5M6.25 12.25h3.5M15.5 11.5h.01M17.5 13.5h.01"),
        ("printer", "Impressora", "Printer", "M7 8V3h10v5M7 17H3v-7a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v7h-4M7 13h10v8H7z"),
        ("speaker", "Caixa de som / assistente", "Speaker / assistant", "M6 2.5h12v19H6zM12 14.5m-3 0a3 3 0 1 0 6 0a3 3 0 1 0-6 0M12 6.5h.01"),
        ("camera", "Câmera", "Camera", "M2.5 7h12v10h-12zM14.5 10.5l7-3.5v10l-7-3.5"),
        ("iot", "Casa inteligente", "Smart home", "M9 18h6M10 21h4M12 3a6 6 0 0 0-4 10.5c.7.7 1 1.5 1 2.5h6c0-1 .3-1.8 1-2.5A6 6 0 0 0 12 3z"),
        ("watch", "Relógio", "Watch", "M7.5 6.5h9v11h-9zM9 6.5l1-3.5h4l1 3.5M9 17.5l1 3.5h4l1-3.5"),
        ("unknown", "Aparelho", "Device", "M12 12m-9 0a9 9 0 1 0 18 0a9 9 0 1 0-18 0M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6v.6M12 17h.01"),
    };

    public static string Label(string id) => All.FirstOrDefault(t => t.id == id) is { id: not null } t ? Loc.T(t.pt, t.en) : Loc.T("Aparelho", "Device");
    public static string Icon(string id) => All.FirstOrDefault(t => t.id == id).icon ?? All[^1].icon;

    static readonly (string type, string[] words)[] Rules =
    {
        ("console", new[] { "playstation", "ps4", "ps5", "xbox", "nintendo", "steam deck" }),
        ("tv", new[] { "chromecast", "roku", "fire tv", "firetv", "bravia", "webos", "tizen", "smart tv", "smarttv", "android tv", "androidtv", "[tv]", " tv", "tv ", "mediarenderer", "apple tv", "appletv", "mi box", "tcl", "hisense", "aoc", "philco" }),
        ("printer", new[] { "printer", "impressora", "epson", "brother", "canon", "deskjet", "laserjet", "officejet", "kyocera", "lexmark", "ricoh", "xerox" }),
        ("speaker", new[] { "echo", "alexa", "sonos", "homepod", "google home", "nest", "jbl", "bose" }),
        ("camera", new[] { "camera", "câmera", "ipcam", "ipc", "hikvision", "intelbras", "dahua", "wyze", "ezviz", "imou", "yi-", "reolink" }),
        ("watch", new[] { "watch", "galaxy fit", "mi band", "amazfit" }),
        ("tablet", new[] { "ipad", "tablet", "galaxy tab", "-tab", "kindle" }),
        ("iphone", new[] { "iphone" }),
        ("android", new[] { "android", "galaxy", "redmi", "poco", "moto", "pixel", "oneplus", "realme", "xiaomi", "oppo", "infinix", "motorola" }),
        ("phone", new[] { "celular", "phone" }),
        ("router", new[] { "router", "roteador", "gateway", "tp-link", "tplink", "mercusys", "tenda", "ubiquiti", "mikrotik", "repeater", "repetidor", "extender", "mesh", "deco", "access point", "huawei technologies", "zte", "askey", "sagemcom", "arcadyan", "technicolor", "intelbras wifi" }),
        ("iot", new[] { "espressif", "tuya", "shelly", "sonoff", "smart plug", "tomada", "lamp", "lâmpada", "bulb", "positivo casa", "geonav", "broadlink", "ezviz", "philips hue", "hue" }),
        ("notebook", new[] { "book", "laptop", "notebook", "note-", "nb-" }),
        ("desktop", new[] { "desktop", "pc-", "-pc", "imac", "mac mini", "gamer" }),
        ("pc", new[] { "windows", "dell", "lenovo", "asus", "acer", "msi", "micro-star", "gigabyte", "asrock", "realtek", "intel corporate", "hewlett", "raspberry" }),
    };

    public static string Guess(LanDevice d)
    {
        if (d.IsSelf) return Info.CpuInfo.IsLaptop() ? "notebook" : "desktop";
        if (d.IsGateway) return "router";
        // nome anunciado pelo aparelho pesa mais que o fabricante da placa de rede
        foreach (var text in new[] { d.Friendly, d.Model, d.Hostname, d.Vendor })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var t = " " + text.ToLowerInvariant() + " ";
            foreach (var (type, words) in Rules)
                if (words.Any(t.Contains)) return type;
        }
        if (d.RandomMac) return "phone"; // MAC aleatório é quase sempre celular, tablet ou relógio
        return "unknown";
    }
}

/// <summary>Fabricante pelo começo do MAC (lista do Wireshark, baixada uma vez e guardada).</summary>
public static class LanVendors
{
    static readonly string FilePath = Path.Combine(Paths.AppData, "fabricantes.txt");
    static Dictionary<int, string>? _p24;
    static List<(long prefix, int bits, string name)> _long = new();
    static readonly object _lock = new();

    public static async Task EnsureAsync()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length < 100_000 || DateTime.Now - fi.LastWriteTime > TimeSpan.FromDays(90))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var bytes = await Http.Client.GetByteArrayAsync("https://www.wireshark.org/download/automated/data/manuf", cts.Token);
                if (bytes.Length > 100_000)
                {
                    await File.WriteAllBytesAsync(FilePath + ".tmp", bytes);
                    File.Move(FilePath + ".tmp", FilePath, true);
                    lock (_lock) _p24 = null;
                }
            }
        }
        catch (Exception ex) { Paths.LogError("LanVendors.Download", ex); }
    }

    public static string? Lookup(string mac)
    {
        lock (_lock)
        {
            if (_p24 == null) Load();
            var hex = mac.Replace(":", "");
            if (hex.Length != 12) return null;
            long m = Convert.ToInt64(hex, 16);
            foreach (var (prefix, bits, name) in _long)
                if (m >> (48 - bits) == prefix >> (48 - bits)) return name;
            return _p24!.TryGetValue((int)(m >> 24), out var n) ? n : null;
        }
    }

    static void Load()
    {
        _p24 = new();
        _long = new();
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var raw in File.ReadLines(FilePath))
            {
                if (raw.Length < 8 || raw[0] == '#') continue;
                var cols = raw.Split('\t');
                if (cols.Length < 2) continue;
                var key = cols[0].Trim();
                var name = Clean(cols.Length >= 3 && cols[2].Trim().Length > 0 ? cols[2].Trim() : cols[1].Trim());
                int bits = 24;
                int slash = key.IndexOf('/');
                if (slash > 0) { bits = int.Parse(key[(slash + 1)..]); key = key[..slash]; }
                var hex = key.Replace(":", "").Replace("-", "");
                if (hex.Length < 6) continue;
                long v = Convert.ToInt64(hex.PadRight(12, '0')[..12], 16);
                if (bits == 24) _p24[(int)(v >> 24)] = name;
                else _long.Add((v, bits, name));
            }
            _long.Sort((a, b) => b.bits.CompareTo(a.bits)); // prefixo mais específico primeiro
        }
        catch (Exception ex) { Paths.LogError("LanVendors.Load", ex); }
    }

    static readonly string[] Suffixes = { " Co.,Ltd", " Co., Ltd", " Co.,Ltd.", " Co., Ltd.", ", Inc.", " Inc.", ", Inc", " Inc", " Corporation", " Corp.", " Corp", " Limited", " Ltd.", " Ltd", " LLC", " GmbH", " S.A.", " SA", " AG", " Technologies", " Technology", " Electronics", " International", " Communication", " Communications", " Co.", ",", "." };

    /// <summary>"Samsung Electronics Co.,Ltd" → "Samsung".</summary>
    static string Clean(string name)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in Suffixes)
                if (name.Length > s.Length + 2 && name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { name = name[..^s.Length].TrimEnd(); changed = true; }
        }
        return name;
    }
}

/// <summary>
/// Descobre os aparelhos da rede local sem precisar do roteador: ARP em todos os endereços da sub-rede,
/// ping para latência e nomes por DNS reverso, NetBIOS, mDNS (Bonjour) e UPnP/SSDP.
/// Em segundo plano repete a varredura e avisa pela bandeja quando um aparelho novo entra.
/// </summary>
public static class LanScanner
{
    static readonly string DataFile = Path.Combine(Paths.AppData, "rede.json");
    static readonly object _lock = new();
    static readonly SemaphoreSlim _scanGate = new(1, 1);
    static LanData? _data;
    static Timer? _timer;

    public static LanNet? Net { get; private set; }
    public static bool Scanning { get; private set; }
    public static string? Error { get; private set; }
    /// <summary>Disparado (em qualquer thread) quando a lista muda.</summary>
    public static event Action? Changed;
    /// <summary>Progresso da varredura em andamento (0–100, texto).</summary>
    public static event Action<double, string>? Progress;

    static LanData Data
    {
        get
        {
            lock (_lock) return _data ??= Json.Load<LanData>(DataFile);
        }
    }

    public static DateTime? LastScan => Data.LastScan;

    /// <summary>Aparelhos da rede atual (ou de todas, se ainda não souber qual é a atual).</summary>
    public static List<LanDevice> Devices()
    {
        var d = Data;
        lock (_lock)
        {
            string? net = CurrentNetworkId;
            return d.Devices.Where(x => net == null || x.Network == net).ToList();
        }
    }

    static string? CurrentNetworkId { get; set; }

    /// <summary>Descobre em qual rede o PC está (rápido: o MAC do roteador já está no cache ARP). Chamar fora da thread da interface.</summary>
    public static void EnsureNetwork()
    {
        if (CurrentNetworkId != null) return;
        var net = DetectNetwork();
        Net = net;
        if (net != null) CurrentNetworkId = Arp(net.Gateway) ?? "gw-" + net.Gateway;
    }

    public static void Save()
    {
        lock (_lock) if (_data != null) Json.Save(DataFile, _data);
        Changed?.Invoke();
    }

    public static void Update(LanDevice d, string? name, string? type)
    {
        lock (_lock)
        {
            d.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            d.Type = type;
            d.Known = true;
        }
        Save();
    }

    public static void Acknowledge(LanDevice d)
    {
        lock (_lock) d.Known = true;
        Save();
    }

    public static void AcknowledgeAll()
    {
        lock (_lock) foreach (var d in Devices()) d.Known = true;
        Save();
    }

    public static void Forget(LanDevice d)
    {
        lock (_lock) Data.Devices.Remove(d);
        Save();
    }

    // ------------------------------------------------------------------ monitoramento em segundo plano

    /// <summary>Liga/desliga a varredura periódica conforme as configurações (só depois da primeira varredura manual).</summary>
    public static void Start()
    {
        var cfg = Settings.Current.Lan;
        bool on = cfg.Monitor && Data.Baselines.Count > 0;
        var every = TimeSpan.FromMinutes(Math.Clamp(cfg.IntervalMin, 1, 120));
        if (!on) { _timer?.Dispose(); _timer = null; return; }
        if (_timer == null) _timer = new Timer(_ => _ = ScanAsync(background: true), null, TimeSpan.FromSeconds(45), every);
        else _timer.Change(every, every);
    }

    // ------------------------------------------------------------------ varredura

    public sealed record Result(int Online, List<LanDevice> NewDevices);

    public static async Task<Result?> ScanAsync(bool background = false)
    {
        if (!await _scanGate.WaitAsync(0)) return null;
        Scanning = true;
        Changed?.Invoke();
        try
        {
            return await Task.Run(() => ScanCore(background));
        }
        catch (Exception ex)
        {
            Paths.LogError("LanScanner.Scan", ex);
            Error = ex.Message;
            return null;
        }
        finally
        {
            Scanning = false;
            _scanGate.Release();
            Changed?.Invoke();
        }
    }

    static void Report(double pct, string text) => Progress?.Invoke(pct, text);

    static async Task<Result?> ScanCore(bool background)
    {
        Report(0, Loc.T("Identificando a rede…", "Detecting the network…"));
        var net = DetectNetwork();
        Net = net;
        if (net == null)
        {
            Error = Loc.T("O PC não está conectado a uma rede local com roteador.", "This PC isn't connected to a local network with a router.");
            return null;
        }
        Error = null;
        var vendorsTask = LanVendors.EnsureAsync();
        var ssdpTask = SsdpDiscoverAsync(net.LocalIp);

        // 1) ARP em todos os endereços (responde mesmo quando o aparelho bloqueia ping)
        var found = new ConcurrentDictionary<string, string>(); // ip → mac
        int done = 0, total = net.Hosts.Length;
        RunThreads(net.Hosts, 128, ip =>
        {
            if (Arp(ip) is string mac) found[ip.ToString()] = mac;
            int n = Interlocked.Increment(ref done);
            if (n % 8 == 0) Report(n * 70.0 / total, Loc.T($"Procurando aparelhos… {n}/{total}", $"Looking for devices… {n}/{total}"));
        });
        // 2) segunda chance para aparelhos conhecidos que não responderam (celular em repouso demora)
        var known = Devices().Where(d => d.Ip != null && !found.ContainsKey(d.Ip)).Select(d => IPAddress.TryParse(d.Ip, out var a) ? a : null).OfType<IPAddress>()
            .Where(a => net.Hosts.Contains(a)).ToList();
        RunThreads(known, 32, ip => { if (Arp(ip) is string mac) found[ip.ToString()] = mac; });
        found[net.LocalIp.ToString()] = net.LocalMac;

        string gwMac = found.TryGetValue(net.Gateway.ToString(), out var g) ? g : "gw-" + net.Gateway;
        CurrentNetworkId = gwMac;

        // 3) nomes e latência
        Report(72, Loc.T($"Identificando {found.Count} aparelhos…", $"Identifying {found.Count} devices…"));
        var ssdp = await ssdpTask;
        await vendorsTask;
        var infos = await Task.WhenAll(found.Select(async kv =>
        {
            var ip = IPAddress.Parse(kv.Key);
            bool self = ip.Equals(net.LocalIp);
            var ping = self ? Task.FromResult<double?>(0) : PingAsync(ip);
            var dns = ReverseDnsAsync(ip);
            var nb = self ? Task.FromResult<string?>(null) : NetBiosAsync(ip);
            var md = self ? Task.FromResult<string?>(null) : MdnsAsync(ip);
            var up = ssdp.TryGetValue(kv.Key, out var loc) ? UpnpAsync(loc) : Task.FromResult<(string?, string?)>((null, null));
            await Task.WhenAll(ping, dns, nb, md, up);
            string? host = self ? Environment.MachineName : md.Result ?? nb.Result ?? dns.Result;
            return (ip: kv.Key, mac: kv.Value, self, latency: ping.Result, host, upnp: up.Result);
        }));

        // 4) mescla com o histórico
        Report(96, Loc.T("Salvando…", "Saving…"));
        var now = DateTime.Now;
        var gap = TimeSpan.FromMinutes(Math.Max(12, Settings.Current.Lan.IntervalMin * 2.5));
        var news = new List<LanDevice>();
        lock (_lock)
        {
            var data = Data;
            bool baseline = data.Baselines.Contains(gwMac);
            var mine = data.Devices.Where(d => d.Network == gwMac).ToList();
            foreach (var d in mine) { d.Online = false; d.LatencyMs = null; }
            foreach (var i in infos)
            {
                var d = mine.FirstOrDefault(x => x.Mac == i.mac);
                if (d == null)
                {
                    d = new LanDevice { Mac = i.mac, Network = gwMac, FirstSeen = now, Known = !baseline };
                    data.Devices.Add(d);
                    mine.Add(d);
                    if (baseline) news.Add(d);
                }
                // outro aparelho que usava este IP antes perde o IP
                foreach (var other in mine) if (other != d && other.Ip == i.ip) other.Ip = null;
                d.Ip = i.ip;
                d.IsSelf = i.self;
                d.IsGateway = i.ip == net.Gateway.ToString();
                d.Online = true;
                d.LatencyMs = i.latency;
                d.LastSeen = now;
                if (i.host != null) d.Hostname = i.host;
                d.Vendor = d.RandomMac ? null : LanVendors.Lookup(i.mac) ?? d.Vendor;
                if (i.upnp.Item1 != null) d.Friendly = i.upnp.Item1;
                if (i.upnp.Item2 != null) d.Model = i.upnp.Item2;
                var last = d.Sessions.LastOrDefault();
                if (last != null && now - last.End <= gap) last.End = now;
                else d.Sessions.Add(new LanSession { Start = now, End = now });
                d.Sessions.RemoveAll(s => now - s.End > TimeSpan.FromDays(30));
                if (d.Sessions.Count > 500) d.Sessions.RemoveRange(0, d.Sessions.Count - 500);
            }
            if (!baseline) data.Baselines.Add(gwMac);
            data.LastScan = now;
            Json.Save(DataFile, data);
        }
        Report(100, "");
        Changed?.Invoke();
        if (!background) Start(); // primeira varredura habilita o monitoramento

        if (news.Count > 0 && Settings.Current.Lan.NotifyNew)
        {
            string text = news.Count == 1
                ? $"{news[0].DisplayName} · {news[0].Ip}"
                : Loc.T($"{news.Count} aparelhos novos, entre eles {news[0].DisplayName}", $"{news.Count} new devices, including {news[0].DisplayName}");
            Notifier.Notify(Loc.T("Aparelho novo na rede", "New device on your network"), text, warning: false);
        }
        return new Result(infos.Length, news);
    }

    /// <summary>Executa em N threads próprias (o ARP do Windows bloqueia até ~3 s por endereço ausente).</summary>
    static void RunThreads<T>(IEnumerable<T> items, int threads, Action<T> work)
    {
        var q = new ConcurrentQueue<T>(items);
        var list = Enumerable.Range(0, Math.Min(threads, Math.Max(1, q.Count))).Select(_ => new Thread(() =>
        {
            while (q.TryDequeue(out var it))
            {
                try { work(it); } catch { }
            }
        }) { IsBackground = true, Name = "Pulso.Lan" }).ToList();
        list.ForEach(t => t.Start());
        list.ForEach(t => t.Join());
    }

    // ------------------------------------------------------------------ rede atual

    static LanNet? DetectNetwork()
    {
        var best = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => (n, p: n.GetIPProperties()))
            .Select(x => (x.n, x.p,
                gw: x.p.GatewayAddresses.Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)),
                ua: x.p.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)))
            .Where(x => x.gw != null && x.ua != null)
            // cabo e Wi-Fi antes de adaptadores virtuais (VPN, Hyper-V…)
            .OrderBy(x => x.n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .ThenBy(x => x.n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();
        if (best.n == null) return null;

        var local = best.ua!.Address;
        int prefix = best.ua.PrefixLength;
        if (prefix < 22) prefix = 24; // redes enormes: varre só o bloco /24 do PC
        var b = local.GetAddressBytes();
        uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        uint mask = uint.MaxValue << (32 - prefix);
        uint first = (ip & mask) + 1, last = (ip | ~mask) - 1;
        var hosts = new List<IPAddress>();
        for (uint a = first; a <= last && hosts.Count < 1024; a++)
            hosts.Add(new IPAddress(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a }));

        var mac = string.Join(":", best.n.GetPhysicalAddress().GetAddressBytes().Select(x => x.ToString("X2")));
        string kind = best.n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : best.n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? Loc.T("Cabo", "Ethernet") : best.n.NetworkInterfaceType.ToString();
        return new LanNet(best.n.Name, kind, local, mac, best.gw!, prefix, hosts.ToArray());
    }

    // ------------------------------------------------------------------ ARP, ping, nomes

    [DllImport("iphlpapi.dll")]
    static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref int len);

    /// <summary>Linux: provoca a resolução ARP com um datagrama UDP e lê o resultado em /proc/net/arp.</summary>
    static string? ArpLinux(IPAddress ip)
    {
        string target = ip.ToString();
        try
        {
            using var u = new System.Net.Sockets.UdpClient();
            u.Send(new byte[] { 0 }, 1, new IPEndPoint(ip, 9));
        }
        catch { }
        for (int i = 0; i < 10; i++)
        {
            try
            {
                foreach (var line in System.IO.File.ReadLines("/proc/net/arp").Skip(1))
                {
                    var c = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // IP address | HW type | Flags | HW address | Mask | Device
                    if (c.Length < 4 || c[0] != target) continue;
                    if (c[2] == "0x0" || c[3] == "00:00:00:00:00:00") break; // ainda resolvendo
                    return c[3].ToUpperInvariant();
                }
            }
            catch { }
            Thread.Sleep(40);
        }
        return null;
    }

    static string? Arp(IPAddress ip)
    {
        if (OperatingSystem.IsLinux()) return ArpLinux(ip);
        var mac = new byte[6];
        int len = mac.Length;
        if (SendARP(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), 0, mac, ref len) != 0 || len < 6) return null;
        if (mac.All(x => x == 0) || mac.All(x => x == 0xFF)) return null;
        return string.Join(":", mac.Take(6).Select(x => x.ToString("X2")));
    }

    static async Task<double?> PingAsync(IPAddress ip)
    {
        try
        {
            using var p = new Ping();
            for (int i = 0; i < 2; i++)
            {
                var r = await p.SendPingAsync(ip, 900);
                if (r.Status == IPStatus.Success) return r.RoundtripTime;
            }
        }
        catch { }
        return null;
    }

    static async Task<string?> ReverseDnsAsync(IPAddress ip)
    {
        try
        {
            var e = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(2));
            return CleanHost(e.HostName, ip);
        }
        catch { return null; }
    }

    static string? CleanHost(string? h, IPAddress ip)
    {
        if (string.IsNullOrWhiteSpace(h) || h == ip.ToString()) return null;
        h = h.Trim().TrimEnd('.');
        // tira sufixos de domínio local (.lan, .home, .local, .localdomain…)
        int dot = h.IndexOf('.');
        if (dot > 0) h = h[..dot];
        return h.Length == 0 || IPAddress.TryParse(h, out _) ? null : h;
    }

    /// <summary>Nome Windows (NetBIOS, porta 137): pergunta "quem é você" direto ao aparelho.</summary>
    static async Task<string?> NetBiosAsync(IPAddress ip)
    {
        try
        {
            using var u = new UdpClient();
            var q = new byte[50];
            q[0] = 0x13; q[1] = 0x37; q[5] = 1;         // id, 1 pergunta
            q[12] = 0x20; q[13] = (byte)'C'; q[14] = (byte)'K';
            for (int i = 15; i < 45; i++) q[i] = (byte)'A'; // nome "*" codificado
            q[46] = 0x00; q[47] = 0x21; q[48] = 0x00; q[49] = 0x01; // NBSTAT, IN
            await u.SendAsync(q, q.Length, new IPEndPoint(ip, 137));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var r = (await u.ReceiveAsync(cts.Token)).Buffer;
            if (r.Length < 57) return null;
            int count = r[56], off = 57;
            for (int i = 0; i < count && off + 18 <= r.Length; i++, off += 18)
            {
                byte suffix = r[off + 15];
                bool group = (r[off + 16] & 0x80) != 0;
                if (suffix == 0x00 && !group) return Encoding.ASCII.GetString(r, off, 15).Trim();
            }
        }
        catch { }
        return null;
    }

    /// <summary>Nome Bonjour/mDNS (iPhone, Mac, Android, Chromecast, impressoras): consulta reversa direto ao aparelho.</summary>
    static async Task<string?> MdnsAsync(IPAddress ip)
    {
        try
        {
            var b = ip.GetAddressBytes();
            var name = $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
            var q = new List<byte> { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            foreach (var label in name.Split('.')) { q.Add((byte)label.Length); q.AddRange(Encoding.ASCII.GetBytes(label)); }
            q.AddRange(new byte[] { 0x00, 0x00, 0x0C, 0x00, 0x01 }); // PTR, IN
            using var u = new UdpClient();
            await u.SendAsync(q.ToArray(), q.Count, new IPEndPoint(ip, 5353));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var r = (await u.ReceiveAsync(cts.Token)).Buffer;
            int qd = r[4] << 8 | r[5], an = r[6] << 8 | r[7];
            int off = 12;
            for (int i = 0; i < qd; i++) { ReadName(r, ref off); off += 4; }
            for (int i = 0; i < an && off + 10 <= r.Length; i++)
            {
                ReadName(r, ref off);
                int type = r[off] << 8 | r[off + 1];
                int len = r[off + 8] << 8 | r[off + 9];
                off += 10;
                if (type == 12)
                {
                    int p = off;
                    return CleanHost(ReadName(r, ref p), ip);
                }
                off += len;
            }
        }
        catch { }
        return null;
    }

    static string ReadName(byte[] r, ref int off)
    {
        var parts = new List<string>();
        int jumps = 0, p = off;
        bool jumped = false;
        while (p < r.Length && jumps < 10)
        {
            int len = r[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (!jumped) off = p + 2;
                p = (len & 0x3F) << 8 | r[p + 1];
                jumped = true; jumps++;
                continue;
            }
            parts.Add(Encoding.UTF8.GetString(r, p + 1, len));
            p += len + 1;
        }
        if (!jumped) off = p;
        return string.Join(".", parts);
    }

    /// <summary>UPnP/SSDP: TVs, roteadores, consoles e impressoras se anunciam com nome e modelo.</summary>
    static async Task<Dictionary<string, string>> SsdpDiscoverAsync(IPAddress local)
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var u = new UdpClient(new IPEndPoint(local, 0));
            var msg = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: upnp:rootdevice\r\n\r\n");
            var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            await u.SendAsync(msg, msg.Length, ep);
            await Task.Delay(150);
            await u.SendAsync(msg, msg.Length, ep);
            var end = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < end)
            {
                using var cts = new CancellationTokenSource(end - DateTime.UtcNow);
                UdpReceiveResult r;
                try { r = await u.ReceiveAsync(cts.Token); } catch (OperationCanceledException) { break; }
                var text = Encoding.ASCII.GetString(r.Buffer);
                foreach (var line in text.Split("\r\n"))
                    if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                        map.TryAdd(r.RemoteEndPoint.Address.ToString(), line[9..].Trim());
            }
        }
        catch (Exception ex) { Paths.LogError("LanScanner.Ssdp", ex); }
        return map;
    }

    static async Task<(string? friendly, string? model)> UpnpAsync(string location)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var xml = await Http.Client.GetStringAsync(location, cts.Token);
            var dev = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
            if (dev == null) return (null, null);
            string? V(string n) => dev.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim() is { Length: > 0 } s ? s : null;
            var maker = V("manufacturer");
            var model = V("modelName");
            string? full = maker != null && model != null && !model.StartsWith(maker.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? maker + " " + model : model ?? maker;
            return (V("friendlyName"), full);
        }
        catch { return (null, null); }
    }
}
