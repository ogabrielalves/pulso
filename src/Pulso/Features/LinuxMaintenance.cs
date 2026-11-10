using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>
/// Manutenção no Linux, com as ferramentas da própria distro (apt/dnf/pacman/zypper, fstrim, smartctl,
/// resolvectl, flatpak, snap, fwupdmgr). O que exige root passa pelo pkexec (pede a senha numa janela).
/// </summary>
public static class LinuxMaintenance
{
    public sealed record Outcome(bool Ok, string Summary, string Log, bool Warning = false);

    public enum Pm { None, Apt, Dnf, Pacman, Zypper }

    public sealed class CleanItem
    {
        public required string Id, Name, Desc;
        public bool Default;
        public long? Size;
        /// <summary>Comando de shell que precisa de root (junta tudo num só pedido de senha). Null = limpeza do próprio usuário.</summary>
        public string? RootCommand;
        public Func<long>? Measure;
        public Action? CleanUser;
    }

    // ------------------------------------------------------------- execução

    static bool IsRoot => Pulso.Platform.OS.Current.IsElevated;

    public static string? Which(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat(new[] { "/usr/sbin", "/sbin", "/usr/local/sbin" });
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static bool Has(string name) => Which(name) != null;

    public static bool CanElevate => IsRoot || Has("pkexec");

    /// <summary>Roda um comando entregando cada linha de saída (stdout + stderr). Com <paramref name="root"/>, usa pkexec se preciso.</summary>
    public static async Task<(int code, string output)> RunAsync(string exe, string[] args, bool root, Action<string>? onLine)
    {
        var log = new StringBuilder();
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        if (root && !IsRoot)
        {
            if (!Has("pkexec")) return (-1, Loc.T("pkexec não encontrado: rode o Pulso com sudo ou instale o polkit.", "pkexec not found: run Pulso with sudo or install polkit."));
            psi.FileName = Which("pkexec")!;
            psi.ArgumentList.Add(Which(exe) ?? exe);
        }
        else psi.FileName = Which(exe) ?? exe;
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) { return (-1, ex.Message); }
        if (p == null) return (-1, exe);
        using (p)
        {
            p.StandardInput.Close();
            void Feed(string? l)
            {
                if (l == null) return;
                lock (log) log.AppendLine(l);
                onLine?.Invoke(l);
            }
            var t1 = Task.Run(async () => { string? l; while ((l = await p.StandardOutput.ReadLineAsync()) != null) Feed(l); });
            var t2 = Task.Run(async () => { string? l; while ((l = await p.StandardError.ReadLineAsync()) != null) Feed(l); });
            await p.WaitForExitAsync();
            await Task.WhenAll(t1, t2);
            int code = p.ExitCode;
            if (root && !IsRoot && code is 126 or 127 && log.Length < 200)
                return (code, Loc.T("Autorização negada ou cancelada.", "Authorization denied or cancelled."));
            return (code, log.ToString());
        }
    }

    /// <summary>Roda um script de shell (várias etapas) numa única autorização.</summary>
    static Task<(int code, string output)> Sh(string script, bool root, Action<string>? onLine) =>
        RunAsync("sh", new[] { "-c", script }, root, onLine);

    static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    // ------------------------------------------------------------- distro

    public static Pm Manager { get; } = Has("apt-get") ? Pm.Apt : Has("dnf") ? Pm.Dnf : Has("pacman") ? Pm.Pacman : Has("zypper") ? Pm.Zypper : Pm.None;

    public static string ManagerName => Manager switch { Pm.Apt => "apt", Pm.Dnf => "dnf", Pm.Pacman => "pacman", Pm.Zypper => "zypper", _ => "—" };

    /// <summary>Instala um pacote da distro (pede a senha de administrador).</summary>
    public static async Task<Outcome> InstallPackageAsync(string package, Action<string> onLine)
    {
        string? script = Manager switch
        {
            Pm.Apt => $"apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y {package}",
            Pm.Dnf => $"dnf -y install {package}",
            Pm.Pacman => $"pacman -S --noconfirm {package}",
            Pm.Zypper => $"zypper --non-interactive install {package}",
            _ => null,
        };
        if (script == null) return new Outcome(false, Loc.T("Gerenciador de pacotes não reconhecido.", "Package manager not recognized."), "");
        var (code, output) = await Sh(script, true, onLine);
        return new Outcome(code == 0, code == 0 ? Loc.T($"{package} instalado ✓", $"{package} installed ✓") : Last(output), output);
    }

    // ------------------------------------------------------------- integridade dos pacotes (equivale ao DISM/SFC)

    public static async Task<Outcome> CheckPackagesAsync(Action<string> onLine)
    {
        string exe; string[] args; string? hint = null;
        switch (Manager)
        {
            case Pm.Apt:
                if (!Has("debsums")) return new Outcome(false, Loc.T("Instale o debsums: sudo apt install debsums", "Install debsums: sudo apt install debsums"), "");
                (exe, args) = ("debsums", new[] { "-s" });
                hint = "sudo apt reinstall <pacote>";
                break;
            case Pm.Dnf or Pm.Zypper:
                (exe, args) = ("rpm", new[] { "-Va" });
                hint = Manager == Pm.Dnf ? "sudo dnf reinstall <pacote>" : "sudo zypper install -f <pacote>";
                break;
            case Pm.Pacman:
                (exe, args) = ("pacman", new[] { "-Qkk" });
                hint = "sudo pacman -S <pacote>";
                break;
            default:
                return new Outcome(false, Loc.T("Gerenciador de pacotes não reconhecido.", "Package manager not recognized."), "");
        }
        var (code, output) = await RunAsync(exe, args, true, onLine);
        if (code == -1) return new Outcome(false, output, output);

        var bad = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => Manager switch
            {
                Pm.Pacman => l.Contains("warning:", StringComparison.OrdinalIgnoreCase) && !l.Contains("backup file", StringComparison.OrdinalIgnoreCase),
                Pm.Dnf or Pm.Zypper => !Regex.IsMatch(l, @"^\S+\s+c\s"), // ignora arquivos de configuração, que mudam por design
                _ => true,
            }).ToList();
        if (bad.Count == 0) return new Outcome(true, Loc.T("Nenhum arquivo do sistema alterado ✓", "No system file modified ✓"), output);
        return new Outcome(false,
            Loc.T($"{bad.Count} arquivo(s) diferem do pacote original. Para consertar, reinstale o pacote dono do arquivo ({hint}). Detalhes no log.",
                  $"{bad.Count} file(s) differ from the original package. To fix, reinstall the owning package ({hint}). Details in the log."),
            output, true);
    }

    // ------------------------------------------------------------- discos

    public static async Task<Outcome> SmartAsync(Action<string> onLine)
    {
        if (!Has("smartctl")) return new Outcome(false, Loc.T("Instale o smartmontools (smartctl) para ler a saúde dos discos.", "Install smartmontools (smartctl) to read drive health."), "");
        var (lc, lo) = await RunAsync("lsblk", new[] { "-dno", "NAME,TYPE" }, false, null);
        var disks = lo.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2 && p[1] == "disk" && Regex.IsMatch(p[0], "^[a-zA-Z0-9]+$")).Select(p => p[0]).ToList();
        if (lc != 0 || disks.Count == 0) return new Outcome(false, Loc.T("Nenhum disco encontrado.", "No drives found."), lo);
        var script = string.Join("; ", disks.Select(d => $"echo '=== /dev/{d}'; smartctl -H /dev/{d}"));
        var (_, output) = await Sh(script, true, onLine);
        if (output.StartsWith(Loc.T("Autorização", "Authorization"))) return new Outcome(false, output, output);
        int passed = Regex.Matches(output, "PASSED|: OK").Count, failed = Regex.Matches(output, "FAILED").Count;
        if (failed > 0) return new Outcome(false, Loc.T($"{failed} disco(s) reportam FALHA no SMART: faça backup agora.", $"{failed} drive(s) report SMART FAILURE: back up now."), output);
        if (passed == 0) return new Outcome(false, Loc.T("Não foi possível ler o SMART (veja o log).", "Could not read SMART (see the log)."), output, true);
        return new Outcome(true, Loc.T($"{passed} disco(s) com SMART OK ✓", $"{passed} drive(s) with SMART OK ✓"), output);
    }

    public static async Task<Outcome> TrimAsync(Action<string> onLine)
    {
        if (!Has("fstrim")) return new Outcome(false, Loc.T("fstrim não encontrado (pacote util-linux).", "fstrim not found (util-linux package)."), "");
        var (code, output) = await RunAsync("fstrim", new[] { "-av" }, true, onLine);
        if (code != 0) return new Outcome(false, Last(output), output);
        return new Outcome(true, string.IsNullOrWhiteSpace(output) ? Loc.T("Nenhuma unidade compatível com TRIM.", "No TRIM-capable drive.") : Loc.T("TRIM concluído ✓", "TRIM done ✓"), output);
    }

    // ------------------------------------------------------------- rede

    public static async Task<Outcome> FlushDnsAsync(Action<string>? onLine = null)
    {
        string[]? cmd = Has("resolvectl") ? new[] { "resolvectl", "flush-caches" }
            : Has("systemd-resolve") ? new[] { "systemd-resolve", "--flush-caches" } : null;
        if (cmd == null) return new Outcome(false, Loc.T("Nenhum cache de DNS do sistema encontrado (systemd-resolved).", "No system DNS cache found (systemd-resolved)."), "");
        var (code, output) = await RunAsync(cmd[0], cmd[1..], true, onLine);
        return new Outcome(code == 0, code == 0 ? Loc.T("Cache de DNS limpo ✓", "DNS cache cleared ✓") : Last(output), output);
    }

    public static async Task<Outcome> RestartNetworkAsync(Action<string>? onLine = null)
    {
        if (!Has("systemctl")) return new Outcome(false, "systemctl", "");
        var svc = Has("nmcli") ? "NetworkManager" : "systemd-networkd";
        var (code, output) = await RunAsync("systemctl", new[] { "restart", svc }, true, onLine);
        return new Outcome(code == 0, code == 0 ? Loc.T("Rede reiniciada ✓", "Network restarted ✓") : Last(output), output);
    }

    // ------------------------------------------------------------- limpeza

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    static long DirSize(string dir, TimeSpan? olderThan = null)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep))
                if (olderThan == null || DateTime.UtcNow - f.LastWriteTimeUtc > olderThan) total += f.Length;
        }
        catch { }
        return total;
    }

    static void DeleteContents(string dir, TimeSpan? olderThan = null, params string[] skip)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Deep))
            {
                if (skip.Any(s => f.FullName.StartsWith(s))) continue;
                if (olderThan != null && DateTime.UtcNow - f.LastWriteTimeUtc <= olderThan) continue;
                try { f.Delete(); } catch { }
            }
            if (olderThan == null)
                foreach (var d in new DirectoryInfo(dir).EnumerateDirectories()) try { d.Delete(true); } catch { }
        }
        catch { }
    }

    static long JournalSize()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("journalctl", "--disk-usage") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var m = Regex.Match(o, @"take up ([\d.,]+)([KMGT]?)");
            if (!m.Success) return 0;
            double v = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            return (long)(v * m.Groups[2].Value switch { "K" => 1024.0, "M" => 1024.0 * 1024, "G" => 1024.0 * 1024 * 1024, "T" => Math.Pow(1024, 4), _ => 1 });
        }
        catch { return 0; }
    }

    public static List<CleanItem> CleanItems()
    {
        var cache = Path.Combine(Home, ".cache");
        var thumbs = Path.Combine(cache, "thumbnails");
        var trash = Path.Combine(Home, ".local/share/Trash");
        var items = new List<CleanItem>
        {
            new() { Id = "trash", Name = Loc.T("Lixeira", "Trash"), Desc = Loc.T("Arquivos apagados que ainda estão na lixeira. Não dá para recuperar depois.", "Deleted files still in the trash. They can't be recovered afterwards."),
                Default = false, Measure = () => DirSize(Path.Combine(trash, "files")),
                CleanUser = () => { DeleteContents(Path.Combine(trash, "files")); DeleteContents(Path.Combine(trash, "info")); } },
            new() { Id = "thumbs", Name = Loc.T("Miniaturas de imagens", "Image thumbnails"), Desc = Loc.T("O gerenciador de arquivos recria quando precisar.", "The file manager recreates them when needed."),
                Default = true, Measure = () => DirSize(thumbs), CleanUser = () => DeleteContents(thumbs) },
            new() { Id = "appcache", Name = Loc.T("Cache dos aplicativos (+7 dias)", "App caches (7+ days old)"), Desc = Loc.T("Arquivos de ~/.cache sem uso há mais de 7 dias. Os apps recriam o que precisarem.", "Files in ~/.cache untouched for over 7 days. Apps recreate what they need."),
                Default = true, Measure = () => DirSize(cache, TimeSpan.FromDays(7)) - DirSize(thumbs, TimeSpan.FromDays(7)),
                CleanUser = () => DeleteContents(cache, TimeSpan.FromDays(7), thumbs) },
        };
        if (Has("journalctl"))
            items.Add(new() { Id = "journal", Name = Loc.T("Logs do sistema (journal)", "System logs (journal)"), Desc = Loc.T("Mantém só os últimos 7 dias. Precisa de senha de administrador.", "Keeps only the last 7 days. Needs the administrator password."),
                Default = true, Measure = JournalSize, RootCommand = "journalctl --vacuum-time=7d" });
        switch (Manager)
        {
            case Pm.Apt:
                items.Add(new() { Id = "pkgcache", Name = Loc.T("Cache de pacotes (apt)", "Package cache (apt)"), Desc = Loc.T("Instaladores .deb já baixados. Precisa de senha de administrador.", "Already downloaded .deb installers. Needs the administrator password."),
                    Default = true, Measure = () => DirSize("/var/cache/apt/archives"), RootCommand = "apt-get clean" });
                items.Add(new() { Id = "autoremove", Name = Loc.T("Pacotes órfãos (apt autoremove)", "Orphaned packages (apt autoremove)"), Desc = Loc.T("Dependências que nenhum programa usa mais. Remove pacotes: confira antes.", "Dependencies no program uses anymore. Removes packages: review first."),
                    Default = false, RootCommand = "DEBIAN_FRONTEND=noninteractive apt-get -y autoremove" });
                break;
            case Pm.Dnf:
                items.Add(new() { Id = "pkgcache", Name = Loc.T("Cache de pacotes (dnf)", "Package cache (dnf)"), Desc = Loc.T("Metadados e pacotes baixados. Precisa de senha de administrador.", "Downloaded metadata and packages. Needs the administrator password."),
                    Default = true, Measure = () => DirSize("/var/cache/libdnf5") + DirSize("/var/cache/dnf"), RootCommand = "dnf clean all" });
                items.Add(new() { Id = "autoremove", Name = Loc.T("Pacotes órfãos (dnf autoremove)", "Orphaned packages (dnf autoremove)"), Desc = Loc.T("Dependências que nenhum programa usa mais. Remove pacotes: confira antes.", "Dependencies no program uses anymore. Removes packages: review first."),
                    Default = false, RootCommand = "dnf -y autoremove" });
                break;
            case Pm.Pacman:
                items.Add(new() { Id = "pkgcache", Name = Loc.T("Cache de pacotes (pacman)", "Package cache (pacman)"), Desc = Loc.T("Pacotes de versões antigas ou já desinstalados. Precisa de senha de administrador.", "Packages of old or uninstalled versions. Needs the administrator password."),
                    Default = true, Measure = () => DirSize("/var/cache/pacman/pkg"), RootCommand = "pacman -Sc --noconfirm" });
                break;
            case Pm.Zypper:
                items.Add(new() { Id = "pkgcache", Name = Loc.T("Cache de pacotes (zypper)", "Package cache (zypper)"), Desc = Loc.T("Pacotes baixados. Precisa de senha de administrador.", "Downloaded packages. Needs the administrator password."),
                    Default = true, Measure = () => DirSize("/var/cache/zypp"), RootCommand = "zypper clean --all" });
                break;
        }
        return items;
    }

    public static void MeasureAll(List<CleanItem> items)
    {
        foreach (var it in items) try { it.Size = it.Measure?.Invoke(); } catch { }
    }

    public static async Task<Outcome> CleanAsync(List<CleanItem> items, Action<string> onLine)
    {
        long before = items.Sum(i => i.Size ?? 0);
        var log = new StringBuilder();
        foreach (var it in items.Where(i => i.CleanUser != null))
        {
            onLine("• " + it.Name);
            await Task.Run(() => it.CleanUser!());
        }
        var rootCmds = items.Where(i => i.RootCommand != null).Select(i => i.RootCommand!).ToList();
        if (rootCmds.Count > 0)
        {
            var (code, output) = await Sh(string.Join("; ", rootCmds), true, onLine);
            log.Append(output);
            if (code != 0) return new Outcome(false, Last(output), log.ToString());
        }
        return new Outcome(true, Loc.T($"Limpeza concluída ✓ (cerca de {Fmt.Bytes(before)} liberados)", $"Cleanup done ✓ (about {Fmt.Bytes(before)} freed)"), log.ToString());
    }

    // ------------------------------------------------------------- atualizações (equivale ao winget / Windows Update)

    public static async Task<Outcome> UpdateSystemAsync(Action<string> onLine)
    {
        string? script = Manager switch
        {
            Pm.Apt => "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get -y upgrade",
            Pm.Dnf => "dnf -y upgrade --refresh",
            Pm.Pacman => "pacman -Syu --noconfirm",
            Pm.Zypper => "zypper --non-interactive refresh && zypper --non-interactive update",
            _ => null,
        };
        if (script == null) return new Outcome(false, Loc.T("Gerenciador de pacotes não reconhecido.", "Package manager not recognized."), "");
        var (code, output) = await Sh(script, true, onLine);
        return new Outcome(code == 0, code == 0 ? Loc.T("Pacotes do sistema atualizados ✓", "System packages updated ✓") : Last(output), output);
    }

    public static async Task<Outcome> UpdateFlatpakAsync(Action<string> onLine)
    {
        var (code, output) = await RunAsync("flatpak", new[] { "update", "-y", "--noninteractive" }, false, onLine);
        return new Outcome(code == 0, code == 0 ? Loc.T("Flatpaks atualizados ✓", "Flatpaks updated ✓") : Last(output), output);
    }

    public static async Task<Outcome> UpdateSnapAsync(Action<string> onLine)
    {
        var (code, output) = await RunAsync("snap", new[] { "refresh" }, true, onLine);
        return new Outcome(code == 0, code == 0 ? Loc.T("Snaps atualizados ✓", "Snaps updated ✓") : Last(output), output);
    }

    public static async Task<Outcome> FirmwareCheckAsync(Action<string> onLine)
    {
        await RunAsync("fwupdmgr", new[] { "refresh", "--force" }, false, null);
        var (code, output) = await RunAsync("fwupdmgr", new[] { "get-updates", "--no-unreported-check" }, false, onLine);
        // código 2 = nada para atualizar
        if (code == 2 || output.Contains("No updates", StringComparison.OrdinalIgnoreCase) || output.Contains("No updatable", StringComparison.OrdinalIgnoreCase))
            return new Outcome(true, Loc.T("Firmware em dia ✓", "Firmware up to date ✓"), output);
        return code == 0 ? new Outcome(true, Loc.T("Há atualizações de firmware (veja o log). Use 'Instalar firmware'.", "Firmware updates available (see the log). Use 'Install firmware'."), output, true)
                         : new Outcome(false, Last(output), output);
    }

    public static async Task<Outcome> FirmwareInstallAsync(Action<string> onLine)
    {
        var (code, output) = await RunAsync("fwupdmgr", new[] { "update", "-y", "--no-reboot-check" }, false, onLine);
        return new Outcome(code == 0, code == 0 ? Loc.T("Firmware atualizado ✓ (pode pedir reinicialização)", "Firmware updated ✓ (may ask for a restart)") : Last(output), output);
    }

    public static async Task<Outcome> GpuDriversListAsync(Action<string> onLine)
    {
        var (code, output) = await RunAsync("ubuntu-drivers", new[] { "devices" }, false, onLine);
        return new Outcome(code == 0, code == 0 ? (string.IsNullOrWhiteSpace(output) ? Loc.T("Nenhum driver proprietário adicional necessário ✓", "No additional proprietary driver needed ✓") : Loc.T("Veja o log: o marcado 'recommended' é o indicado.", "See the log: the one marked 'recommended' is suggested.")) : Last(output), output);
    }

    public static async Task<Outcome> GpuDriversInstallAsync(Action<string> onLine)
    {
        var (code, output) = await RunAsync("ubuntu-drivers", new[] { "install" }, true, onLine);
        return new Outcome(code == 0, code == 0 ? Loc.T("Driver recomendado instalado ✓ Reinicie o PC.", "Recommended driver installed ✓ Restart the PC.") : Last(output), output);
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
}
