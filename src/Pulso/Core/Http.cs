using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Pulso.Core;

public static class Http
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 16,
        };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Pulso/1.0");
        c.DefaultRequestHeaders.Referrer = new Uri("https://speed.cloudflare.com/");
        return c;
    }

    public sealed class PublicInfo
    {
        public string? Ip, Ipv4, Ipv6, Isp, City, Region, Country, Asn, Colo;
    }

    static PublicInfo? _cache;
    static DateTime _cacheTime;

    /// <summary>IP público e provedor (via Cloudflare). Resultado guardado por 5 minutos.</summary>
    public static async Task<PublicInfo> GetPublicInfoAsync(bool force = false)
    {
        if (!force && _cache != null && DateTime.Now - _cacheTime < TimeSpan.FromMinutes(5)) return _cache;
        var r = new PublicInfo();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var json = await Client.GetStringAsync("https://speed.cloudflare.com/meta", cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? S(string n) => root.TryGetProperty(n, out var v) ? v.ToString() : null;
            r.Ip = S("clientIp");
            r.Isp = S("asOrganization");
            r.City = S("city");
            r.Region = S("region");
            r.Country = S("country");
            r.Asn = S("asn") is string a ? "AS" + a : null;
            if (root.TryGetProperty("colo", out var colo) && colo.ValueKind == JsonValueKind.Object && colo.TryGetProperty("city", out var cc)) r.Colo = cc.GetString();
        }
        catch { }
        r.Ipv4 = await Trace("https://1.1.1.1/cdn-cgi/trace");
        r.Ipv6 = await Trace("https://[2606:4700:4700::1111]/cdn-cgi/trace");
        r.Ip ??= r.Ipv4 ?? r.Ipv6;
        _cache = r;
        _cacheTime = DateTime.Now;
        return r;
    }

    static async Task<string?> Trace(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var txt = await Client.GetStringAsync(url, cts.Token);
            foreach (var line in txt.Split('\n'))
                if (line.StartsWith("ip=")) return line[3..].Trim();
        }
        catch { }
        return null;
    }
}
