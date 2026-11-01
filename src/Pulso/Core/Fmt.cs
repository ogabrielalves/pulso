using System.Globalization;

namespace Pulso.Core;

public enum Unit { None, Percent, Temp, MHz, Watt, Volt, MB, GB, BytesPerSec, Fps, Ms, Rpm, Score }

public static class Fmt
{
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Loc.Code(Loc.Current));

    public static string NA => Loc.T("não disponível", "not available");
    public static string Dash => "—";

    public static double ToDisplayTemp(double c) => Settings.Current.Fahrenheit ? c * 9 / 5 + 32 : c;
    public static string TempUnit => Settings.Current.Fahrenheit ? "°F" : "°C";

    public static string N(double v, int decimals = 0) => v.ToString("N" + decimals, Culture);

    /// <summary>Formata um valor com unidade. Nulo vira "—" (compacto) para cartões e gráficos.</summary>
    public static string Value(double? v, Unit unit, bool compact = false)
    {
        if (v is null || double.IsNaN(v.Value)) return Dash;
        double x = v.Value;
        return unit switch
        {
            Unit.Percent => N(x, 0) + "%",
            Unit.Temp => N(ToDisplayTemp(x), 0) + " " + TempUnit,
            Unit.MHz => x >= 1000 && !compact ? N(x / 1000, 2) + " GHz" : N(x, 0) + " MHz",
            Unit.Watt => N(x, x < 10 ? 1 : 0) + " W",
            Unit.Volt => N(x, 3) + " V",
            Unit.MB => x >= 1024 ? N(x / 1024, 1) + " GB" : N(x, 0) + " MB",
            Unit.GB => N(x, 1) + " GB",
            Unit.BytesPerSec => Rate(x),
            Unit.Fps => N(x, 0) + " FPS",
            Unit.Ms => N(x, 1) + " ms",
            Unit.Rpm => N(x, 0) + " RPM",
            Unit.Score => N(x, 0),
            _ => N(x, 1),
        };
    }

    /// <summary>Somente o número convertido (sem unidade), usado em overlay e CSV.</summary>
    public static string Bare(double? v, Unit unit)
    {
        if (v is null || double.IsNaN(v.Value)) return "-";
        double x = unit == Unit.Temp ? ToDisplayTemp(v.Value) : v.Value;
        int dec = unit switch { Unit.Volt => 3, Unit.Ms => 1, Unit.GB => 1, Unit.Watt => 1, _ => 0 };
        return x.ToString("N" + dec, Culture);
    }

    public static string UnitSuffix(Unit unit) => unit switch
    {
        Unit.Percent => "%",
        Unit.Temp => TempUnit,
        Unit.MHz => "MHz",
        Unit.Watt => "W",
        Unit.Volt => "V",
        Unit.MB => "MB",
        Unit.GB => "GB",
        Unit.BytesPerSec => "MB/s",
        Unit.Fps => "FPS",
        Unit.Ms => "ms",
        Unit.Rpm => "RPM",
        _ => "",
    };

    public static string Bytes(double bytes, int dec = 1)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (Math.Abs(bytes) >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
        return N(bytes, i == 0 ? 0 : dec) + " " + u[i];
    }

    /// <summary>Capacidade de disco no padrão do fabricante (base 1000).</summary>
    public static string Capacity(double bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (Math.Abs(bytes) >= 1000 && i < u.Length - 1) { bytes /= 1000; i++; }
        return N(bytes, i >= 3 ? 1 : 0) + " " + u[i];
    }

    public static string Rate(double bytesPerSec)
    {
        if (bytesPerSec < 1024 * 1024) return N(bytesPerSec / 1024, 0) + " KB/s";
        return N(bytesPerSec / 1024 / 1024, 1) + " MB/s";
    }

    public static string Mbps(double bitsPerSec) => N(bitsPerSec / 1_000_000, bitsPerSec < 10_000_000 ? 2 : 1) + " Mbps";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return Loc.T($"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}min", $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m");
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}min";
        return $"{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? NA : s.Trim();
    public static string Or(double? v, Unit u) => v is null ? NA : Value(v, u);
    public static string YesNo(bool b) => b ? Loc.T("Sim", "Yes") : Loc.T("Não", "No");
    public static string YesNo(bool? b) => b is null ? NA : YesNo(b.Value);
}
