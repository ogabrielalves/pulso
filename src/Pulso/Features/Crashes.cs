using System.Diagnostics.Eventing.Reader;
using System.IO;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>
/// Histórico de travamentos lido dos registros de eventos do Windows: telas azuis, desligamentos inesperados,
/// programas que fecharam ou pararam de responder, driver de vídeo reiniciando e erros de hardware (WHEA).
/// </summary>
public static class Crashes
{
    public enum Kind { BlueScreen, Shutdown, AppCrash, AppHang, Gpu, Hardware }

    public sealed record Event(Kind Kind, DateTime When, string Title, string Detail, string? Hint, string? App);

    public static string MinidumpFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

    public static List<Event> Read(int days)
    {
        long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
        var list = new List<Event>();

        // tela azul (registrada no boot seguinte)
        foreach (var e in Query("System", "Microsoft-Windows-WER-SystemErrorReporting", "1001", ms))
        {
            var raw = Prop(e, 0) ?? "";
            uint code = ParseHex(raw.Split(' ', 2)[0]);
            var (name, explain, hint) = Bugcheck(code);
            var dump = Prop(e, 1);
            list.Add(new Event(Kind.BlueScreen, e.TimeCreated ?? DateTime.MinValue,
                Loc.T("Tela azul", "Blue screen") + (name != null ? ": " + name : $" (0x{code:X})"),
                explain + (string.IsNullOrEmpty(dump) ? "" : Loc.T($" Arquivo de análise: {dump}", $" Dump file: {dump}")), hint, null));
        }

        // desligou sem passar pelo desligamento normal
        foreach (var e in Query("System", "Microsoft-Windows-Kernel-Power", "41", ms))
        {
            var when = e.TimeCreated ?? DateTime.MinValue;
            uint bug = e.Properties.Count > 0 && e.Properties[0].Value is { } b ? Convert.ToUInt32(b) : 0;
            // a tela azul já aparece pelo evento 1001: não conta duas vezes
            if (bug != 0 && list.Any(x => x.Kind == Kind.BlueScreen && Math.Abs((x.When - when).TotalMinutes) < 15)) continue;
            if (bug != 0)
            {
                var (name, explain, hint) = Bugcheck(bug);
                list.Add(new Event(Kind.BlueScreen, when, Loc.T("Tela azul", "Blue screen") + (name != null ? ": " + name : $" (0x{bug:X})"), explain, hint, null));
                continue;
            }
            bool button = e.Properties.Count > 6 && e.Properties[6].Value is { } pb && Convert.ToUInt64(pb) != 0;
            list.Add(new Event(Kind.Shutdown, when, Loc.T("Desligamento inesperado", "Unexpected shutdown"),
                button ? Loc.T("O botão de ligar foi segurado para forçar o desligamento (o PC provavelmente travou antes).", "The power button was held to force a shutdown (the PC probably froze first).")
                       : Loc.T("O PC desligou ou reiniciou de repente, sem tela azul: queda de energia, travamento total, fonte ou superaquecimento.", "The PC shut down or restarted suddenly without a blue screen: power loss, full freeze, power supply or overheating."),
                button ? null : Loc.T("Se repete: confira as temperaturas no Monitoramento e a estabilidade com o Teste de estresse. No notebook, veja se a bateria não chegou a zero.", "If it keeps happening: check temperatures in Monitoring and stability with the Stress test. On a laptop, check whether the battery ran out."), null));
        }

        // programa fechou sozinho
        foreach (var e in Query("Application", "Application Error", "1000", ms, 400))
        {
            var app = Prop(e, 0) ?? "?";
            var module = Prop(e, 3);
            var exc = Prop(e, 6) ?? "";
            var (what, hint) = ExceptionInfo(exc);
            string where = module != null && !module.Equals(app, StringComparison.OrdinalIgnoreCase) ? Loc.T($" no componente {module}", $" in {module}") : "";
            list.Add(new Event(Kind.AppCrash, e.TimeCreated ?? DateTime.MinValue, Loc.T($"{app} fechou sozinho", $"{app} crashed"),
                Loc.T($"Erro{where}: {what} (0x{exc}).", $"Error{where}: {what} (0x{exc})."), hint, app));
        }

        // programa travou e o Windows fechou
        foreach (var e in Query("Application", "Application Hang", "1002", ms, 200))
        {
            var app = Prop(e, 0) ?? "?";
            list.Add(new Event(Kind.AppHang, e.TimeCreated ?? DateTime.MinValue, Loc.T($"{app} parou de responder", $"{app} stopped responding"),
                Loc.T("O programa congelou e foi fechado (por você ou pelo Windows).", "The program froze and was closed (by you or by Windows)."), null, app));
        }

        // driver de vídeo reiniciou (tela preta por alguns segundos)
        foreach (var e in Query("System", "Display", "4101", ms))
        {
            var drv = Prop(e, 0);
            list.Add(new Event(Kind.Gpu, e.TimeCreated ?? DateTime.MinValue, Loc.T("Driver de vídeo parou de responder", "Display driver stopped responding"),
                Loc.T($"O Windows reiniciou o driver{(drv != null ? " " + drv : "")} (tela piscando ou preta por alguns segundos).", $"Windows restarted the driver{(drv != null ? " " + drv : "")} (screen flickering or black for a few seconds)."),
                Loc.T("Atualize o driver da placa de vídeo. Se a placa estiver com overclock, volte ao padrão. Temperatura alta da GPU também causa isso.", "Update the graphics driver. If the card is overclocked, revert to stock. High GPU temperature also causes this."), null));
        }

        // erros de hardware relatados pelo processador
        foreach (var e in Query("System", "Microsoft-Windows-WHEA-Logger", "1 or EventID=17 or EventID=18 or EventID=19 or EventID=47", ms, 200))
        {
            string first = "";
            try { first = e.FormatDescription()?.Split('\n')[0].Trim() ?? ""; } catch { }
            bool fatal = e.Id is 1 or 18;
            list.Add(new Event(Kind.Hardware, e.TimeCreated ?? DateTime.MinValue,
                fatal ? Loc.T("Erro de hardware grave", "Fatal hardware error") : Loc.T("Erro de hardware corrigido", "Corrected hardware error"),
                first.Length > 0 ? first : Loc.T("O processador relatou um erro de hardware.", "The processor reported a hardware error."),
                Loc.T("Costuma indicar CPU ou memória instável: overclock, perfil XMP/EXPO da RAM, undervolt ou temperatura alta. Também pode ser PCIe (placa de vídeo ou SSD mal encaixados).",
                      "Usually means an unstable CPU or memory: overclock, RAM XMP/EXPO profile, undervolt or high temperature. It can also be PCIe (graphics card or SSD poorly seated)."), null));
        }

        return list.OrderByDescending(e => e.When).ToList();
    }

    static IEnumerable<EventRecord> Query(string log, string provider, string ids, long ms, int max = 300)
    {
        var xpath = $"*[System[Provider[@Name='{provider}'] and (EventID={ids}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        var result = new List<EventRecord>();
        try
        {
            using var r = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
            for (var e = r.ReadEvent(); e != null && result.Count < max; e = r.ReadEvent()) result.Add(e);
        }
        catch (Exception ex) { Paths.LogError("Crashes." + provider, ex); }
        return result;
    }

    static string? Prop(EventRecord e, int i) => i < e.Properties.Count && e.Properties[i].Value?.ToString() is { Length: > 0 } s ? s.Trim() : null;

    static uint ParseHex(string s)
    {
        s = s.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    /// <summary>Códigos de tela azul mais comuns, com explicação e o que fazer.</summary>
    static (string? name, string explain, string? hint) Bugcheck(uint code) => code switch
    {
        0x0A => ("IRQL_NOT_LESS_OR_EQUAL", Loc.T("Um driver acessou memória que não devia.", "A driver accessed memory it shouldn't."), Loc.T("Quase sempre driver com defeito (rede, áudio, vídeo). Atualize os drivers; se persistir, teste a memória RAM.", "Almost always a faulty driver (network, audio, video). Update drivers; if it persists, test the RAM.")),
        0xD1 => ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", Loc.T("Um driver acessou memória inválida.", "A driver accessed invalid memory."), Loc.T("Driver com defeito, muito comum com placa de rede/Wi-Fi e antivírus. Atualize ou reinstale os drivers.", "Faulty driver, very common with network/Wi-Fi and antivirus. Update or reinstall drivers.")),
        0x1A => ("MEMORY_MANAGEMENT", Loc.T("Erro no gerenciamento da memória.", "Memory management error."), Loc.T("Suspeite da RAM: desligue o XMP/EXPO e rode o Diagnóstico de Memória do Windows.", "Suspect the RAM: disable XMP/EXPO and run Windows Memory Diagnostic.")),
        0x50 => ("PAGE_FAULT_IN_NONPAGED_AREA", Loc.T("Acesso a um endereço de memória que não existe.", "Access to a memory address that doesn't exist."), Loc.T("RAM com defeito, driver ruim ou disco com problema. Teste a memória e o SMART do disco.", "Bad RAM, bad driver or disk problems. Test memory and disk SMART.")),
        0x3B => ("SYSTEM_SERVICE_EXCEPTION", Loc.T("Erro dentro de um serviço do sistema ou driver.", "Error inside a system service or driver."), Loc.T("Geralmente driver de vídeo ou antivírus. Atualize o driver de vídeo.", "Usually the video driver or antivirus. Update the video driver.")),
        0x7E => ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", Loc.T("Um driver gerou um erro que não foi tratado.", "A driver raised an unhandled error."), Loc.T("Driver com defeito ou incompatível. Veja se instalou algo novo antes do problema começar.", "Faulty or incompatible driver. Check whether you installed something new before it started.")),
        0x1E => ("KMODE_EXCEPTION_NOT_HANDLED", Loc.T("Erro não tratado no núcleo do Windows.", "Unhandled error in the Windows kernel."), Loc.T("Driver ou hardware instável (overclock).", "Driver or unstable hardware (overclock).")),
        0x124 => ("WHEA_UNCORRECTABLE_ERROR", Loc.T("O processador detectou um erro de hardware que não conseguiu corrigir.", "The CPU detected a hardware error it couldn't correct."), Loc.T("Hardware instável: overclock/undervolt, XMP da RAM, temperatura alta ou fonte fraca. Volte tudo ao padrão.", "Unstable hardware: overclock/undervolt, RAM XMP, high temperature or weak power supply. Revert everything to stock.")),
        0x116 => ("VIDEO_TDR_FAILURE", Loc.T("A placa de vídeo parou de responder e não voltou.", "The graphics card stopped responding and didn't recover."), Loc.T("Atualize ou reinstale o driver de vídeo (DDU). Verifique temperatura e overclock da GPU.", "Update or clean-reinstall the graphics driver (DDU). Check GPU temperature and overclock.")),
        0x117 => ("VIDEO_TDR_TIMEOUT_DETECTED", Loc.T("A placa de vídeo demorou demais para responder.", "The graphics card took too long to respond."), Loc.T("Driver de vídeo ou GPU instável.", "Video driver or unstable GPU.")),
        0x133 => ("DPC_WATCHDOG_VIOLATION", Loc.T("Um driver ficou tempo demais ocupando o processador.", "A driver held the CPU for too long."), Loc.T("Muito comum com firmware de SSD desatualizado ou driver de armazenamento. Atualize o firmware do SSD e o driver do chipset.", "Very common with outdated SSD firmware or storage drivers. Update SSD firmware and chipset driver.")),
        0x9F => ("DRIVER_POWER_STATE_FAILURE", Loc.T("Um driver falhou ao entrar ou sair da suspensão.", "A driver failed entering or leaving sleep."), Loc.T("Normalmente driver de Wi-Fi, USB ou vídeo. Atualize-os; desativar o início rápido também ajuda.", "Usually Wi-Fi, USB or video drivers. Update them; disabling fast startup also helps.")),
        0xEF => ("CRITICAL_PROCESS_DIED", Loc.T("Um processo essencial do Windows morreu.", "An essential Windows process died."), Loc.T("Arquivos do sistema corrompidos ou disco com problema. Rode \"Verificar e reparar\" em Manutenção.", "Corrupted system files or disk problems. Run \"Check and repair\" in Maintenance.")),
        0x139 => ("KERNEL_SECURITY_CHECK_FAILURE", Loc.T("O Windows detectou dados corrompidos na memória.", "Windows detected corrupted data in memory."), Loc.T("Driver incompatível ou RAM com defeito.", "Incompatible driver or bad RAM.")),
        0x101 => ("CLOCK_WATCHDOG_TIMEOUT", Loc.T("Um núcleo do processador parou de responder.", "A CPU core stopped responding.")
            , Loc.T("Processador instável (overclock/undervolt) ou BIOS desatualizada.", "Unstable CPU (overclock/undervolt) or outdated BIOS.")),
        0x7F => ("UNEXPECTED_KERNEL_MODE_TRAP", Loc.T("O processador gerou uma exceção inesperada.", "The CPU raised an unexpected trap."), Loc.T("Hardware instável ou superaquecimento.", "Unstable hardware or overheating.")),
        0x154 => ("UNEXPECTED_STORE_EXCEPTION", Loc.T("Erro na compressão de memória do Windows.", "Error in Windows memory compression."), Loc.T("Disco ou SSD com problema. Confira o SMART em Sistema → Armazenamento.", "Disk or SSD problem. Check SMART in System → Storage.")),
        0x7A => ("KERNEL_DATA_INPAGE_ERROR", Loc.T("Não deu para ler dados do disco para a memória.", "Couldn't read data from disk into memory."), Loc.T("Disco com setores ruins ou cabo SATA com problema. Rode \"Verificar discos\" em Manutenção.", "Disk with bad sectors or faulty SATA cable. Run \"Check drives\" in Maintenance.")),
        0xC000021A => ("STATUS_SYSTEM_PROCESS_TERMINATED", Loc.T("Um processo do sistema foi encerrado.", "A system process was terminated."), Loc.T("Atualização ou arquivo do sistema corrompido. Rode \"Verificar e reparar\" em Manutenção.", "Corrupted update or system file. Run \"Check and repair\" in Maintenance.")),
        0x19 => ("BAD_POOL_HEADER", Loc.T("Memória do núcleo corrompida.", "Corrupted kernel memory."), Loc.T("Driver com defeito ou RAM.", "Faulty driver or RAM.")),
        0xC2 => ("BAD_POOL_CALLER", Loc.T("Um driver fez uma requisição de memória inválida.", "A driver made an invalid memory request."), Loc.T("Driver com defeito.", "Faulty driver.")),
        0x109 => ("CRITICAL_STRUCTURE_CORRUPTION", Loc.T("Uma estrutura crítica do Windows foi alterada.", "A critical Windows structure was modified."), Loc.T("Driver problemático, RAM ou software de trapaça/overclock que mexe no núcleo.", "Problematic driver, RAM or software that patches the kernel.")),
        0xA0 => ("INTERNAL_POWER_ERROR", Loc.T("Erro no gerenciamento de energia.", "Power management error."), Loc.T("Driver de energia/chipset ou BIOS.", "Power/chipset driver or BIOS.")),
        _ => (null, Loc.T("Tela azul do Windows.", "Windows blue screen."), Loc.T("Pesquise o código mostrado e veja se instalou algum driver ou programa pouco antes.", "Search for the code shown and check whether you installed a driver or program shortly before.")),
    };

    /// <summary>Códigos de exceção mais comuns nos programas que fecham sozinhos.</summary>
    static (string what, string? hint) ExceptionInfo(string code) => code.ToLowerInvariant().TrimStart('0', 'x') switch
    {
        "c0000005" => (Loc.T("acesso inválido à memória", "invalid memory access"), Loc.T("Bug do programa, de um plugin/mod ou de um overlay injetado. Atualize o programa.", "A bug in the program, a plugin/mod or an injected overlay. Update the program.")),
        "c0000409" => (Loc.T("proteção contra corrupção de memória", "memory corruption protection"), Loc.T("O próprio programa detectou um erro grave e se encerrou. Atualize ou reinstale.", "The program detected a serious error and terminated itself. Update or reinstall.")),
        "e0434352" => (Loc.T("erro não tratado em programa .NET", "unhandled .NET error"), Loc.T("Erro interno do programa. Atualize; se for sempre igual, reinstale o .NET Desktop Runtime.", "Internal program error. Update it; if it keeps happening, reinstall .NET Desktop Runtime.")),
        "c00000fd" => (Loc.T("estouro de pilha", "stack overflow"), Loc.T("Bug do programa.", "A bug in the program.")),
        "c0000374" => (Loc.T("memória do programa corrompida", "corrupted program memory"), Loc.T("Bug do programa ou plugin. Se acontece em vários programas, teste a RAM.", "Program or plugin bug. If it happens in many programs, test the RAM.")),
        "80000003" => (Loc.T("parada de depuração", "debug breakpoint"), null),
        "c0000142" => (Loc.T("falha ao iniciar", "failed to start"), Loc.T("Arquivos do programa ou do sistema corrompidos. Reinstale o programa.", "Corrupted program or system files. Reinstall the program.")),
        "c000001d" => (Loc.T("instrução inválida", "illegal instruction"), Loc.T("Programa incompatível com o processador ou CPU instável.", "Program incompatible with the CPU, or an unstable CPU.")),
        "40000015" or "c0000602" => (Loc.T("encerramento forçado", "forced termination"), null),
        _ => (Loc.T("erro interno", "internal error"), null),
    };
}
