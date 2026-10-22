using System.IO;

namespace Pulso.Core;

public static class Paths
{
    public static string AppData { get; } = Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pulso"));
    public static string History => Ensure(Path.Combine(AppData, "history"));
    public static string SettingsFile => Path.Combine(AppData, "settings.json");
    public static string ErrorLog => Path.Combine(AppData, "erros.log");

    public static string Documents => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pulso"));
    public static string Logs => Ensure(Path.Combine(Documents, "Logs"));
    public static string Reports => Ensure(Path.Combine(Documents, "Relatorios"));

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Pulso.exe");

    static string Ensure(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    public static void LogError(string context, Exception ex)
    {
        try
        {
            var fi = new FileInfo(ErrorLog);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}\r\n\r\n");
        }
        catch { }
    }
}
