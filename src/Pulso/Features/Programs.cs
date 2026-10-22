using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>
/// Programas instalados (lidos do registro, como em "Aplicativos instalados") e atualizações pelo winget,
/// o gerenciador de pacotes oficial do Windows.
/// </summary>
public static class Programs
{
    public sealed record Installed(string Name, string Publisher, string Version, DateTime? InstalledOn, double? SizeBytes, string? Uninstall, string? Icon);

    public sealed record Update(string Name, string Id, string Version, string Available);

    // ------------------------------------------------------------------ instalados

    public static List<Installed> ReadInstalled()
    {
        var list = new Dictionary<string, Installed>(StringComparer.OrdinalIgnoreCase);
        var roots = new (RegistryHive hive, RegistryView view)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64),
        };
        foreach (var (hive, view) in roots)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var k = key.OpenSubKey(sub);
                        if (k == null) continue;
                        var name = (k.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrEmpty(name)) continue;
                        // componentes de sistema, atualizações e partes de outro programa não aparecem no Windows
                        if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (k.GetValue("ParentKeyName") != null || k.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                        var version = (k.GetValue("DisplayVersion") as string ?? "").Trim();
                        string id = name + "|" + version;
                        if (list.ContainsKey(id)) continue;
                        double? size = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024.0 : null;
                        DateTime? date = null;
                        if (k.GetValue("InstallDate") is string d && d.Length == 8 && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt)) date = dt;
                        var uninstall = (k.GetValue("UninstallString") as string)?.Trim();
                        var icon = (k.GetValue("DisplayIcon") as string)?.Trim().Trim('"');
                        list[id] = new Installed(name, (k.GetValue("Publisher") as string ?? "").Trim(), version, date, size, string.IsNullOrEmpty(uninstall) ? null : uninstall, icon);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Paths.LogError("Programs.Read", ex); }
        }
        return list.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Abre o desinstalador do próprio programa (o mesmo que o Windows chama).</summary>
    public static bool StartUninstall(Installed p)
    {
        if (p.Uninstall is not string cmd) return false;
        try
        {
            string exe, args;
            if (cmd.StartsWith('"'))
            {
                int end = cmd.IndexOf('"', 1);
                exe = cmd[1..end];
                args = cmd[(end + 1)..].Trim();
            }
            else
            {
                // caminho sem aspas pode ter espaços: procura onde termina o .exe
                int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                exe = i > 0 ? cmd[..(i + 4)] : cmd;
                args = i > 0 ? cmd[(i + 4)..].Trim() : "";
            }
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { Paths.LogError("Programs.Uninstall", ex); return false; }
    }

    // ------------------------------------------------------------------ winget

    public static string? WingetPath()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        return File.Exists(p) ? p : null;
    }

    static async Task<(int code, string output)> RunWinget(string args, CancellationToken ct, Action<string>? onLine = null)
    {
        var exe = WingetPath() ?? throw new InvalidOperationException(Loc.T("O winget não está instalado. Instale o \"Instalador de Aplicativo\" pela Microsoft Store.", "winget isn't installed. Install \"App Installer\" from the Microsoft Store."));
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        var read = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                // o winget desenha barras de progresso com \r: fica só com o último trecho
                var last = line.Split('\r').LastOrDefault(s => s.Trim().Length > 0)?.Trim() ?? "";
                sb.AppendLine(last);
                if (last.Length > 0) onLine?.Invoke(last);
            }
        });
        var err = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        await read;
        sb.Append(await err);
        return (p.ExitCode, sb.ToString());
    }

    /// <summary>Programas com versão nova disponível (o mesmo que "winget upgrade").</summary>
    public static async Task<List<Update>> CheckUpdatesAsync(CancellationToken ct = default)
    {
        var (_, output) = await RunWinget("upgrade --include-unknown --accept-source-agreements --disable-interactivity", ct);
        return ParseTable(output);
    }

    /// <summary>
    /// Lê a tabela do winget pela posição das colunas no cabeçalho (os títulos mudam com o idioma do Windows).
    /// Colunas: nome, ID, versão, disponível, origem.
    /// </summary>
    static List<Update> ParseTable(string output)
    {
        var result = new List<Update>();
        var lines = output.Replace("\r", "").Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("-----")) continue;
            var header = lines[i - 1];
            var starts = new List<int>();
            for (int c = 0; c < header.Length; c++)
                if (header[c] != ' ' && (c == 0 || header[c - 1] == ' ')) starts.Add(c);
            if (starts.Count < 4) continue;
            for (int j = i + 1; j < lines.Length; j++)
            {
                var row = lines[j];
                if (row.Trim().Length == 0 || row.Length < starts[3]) break; // fim da tabela ("28 atualizações disponíveis.")
                string Col(int k) => k + 1 < starts.Count ? Cut(row, starts[k], starts[k + 1]) : Cut(row, starts[k], row.Length);
                var u = new Update(Col(0), Col(1), Col(2), Col(3));
                if (u.Id.Length > 0 && !u.Id.Contains(' ')) result.Add(u);
            }
        }
        return result.GroupBy(u => u.Id).Select(g => g.First()).ToList();
    }

    static string Cut(string s, int from, int to) => from >= s.Length ? "" : s[from..Math.Min(to, s.Length)].Trim();

    /// <summary>Atualiza um programa em modo silencioso. Retorna (ok, mensagem).</summary>
    public static async Task<(bool ok, string message)> UpgradeAsync(Update u, Action<string>? progress, CancellationToken ct = default)
    {
        try
        {
            var (code, output) = await RunWinget($"upgrade --id \"{u.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", ct, progress);
            if (code == 0) return (true, Loc.T($"Atualizado para {u.Available}", $"Updated to {u.Available}"));
            return (false, Explain(code, output));
        }
        catch (OperationCanceledException) { return (false, Loc.T("Cancelado", "Canceled")); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Códigos comuns do winget traduzidos para algo útil.</summary>
    static string Explain(int code, string output)
    {
        uint c = unchecked((uint)code);
        return c switch
        {
            0x8A15002B => Loc.T("Nenhuma atualização aplicável (talvez já esteja na versão mais nova).", "No applicable update (it may already be up to date)."),
            0x8A150101 => Loc.T("O programa está aberto. Feche-o e tente de novo.", "The program is running. Close it and try again."),
            0x8A150102 => Loc.T("Outro instalador está rodando. Espere terminar.", "Another installation is in progress. Wait for it to finish."),
            0x8A150104 => Loc.T("Sem espaço em disco.", "Not enough disk space."),
            0x8A150106 => Loc.T("Sem internet para baixar o instalador.", "No internet connection to download the installer."),
            _ => LastMeaningfulLine(output) ?? Loc.T($"Falhou (código 0x{c:X8})", $"Failed (code 0x{c:X8})"),
        };
    }

    static string? LastMeaningfulLine(string output) =>
        output.Replace("\r", "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 8 && !l.All(ch => ch is '-' or '█' or '▒' or ' ' or '\\' or '|' or '/'));
}
