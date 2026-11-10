using System.Globalization;
using System.IO;
using Pulso.Core;

namespace Pulso.Features;

public static partial class Battery
{
    // Linux: /sys/class/power_supply/BAT*. O kernel informa energia (µWh) ou carga (µAh) conforme o fabricante.
    static List<string> LinuxPaths()
    {
        try
        {
            return Directory.EnumerateDirectories("/sys/class/power_supply", "BAT*")
                .Where(d => Platform.Linux.LinuxSensorProvider.Read(Path.Combine(d, "type")) == "Battery")
                .OrderBy(d => d, StringComparer.Ordinal).ToList();
        }
        catch { return new(); }
    }

    static double? Num(string dir, string file) => Platform.Linux.LinuxSensorProvider.ReadNum(Path.Combine(dir, file));
    static string Txt(string dir, string file) => Platform.Linux.LinuxSensorProvider.Read(Path.Combine(dir, file)) ?? "";

    /// <summary>Converte a capacidade (energia µWh ou carga µAh) para mWh.</summary>
    static uint? ToMwh(string dir, string energyFile, string chargeFile)
    {
        if (Num(dir, energyFile) is double e && e > 0) return (uint)(e / 1000);
        if (Num(dir, chargeFile) is double c && c > 0)
        {
            double v = Num(dir, "voltage_min_design") ?? Num(dir, "voltage_now") ?? 0;
            if (v > 0) return (uint)(c / 1000 * v / 1e6);
        }
        return null;
    }

    static List<Info> LinuxReadAll()
    {
        var list = new List<Info>();
        foreach (var dir in LinuxPaths())
        {
            var info = new Info
            {
                DevicePath = dir,
                Name = Txt(dir, "model_name"),
                Manufacturer = Txt(dir, "manufacturer"),
                Serial = Txt(dir, "serial_number"),
                Chemistry = Chem(Txt(dir, "technology")),
                Design = ToMwh(dir, "energy_full_design", "charge_full_design") ?? 0,
                Full = ToMwh(dir, "energy_full", "charge_full") ?? 0,
            };
            if (Num(dir, "cycle_count") is double cc && cc > 0) info.Cycles = (uint)cc;
            if (Num(dir, "temp") is double t) info.TempC = t / 10.0;
            if (Num(dir, "manufacture_year") is double y && Num(dir, "manufacture_month") is double mo && Num(dir, "manufacture_day") is double d && y > 1990)
                try { info.Manufactured = new DateTime((int)y, Math.Clamp((int)mo, 1, 12), Math.Clamp((int)d, 1, 31)); } catch { }
            if (info.Design == 0 && info.Full == 0) { info.Relative = true; info.Design = info.Full = 100; }
            list.Add(info);
        }
        return list;
    }

    static Status? LinuxStatus(Info b)
    {
        string dir = b.DevicePath;
        if (!Directory.Exists(dir)) return null;
        string st = Txt(dir, "status");
        var s = new Status
        {
            Charging = st == "Charging",
            Discharging = st == "Discharging",
            Critical = Txt(dir, "capacity_level") == "Critical",
            Remaining = ToMwh(dir, "energy_now", "charge_now"),
            VoltageMv = Num(dir, "voltage_now") is double v ? (uint)(v / 1000) : null,
        };
        s.OnAc = LinuxAcOnline() ?? !s.Discharging;

        double? mw = null;
        if (Num(dir, "power_now") is double p && p > 0) mw = p / 1000;
        else if (Num(dir, "current_now") is double cur && Num(dir, "voltage_now") is double vv) mw = Math.Abs(cur) * vv / 1e9;
        if (mw is double m) s.RateMw = (int)(s.Discharging ? -m : m);

        if (Num(dir, "capacity") is double cap) s.ChargePct = cap;
        else if (s.Remaining is uint r && b.Full > 0) s.ChargePct = Math.Min(100, r * 100.0 / b.Full);

        if (s.Discharging && s.RateMw is int rd && rd < 0 && s.Remaining is uint rem) s.TimeLeft = TimeSpan.FromHours(rem / (double)-rd);
        else if (s.Charging && s.RateMw is int rc && rc > 0 && s.Remaining is uint rem2 && b.Full > rem2) s.TimeLeft = TimeSpan.FromHours((b.Full - rem2) / (double)rc);
        return s;
    }

    static bool? LinuxAcOnline()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories("/sys/class/power_supply"))
                if (Txt(d, "type") is "Mains" or "USB" && Num(d, "online") == 1) return true;
            return false;
        }
        catch { return null; }
    }
}
