using Avalonia;
using Pulso.Core;
using Pulso.Platform;

namespace Pulso;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        OS.Current = PlatformFactory.Create();
        App.Args = args;

        int probe = Array.IndexOf(args, "--probe-gpu");
        if (probe >= 0 && probe + 1 < args.Length && OperatingSystem.IsWindows())
        {
            try { Info.GpuApis.RunProbe(args[probe + 1]); } catch { }
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Paths.LogError("Main", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
