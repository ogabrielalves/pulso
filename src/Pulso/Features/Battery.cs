using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>
/// Bateria do notebook direto do driver de bateria do Windows (IOCTL_BATTERY_*), sem precisar de administrador:
/// capacidade original × atual (desgaste), ciclos, carga, consumo e um histórico diário da capacidade.
/// </summary>
public static unsafe partial class Battery
{
    public sealed class Info
    {
        public string DevicePath = "", Name = "", Manufacturer = "", Serial = "", Chemistry = "";
        public DateTime? Manufactured;
        public uint Design, Full;        // mWh (ou % se Relative)
        public uint? Cycles;
        public bool Relative;            // a bateria só informa capacidades relativas, não mWh
        public double? TempC;
        public double? WearPct => Design > 0 && Full > 0 && !Relative ? Math.Max(0, 100 - Full * 100.0 / Design) : null;
    }

    public sealed class Status
    {
        public bool OnAc, Charging, Discharging, Critical;
        public uint? Remaining;          // mWh
        public uint? VoltageMv;
        public int? RateMw;              // negativo descarregando
        public double? ChargePct;
        public TimeSpan? TimeLeft;       // até acabar (na bateria) ou até encher (carregando)
    }

    public sealed class HistoryPoint
    {
        public DateTime Date { get; set; }
        public uint Full { get; set; }
        public uint Design { get; set; }
        public uint? Cycles { get; set; }
    }

    static bool? _present;
    public static bool Present => _present ??= (OperatingSystem.IsLinux() ? LinuxPaths().Count : DevicePaths().Count) > 0;

    // ------------------------------------------------------------- leitura

    public static List<Info> ReadAll()
    {
        if (OperatingSystem.IsLinux()) return LinuxReadAll();
        var list = new List<Info>();
        foreach (var path in DevicePaths())
        {
            try
            {
                using var h = Open(path);
                if (h.IsInvalid || Tag(h) is not uint tag) continue;
                var info = new Info { DevicePath = path };
                if (Query<BATTERY_INFORMATION>(h, tag, 0) is BATTERY_INFORMATION bi)
                {
                    info.Relative = (bi.Capabilities & 0x40000000) != 0;
                    info.Design = bi.DesignedCapacity;
                    info.Full = bi.FullChargedCapacity;
                    info.Cycles = bi.CycleCount > 0 ? bi.CycleCount : null;
                    info.Chemistry = Chem(new string(new[] { (char)bi.Chemistry0, (char)bi.Chemistry1, (char)bi.Chemistry2, (char)bi.Chemistry3 }).Trim('\0', ' '));
                }
                info.Name = QueryString(h, tag, 4) ?? "";
                info.Manufacturer = QueryString(h, tag, 6) ?? "";
                info.Serial = QueryString(h, tag, 8) ?? "";
                if (Query<BATTERY_MANUFACTURE_DATE>(h, tag, 5) is BATTERY_MANUFACTURE_DATE md && md.Year > 1990 && md.Month is >= 1 and <= 12 && md.Day is >= 1 and <= 31)
                    try { info.Manufactured = new DateTime(md.Year, md.Month, md.Day); } catch { }
                if (Query<uint>(h, tag, 2) is uint t && t > 2000 && t < 4000) info.TempC = t / 10.0 - 273.15;
                list.Add(info);
            }
            catch (Exception ex) { Paths.LogError("Battery.Read", ex); }
        }
        return list;
    }

    public static Status? ReadStatus(Info b)
    {
        if (OperatingSystem.IsLinux()) return LinuxStatus(b);
        try
        {
            using var h = Open(b.DevicePath);
            if (h.IsInvalid || Tag(h) is not uint tag) return null;
            var wait = new BATTERY_WAIT_STATUS { BatteryTag = tag };
            BATTERY_STATUS st;
            if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS, &wait, sizeof(BATTERY_WAIT_STATUS), &st, sizeof(BATTERY_STATUS), out _, 0)) return null;
            var s = new Status
            {
                OnAc = (st.PowerState & 1) != 0,
                Discharging = (st.PowerState & 2) != 0,
                Charging = (st.PowerState & 4) != 0,
                Critical = (st.PowerState & 8) != 0,
                Remaining = st.Capacity == 0xFFFFFFFF ? null : st.Capacity,
                VoltageMv = st.Voltage == 0xFFFFFFFF ? null : st.Voltage,
                RateMw = st.Rate == unchecked((int)0x80000000) ? null : st.Rate,
            };
            if (s.Remaining is uint r && b.Full > 0) s.ChargePct = Math.Min(100, r * 100.0 / b.Full);
            if (s.Discharging && Query<uint>(h, tag, 3) is uint secs && secs != 0xFFFFFFFF && secs > 0) s.TimeLeft = TimeSpan.FromSeconds(secs);
            else if (s.Discharging && s.RateMw is int rd && rd < 0 && s.Remaining is uint rem) s.TimeLeft = TimeSpan.FromHours(rem / (double)-rd);
            else if (s.Charging && s.RateMw is int rc && rc > 0 && s.Remaining is uint rem2 && b.Full > rem2) s.TimeLeft = TimeSpan.FromHours((b.Full - rem2) / (double)rc);
            return s;
        }
        catch (Exception ex) { Paths.LogError("Battery.Status", ex); return null; }
    }

    static string Chem(string c) => c.ToUpperInvariant() switch
    {
        "LION" or "LI-I" or "LI-ION" or "LI" => Loc.T("Íon de lítio (Li-ion)", "Lithium-ion (Li-ion)"),
        "LIP" or "LIPO" or "LI-P" or "LI-POLY" or "LIPOLY" => Loc.T("Polímero de lítio (Li-Po)", "Lithium polymer (Li-Po)"),
        "NIMH" => "NiMH",
        "NICD" => "NiCd",
        "PBAC" => Loc.T("Chumbo-ácido", "Lead acid"),
        "" => "",
        _ => c,
    };

    // ------------------------------------------------------------- histórico

    static string HistoryFile => Path.Combine(Paths.History, "battery.json");

    public static List<HistoryPoint> History() => Json.Load<List<HistoryPoint>>(HistoryFile);

    /// <summary>Guarda um ponto por dia da capacidade total (chamado na inicialização e ao abrir a tela).</summary>
    public static void RecordDaily()
    {
        try
        {
            if (!Present) return;
            var b = ReadAll().FirstOrDefault(x => x.Full > 0 && !x.Relative);
            if (b == null) return;
            var list = History();
            var today = DateTime.Today;
            list.RemoveAll(p => p.Date.Date == today);
            list.Add(new HistoryPoint { Date = today, Full = b.Full, Design = b.Design, Cycles = b.Cycles });
            Json.Save(HistoryFile, list.OrderBy(p => p.Date).TakeLast(730).ToList());
        }
        catch (Exception ex) { Paths.LogError("Battery.RecordDaily", ex); }
    }

    // ------------------------------------------------------------- driver

    const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_INFORMATION = 0x294044, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    static readonly Guid GUID_DEVCLASS_BATTERY = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_QUERY_INFORMATION { public uint BatteryTag; public int InformationLevel; public int AtRate; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_INFORMATION
    {
        public uint Capabilities;
        public byte Technology, R1, R2, R3;
        public byte Chemistry0, Chemistry1, Chemistry2, Chemistry3;
        public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_MANUFACTURE_DATE { public byte Day, Month; public ushort Year; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_WAIT_STATUS { public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_STATUS { public uint PowerState, Capacity, Voltage; public int Rate; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public nint Reserved; }

    static List<string> DevicePaths()
    {
        var list = new List<string>();
        var guid = GUID_DEVCLASS_BATTERY;
        nint set = SetupDiGetClassDevs(ref guid, 0, 0, 0x2 | 0x10 /* DIGCF_PRESENT | DIGCF_DEVICEINTERFACE */);
        if (set == -1) return list;
        try
        {
            for (uint i = 0; i < 8; i++)
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, i, ref did)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref did, 0, 0, out uint need, 0);
                if (need == 0) continue;
                nint buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(buf, 8); // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W no x64
                    if (SetupDiGetDeviceInterfaceDetail(set, ref did, buf, need, out _, 0))
                        list.Add(Marshal.PtrToStringUni(buf + 4) ?? "");
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list.Where(p => p != "").ToList();
    }

    static SafeFileHandle Open(string path) =>
        CreateFile(path, 0x80000000 | 0x40000000 /* GENERIC_READ | GENERIC_WRITE */, 0x1 | 0x2, 0, 3 /* OPEN_EXISTING */, 0x80, 0);

    static uint? Tag(SafeFileHandle h)
    {
        uint wait = 0, tag = 0;
        return DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, &wait, 4, &tag, 4, out _, 0) && tag != 0 ? tag : null;
    }

    static T? Query<T>(SafeFileHandle h, uint tag, int level) where T : unmanaged
    {
        var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
        T value;
        return DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, &q, sizeof(BATTERY_QUERY_INFORMATION), &value, sizeof(T), out _, 0) ? value : null;
    }

    static string? QueryString(SafeFileHandle h, uint tag, int level)
    {
        var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = level };
        char* buf = stackalloc char[256];
        if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_INFORMATION, &q, sizeof(BATTERY_QUERY_INFORMATION), buf, 512, out uint got, 0) || got < 2) return null;
        var s = new string(buf, 0, (int)(got / 2)).TrimEnd('\0', ' ');
        return s.Length > 0 ? s : null;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint hwnd, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint set, nint devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(nint set, ref SP_DEVICE_INTERFACE_DATA data, nint detail, uint size, out uint required, nint devInfo);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out uint returned, nint overlapped);
}
