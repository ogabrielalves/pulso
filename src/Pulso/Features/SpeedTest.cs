using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Pulso.Core;

namespace Pulso.Features;

public sealed class SpeedServer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Base { get; set; } = "";
    public string Dl { get; set; } = "";
    public string Ul { get; set; } = "";
    public string PingPath { get; set; } = "";
    public bool Cloudflare { get; set; }
    public string Host => Uri.TryCreate(Base, UriKind.Absolute, out var u) ? u.Host : Base;
    public override string ToString() => Name;
}

public sealed class SpeedResult
{
    public DateTime Date { get; set; } = DateTime.Now;
    public string Server { get; set; } = "";
    public double Down { get; set; }
    public double Up { get; set; }
    public double Ping { get; set; }
    public double Jitter { get; set; }
    public double? Loss { get; set; }
}

/// <summary>Teste de velocidade via HTTP (Cloudflare ou servidores LibreSpeed), sem bibliotecas externas.</summary>
public static class SpeedTest
{
    static string HistoryFile => Path.Combine(Paths.History, "internet.json");
    static List<SpeedResult>? _history;
    public static List<SpeedResult> History => _history ??= Json.Load<List<SpeedResult>>(HistoryFile);

    public static void SaveResult(SpeedResult r)
    {
        History.Insert(0, r);
        while (History.Count > 100) History.RemoveAt(History.Count - 1);
        Json.Save(HistoryFile, History);
    }

    static List<SpeedServer>? _servers;

    public static async Task<List<SpeedServer>> ServersAsync()
    {
        if (_servers != null) return _servers;
        var pub = await Http.GetPublicInfoAsync();
        var list = new List<SpeedServer>
        {
            new()
            {
                Id = "cloudflare", Cloudflare = true, Base = "https://speed.cloudflare.com",
                Name = Loc.T("Cloudflare · automático", "Cloudflare · automatic") + (pub.Colo != null ? $" ({pub.Colo})" : ""),
            },
        };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var json = await Http.Client.GetStringAsync("https://librespeed.org/backend-servers/servers.php", cts.Token);
            using var doc = JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string? S(string n) => e.TryGetProperty(n, out var v) ? v.GetString() : null;
                var server = S("server");
                if (string.IsNullOrEmpty(server)) continue;
                if (server.StartsWith("//")) server = "https:" + server;
                if (!server.EndsWith('/')) server += "/";
                list.Add(new SpeedServer
                {
                    Id = "ls:" + e.GetProperty("id").ToString(), Name = S("name") ?? server, Base = server,
                    Dl = S("dlURL") ?? "garbage.php", Ul = S("ulURL") ?? "empty.php", PingPath = S("pingURL") ?? "empty.php",
                });
            }
        }
        catch { }
        return _servers = list;
    }

    public sealed record Progress(string Phase, double Mbps, double Fraction, double? Ping = null, double? Jitter = null);

    public static async Task<SpeedResult> RunAsync(SpeedServer s, IProgress<Progress> progress, CancellationToken ct)
    {
        var r = new SpeedResult { Server = s.Name };

        // 1) latência (HTTP) — conexão já aquecida
        progress.Report(new Progress("ping", 0, 0.02));
        var lat = new List<double>();
        for (int i = 0; i < 18 && !ct.IsCancellationRequested; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, PingUrl(s));
                using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();
                if (i >= 2) lat.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch when (!ct.IsCancellationRequested) { }
        }
        ct.ThrowIfCancellationRequested();
        if (lat.Count > 0)
        {
            var sorted = lat.OrderBy(x => x).ToList();
            r.Ping = sorted[sorted.Count / 2];
            r.Jitter = lat.Count > 1 ? lat.Zip(lat.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
        }
        progress.Report(new Progress("ping", 0, 0.08, r.Ping, r.Jitter));

        // 2) download
        r.Down = await Measure(s, download: true, TimeSpan.FromSeconds(10), streams: 6, (mbps, f) => progress.Report(new Progress("down", mbps, 0.08 + f * 0.42, r.Ping, r.Jitter)), ct);
        // 3) upload
        r.Up = await Measure(s, download: false, TimeSpan.FromSeconds(10), streams: 4, (mbps, f) => progress.Report(new Progress("up", mbps, 0.5 + f * 0.42, r.Ping, r.Jitter)), ct);
        // 4) perda de pacotes (ICMP)
        progress.Report(new Progress("loss", r.Up, 0.94, r.Ping, r.Jitter));
        r.Loss = await PacketLoss(s.Cloudflare ? "1.1.1.1" : s.Host, 30, ct);
        progress.Report(new Progress("done", r.Down, 1, r.Ping, r.Jitter));
        return r;
    }

    static string PingUrl(SpeedServer s) => s.Cloudflare ? "https://speed.cloudflare.com/__down?bytes=0" : s.Base + s.PingPath + (s.PingPath.Contains('?') ? "&" : "?") + "r=" + Random.Shared.Next();
    static string DownUrl(SpeedServer s) => s.Cloudflare ? "https://speed.cloudflare.com/__down?bytes=50000000" : s.Base + s.Dl + (s.Dl.Contains('?') ? "&" : "?") + "ckSize=100&r=" + Random.Shared.Next();
    static string UpUrl(SpeedServer s) => s.Cloudflare ? "https://speed.cloudflare.com/__up" : s.Base + s.Ul + (s.Ul.Contains('?') ? "&" : "?") + "r=" + Random.Shared.Next();

    static readonly byte[] UploadChunk = CreateChunk();
    static byte[] CreateChunk() { var b = new byte[256 * 1024]; Random.Shared.NextBytes(b); return b; }

    /// <summary>Várias conexões em paralelo; descarta os 2 primeiros segundos (aceleração da conexão).</summary>
    static async Task<double> Measure(SpeedServer s, bool download, TimeSpan duration, int streams, Action<double, double> report, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        var warm = TimeSpan.FromSeconds(2);
        long warmBytes = -1;

        async Task Worker()
        {
            var buf = new byte[64 * 1024];
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (download)
                    {
                        using var resp = await Http.Client.GetAsync(DownUrl(s), HttpCompletionOption.ResponseHeadersRead, cts.Token);
                        await using var st = await resp.Content.ReadAsStreamAsync(cts.Token);
                        int n;
                        while ((n = await st.ReadAsync(buf, cts.Token)) > 0) Interlocked.Add(ref bytes, n);
                    }
                    else
                    {
                        using var content = new CountingContent(8 * 1024 * 1024, n => Interlocked.Add(ref bytes, n), cts.Token);
                        using var resp = await Http.Client.PostAsync(UpUrl(s), content, cts.Token);
                    }
                }
                catch when (cts.IsCancellationRequested) { return; }
                catch { await Task.Delay(200, CancellationToken.None); }
            }
        }

        var workers = Enumerable.Range(0, streams).Select(_ => Task.Run(Worker)).ToList();
        double result = 0;
        while (sw.Elapsed < duration + warm && !outer.IsCancellationRequested)
        {
            await Task.Delay(250, CancellationToken.None);
            long b = Interlocked.Read(ref bytes);
            if (sw.Elapsed >= warm)
            {
                if (warmBytes < 0) warmBytes = b;
                double secs = (sw.Elapsed - warm).TotalSeconds;
                if (secs > 0.2) result = (b - warmBytes) * 8 / secs / 1e6;
            }
            else result = b * 8 / Math.Max(0.1, sw.Elapsed.TotalSeconds) / 1e6;
            report(result, Math.Min(1, sw.Elapsed.TotalSeconds / (duration + warm).TotalSeconds));
        }
        cts.Cancel();
        try { await Task.WhenAll(workers); } catch { }
        outer.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Corpo de upload que informa quantos bytes já foram enviados.</summary>
    sealed class CountingContent(int length, Action<int> onBytes, CancellationToken ct) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            int sent = 0;
            while (sent < length && !ct.IsCancellationRequested)
            {
                int n = Math.Min(UploadChunk.Length, length - sent);
                await stream.WriteAsync(UploadChunk.AsMemory(0, n), ct);
                sent += n;
                onBytes(n);
            }
        }

        protected override bool TryComputeLength(out long len) { len = length; return true; }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            SerializeToStreamAsync(stream, context).GetAwaiter().GetResult();
    }

    public static async Task<double?> PacketLoss(string host, int count, CancellationToken ct)
    {
        using var p = new Ping();
        int ok = 0, sent = 0;
        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            sent++;
            try { if ((await p.SendPingAsync(host, 1000)).Status == IPStatus.Success) ok++; } catch { }
            await Task.Delay(80, CancellationToken.None);
        }
        if (ok == 0) return null; // ICMP bloqueado: não dá para medir
        return (sent - ok) * 100.0 / sent;
    }
}

/// <summary>Ping contínuo (ICMP; se o destino bloquear ICMP, mede o tempo de conexão TCP na porta 443).</summary>
public sealed class PingMonitor : IDisposable
{
    public string Target { get; private set; } = "";
    public bool UsingTcp { get; private set; }
    public Pulso.Hardware.Series Data { get; } = new(300);
    public double? Last, Min, Max, Avg;
    public int Sent, Lost;
    public double LossPct => Sent > 0 ? Lost * 100.0 / Sent : 0;
    public event Action? Updated;

    CancellationTokenSource? _cts;
    double _sum;
    int _ok;

    public bool Running => _cts != null;

    public void Start(string target)
    {
        Stop();
        Target = target.Trim();
        Data.Clear();
        Sent = Lost = _ok = 0; _sum = 0; Last = Min = Max = Avg = null; UsingTcp = false;
        _cts = new CancellationTokenSource();
        _ = Loop(_cts.Token);
    }

    async Task Loop(CancellationToken ct)
    {
        using var ping = new Ping();
        int icmpFails = 0;
        while (!ct.IsCancellationRequested)
        {
            double? ms = null;
            var sw = Stopwatch.StartNew();
            if (!UsingTcp)
            {
                try
                {
                    var r = await ping.SendPingAsync(Target, 1500);
                    if (r.Status == IPStatus.Success) { ms = r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds; icmpFails = 0; }
                    else icmpFails++;
                }
                catch { icmpFails++; }
                if (icmpFails >= 3 && _ok == 0 && await TcpTime(ct) is double t) { UsingTcp = true; ms = t; Sent = Lost = 0; }
            }
            else ms = await TcpTime(ct);

            Sent++;
            if (ms is double v)
            {
                _ok++; _sum += v;
                Last = v; Min = Math.Min(Min ?? v, v); Max = Math.Max(Max ?? v, v); Avg = _sum / _ok;
            }
            else { Lost++; Last = null; }
            Data.Add(ms);
            Updated?.Invoke();
            try { await Task.Delay(Math.Max(100, 1000 - (int)sw.ElapsedMilliseconds), ct); } catch { break; }
        }
    }

    async Task<double?> TcpTime(CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1500);
            var sw = Stopwatch.StartNew();
            await c.ConnectAsync(Target, 443, cts.Token);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch { return null; }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Dispose() => Stop();
}
