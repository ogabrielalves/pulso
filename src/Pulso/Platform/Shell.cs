using System.Diagnostics;

namespace Pulso.Platform;

/// <summary>Abrir arquivos, pastas e links com o programa padrão do sistema.</summary>
public static class Shell
{
    public static void Open(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", target);
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { target }, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
        }
        catch { }
    }
}
