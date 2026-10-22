using System.Globalization;
using System.IO;
using System.Text;
using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Features;

/// <summary>Grava todas as leituras em CSV (ex.: durante uma sessão de jogo) para análise posterior.</summary>
public static class CsvLogger
{
    static StreamWriter? _w;
    static List<Metric> _cols = new();
    static IDisposable? _demand, _fps;
    static readonly object _lock = new();

    public static bool Active => _w != null;
    public static string? FilePath { get; private set; }
    public static DateTime Started { get; private set; }
    public static long Rows { get; private set; }
    public static event Action? StateChanged;

    public static void Start()
    {
        lock (_lock)
        {
            if (_w != null) return;
            FilePath = Path.Combine(Paths.Logs, $"sessao_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            _w = new StreamWriter(FilePath, false, new UTF8Encoding(true)) { AutoFlush = false };
            _cols = SensorHub.Instance.MetricList.ToList();
            // Ponto e vírgula: abre direto em colunas no Excel em português.
            _w.WriteLine("Data/Hora;Processo do jogo;" + string.Join(";", _cols.Select(c => $"{c.FullLabel} ({Fmt.UnitSuffix(c.Unit)})")));
            Started = DateTime.Now;
            Rows = 0;
            _demand = SensorHub.Instance.Demand();
            _fps = FpsMonitor.Instance.Acquire();
            SensorHub.Instance.Updated += Write;
        }
        StateChanged?.Invoke();
    }

    static void Write(Snapshot s)
    {
        lock (_lock)
        {
            if (_w == null) return;
            var sb = new StringBuilder();
            sb.Append(s.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(';').Append(s.FpsProcess ?? "");
            var ci = Fmt.Culture;
            foreach (var c in _cols)
            {
                sb.Append(';');
                if (c.Get(s) is double v)
                {
                    double x = c.Unit switch { Unit.Temp => Fmt.ToDisplayTemp(v), Unit.BytesPerSec => v / 1048576.0, _ => v };
                    sb.Append(x.ToString("0.###", ci));
                }
            }
            _w.WriteLine(sb.ToString());
            if (++Rows % 10 == 0) _w.Flush();
        }
    }

    public static void Stop()
    {
        lock (_lock)
        {
            if (_w == null) return;
            SensorHub.Instance.Updated -= Write;
            try { _w.Flush(); _w.Dispose(); } catch { }
            _w = null;
            _demand?.Dispose(); _demand = null;
            _fps?.Dispose(); _fps = null;
        }
        StateChanged?.Invoke();
    }
}
