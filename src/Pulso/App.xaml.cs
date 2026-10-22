using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Pulso.Core;
using Pulso.Features;
using Pulso.Hardware;
using Pulso.Info;

namespace Pulso;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    Mutex? _mutex;
    EventWaitHandle? _showSignal;
    public TrayIcon? Tray { get; private set; }
    public MainWindow? MainWin { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        int probe = Array.IndexOf(args, "--probe-gpu");
        if (probe >= 0 && probe + 1 < args.Length)
        {
            try { GpuApis.RunProbe(args[probe + 1]); } catch { }
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) => { Paths.LogError("UI", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Paths.LogError("Domain", (Exception)ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Paths.LogError("Task", ex.Exception); ex.SetObserved(); };

        Settings.Load();
        Loc.Init(Settings.Current.Language);
        bool minimized = args.Contains("--minimized");

        // Reabre como administrador (sensores de temperatura, SMART e FPS exigem).
        if (!Native.IsAdmin && Settings.Current.RequestAdmin && !args.Contains("--no-elevate"))
        {
            try
            {
                var psi = new ProcessStartInfo(Paths.ExePath, string.Join(" ", args.Append("--no-elevate").Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };
                Process.Start(psi);
                Shutdown();
                return;
            }
            catch (Win32Exception) { /* usuário recusou o UAC: segue com recursos limitados */ }
        }

        _mutex = new Mutex(true, "Pulso.SingleInstance.v1", out bool created);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Pulso.ShowWindow.v1");
        if (!created)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        var signal = _showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(() => ShowMain());
        }) { IsBackground = true, Name = "Pulso.ShowSignal" }.Start();

        ApplyTheme(Settings.Current.Theme);
        base.OnStartup(e);

        var hub = SensorHub.Instance;
        hub.Minimized = minimized;
        hub.Start();
        AlertService.Start();
        FpsMonitor.Instance.Start();
        OverlayController.Instance.Start();
        Tray = new TrayIcon();

        if (!minimized) ShowMain();
        int page = Array.IndexOf(args, "--page");
        if (page >= 0 && page + 1 < args.Length) MainWin?.Navigate(args[page + 1]);
        else Native.TrimWorkingSet();
    }

    public void ShowMain()
    {
        if (MainWin == null)
        {
            MainWin = new MainWindow();
            MainWin.Closed += (_, _) =>
            {
                MainWin = null;
                SensorHub.Instance.Minimized = true;
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Native.TrimWorkingSet);
            };
        }
        SensorHub.Instance.Minimized = false;
        SensorHub.Instance.Poke();
        MainWin.Show();
        if (MainWin.WindowState == WindowState.Minimized) MainWin.WindowState = WindowState.Normal;
        MainWin.Activate();
    }

    /// <summary>Fecha a janela e continua rodando na bandeja (libera a memória da interface).</summary>
    public void HideToTray()
    {
        MainWin?.Close();
    }

    public void Exit()
    {
        try { OverlayController.Instance.Stop(); } catch { }
        try { FpsMonitor.Instance.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        try { SensorHub.Instance.Dispose(); } catch { }
        Shutdown();
    }

    public static void ApplyTheme(string theme)
    {
        var dicts = Current.Resources.MergedDictionaries;
        var src = new Uri(theme == "light" ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        dicts[0] = new ResourceDictionary { Source = src };
        foreach (Window w in Current.Windows)
            if (w is MainWindow mw) mw.ApplyTitleBar();
    }

    public static bool IsDark => Settings.Current.Theme != "light";

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
