namespace Pulso.Core;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public sealed class AlertRule
{
    public string MetricId { get; set; } = "cpu.temp";
    public double Threshold { get; set; } = 90;
    public bool Enabled { get; set; } = true;
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public bool OnlyInGames { get; set; } = true;
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopRight;
    public double FontSize { get; set; } = 13;
    public string TextColor { get; set; } = "#FFFFFF";
    public double BackgroundOpacity { get; set; } = 0.72;
    public string Hotkey { get; set; } = "Ctrl+Shift+O";
    public List<string> Items { get; set; } = new() { "fps", "cpu", "gpu", "ram", "temp" };
    public int UpdateMs { get; set; } = 1000;
}

public sealed class AppSettings
{
    public string Language { get; set; } = "pt-BR";
    public string Theme { get; set; } = "dark";
    public bool Fahrenheit { get; set; }
    public int IntervalMs { get; set; } = 1000;
    public int MinimizedIntervalMs { get; set; } = 5000;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool RequestAdmin { get; set; } = true;

    public bool AlertsEnabled { get; set; } = true;
    public int AlertCooldownSec { get; set; } = 120;
    public List<AlertRule> Alerts { get; set; } = new()
    {
        new() { MetricId = "cpu.temp", Threshold = 90 },
        new() { MetricId = "gpu0.temp", Threshold = 85 },
        new() { MetricId = "gpu0.hotspot", Threshold = 100, Enabled = false },
        new() { MetricId = "ram.load", Threshold = 95, Enabled = false },
    };

    /// <summary>Temperaturas que disparam a parada de segurança dos testes de estresse.</summary>
    public double StressCpuLimit { get; set; } = 95;
    public double StressGpuLimit { get; set; } = 90;

    public OverlaySettings Overlay { get; set; } = new();

    public string SpeedTestServer { get; set; } = "cloudflare";
    public string PingTarget { get; set; } = "1.1.1.1";
    public bool PawnIoPromptDismissed { get; set; }
}

public static class Settings
{
    public static AppSettings Current { get; private set; } = new();
    public static event Action? Changed;

    public static void Load()
    {
        Current = Json.Load<AppSettings>(Paths.SettingsFile);
        Current.IntervalMs = Math.Clamp(Current.IntervalMs, 250, 10000);
        Current.Alerts ??= new();
        Current.Overlay ??= new();
        Current.Overlay.Items ??= new();
    }

    public static void Save()
    {
        Json.Save(Paths.SettingsFile, Current);
        Changed?.Invoke();
    }
}
