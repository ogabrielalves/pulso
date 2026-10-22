using System.Diagnostics;
using Microsoft.Win32;
using Pulso.Core;

namespace Pulso.Features;

/// <summary>
/// Iniciar com o Windows. Como administrador usa o Agendador de Tarefas (abre já elevado, sem pedir UAC
/// a cada login); sem administrador usa a chave Run do usuário (sensores limitados).
/// </summary>
public static class Startup
{
    const string TaskName = "Pulso";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        if (RunSchtasks($"/Query /TN \"{TaskName}\"") == 0) return true;
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(TaskName) != null;
    }

    public static bool Set(bool on)
    {
        string exe = Paths.ExePath;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            k?.DeleteValue(TaskName, false);
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            if (!on) return true;

            if (Native.IsAdmin)
            {
                string tr = $"\\\"{exe}\\\" --minimized --no-elevate";
                if (RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"{tr}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:15 /F") == 0) return true;
            }
            k?.SetValue(TaskName, $"\"{exe}\" --minimized");
            return true;
        }
        catch (Exception ex)
        {
            Paths.LogError("Startup", ex);
            return false;
        }
    }

    static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(8000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}

/// <summary>Instala o driver PawnIO (necessário para temperatura/consumo da CPU e sensores da placa-mãe).</summary>
public static class PawnIo
{
    public static async Task<(bool ok, string message)> InstallAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("winget", "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "winget");
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            bool ok = p.ExitCode == 0 || output.Contains("already installed", StringComparison.OrdinalIgnoreCase) || output.Contains("instalado", StringComparison.OrdinalIgnoreCase);
            return (ok, ok ? "" : output.Trim().Split('\n').LastOrDefault() ?? $"exit {p.ExitCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static void OpenWebsite()
    {
        try { Process.Start(new ProcessStartInfo("https://pawnio.eu/") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Reabre o Pulso (o driver só é carregado na inicialização).</summary>
    public static void Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Paths.ExePath, Native.IsAdmin ? "--no-elevate" : "") { UseShellExecute = true });
            App.Current.Exit();
        }
        catch { }
    }
}
