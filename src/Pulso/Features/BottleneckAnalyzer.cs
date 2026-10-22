using Pulso.Core;
using Pulso.Hardware;
using Pulso.Info;

namespace Pulso.Features;

/// <summary>Analisa as leituras recentes e diz qual componente limita o desempenho.</summary>
public static class BottleneckAnalyzer
{
    public sealed record Sample(DateTime T, double Gpu, double Cpu, double Top, double Ram, double? Vram, double Disk, bool CpuThrottle, bool GpuThrottle, double? Fps, string? Process, double? CpuTemp, double? GpuTemp);

    public sealed class Result
    {
        public string Title = "", Text = "";
        public double Balance;            // -100 = limitado pela CPU, +100 = limitado pela GPU
        public double CpuBottleneck;      // % estimado de desempenho perdido pela CPU
        public bool Enough;
        public double Gpu, Cpu, Top, Ram, Vram = double.NaN, Fps = double.NaN;
        public double RamUsedGB, RamTotalGB, VramUsedGB = double.NaN, VramTotalGB = double.NaN;
        public string? Process;
        public List<(string k, string v, Health h)> Factors = new();
        public List<(string t, string d)> Recs = new();
        public TimeSpan Window;
    }

    static readonly List<Sample> _samples = new();
    static readonly object _lock = new();
    static int _users;
    static IDisposable? _demand, _fps;

    public static void Begin()
    {
        if (Interlocked.Increment(ref _users) != 1) return;
        _demand = SensorHub.Instance.Demand();
        _fps = FpsMonitor.Instance.Acquire();
        SensorHub.Instance.Updated += Add;
    }

    public static void End()
    {
        if (Interlocked.Decrement(ref _users) != 0) return;
        SensorHub.Instance.Updated -= Add;
        _demand?.Dispose(); _fps?.Dispose();
        _demand = _fps = null;
    }

    public static void Reset() { lock (_lock) _samples.Clear(); }

    static void Add(Snapshot s)
    {
        var g = s.Gpu;
        var smp = new Sample(s.Time, g?.Load ?? 0, s.Cpu.Load ?? 0, s.Cpu.MaxThreadLoad ?? s.Cpu.Load ?? 0, s.Mem.Load ?? 0, g?.VramPct,
            s.Disks.Count > 0 ? s.Disks.Max(d => d.Activity ?? 0) : 0, s.Cpu.ThermalThrottle == true, g?.Throttling == true && (g.ThrottleReason ?? "").Contains(Loc.T("temperatura", "temperature")),
            s.Fps, s.FpsProcess, s.Cpu.Temp, g?.Temp);
        lock (_lock)
        {
            _samples.Add(smp);
            while (_samples.Count > 0 && (s.Time - _samples[0].T).TotalSeconds > 120) _samples.RemoveAt(0);
        }
    }

    /// <summary>Analisa os últimos <paramref name="seconds"/> segundos. Em modo jogo só usa amostras com jogo rodando.</summary>
    public static Result Analyze(bool gameMode, int seconds = 30)
    {
        List<Sample> win;
        lock (_lock)
        {
            var now = DateTime.Now;
            win = _samples.Where(x => (now - x.T).TotalSeconds <= seconds).ToList();
        }
        if (gameMode) win = win.Where(x => x.Fps != null).ToList();
        var r = new Result { Enough = win.Count >= 5 };
        var snap = SensorHub.Instance.Last;
        r.RamTotalGB = snap?.Mem.TotalGB ?? 0;
        r.RamUsedGB = snap?.Mem.UsedGB ?? 0;
        if (snap?.Gpu?.VramTotalMB is double vt) { r.VramTotalGB = vt / 1024; r.VramUsedGB = (snap.Gpu.VramUsedMB ?? 0) / 1024; }
        if (!r.Enough)
        {
            r.Title = gameMode ? Loc.T("Aguardando um jogo", "Waiting for a game") : Loc.T("Coletando dados…", "Collecting data…");
            r.Text = gameMode
                ? (FpsMonitor.Instance.Error ?? Loc.T("Abra um jogo e jogue por alguns segundos. O Pulso identifica o jogo em primeiro plano automaticamente e mede FPS, CPU e GPU ao mesmo tempo.",
                                                      "Open a game and play for a few seconds. Pulso detects the foreground game automatically and measures FPS, CPU and GPU together."))
                : Loc.T("O teste 3D está rodando; a análise aparece em alguns segundos.", "The 3D test is running; the analysis will appear in a few seconds.");
            return r;
        }

        r.Window = win[^1].T - win[0].T;
        r.Gpu = win.Average(x => x.Gpu);
        r.Cpu = win.Average(x => x.Cpu);
        r.Top = win.Average(x => x.Top);
        r.Ram = win.Average(x => x.Ram);
        var vr = win.Where(x => x.Vram != null).Select(x => x.Vram!.Value).ToList();
        if (vr.Count > 0) r.Vram = vr.Average();
        var fp = win.Where(x => x.Fps != null).Select(x => x.Fps!.Value).ToList();
        if (fp.Count > 0) r.Fps = fp.Average();
        r.Process = win.LastOrDefault(x => x.Process != null)?.Process;
        double disk = win.Average(x => x.Disk);
        bool cpuThr = win.Count(x => x.CpuThrottle) > win.Count / 5;
        bool gpuThr = win.Count(x => x.GpuThrottle) > win.Count / 5;
        double cpuPressure = Math.Max(r.Top, r.Cpu);
        var mem = SystemSummary.GetAsync().IsCompletedSuccessfully ? SystemSummary.GetAsync().Result.Memory : null;
        bool ramSlow = mem?.XmpActive == false && mem.Warning != null;
        var gpuInfo = SystemSummary.GetAsync().IsCompletedSuccessfully ? SystemSummary.GetAsync().Result.Gpus.FirstOrDefault() : null;
        bool rtx = gpuInfo?.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase) == true;
        string upscaler = rtx ? "DLSS" : "FSR";

        // --------- veredito principal
        if (r.Gpu >= 90)
        {
            r.Balance = Math.Min(100, 50 + (r.Gpu - 90) * 5);
            r.CpuBottleneck = Math.Max(0, Math.Round(100 - r.Gpu));
            r.Title = Loc.T("Limitado pela GPU", "GPU-bound");
            r.Text = Loc.T("A placa de vídeo está trabalhando perto de 100% e o processador ainda tem folga. Esse é o cenário ideal para jogos: você está aproveitando toda a potência da GPU.",
                           "The graphics card is working near 100% and the processor still has headroom. This is the ideal scenario for games: you're using all of the GPU's power.");
            r.Recs.Add((Loc.T("Sua configuração está equilibrada", "Your setup is balanced"),
                        Loc.T($"Com a GPU no limite, trocar o processador não aumentaria o FPS. Se quiser mais FPS, reduza a qualidade gráfica ou ative o {upscaler} no modo Qualidade.",
                              $"With the GPU maxed out, a faster CPU wouldn't raise FPS. For more FPS, lower graphics quality or enable {upscaler} in Quality mode.")));
        }
        else if (cpuPressure >= 85)
        {
            r.Balance = -Math.Min(100, 50 + (cpuPressure - 85) * 3.3);
            r.CpuBottleneck = Math.Round(Math.Clamp(95 - r.Gpu, 0, 100));
            r.Title = Loc.T("Limitado pela CPU", "CPU-bound");
            bool oneThread = r.Top >= 85 && r.Cpu < 70;
            r.Text = oneThread
                ? Loc.T($"Um núcleo do processador está no máximo ({Fmt.N(r.Top)}%) enquanto a placa de vídeo está em {Fmt.N(r.Gpu)}%. O jogo depende muito de um único núcleo e a GPU fica esperando.",
                        $"One CPU core is maxed out ({Fmt.N(r.Top)}%) while the graphics card is at {Fmt.N(r.Gpu)}%. The game depends heavily on a single core and the GPU is left waiting.")
                : Loc.T($"O processador está quase no limite ({Fmt.N(r.Cpu)}%) e a placa de vídeo em {Fmt.N(r.Gpu)}%. A GPU poderia entregar mais quadros se a CPU acompanhasse.",
                        $"The processor is near its limit ({Fmt.N(r.Cpu)}%) and the graphics card is at {Fmt.N(r.Gpu)}%. The GPU could deliver more frames if the CPU kept up.");
            r.Recs.Add((Loc.T("Aproveite a folga da GPU", "Use the GPU's headroom"),
                        Loc.T("Aumente a resolução, a qualidade gráfica ou o anti-aliasing: o FPS quase não muda, porque quem limita é o processador.",
                              "Raise resolution, graphics quality or anti-aliasing: FPS will barely change, since the processor is the limit.")));
            r.Recs.Add((Loc.T("Libere o processador", "Free up the processor"),
                        Loc.T("Feche programas em segundo plano (navegador, gravação, launchers) e reduza opções que pesam na CPU: distância de visão, quantidade de NPCs/física e sombras dinâmicas.",
                              "Close background programs (browser, recording, launchers) and lower CPU-heavy options: view distance, NPC/physics density and dynamic shadows.")));
            if (r.CpuBottleneck >= 25)
                r.Recs.Add((Loc.T("Upgrade sugerido: processador", "Suggested upgrade: processor"),
                            Loc.T("Se o gargalo aparece em vários jogos, um processador com núcleos mais rápidos (e mais cache L3) traria o maior ganho.",
                                  "If the bottleneck shows up in several games, a CPU with faster cores (and more L3 cache) would give the biggest gain.")));
        }
        else
        {
            r.Balance = Math.Clamp((r.Gpu - cpuPressure) / 2, -40, 40);
            r.CpuBottleneck = 0;
            r.Title = Loc.T("Nenhum componente no limite", "No component at its limit");
            r.Text = Loc.T($"GPU em {Fmt.N(r.Gpu)}% e núcleo mais usado em {Fmt.N(r.Top)}%. Quando nada está no máximo, o FPS costuma estar limitado por V-Sync, limite de FPS do jogo, ou pelo próprio motor do jogo.",
                           $"GPU at {Fmt.N(r.Gpu)}% and busiest core at {Fmt.N(r.Top)}%. When nothing is maxed out, FPS is usually capped by V-Sync, the game's FPS limit, or the game engine itself.");
            r.Recs.Add((Loc.T("Verifique limites de FPS", "Check FPS limits"),
                        Loc.T("Desative V-Sync / limite de FPS no jogo e no painel do driver se quiser FPS máximo. Se o limite é proposital (economia, menos calor), está tudo certo.",
                              "Turn off V-Sync / the FPS cap in the game and driver panel if you want maximum FPS. If the cap is intentional (efficiency, less heat), all good.")));
        }

        // --------- outros fatores
        r.Factors.Add((Loc.T("RAM cheia", "RAM full"), r.Ram >= 90 ? Loc.T($"Sim · {Fmt.N(r.Ram)}%", $"Yes · {Fmt.N(r.Ram)}%") : Loc.T("Não", "No"), r.Ram >= 90 ? Health.Warning : Health.Good));
        r.Factors.Add((Loc.T("VRAM cheia", "VRAM full"), double.IsNaN(r.Vram) ? Fmt.NA : r.Vram >= 95 ? Loc.T($"Sim · {Fmt.N(r.Vram)}%", $"Yes · {Fmt.N(r.Vram)}%") : Loc.T("Não", "No"), double.IsNaN(r.Vram) ? Health.Unknown : r.Vram >= 95 ? Health.Warning : Health.Good));
        r.Factors.Add((Loc.T("Disco a 100%", "Disk at 100%"), disk >= 90 ? Loc.T("Sim", "Yes") : Loc.T("Não", "No"), disk >= 90 ? Health.Warning : Health.Good));
        r.Factors.Add(("Thermal throttling", cpuThr || gpuThr ? (cpuThr && gpuThr ? "CPU + GPU" : cpuThr ? "CPU" : "GPU") : Loc.T("Não", "No"), cpuThr || gpuThr ? Health.Critical : Health.Good));
        if (mem != null)
        {
            var mods = mem.Modules;
            int cfg = mods.Count > 0 ? mods.Min(m => m.ConfiguredSpeed) : 0, rated = mods.Count > 0 ? mods.Max(m => m.RatedSpeed) : 0;
            r.Factors.Add((Loc.T("Memória abaixo da velocidade nominal", "Memory below rated speed"), ramSlow ? Loc.T($"Sim · {cfg} de {rated} MT/s", $"Yes · {cfg} of {rated} MT/s") : Loc.T("Não", "No"), ramSlow ? Health.Warning : Health.Good));
            if (mem.ChannelMode.StartsWith("Single") && mods.Count == 1)
                r.Factors.Add((Loc.T("Memória em single channel", "Single-channel memory"), Loc.T("Sim", "Yes"), Health.Warning));
        }

        // --------- recomendações por fator
        if (ramSlow)
            r.Recs.Insert(0, (Loc.T("Ative o perfil XMP/EXPO da memória", "Enable the memory XMP/EXPO profile"),
                              Loc.T("A RAM está abaixo da velocidade anunciada. Ativar o perfil na BIOS costuma dar 5 a 10% a mais em jogos limitados pela CPU.",
                                    "RAM is below its advertised speed. Enabling the BIOS profile usually gives 5 to 10% more in CPU-bound games.")));
        if (mem?.ChannelMode.StartsWith("Single") == true && mem.Modules.Count == 1)
            r.Recs.Add((Loc.T("Use dois pentes de memória", "Use two memory modules"), Loc.T("Com um pente só a memória roda em single channel, com metade da banda. Dois pentes iguais (dual channel) ajudam muito, principalmente com vídeo integrado.",
                                                                                                "With a single module memory runs single-channel at half bandwidth. Two matching modules (dual channel) help a lot, especially with integrated graphics.")));
        if (r.Ram >= 90)
            r.Recs.Add((Loc.T("A memória RAM está no limite", "RAM is at its limit"), Loc.T($"Feche programas abertos. Se acontecer sempre, considere ir de {Fmt.N(Math.Round(r.RamTotalGB))} GB para {Fmt.N(Math.Max(16, Math.Round(r.RamTotalGB) * 2))} GB.",
                                                                                          $"Close open programs. If it always happens, consider going from {Fmt.N(Math.Round(r.RamTotalGB))} GB to {Fmt.N(Math.Max(16, Math.Round(r.RamTotalGB) * 2))} GB.")));
        if (!double.IsNaN(r.Vram) && r.Vram >= 95)
            r.Recs.Add((Loc.T("A memória de vídeo encheu", "Video memory is full"), Loc.T("Reduza a qualidade das texturas ou a resolução. VRAM cheia causa travadinhas (stutter) mesmo com FPS alto.",
                                                                                     "Lower texture quality or resolution. Full VRAM causes stutter even with high FPS.")));
        if (disk >= 90)
            r.Recs.Add((Loc.T("O disco está sobrecarregado", "The drive is overloaded"), Loc.T("Instale o jogo num SSD (de preferência NVMe) e evite downloads/atualizações durante a partida.",
                                                                                            "Install the game on an SSD (preferably NVMe) and avoid downloads/updates while playing.")));
        if (cpuThr || gpuThr)
            r.Recs.Insert(0, (Loc.T("Verifique a refrigeração", "Check cooling"), Loc.T("Há queda de clock por temperatura. Limpe a poeira, verifique as ventoinhas, melhore o fluxo de ar do gabinete e considere trocar a pasta térmica.",
                                                                                      "Clocks are dropping due to temperature. Clean dust, check fans, improve case airflow and consider replacing thermal paste.")));
        if (r.Recs.Count == 1 && r.Gpu >= 90)
            r.Recs.Add((Loc.T("Nenhum upgrade necessário agora", "No upgrade needed right now"), Loc.T("Para esta configuração gráfica, processador e placa de vídeo estão bem pareados.", "For these graphics settings, CPU and GPU are well matched.")));
        return r;
    }
}
