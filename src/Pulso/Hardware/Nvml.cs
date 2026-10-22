using System.Runtime.InteropServices;
using System.IO;
using System.Text;

namespace Pulso.Hardware;

/// <summary>
/// Acesso mínimo à NVML (biblioteca de gerenciamento da NVIDIA, instalada com o driver).
/// Fornece dados que os sensores comuns não dão: barramento, clocks máximos, PCIe,
/// encoder/decoder e o motivo de redução de clock (throttling).
/// </summary>
public static unsafe class Nvml
{
    static nint _lib;
    static bool _tried;
    public static bool Available { get; private set; }

    static delegate* unmanaged[Cdecl]<int> _init;
    static delegate* unmanaged[Cdecl]<uint*, int> _count;
    static delegate* unmanaged[Cdecl]<uint, nint*, int> _handle;
    static delegate* unmanaged[Cdecl]<nint, byte*, uint, int> _name;
    static delegate* unmanaged[Cdecl]<nint, int, uint*, int> _maxClock;
    static delegate* unmanaged[Cdecl]<nint, int, uint*, int> _clock;
    static delegate* unmanaged[Cdecl]<nint, uint*, int> _busWidth;
    static delegate* unmanaged[Cdecl]<nint, uint*, int> _curGen, _curWidth, _maxGen, _maxWidth, _gpuMaxGen;
    static delegate* unmanaged[Cdecl]<nint, uint*, uint*, int> _enc, _dec;
    static delegate* unmanaged[Cdecl]<nint, ulong*, int> _throttle;
    static delegate* unmanaged[Cdecl]<nint, uint*, int> _powerDefault;
    static delegate* unmanaged[Cdecl]<nint, uint*, int> _arch;
    static delegate* unmanaged[Cdecl]<nint, uint*, int> _cores;
    static delegate* unmanaged[Cdecl]<nint, byte*, uint, int> _vbios;
    static delegate* unmanaged[Cdecl]<byte*, uint, int> _driver;

    public static bool Init()
    {
        if (_tried) return Available;
        _tried = true;
        try
        {
            string sys = Path.Combine(Environment.SystemDirectory, "nvml.dll");
            if (!NativeLibrary.TryLoad(sys, out _lib) && !NativeLibrary.TryLoad("nvml.dll", out _lib)) return false;
            nint F(string n) => NativeLibrary.TryGetExport(_lib, n, out var p) ? p : 0;

            _init = (delegate* unmanaged[Cdecl]<int>)F("nvmlInit_v2");
            _count = (delegate* unmanaged[Cdecl]<uint*, int>)F("nvmlDeviceGetCount_v2");
            _handle = (delegate* unmanaged[Cdecl]<uint, nint*, int>)F("nvmlDeviceGetHandleByIndex_v2");
            _name = (delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)F("nvmlDeviceGetName");
            _maxClock = (delegate* unmanaged[Cdecl]<nint, int, uint*, int>)F("nvmlDeviceGetMaxClockInfo");
            _clock = (delegate* unmanaged[Cdecl]<nint, int, uint*, int>)F("nvmlDeviceGetClockInfo");
            _busWidth = (delegate* unmanaged[Cdecl]<nint, uint*, int>)F("nvmlDeviceGetMemoryBusWidth");
            _curGen = (delegate* unmanaged[Cdecl]<nint, uint*, int>)F("nvmlDeviceGetCurrPcieLinkGeneration");
            _curWidth = (delegate* unmanaged[Cdecl]<nint, uint*, int>)F("nvmlDeviceGetCurrPcieLinkWidth");
            _maxGen = (delegate* unmanaged[Cdecl]<nint, uint*, int>)F("nvmlDeviceGetMaxPcieLinkGeneration");
            _maxWidth = (delegate* unmanaged[Cdecl]<nint, uint*, int>)F("nvmlDeviceGetMaxPcieLinkWidth");
            _gpuMaxGen = (delegate* unmanaged[Cdecl]<nint, uint*, int>)F("nvmlDeviceGetGpuMaxPcieLinkGeneration");
            _enc = (delegate* unmanaged[Cdecl]<nint, uint*, uint*, int>)F("nvmlDeviceGetEncoderUtilization");
            _dec = (delegate* unmanaged[Cdecl]<nint, uint*, uint*, int>)F("nvmlDeviceGetDecoderUtilization");
            nint thr = F("nvmlDeviceGetCurrentClocksEventReasons");
            if (thr == 0) thr = F("nvmlDeviceGetCurrentClocksThrottleReasons");
            _throttle = (delegate* unmanaged[Cdecl]<nint, ulong*, int>)thr;
            _powerDefault = (delegate* unmanaged[Cdecl]<nint, uint*, int>)F("nvmlDeviceGetPowerManagementDefaultLimit");
            _arch = (delegate* unmanaged[Cdecl]<nint, uint*, int>)F("nvmlDeviceGetArchitecture");
            _cores = (delegate* unmanaged[Cdecl]<nint, uint*, int>)F("nvmlDeviceGetNumGpuCores");
            _vbios = (delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)F("nvmlDeviceGetVbiosVersion");
            _driver = (delegate* unmanaged[Cdecl]<byte*, uint, int>)F("nvmlSystemGetDriverVersion");

            if (_init == null || _count == null || _handle == null) return false;
            Available = _init() == 0;
        }
        catch { Available = false; }
        return Available;
    }

    public sealed class Device
    {
        public nint Handle;
        public string Name = "";
    }

    public static List<Device> Devices()
    {
        var list = new List<Device>();
        if (!Init()) return list;
        uint n = 0;
        if (_count(&n) != 0) return list;
        for (uint i = 0; i < n; i++)
        {
            nint h;
            if (_handle(i, &h) != 0) continue;
            nint hh = h;
            list.Add(new Device { Handle = hh, Name = Str(b => _name(hh, b, 96)) ?? "" });
        }
        return list;
    }

    delegate int BufCall(byte* buf);

    static string? Str(BufCall call)
    {
        byte* buf = stackalloc byte[128];
        new Span<byte>(buf, 128).Clear();
        if (call(buf) != 0) return null;
        int len = 0;
        while (len < 128 && buf[len] != 0) len++;
        return Encoding.ASCII.GetString(buf, len);
    }

    static uint? U(delegate* unmanaged[Cdecl]<nint, uint*, int> f, nint h)
    {
        if (f == null) return null;
        uint v;
        return f(h, &v) == 0 ? v : null;
    }

    public static uint? MaxClock(Device d, int type) { if (_maxClock == null) return null; uint v; return _maxClock(d.Handle, type, &v) == 0 ? v : null; }
    public static uint? Clock(Device d, int type) { if (_clock == null) return null; uint v; return _clock(d.Handle, type, &v) == 0 ? v : null; }
    public static uint? BusWidth(Device d) => U(_busWidth, d.Handle);
    public static uint? PcieCurGen(Device d) => U(_curGen, d.Handle);
    public static uint? PcieCurWidth(Device d) => U(_curWidth, d.Handle);
    public static uint? PcieMaxGen(Device d) => U(_gpuMaxGen, d.Handle) ?? U(_maxGen, d.Handle);
    public static uint? PcieMaxWidth(Device d) => U(_maxWidth, d.Handle);
    public static uint? DefaultPowerLimitMw(Device d) => U(_powerDefault, d.Handle);
    public static uint? Architecture(Device d) => U(_arch, d.Handle);
    public static uint? Cores(Device d) => U(_cores, d.Handle);
    public static string? Vbios(Device d) => _vbios == null ? null : Str(b => _vbios(d.Handle, b, 96));
    public static string? DriverVersion() => _driver == null ? null : Str(b => _driver(b, 96));

    public static uint? EncoderUtil(Device d) { if (_enc == null) return null; uint v, p; return _enc(d.Handle, &v, &p) == 0 ? v : null; }
    public static uint? DecoderUtil(Device d) { if (_dec == null) return null; uint v, p; return _dec(d.Handle, &v, &p) == 0 ? v : null; }

    public static ulong? ThrottleReasons(Device d)
    {
        if (_throttle == null) return null;
        ulong v;
        return _throttle(d.Handle, &v) == 0 ? v : null;
    }

    public static string ArchName(uint a) => a switch
    {
        2 => "Kepler", 3 => "Maxwell", 4 => "Pascal", 5 => "Volta", 6 => "Turing",
        7 => "Ampere", 8 => "Ada Lovelace", 9 => "Hopper", 10 => "Blackwell", _ => "?",
    };

    /// <summary>Converte a máscara de motivos em (está limitando por temperatura/energia?, descrição).</summary>
    public static (bool throttling, string? reason) DescribeThrottle(ulong r)
    {
        var parts = new List<string>();
        bool thermal = (r & (0x20 | 0x40)) != 0;
        bool power = (r & (0x4 | 0x80)) != 0;
        if (thermal) parts.Add(Core.Loc.T("temperatura", "temperature"));
        if ((r & 0x8) != 0) parts.Add(Core.Loc.T("proteção de hardware", "hardware slowdown"));
        if (power) parts.Add(Core.Loc.T("limite de energia", "power limit"));
        return (thermal || (r & 0x8) != 0, parts.Count > 0 ? string.Join(", ", parts) : null);
    }
}
