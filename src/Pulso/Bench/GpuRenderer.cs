using System.Diagnostics;
using System.Runtime.InteropServices;
using Pulso.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Pulso.Bench;

/// <summary>
/// Renderizador 3D próprio (Direct3D 11) usado no benchmark e no teste de estresse da GPU.
/// Desenha uma cena em raymarching (formas animadas, sombras suaves, oclusão ambiente) numa janela própria.
/// </summary>
public static unsafe class GpuRenderer
{
    public sealed record Result(double AvgFps, long Frames, long Errors, double Seconds, int Width, int Height);

    /// <summary>Benchmark: resolução interna fixa, duração fixa, devolve FPS médio.</summary>
    public static Task<Result> BenchmarkAsync(int width, int height, TimeSpan duration, Action<double, double>? onTick, CancellationToken ct) =>
        RunOnThread(width, height, 1280, 720, Loc.T("Pulso — teste 3D", "Pulso — 3D test"), duration, verify: false, onTick, ct);

    /// <summary>Estresse: roda até cancelar, conferindo quadros de referência para detectar artefatos.</summary>
    public static Task<Result> StressAsync(Action<double, double>? onTick, CancellationToken ct) =>
        RunOnThread(2560, 1440, 960, 540, Loc.T("Pulso — estresse da GPU (feche para parar)", "Pulso — GPU stress (close to stop)"), null, verify: true, onTick, ct);

    static Task<Result> RunOnThread(int rw, int rh, int ww, int wh, string title, TimeSpan? dur, bool verify, Action<double, double>? onTick, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(Run(rw, rh, ww, wh, title, dur, verify, onTick, ct)); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Pulso.GPU" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    // ------------------------------------------------------------------ janela Win32

    delegate nint WndProcFn(nint h, uint msg, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int l, t, r, b; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(int ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint p);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, nint h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] static extern bool AdjustWindowRect(ref RECT r, uint style, bool menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(nint h, string s);
    [DllImport("user32.dll")] static extern nint LoadCursorW(nint inst, nint id);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? n);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint h);

    static WndProcFn? _proc;
    static bool _registered;
    [ThreadStatic] static bool _closed;

    static nint WndProc(nint h, uint msg, nint w, nint l)
    {
        if (msg == 0x0010) { _closed = true; return 0; }        // WM_CLOSE
        if (msg == 0x0100 && w == 0x1B) { _closed = true; return 0; } // ESC
        return DefWindowProcW(h, msg, w, l);
    }

    static nint CreateWin(string title, int w, int h)
    {
        nint inst = GetModuleHandleW(null);
        if (!_registered)
        {
            _proc = WndProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), style = 0x3, lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = inst, hCursor = LoadCursorW(0, 32512), lpszClassName = "PulsoGpuWindow",
            };
            RegisterClassExW(ref wc);
            _registered = true;
        }
        const uint style = 0x00C00000 | 0x00080000 | 0x00020000 | 0x10000000; // caption | sysmenu | minimize | visible
        var r = new RECT { r = w, b = h };
        AdjustWindowRect(ref r, style, false);
        int ww = r.r - r.l, wh = r.b - r.t;
        int x = (GetSystemMetrics(0) - ww) / 2, y = (GetSystemMetrics(1) - wh) / 2;
        var hwnd = CreateWindowExW(0, "PulsoGpuWindow", title, style, x, y, ww, wh, 0, 0, inst, 0);
        ShowWindow(hwnd, 5);
        SetForegroundWindow(hwnd);
        return hwnd;
    }

    // ------------------------------------------------------------------ renderização

    [StructLayout(LayoutKind.Sequential)]
    struct Constants { public float W, H, Time, Quality; }

    static Result Run(int rw, int rh, int ww, int wh, string title, TimeSpan? dur, bool verify, Action<double, double>? onTick, CancellationToken ct)
    {
        _closed = false;
        nint hwnd = CreateWin(title, ww, wh);
        try
        {
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? device, out ID3D11DeviceContext? ctx).CheckError();
            using var dev = device!;
            using var dc = ctx!;
            using var dxgiDevice = dev.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = (uint)rw, Height = (uint)rh, Format = Format.B8G8R8A8_UNorm, BufferCount = 1,
                BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.Discard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
            };
            using var swap = factory.CreateSwapChainForHwnd(dev, hwnd, desc);
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
            using var back = swap.GetBuffer<ID3D11Texture2D>(0);
            using var rtv = dev.CreateRenderTargetView(back);

            var vsCode = Compiler.Compile(Shader, "vs", "pulso.hlsl", "vs_5_0");
            var psCode = Compiler.Compile(Shader, "ps", "pulso.hlsl", "ps_5_0");
            using var vs = dev.CreateVertexShader(vsCode.Span);
            using var ps = dev.CreatePixelShader(psCode.Span);
            using var cb = dev.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

            // Alvo pequeno para os quadros de verificação (estresse).
            const int vw = 256, vh = 144;
            using var vtex = dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, vw, vh, 1, 1, BindFlags.RenderTarget));
            using var vrtv = dev.CreateRenderTargetView(vtex);
            using var staging = dev.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, vw, vh, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            ulong? reference = null;
            long errors = 0;

            void Draw(ID3D11RenderTargetView target, int w, int h, float time)
            {
                var m = dc.Map(cb, 0, MapMode.WriteDiscard);
                *(Constants*)m.DataPointer = new Constants { W = w, H = h, Time = time, Quality = 1 };
                dc.Unmap(cb, 0);
                dc.OMSetRenderTargets(target);
                dc.RSSetViewport(0, 0, w, h);
                dc.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                dc.VSSetShader(vs);
                dc.PSSetShader(ps);
                dc.PSSetConstantBuffer(0, cb);
                dc.Draw(3, 0);
            }

            ulong Checksum()
            {
                Draw(vrtv, vw, vh, 3.25f);
                dc.CopyResource(staging, vtex);
                var m = dc.Map(staging, 0, MapMode.Read);
                ulong h = 1469598103934665603;
                for (int y = 0; y < vh; y++)
                {
                    byte* row = (byte*)m.DataPointer + y * m.RowPitch;
                    for (int x = 0; x < vw * 4; x++) { h ^= row[x]; h *= 1099511628211; }
                }
                dc.Unmap(staging, 0);
                return h;
            }

            var sw = Stopwatch.StartNew();
            var warm = TimeSpan.FromSeconds(dur.HasValue ? 2 : 0);
            long frames = 0, measured = 0;
            double lastReport = 0;
            long lastFrames = 0;
            if (verify) reference = Checksum();

            while (!ct.IsCancellationRequested && !_closed)
            {
                while (PeekMessageW(out var msg, 0, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
                if (_closed) break;
                var el = sw.Elapsed;
                if (dur.HasValue && el >= warm + dur.Value) break;

                Draw(rtv, rw, rh, (float)el.TotalSeconds);
                swap.Present(0, PresentFlags.None);
                frames++;
                if (el >= warm) measured++;

                if (verify && frames % 240 == 0 && Checksum() != reference) errors++;

                double s = el.TotalSeconds;
                if (s - lastReport >= 1)
                {
                    double fps = (frames - lastFrames) / (s - lastReport);
                    lastFrames = frames; lastReport = s;
                    SetWindowTextW(hwnd, $"{title} · {fps:0} FPS · {rw}×{rh}");
                    onTick?.Invoke(fps, dur.HasValue ? Math.Min(1, s / (warm + dur.Value).TotalSeconds) : 0);
                }
            }
            double secs = Math.Max(0.001, (sw.Elapsed - warm).TotalSeconds);
            if (dur.HasValue && (sw.Elapsed < warm + dur.Value) && (_closed || ct.IsCancellationRequested))
                throw new OperationCanceledException();
            return new Result(measured / secs, frames, errors, secs, rw, rh);
        }
        finally
        {
            DestroyWindow(hwnd);
            while (PeekMessageW(out var msg, 0, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
        }
    }

    const string Shader = """
cbuffer C : register(b0) { float2 res; float time; float quality; };
struct VO { float4 pos : SV_Position; };

VO vs(uint id : SV_VertexID)
{
    VO o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float sdBox(float3 p, float3 b) { float3 q = abs(p) - b; return length(max(q, 0)) + min(max(q.x, max(q.y, q.z)), 0); }
float smin(float a, float b, float k) { float h = saturate(0.5 + 0.5 * (b - a) / k); return lerp(b, a, h) - k * h * (1 - h); }

float2 map(float3 p)
{
    float floorD = p.y + 1.0;
    float2 cell = floor(p.xz / 4.0);
    float3 q = p;
    q.xz = q.xz - 4.0 * cell - 2.0;
    float ph = time + dot(cell, float2(1.7, 2.3));
    float3 r = q - float3(0, 0.2 + 0.4 * sin(ph), 0);
    float c = cos(ph * 0.7), s = sin(ph * 0.7);
    r.xz = float2(c * r.x - s * r.z, s * r.x + c * r.z);
    r.xy = float2(c * r.x + s * r.y, -s * r.x + c * r.y);
    float box = sdBox(r, float3(0.5, 0.5, 0.5)) - 0.08;
    float sph = length(q - float3(0.0, 0.95 + 0.3 * cos(ph * 1.3), 0.0)) - 0.45;
    float tor = length(float2(length(q.xz) - 1.2, q.y + 0.75 - 0.1 * sin(ph * 2.0))) - 0.12;
    float obj = smin(smin(box, sph, 0.35), tor, 0.2);
    if (obj < floorD) return float2(obj, 1.0 + frac(abs(cell.x * 3.0 + cell.y * 7.0) * 0.137));
    return float2(floorD, 0);
}

float3 calcNormal(float3 p)
{
    float2 e = float2(0.0015, 0);
    return normalize(float3(map(p + e.xyy).x - map(p - e.xyy).x, map(p + e.yxy).x - map(p - e.yxy).x, map(p + e.yyx).x - map(p - e.yyx).x));
}

float softShadow(float3 ro, float3 rd)
{
    float res = 1, t = 0.03;
    [loop] for (int i = 0; i < 40; i++)
    {
        float h = map(ro + rd * t).x;
        res = min(res, 10.0 * h / t);
        t += clamp(h, 0.02, 0.6);
        if (res < 0.002 || t > 25.0) break;
    }
    return saturate(res);
}

float ambientOcc(float3 p, float3 n)
{
    float o = 0, sc = 1;
    [unroll] for (int i = 0; i < 5; i++) { float h = 0.02 + 0.13 * i; o += (h - map(p + n * h).x) * sc; sc *= 0.85; }
    return saturate(1 - 2.0 * o);
}

float3 skyColor(float3 rd) { return lerp(float3(0.07, 0.08, 0.075), float3(0.20, 0.24, 0.21), saturate(rd.y * 0.6 + 0.4)); }

float march(float3 ro, float3 rd, out float mat)
{
    float t = 0.0; mat = -1;
    [loop] for (int i = 0; i < 200; i++)
    {
        float2 r = map(ro + rd * t);
        if (r.x < 0.0008 * t) { mat = r.y; return t; }
        t += r.x * 0.9;
        if (t > 70.0) break;
    }
    return -1;
}

float3 lit(float3 p, float3 rd, float3 n, float mat, float t)
{
    float3 L = normalize(float3(0.55, 0.75, -0.35));
    float3 base;
    if (mat < 0.5)
    {
        float chk = fmod(floor(p.x) + floor(p.z) + 1000.0, 2.0);
        base = chk < 1.0 ? float3(0.10, 0.11, 0.105) : float3(0.16, 0.18, 0.165);
    }
    else base = lerp(float3(0.77, 0.95, 0.42), float3(0.95, 0.72, 0.29), frac(mat * 3.0));
    float dif = saturate(dot(n, L)) * softShadow(p + n * 0.01, L);
    float spe = pow(saturate(dot(reflect(rd, n), L)), 40.0) * dif;
    float occ = ambientOcc(p, n);
    float3 col = base * (0.18 * occ + 1.15 * dif) + spe * 0.7;
    return lerp(col, skyColor(rd), 1.0 - exp(-0.0018 * t * t));
}

float4 ps(VO i) : SV_Target
{
    float2 uv = (i.pos.xy * 2.0 - res) / res.y;
    uv.y = -uv.y;
    float3 ro = float3(2.0 * sin(time * 0.25), 1.8 + 0.4 * sin(time * 0.4), time * 2.2);
    float3 ta = ro + float3(sin(time * 0.2) * 0.8, -0.45, 1.0);
    float3 ww = normalize(ta - ro);
    float3 uu = normalize(cross(float3(0, 1, 0), ww));
    float3 vv = cross(ww, uu);
    float3 rd = normalize(uv.x * uu + uv.y * vv + 1.6 * ww);

    float mat;
    float t = march(ro, rd, mat);
    if (t < 0) return float4(skyColor(rd), 1);
    float3 p = ro + rd * t;
    float3 n = calcNormal(p);
    float3 col = lit(p, rd, n, mat, t);

    // um reflexo
    float3 rd2 = reflect(rd, n);
    float mat2;
    float t2 = march(p + n * 0.02, rd2, mat2);
    float3 refl = skyColor(rd2);
    if (t2 > 0) { float3 p2 = p + n * 0.02 + rd2 * t2; refl = lit(p2, rd2, calcNormal(p2), mat2, t2); }
    float fres = pow(1.0 - saturate(dot(-rd, n)), 3.0);
    col = lerp(col, refl, (mat < 0.5 ? 0.25 : 0.12) + 0.5 * fres);

    col = pow(saturate(col), 1.0 / 2.2);
    float2 q = i.pos.xy / res;
    col *= 0.6 + 0.4 * pow(16.0 * q.x * q.y * (1 - q.x) * (1 - q.y), 0.15);
    return float4(col, 1);
}
""";
}
