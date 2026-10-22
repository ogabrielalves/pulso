using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Pulso.Core;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Feature = Vortice.Direct3D12.Feature;

namespace Pulso.Info;

/// <summary>
/// Suporte a DirectX 12 / Vulkan / OpenGL. Consultar essas APIs carrega o driver de vídeo
/// inteiro no processo, então a consulta roda num processo auxiliar descartável
/// ("Pulso.exe --probe-gpu arquivo.json") e o app principal fica leve.
/// </summary>
public static class GpuApis
{
    public sealed class AdapterApi
    {
        public uint VendorId { get; set; }
        public uint DeviceId { get; set; }
        public string Name { get; set; } = "";
        public string? D3D12FeatureLevel { get; set; }
        public string? ShaderModel { get; set; }
        public string? RaytracingTier { get; set; }
        public string? MeshShaderTier { get; set; }
        public string? VulkanVersion { get; set; }
        public string? VulkanDriver { get; set; }
    }

    public sealed class ProbeResult
    {
        public List<AdapterApi> Adapters { get; set; } = new();
        public string? OpenGlVersion { get; set; }
        public string? OpenGlRenderer { get; set; }
    }

    static ProbeResult? _cached;

    public static async Task<ProbeResult> GetAsync()
    {
        if (_cached != null) return _cached;
        string file = Path.Combine(Path.GetTempPath(), $"pulso_probe_{Environment.ProcessId}.json");
        try
        {
            var psi = new ProcessStartInfo(Paths.ExePath, $"--probe-gpu \"{file}\"") { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try { await p.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { try { p.Kill(); } catch { } }
            }
            if (File.Exists(file))
            {
                _cached = Json.Load<ProbeResult>(file);
                File.Delete(file);
            }
        }
        catch (Exception ex) { Paths.LogError("GpuApis.GetAsync", ex); }
        return _cached ?? new ProbeResult();
    }

    /// <summary>Executado no processo auxiliar.</summary>
    public static void RunProbe(string outFile)
    {
        var r = new ProbeResult();
        try { ProbeD3D12(r); } catch { }
        try { ProbeVulkan(r); } catch { }
        try { ProbeOpenGl(r); } catch { }
        Json.Save(outFile, r);
    }

    static void ProbeD3D12(ProbeResult r)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
        {
            using (adapter)
            {
                var d = adapter.Description1;
                if ((d.Flags & AdapterFlags.Software) != 0) continue;
                var a = new AdapterApi { VendorId = d.VendorId, DeviceId = d.DeviceId, Name = d.Description };
                r.Adapters.Add(a);
                if (D3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? dev).Failure || dev == null) continue;
                using (dev)
                {
                    var levels = new[] { FeatureLevel.Level_12_2, FeatureLevel.Level_12_1, FeatureLevel.Level_12_0, FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
                    var fl = dev.CheckMaxSupportedFeatureLevel(levels);
                    a.D3D12FeatureLevel = fl.ToString().Replace("Level_", "");

                    var sm = new FeatureDataShaderModel { HighestShaderModel = ShaderModel.Model6_8 };
                    for (int tries = 0; tries < 9; tries++)
                    {
                        if (dev.CheckFeatureSupport(Feature.ShaderModel, ref sm)) { a.ShaderModel = sm.HighestShaderModel.ToString().Replace("Model", "").Replace("_", "."); break; }
                        sm.HighestShaderModel = (ShaderModel)((int)sm.HighestShaderModel - 1);
                    }

                    var o5 = new FeatureDataD3D12Options5();
                    if (dev.CheckFeatureSupport(Feature.Options5, ref o5))
                        a.RaytracingTier = o5.RaytracingTier switch
                        {
                            RaytracingTier.NotSupported => null,
                            RaytracingTier.Tier1_0 => "1.0",
                            RaytracingTier.Tier1_1 => "1.1",
                            var t => t.ToString(),
                        };
                    var o7 = new FeatureDataD3D12Options7();
                    if (dev.CheckFeatureSupport(Feature.Options7, ref o7) && o7.MeshShaderTier != MeshShaderTier.NotSupported)
                        a.MeshShaderTier = o7.MeshShaderTier.ToString();
                }
            }
        }
    }

    // ------------------------------------------------------------ Vulkan
    [StructLayout(LayoutKind.Sequential)]
    struct VkApplicationInfo { public int sType; public nint pNext; public nint pApplicationName; public uint applicationVersion; public nint pEngineName; public uint engineVersion; public uint apiVersion; }

    [StructLayout(LayoutKind.Sequential)]
    struct VkInstanceCreateInfo { public int sType; public nint pNext; public uint flags; public nint pApplicationInfo; public uint enabledLayerCount; public nint ppEnabledLayerNames; public uint enabledExtensionCount; public nint ppEnabledExtensionNames; }

    [DllImport("vulkan-1.dll")] static extern int vkCreateInstance(ref VkInstanceCreateInfo info, nint alloc, out nint instance);
    [DllImport("vulkan-1.dll")] static extern int vkEnumeratePhysicalDevices(nint instance, ref uint count, [Out] nint[]? devices);
    [DllImport("vulkan-1.dll")] static extern void vkGetPhysicalDeviceProperties(nint device, nint props);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyInstance(nint instance, nint alloc);

    static void ProbeVulkan(ProbeResult r)
    {
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out _)) return;
        var app = new VkApplicationInfo { sType = 0, apiVersion = (1u << 22) | (1u << 12) };
        nint pApp = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
        nint props = Marshal.AllocHGlobal(2048);
        try
        {
            Marshal.StructureToPtr(app, pApp, false);
            var ci = new VkInstanceCreateInfo { sType = 1, pApplicationInfo = pApp };
            if (vkCreateInstance(ref ci, 0, out nint inst) != 0) return;
            try
            {
                uint n = 0;
                vkEnumeratePhysicalDevices(inst, ref n, null);
                var devs = new nint[n];
                vkEnumeratePhysicalDevices(inst, ref n, devs);
                foreach (var d in devs)
                {
                    vkGetPhysicalDeviceProperties(d, props);
                    uint api = (uint)Marshal.ReadInt32(props, 0);
                    uint vendor = (uint)Marshal.ReadInt32(props, 8);
                    uint device = (uint)Marshal.ReadInt32(props, 12);
                    string name = Marshal.PtrToStringUTF8(props + 20) ?? "";
                    string ver = $"{(api >> 22) & 0x7F}.{(api >> 12) & 0x3FF}.{api & 0xFFF}";
                    var a = r.Adapters.FirstOrDefault(x => x.VendorId == vendor && x.DeviceId == device);
                    if (a == null) { a = new AdapterApi { VendorId = vendor, DeviceId = device, Name = name }; r.Adapters.Add(a); }
                    a.VulkanVersion = ver;
                    a.VulkanDriver = name;
                }
            }
            finally { vkDestroyInstance(inst, 0); }
        }
        finally
        {
            Marshal.FreeHGlobal(pApp);
            Marshal.FreeHGlobal(props);
        }
    }

    // ------------------------------------------------------------ OpenGL
    [StructLayout(LayoutKind.Sequential)]
    struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved; public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(int ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] static extern nint GetDC(nint h);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(nint dc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(nint dc, int fmt, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("opengl32.dll")] static extern nint wglCreateContext(nint dc);
    [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(nint dc, nint ctx);
    [DllImport("opengl32.dll")] static extern bool wglDeleteContext(nint ctx);
    [DllImport("opengl32.dll")] static extern nint glGetString(uint name);

    static void ProbeOpenGl(ProbeResult r)
    {
        nint hwnd = CreateWindowExW(0, "STATIC", "pulso-gl", 0x80000000 /*WS_POPUP*/, 0, 0, 8, 8, 0, 0, 0, 0);
        if (hwnd == 0) return;
        nint dc = GetDC(hwnd);
        try
        {
            var pfd = new PIXELFORMATDESCRIPTOR { nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1, dwFlags = 0x4 | 0x20 | 0x1, cColorBits = 32, cDepthBits = 24 };
            int fmt = ChoosePixelFormat(dc, ref pfd);
            if (fmt == 0 || !SetPixelFormat(dc, fmt, ref pfd)) return;
            nint ctx = wglCreateContext(dc);
            if (ctx == 0) return;
            try
            {
                wglMakeCurrent(dc, ctx);
                r.OpenGlVersion = Marshal.PtrToStringAnsi(glGetString(0x1F02));
                r.OpenGlRenderer = Marshal.PtrToStringAnsi(glGetString(0x1F01));
                wglMakeCurrent(0, 0);
            }
            finally { wglDeleteContext(ctx); }
        }
        finally
        {
            ReleaseDC(hwnd, dc);
            DestroyWindow(hwnd);
        }
    }
}

/// <summary>Velocidade e largura do link PCI Express lidas do gerenciador de dispositivos.</summary>
public static class Pcie
{
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buf, int len, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, out uint buf, ref uint size, uint flags);

    static readonly Guid PciGuid = new("3AB22E31-8264-4b4e-9AF5-A8D2D8E33E62");

    static uint? Prop(uint dev, uint pid)
    {
        var key = new DEVPROPKEY { fmtid = PciGuid, pid = pid };
        uint size = 4;
        return CM_Get_DevNode_PropertyW(dev, ref key, out _, out uint v, ref size, 0) == 0 ? v : null;
    }

    public sealed record Link(uint? CurGen, uint? CurWidth, uint? MaxGen, uint? MaxWidth);

    /// <summary>Para discos NVMe: sobe do disco até o controlador PCIe e lê o link dele.</summary>
    public static Link? ReadAncestor(string pnpDeviceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint dev, pnpDeviceId, 0) != 0) return null;
            uint cur = dev;
            for (int i = 0; i < 5; i++)
            {
                if (Prop(cur, 9) != null || Prop(cur, 7) != null)
                    return new Link(Prop(cur, 9), Prop(cur, 10), Prop(cur, 7), Prop(cur, 8));
                if (CM_Get_Parent(out uint parent, cur, 0) != 0) break;
                cur = parent;
            }
        }
        catch { }
        return null;
    }

    public static Link? Read(string pnpDeviceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint dev, pnpDeviceId, 0) != 0) return null;
            // GPUs AMD têm um switch PCIe interno na própria placa: subimos até o link real do slot.
            string vendor = System.Text.RegularExpressions.Regex.Match(pnpDeviceId, "VEN_[0-9A-F]{4}", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
            uint cur = dev;
            for (int i = 0; i < 4 && vendor.Length > 0; i++)
            {
                if (CM_Get_Parent(out uint parent, cur, 0) != 0) break;
                var sb = new StringBuilder(512);
                if (CM_Get_Device_IDW(parent, sb, sb.Capacity, 0) != 0) break;
                if (!sb.ToString().Contains(vendor, StringComparison.OrdinalIgnoreCase) || Prop(parent, 9) == null) break;
                cur = parent;
            }
            var l = new Link(Prop(cur, 9), Prop(cur, 10), Prop(cur, 7), Prop(cur, 8));
            return l.CurGen == null && l.MaxGen == null ? null : l;
        }
        catch { return null; }
    }
}
