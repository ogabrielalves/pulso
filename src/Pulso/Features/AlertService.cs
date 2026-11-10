using Pulso.Core;
using Pulso.Hardware;

namespace Pulso.Features;

/// <summary>Verifica os limites configurados a cada leitura e avisa pela bandeja do Windows.</summary>
public static class AlertService
{
    static readonly Dictionary<string, DateTime> _last = new();
    static readonly object _lock = new();

    public sealed record ActiveAlert(string MetricId, string Text, DateTime Since);

    /// <summary>Alertas acima do limite agora (exibidos no Painel).</summary>
    public static List<ActiveAlert> Active { get; private set; } = new();

    public static void Start() => SensorHub.Instance.Updated += Check;

    static void Check(Snapshot s)
    {
        var cfg = Settings.Current;
        var hub = SensorHub.Instance;
        var active = new List<ActiveAlert>();
        foreach (var rule in cfg.Alerts)
        {
            if (!rule.Enabled || !hub.MetricById.TryGetValue(rule.MetricId, out var m)) continue;
            if (m.Get(s) is not double v || v < rule.Threshold) continue;
            string text = $"{m.FullLabel}: {Fmt.Value(v, m.Unit)} ({Loc.T("limite", "limit")} {Fmt.Value(rule.Threshold, m.Unit)})";
            var prev = Active.FirstOrDefault(a => a.MetricId == rule.MetricId);
            active.Add(new ActiveAlert(rule.MetricId, text, prev?.Since ?? DateTime.Now));

            if (!cfg.AlertsEnabled) continue;
            lock (_lock)
            {
                if (_last.TryGetValue(rule.MetricId, out var t) && DateTime.Now - t < TimeSpan.FromSeconds(cfg.AlertCooldownSec)) continue;
                _last[rule.MetricId] = DateTime.Now;
            }
            Notifier.Notify(Loc.T("Pulso — alerta", "Pulso — alert"), text);
        }
        Active = active;
    }
}
