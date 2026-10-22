using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>
/// Reparo e limpeza com as ferramentas do próprio Windows (DISM, SFC, chkdsk, defrag, ipconfig, netsh).
/// Ordem certa (DISM antes do SFC), verificação de disco online (sem reiniciar) e limpeza que mostra
/// o tamanho antes de apagar e pula arquivos em uso ou recém-criados.
/// </summary>
public static partial class Maintenance
{
    public sealed record Outcome(bool Ok, string Summary, string Log, bool Warning = false);

    public delegate void Report(double? pct, string text);

    // ------------------------------------------------------------- integridade do Windows

    public static async Task<Outcome> RepairWindowsAsync(Report report, CancellationToken ct)
    {
        if (!Native.IsAdmin) return new Outcome(false, Loc.T("O reparo do Windows precisa de administrador.", "Windows repair requires administrator."), "");
        var log = new StringBuilder();
        report(0, Loc.T("Etapa 1 de 2 · DISM: reparando a imagem do Windows…", "Step 1 of 2 · DISM: repairing the Windows image…"));
        var (dc, dout) = await RunAsync("dism.exe", "/Online /Cleanup-Image /RestoreHealth", false,
            l => { if (Pct(l) is double p) report(p / 2, Loc.T($"Etapa 1 de 2 · DISM: {Fmt.N(p, 1)}%", $"Step 1 of 2 · DISM: {Fmt.N(p, 1)}%")); }, ct);
        log.AppendLine("== DISM /RestoreHealth (exit " + dc + ")").AppendLine(dout);
        if (dc != 0)
            return new Outcome(false, Loc.T("O DISM não conseguiu reparar a imagem: ", "DISM could not repair the image: ") + DismError(dc), log.ToString());

        report(50, Loc.T("Etapa 2 de 2 · SFC: verificando os arquivos do sistema…", "Step 2 of 2 · SFC: checking system files…"));
        var (sc, sout) = await RunAsync("sfc.exe", "/scannow", true,
            l => { if (Pct(l) is double p) report(50 + p / 2, Loc.T($"Etapa 2 de 2 · SFC: {Fmt.N(p)}%", $"Step 2 of 2 · SFC: {Fmt.N(p)}%")); }, ct);
        log.AppendLine("== SFC /scannow (exit " + sc + ")").AppendLine(sout);
        string n = Normalize(sout);
        if (n.Contains("nao encontrou nenhuma violacao") || n.Contains("did not find any integrity violations"))
            return new Outcome(true, Loc.T("Nenhum problema: os arquivos do Windows estão íntegros ✓", "No problems: Windows files are intact ✓"), log.ToString());
        if (Regex.IsMatch(n, "repar(ou|ados) com (exito|sucesso)|successfully repaired"))
            return new Outcome(true, Loc.T("Foram encontrados arquivos corrompidos e todos foram reparados ✓ Reinicie o Windows.", "Corrupt files were found and all were repaired ✓ Restart Windows."), log.ToString(), true);
        if (Regex.IsMatch(n, "nao (pode|pôde|conseguiu) (corrigir|reparar)|unable to fix"))
            return new Outcome(false, Loc.T("Há arquivos corrompidos que não puderam ser reparados. Reinicie e rode de novo; se persistir, detalhes em C:\\Windows\\Logs\\CBS\\CBS.log.", "Some corrupt files could not be repaired. Restart and run again; if it persists, see C:\\Windows\\Logs\\CBS\\CBS.log."), log.ToString());
        if (n.Contains("reparo pendente") || n.Contains("repair pending") || n.Contains("pending"))
            return new Outcome(false, Loc.T("Há um reparo pendente do Windows. Reinicie o computador e rode de novo.", "There is a pending Windows repair. Restart the computer and run again."), log.ToString());
        return new Outcome(sc == 0, LastLine(sout) ?? Loc.T($"SFC terminou com código {sc}", $"SFC finished with code {sc}"), log.ToString());
    }

    static string DismError(int code) => (uint)code switch
    {
        0x800F081F or 0x800F0906 or 0x800F0907 => Loc.T("não encontrou os arquivos de reparo (sem internet ou Windows Update bloqueado)", "could not find repair files (no internet or Windows Update blocked)"),
        0x800F0922 => Loc.T("há atualizações pendentes; reinicie e tente de novo", "there are pending updates; restart and try again"),
        0x80070005 or 740 => Loc.T("precisa de administrador", "administrator required"),
        0x800705B4 => Loc.T("tempo esgotado", "timed out"),
        _ => $"0x{(uint)code:X8}",
    };

    // ------------------------------------------------------------- disco

    public sealed class Volume
    {
        public string Letter = "", Label = "", Format = "";
        public long Size, Free;
        public DateTime? LastOptimized;
        public string LastOptimizedOp = "";
        public string Display => $"{Letter}{(Label != "" ? " " + Label : "")} · {Format} · {Fmt.Capacity(Size)}";
    }

    public static List<Volume> Volumes()
    {
        var list = new List<Volume>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new Volume { Letter = d.Name.TrimEnd('\\'), Label = d.VolumeLabel, Format = d.DriveFormat, Size = d.TotalSize, Free = d.AvailableFreeSpace });
            }
            catch { }
        }
        // Última otimização: evento 258 do "Microsoft-Windows-Defrag" (Properties[0] = operação, [1] = "(C:)")
        try
        {
            var q = new EventLogQuery("Application", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-Defrag'] and (EventID=258)]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            for (int i = 0; i < 300; i++)
            {
                using var e = r.ReadEvent();
                if (e == null) break;
                if (e.Properties.Count < 2 || e.Properties[1].Value is not string vol) continue;
                var op = e.Properties[0].Value as string ?? "";
                if (op.Contains("estimativa", StringComparison.OrdinalIgnoreCase) || op.Contains("estimate", StringComparison.OrdinalIgnoreCase) || op.Contains("análise", StringComparison.OrdinalIgnoreCase) || op.Contains("analysis", StringComparison.OrdinalIgnoreCase)) continue;
                var v = list.FirstOrDefault(x => vol.Contains(x.Letter, StringComparison.OrdinalIgnoreCase));
                if (v != null && v.LastOptimized == null) { v.LastOptimized = e.TimeCreated; v.LastOptimizedOp = op; }
                if (list.All(x => x.LastOptimized != null)) break;
            }
        }
        catch { }
        return list;
    }

    /// <summary>chkdsk /scan: verificação online (sem reiniciar). Código 3 = precisa de reparo offline.</summary>
    public static async Task<Outcome> ScanDiskAsync(Volume v, Report report, CancellationToken ct)
    {
        if (!v.Format.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            return new Outcome(true, Loc.T($"{v.Letter} usa {v.Format}: a verificação online só existe para NTFS.", $"{v.Letter} uses {v.Format}: online scan is only available for NTFS."), "", true);
        if (!Native.IsAdmin) return new Outcome(false, Loc.T("A verificação de disco precisa de administrador.", "Drive check requires administrator."), "");
        report(0, Loc.T($"Verificando {v.Letter}…", $"Checking {v.Letter}…"));
        var (code, output) = await RunAsync("chkdsk.exe", $"{v.Letter} /scan", false, l => { if (Pct(l) is double p) report(p, Loc.T($"Verificando {v.Letter}: {Fmt.N(p)}%", $"Checking {v.Letter}: {Fmt.N(p)}%")); }, ct);
        string log = $"== chkdsk {v.Letter} /scan (exit {code})\r\n{output}";
        // o código 3 também sai quando o chkdsk nem conseguiu rodar (acesso negado, volume em uso): só é "erro no disco" se ele chegou a verificar
        string n = Normalize(output);
        bool ran = Regex.IsMatch(n, "etapa|stage|verific|scann|arquivos|files");
        if (code == 3 && (!ran || Regex.IsMatch(n, "acesso negado|access denied|privileg|elevac|elevation")))
            return new Outcome(false, Loc.T($"Não foi possível verificar {v.Letter}: ", $"Could not check {v.Letter}: ") + (LastLine(output) ?? Loc.T("o chkdsk não rodou", "chkdsk did not run")), log);
        return code switch
        {
            0 => new Outcome(true, Loc.T($"{v.Letter} sem erros ✓", $"{v.Letter} has no errors ✓"), log),
            1 => new Outcome(true, Loc.T($"{v.Letter}: erros encontrados e corrigidos ✓", $"{v.Letter}: errors found and fixed ✓"), log, true),
            2 => new Outcome(true, Loc.T($"{v.Letter} sem erros (limpeza interna feita) ✓", $"{v.Letter} has no errors (internal cleanup done) ✓"), log),
            3 => new Outcome(false, Loc.T($"{v.Letter} tem erros que só podem ser corrigidos com o disco fora de uso. Agende o reparo para a próxima reinicialização.", $"{v.Letter} has errors that can only be fixed with the drive offline. Schedule the repair for the next restart."), log),
            _ => new Outcome(false, LastLine(output) ?? Loc.T($"chkdsk terminou com código {code}", $"chkdsk finished with code {code}"), log),
        };
    }

    /// <summary>Marca o volume para o Windows verificar e corrigir na inicialização (equivale ao chkdsk /f agendado).</summary>
    public static async Task<Outcome> ScheduleDiskRepairAsync(Volume v)
    {
        var (code, output) = await RunAsync("fsutil.exe", $"dirty set {v.Letter}", false, null, CancellationToken.None);
        return code == 0
            ? new Outcome(true, Loc.T($"Reparo de {v.Letter} agendado: o Windows vai corrigir o disco na próxima reinicialização (pode levar alguns minutos).", $"{v.Letter} repair scheduled: Windows will fix the drive on the next restart (it may take a few minutes)."), output)
            : new Outcome(false, LastLine(output) ?? $"fsutil {code}", output);
    }

    /// <summary>defrag /O: o Windows escolhe TRIM (SSD) ou desfragmentação (HD).</summary>
    public static async Task<Outcome> OptimizeAsync(Volume v, Report report, CancellationToken ct)
    {
        if (!Native.IsAdmin) return new Outcome(false, Loc.T("Otimizar unidades precisa de administrador.", "Optimizing drives requires administrator."), "");
        report(null, Loc.T($"Otimizando {v.Letter}…", $"Optimizing {v.Letter}…"));
        var (code, output) = await RunAsync("defrag.exe", $"{v.Letter} /O /U", false, l => { if (Pct(l) is double p) report(p, Loc.T($"Otimizando {v.Letter}: {Fmt.N(p)}%", $"Optimizing {v.Letter}: {Fmt.N(p)}%")); }, ct);
        string log = $"== defrag {v.Letter} /O (exit {code})\r\n{output}";
        // o defrag às vezes falha e mesmo assim sai com 0: confere o texto
        if (Regex.IsMatch(Normalize(output), @"nao (pode|pôde|foi possivel)|cannot|could not|0x8[0-9a-f]{7}")) code = code == 0 ? 1 : code;
        return code == 0
            ? new Outcome(true, Loc.T($"{v.Letter} otimizada ✓", $"{v.Letter} optimized ✓"), log)
            : new Outcome(false, Loc.T($"{v.Letter}: ", $"{v.Letter}: ") + (LastLine(output) ?? $"defrag {code}"), log);
    }

    // ------------------------------------------------------------- limpeza

    public sealed class CleanItem
    {
        public required string Id, Name, Desc;
        public bool Default;
        public long? Size;               // null = não dá para medir antes
        public required Func<long> Measure;
        public required Func<CancellationToken, Task> Clean;
    }

    static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly string LocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string ProgData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static List<CleanItem> CleanItems()
    {
        string wuDownload = Path.Combine(Win, "SoftwareDistribution", "Download");
        string[] doCache =
        {
            Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
            Path.Combine(Win, @"SoftwareDistribution\DeliveryOptimization"),
        };
        string[] dumps =
        {
            Path.Combine(Win, "Minidump"), Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive"),
            Path.Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(LocalApp, "CrashDumps"),
        };
        string memDmp = Path.Combine(Win, "MEMORY.DMP");
        string explorer = Path.Combine(LocalApp, @"Microsoft\Windows\Explorer");

        return new List<CleanItem>
        {
            new()
            {
                Id = "temp-user", Default = true, Name = Loc.T("Arquivos temporários do usuário", "User temporary files"),
                Desc = Loc.T("Sobras de instaladores e programas. Arquivos em uso ou criados na última hora são mantidos.", "Leftovers from installers and programs. Files in use or created in the last hour are kept."),
                Measure = () => DirSize(Path.GetTempPath()), Clean = _ => Task.Run(() => DeleteContents(Path.GetTempPath(), TimeSpan.FromHours(1))),
            },
            new()
            {
                Id = "temp-win", Default = true, Name = Loc.T("Arquivos temporários do Windows", "Windows temporary files"),
                Desc = Loc.T("Pasta C:\\Windows\\Temp. Arquivos em uso ou criados na última hora são mantidos.", "C:\\Windows\\Temp folder. Files in use or created in the last hour are kept."),
                Measure = () => DirSize(Path.Combine(Win, "Temp")), Clean = _ => Task.Run(() => DeleteContents(Path.Combine(Win, "Temp"), TimeSpan.FromHours(1))),
            },
            new()
            {
                Id = "wu-download", Default = true, Name = Loc.T("Downloads do Windows Update", "Windows Update downloads"),
                Desc = Loc.T("Pacotes já baixados. O histórico de atualizações é mantido; o Windows Update pausa por alguns segundos.", "Already downloaded packages. Update history is kept; Windows Update pauses for a few seconds."),
                Measure = () => DirSize(wuDownload),
                Clean = async ct =>
                {
                    await RunAsync("net.exe", "stop wuauserv", false, null, ct);
                    await RunAsync("net.exe", "stop bits", false, null, ct);
                    try { DeleteContents(wuDownload, TimeSpan.Zero); }
                    finally
                    {
                        await RunAsync("net.exe", "start bits", false, null, CancellationToken.None);
                        await RunAsync("net.exe", "start wuauserv", false, null, CancellationToken.None);
                    }
                },
            },
            new()
            {
                Id = "delivery", Default = true, Name = Loc.T("Cache de Otimização de Entrega", "Delivery Optimization cache"),
                Desc = Loc.T("Cópias de atualizações que o Windows compartilha com outros PCs. Ele baixa de novo se precisar.", "Copies of updates Windows shares with other PCs. It downloads again if needed."),
                Measure = () => doCache.Sum(DirSize),
                Clean = ct => RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"", false, null, ct),
            },
            new()
            {
                Id = "thumbs", Default = true, Name = Loc.T("Cache de miniaturas", "Thumbnail cache"),
                Desc = Loc.T("Miniaturas de fotos e vídeos do Explorador. São recriadas quando você abre as pastas.", "Photo and video thumbnails in Explorer. They are recreated when you open the folders."),
                Measure = () => FilesSize(explorer, "thumbcache_*.db") + FilesSize(explorer, "iconcache_*.db"),
                Clean = _ => Task.Run(() => { DeleteFiles(explorer, "thumbcache_*.db"); DeleteFiles(explorer, "iconcache_*.db"); }),
            },
            new()
            {
                Id = "dumps", Default = false, Name = Loc.T("Relatórios e despejos de erro", "Error reports and crash dumps"),
                Desc = Loc.T("Gerados quando um programa trava ou dá tela azul. Podem ocupar vários GB, mas servem para investigar a causa: só apague se não precisar.", "Created when a program crashes or a blue screen happens. They can take several GB but help find the cause: only delete if you don't need them."),
                Measure = () => dumps.Sum(DirSize) + (File.Exists(memDmp) ? new FileInfo(memDmp).Length : 0),
                Clean = _ => Task.Run(() => { foreach (var d in dumps) DeleteContents(d, TimeSpan.Zero); TryDelete(memDmp); }),
            },
            new()
            {
                Id = "components", Default = false, Name = Loc.T("Componentes antigos do Windows Update", "Old Windows Update components"),
                Desc = Loc.T("Versões substituídas de arquivos do sistema (DISM StartComponentCleanup). Costuma liberar alguns GB e leva alguns minutos; depois não dá para desinstalar atualizações antigas.", "Superseded versions of system files (DISM StartComponentCleanup). Usually frees a few GB and takes a few minutes; afterwards old updates cannot be uninstalled."),
                Measure = () => -1,
                Clean = ct => RunAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", false, null, ct),
            },
            new()
            {
                Id = "recycle", Default = false, Name = Loc.T("Lixeira", "Recycle Bin"),
                Desc = Loc.T("Esvazia a lixeira de todas as unidades. Não dá para recuperar depois.", "Empties the Recycle Bin on every drive. Cannot be undone."),
                Measure = RecycleSize,
                Clean = _ => Task.Run(() => SHEmptyRecycleBin(0, null, 0x1 | 0x2 | 0x4)),
            },
        };
    }

    public static void MeasureAll(List<CleanItem> items)
    {
        foreach (var it in items)
        {
            try { long s = it.Measure(); it.Size = s < 0 ? null : s; }
            catch { it.Size = null; }
        }
    }

    /// <summary>Limpa os itens escolhidos e devolve quanto foi liberado (medindo antes e depois).</summary>
    public static async Task<Outcome> CleanAsync(List<CleanItem> items, Report report, CancellationToken ct)
    {
        var log = new StringBuilder();
        long freed = 0;
        string disk = Path.GetPathRoot(Win) ?? "C:\\";
        long freeBefore = new DriveInfo(disk).AvailableFreeSpace;
        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var it = items[i];
            report(i * 100.0 / items.Count, Loc.T($"Limpando: {it.Name}…", $"Cleaning: {it.Name}…"));
            long before = SafeMeasure(it);
            try { await it.Clean(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.AppendLine($"{it.Name}: {ex.Message}"); }
            long after = SafeMeasure(it);
            long f = before >= 0 && after >= 0 ? Math.Max(0, before - after) : 0;
            freed += f;
            it.Size = after < 0 ? null : after;
            log.AppendLine($"{it.Name}: {Fmt.Bytes(f)}");
        }
        // Itens sem medição prévia (ex.: componentes do Windows) entram pela diferença de espaço livre
        long diskFreed = new DriveInfo(disk).AvailableFreeSpace - freeBefore;
        long total = Math.Max(freed, diskFreed);
        log.AppendLine($"Total: {Fmt.Bytes(total)}");
        return new Outcome(true, Loc.T($"{Fmt.Bytes(total)} liberados ✓", $"{Fmt.Bytes(total)} freed ✓"), log.ToString());
    }

    static long SafeMeasure(CleanItem it)
    {
        try { return it.Measure(); } catch { return -1; }
    }

    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long s = 0;
        try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep)) try { s += f.Length; } catch { } }
        catch { }
        return s;
    }

    static long FilesSize(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return 0;
        long s = 0;
        try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern)) try { s += f.Length; } catch { } }
        catch { }
        return s;
    }

    /// <summary>Apaga o conteúdo da pasta (não a pasta), pulando o que está em uso ou foi mexido há menos de <paramref name="minAge"/>.</summary>
    static void DeleteContents(string dir, TimeSpan minAge)
    {
        if (!Directory.Exists(dir)) return;
        var limit = DateTime.Now - minAge;
        var root = new DirectoryInfo(dir);
        foreach (var f in root.EnumerateFiles("*", Deep))
        {
            try
            {
                if (minAge > TimeSpan.Zero && (f.LastWriteTime > limit || f.CreationTime > limit)) continue;
                if (f.IsReadOnly) f.IsReadOnly = false;
                f.Delete();
            }
            catch { }
        }
        // pastas vazias, das mais fundas para as mais rasas
        foreach (var d in root.EnumerateDirectories("*", Deep).OrderByDescending(d => d.FullName.Length))
            try { if (!d.EnumerateFileSystemInfos().Any()) d.Delete(); } catch { }
    }

    static void DeleteFiles(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, pattern)) TryDelete(f);
    }

    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    static long RecycleSize()
    {
        long s = 0;
        string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) return 0;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                s += DirSize(Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid));
            }
            catch { }
        }
        return s;
    }

    // ------------------------------------------------------------- rede e memória

    public static async Task<Outcome> FlushDnsAsync()
    {
        if (!Native.IsAdmin) return new Outcome(false, Loc.T("Limpar o DNS precisa de administrador.", "Clearing DNS requires administrator."), "");
        var (code, output) = await RunAsync("ipconfig.exe", "/flushdns", false, null, CancellationToken.None);
        return new Outcome(code == 0, code == 0 ? Loc.T("Cache de DNS limpo ✓", "DNS cache cleared ✓") : LastLine(output) ?? $"ipconfig {code}", output);
    }

    public static async Task<Outcome> ResetNetworkAsync()
    {
        var (c1, o1) = await RunAsync("netsh.exe", "winsock reset", false, null, CancellationToken.None);
        var (c2, o2) = await RunAsync("netsh.exe", "int ip reset", false, null, CancellationToken.None);
        bool ok = c1 == 0; // "int ip reset" pode falhar em uma chave protegida e mesmo assim resetar o resto
        return new Outcome(ok, ok ? Loc.T("Rede resetada ✓ Reinicie o Windows para concluir.", "Network reset ✓ Restart Windows to finish.") : LastLine(o1) ?? $"netsh {c1}", o1 + "\r\n" + o2, ok);
    }

    /// <summary>Abre o Diagnóstico de Memória do Windows (testa toda a RAM na reinicialização).</summary>
    public static void OpenMemoryDiagnostic()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mdsched.exe")) { UseShellExecute = true }); }
        catch (Exception ex) { Paths.LogError("Maintenance.mdsched", ex); }
    }

    // ------------------------------------------------------------- relatório

    public static string SaveReport(string text)
    {
        var dir = Path.Combine(Paths.Documents, "Manutencao");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"manutencao-{DateTime.Now:yyyy-MM-dd_HH-mm}.txt");
        File.WriteAllText(file, text, Encoding.UTF8);
        return file;
    }

    // ------------------------------------------------------------- execução

    /// <summary>
    /// Roda uma ferramenta de linha de comando, entregando cada linha (inclusive as de progresso, que usam \r).
    /// O SFC escreve em UTF-16; as demais na página ANSI ou UTF-8, decodificada linha a linha.
    /// </summary>
    static async Task<(int code, string output)> RunAsync(string exe, string args, bool utf16, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        if (exe == "powershell.exe") psi.FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(exe);
        p.StandardInput.Close(); // nenhuma ferramenta deve ficar esperando "S/N"
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var log = new StringBuilder();
        var errBytes = new MemoryStream();
        var err = p.StandardError.BaseStream.CopyToAsync(errBytes);
        var stream = p.StandardOutput.BaseStream;
        var buf = new byte[4096];
        var line = new List<byte>(256);
        string? crLine = null; // linha terminada em \r: progresso (sobrescrito) ou metade de um \r\n
        int n;
        void Keep(string s)
        {
            // linhas de progresso repetidas não vão para o relatório
            if (!(s.Length < 90 && Pct(s) != null)) log.AppendLine(s);
        }
        void Separator(bool cr)
        {
            string? s = line.Count == 0 ? null : Decode(line.ToArray(), utf16).Replace("\0", "").Trim();
            line.Clear();
            if (cr)
            {
                if (!string.IsNullOrEmpty(s)) { onLine?.Invoke(s); crLine = s; }
                return;
            }
            if (!string.IsNullOrEmpty(s)) { onLine?.Invoke(s); Keep(s); }
            else if (crLine != null) Keep(crLine); // era um \r\n
            crLine = null;
        }
        while ((n = await stream.ReadAsync(buf)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                bool sep = b is 0x0D or 0x0A;
                // em UTF-16 o separador vem seguido de 0x00
                if (sep && utf16 && i + 1 < n && buf[i + 1] == 0) { Separator(b == 0x0D); i++; continue; }
                if (sep && !utf16) { Separator(b == 0x0D); continue; }
                if (crLine != null && line.Count == 0) crLine = null; // \r sozinho: a linha foi sobrescrita
                line.Add(b);
            }
        }
        Separator(false);
        await p.WaitForExitAsync(CancellationToken.None);
        try { await err; } catch { }
        ct.ThrowIfCancellationRequested();
        // mensagens de erro (ex.: "requer elevação") costumam vir pelo stderr
        if (errBytes.Length > 0)
            foreach (var l in Decode(errBytes.ToArray(), utf16).Replace("\0", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
                log.AppendLine(l);
        return (p.ExitCode, log.ToString());
    }

    static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
    static Encoding? _ansi, _oem;

    /// <summary>
    /// Cada ferramenta escolhe sua página de código (ipconfig/defrag usam OEM, chkdsk usa ANSI),
    /// então decodifica nas duas e fica com a que produz letras acentuadas de verdade.
    /// </summary>
    static string Decode(byte[] bytes, bool utf16)
    {
        if (utf16) return Encoding.Unicode.GetString(bytes.Length % 2 == 0 ? bytes : bytes[..^1]);
        try { return Utf8Strict.GetString(bytes); }
        catch (DecoderFallbackException) { }
        if (_ansi == null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _ansi = Encoding.GetEncoding((int)GetACP());
            _oem = Encoding.GetEncoding((int)GetOEMCP());
        }
        string a = _ansi.GetString(bytes), o = _oem!.GetString(bytes);
        return Score(o) > Score(a) ? o : a;
    }

    const string Accents = "áéíóúâêôãõçàüÁÉÍÓÚÂÊÔÃÕÇÀÜ";

    static int Score(string s)
    {
        int score = 0;
        foreach (var c in s)
        {
            if (Accents.Contains(c)) score += 2;
            else if (c > 127) score -= 1; // símbolos e caixas: decodificação errada
        }
        return score;
    }

    static readonly Regex PctRx = new(@"(\d{1,3}(?:[.,]\d+)?)\s?%", RegexOptions.Compiled);

    static double? Pct(string line)
    {
        var ms = PctRx.Matches(line);
        if (ms.Count == 0) return null;
        // chkdsk mostra "Etapa: 40%; Total: 13%": o último é o total
        var v = ms[^1].Groups[1].Value.Replace(',', '.');
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d <= 100 ? d : null;
    }

    static string Normalize(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return Regex.Replace(sb.ToString(), @"\s+", " ");
    }

    static string? LastLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 3 && Pct(l) == null);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetOEMCP();

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(nint hwnd, string? root, uint flags);
}
