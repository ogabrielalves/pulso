using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Hardware;
using Pulso.Info;
using Pulso.Platform;

namespace Pulso;

public partial class App : Application
{
    public static string[] Args { get; set; } = Array.Empty<string>();
    public static new App Current => (App)Application.Current!;

    FileStream? _lock;
    CancellationTokenSource? _pipeCts;
    TrayIcon? _tray;
    public MainWindow? MainWin { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!Startup(desktop)) { desktop.Shutdown(); return; }
        }
        base.OnFrameworkInitializationCompleted();
    }

    bool Startup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = Args;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Paths.LogError("Domain", (Exception)ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Paths.LogError("Task", ex.Exception); ex.SetObserved(); };

        Settings.Load();
        Loc.Init(Settings.Current.Language);
        bool minimized = args.Contains("--minimized");

        // Windows: reabre como administrador (sensores de temperatura, SMART e FPS exigem).
        if (OperatingSystem.IsWindows() && !OS.Current.IsElevated && Settings.Current.RequestAdmin && !args.Contains("--no-elevate"))
        {
            try
            {
                var psi = new ProcessStartInfo(Paths.ExePath, string.Join(" ", args.Append("--no-elevate").Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };
                Process.Start(psi);
                return false;
            }
            catch (System.ComponentModel.Win32Exception) { /* recusou o UAC: segue com recursos limitados */ }
        }

        // Reaberto como root pelo próprio Pulso: encerra a instância antiga (sem root) e espera ela liberar o bloqueio.
        int rep = Array.IndexOf(args, "--replace");
        if (OperatingSystem.IsLinux() && rep >= 0 && rep + 1 < args.Length && int.TryParse(args[rep + 1], out int oldPid))
        {
            try
            {
                kill(oldPid, 15);
                using var old = Process.GetProcessById(oldPid);
                old.WaitForExit(8000);
            }
            catch { /* já tinha fechado */ }
        }

        if (!AcquireSingleInstance()) return false;

        ApplyTheme(Settings.Current.Theme);
        Notifier.ExitRequested += () => Dispatcher.UIThread.Post(Exit);

        var hub = SensorHub.Instance;
        hub.Minimized = minimized;
        hub.Start();
        AlertService.Start();
        if (OperatingSystem.IsWindows()) StartWindowsOnlyServices();
        else OverlayController.Instance.Start();
        SetupTray();
        LanScanner.Start();
        Task.Run(Battery.RecordDaily);

        if (!minimized) ShowMain();
        int page = Array.IndexOf(args, "--page");
        if (page >= 0 && page + 1 < args.Length) MainWin?.Navigate(args[page + 1]);

        // Ferramenta de desenvolvimento: --shot arquivo.png [--delay s] desenha só a janela do Pulso em PNG e sai.
        int shot = Array.IndexOf(args, "--shot");
        if (shot >= 0 && shot + 1 < args.Length)
        {
            int di = Array.IndexOf(args, "--delay");
            int delay = di >= 0 && di + 1 < args.Length && int.TryParse(args[di + 1], out var dsec) ? dsec : 5;
            string file = args[shot + 1];
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    var w = MainWin!;
                    Dispatcher.UIThread.RunJobs();
                    w.UpdateLayout();
                    if (Args.Contains("--layout-debug") && w.Content is Avalonia.Controls.Grid gr)
                        foreach (var ch in gr.Children.OfType<Avalonia.Controls.ContentControl>())
                            if (ch.Content is Avalonia.Controls.UserControl uc && uc.Content is Avalonia.Controls.ScrollViewer sv)
                            {
                                Paths.LogError("layout", new Exception($"viewport={sv.Viewport} extent={sv.Extent} offset={sv.Offset} svBounds={sv.Bounds} content={(sv.Content as Avalonia.Controls.Control)?.Bounds} pad={sv.Padding}"));
                                if (sv.Content is Avalonia.Controls.Panel pn)
                                    foreach (var c in pn.Children)
                                    {
                                        string d = $"  child {c.GetType().Name} bounds={c.Bounds} desired={c.DesiredSize}";
                                        if (c is Avalonia.Controls.Border bd && bd.Child is Avalonia.Controls.Panel p2)
                                            foreach (var c2 in p2.Children) d += $"\n     inner {c2.GetType().Name} bounds={c2.Bounds} desired={c2.DesiredSize}";
                                        Paths.LogError("layout", new Exception(d));
                                    }
                            }
                    var size = new Avalonia.PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height);
                    var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
                    rtb.Render(w);
                    rtb.Save(file);
                }
                catch (Exception ex) { Paths.LogError("shot", ex); }
                Exit();
            };
            t.Start();
        }
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void StartWindowsOnlyServices()
    {
        StressEngine_Hook();
        FpsMonitor.Instance.Start();
        OverlayController.Instance.Start();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void StopWindowsOnlyServices()
    {
        try { OverlayController.Instance.Stop(); } catch { }
        try { FpsMonitor.Instance.Dispose(); } catch { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void StressEngine_Hook()
    {
        Bench.StressEngine.GpuStress = async (tick, ct) =>
        {
            var r = await Bench.GpuRenderer.StressAsync(tick, ct);
            return new Bench.StressEngine.GpuStressResult(r.Errors);
        };
    }

    // ------------------------------------------------------------- instância única

    bool AcquireSingleInstance()
    {
        try
        {
            string lockFile = Path.Combine(Paths.AppData, "instance.lock");
            _lock = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Já existe outra: pede para ela mostrar a janela e sai.
            try
            {
                using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                c.Connect(1500);
                c.WriteByte(1);
            }
            catch { }
            return false;
        }

        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    server.ReadByte();
                    Dispatcher.UIThread.Post(ShowMain);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(500); }
            }
        });
        return true;
    }

    static string PipeName => "Pulso.ShowWindow.v1." + Environment.UserName;

    // ------------------------------------------------------------- janela / bandeja

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int sig);

    /// <summary>No Linux, sem root a bandeja funciona; como root ela pode não aparecer (barramento de sessão é do usuário).</summary>
    public static bool TrayUsable => !(OperatingSystem.IsLinux() && OS.Current.IsElevated);

    /// <summary>Linux: reabre o Pulso como root via sudo sem senha ou, se precisar de senha, pkexec. A instância atual só fecha quando o root sobe.</summary>
    public bool RelaunchAsRoot()
    {
        if (!OperatingSystem.IsLinux() || OS.Current.IsElevated) return false;
        try
        {
            // sudo sem senha (NOPASSWD) abre direto; senão usa o pkexec, que mostra a janela de senha do sistema.
            bool sudoOk = false;
            try
            {
                using var chk = Process.Start(new ProcessStartInfo("sudo", new[] { "-n", "true" }) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
                sudoOk = chk != null && chk.WaitForExit(3000) && chk.ExitCode == 0;
            }
            catch { }
            var psi = new ProcessStartInfo(sudoOk ? "sudo" : "pkexec") { UseShellExecute = false };
            if (sudoOk) psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("env");
            foreach (var v in new[] { "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS" })
                if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } val) psi.ArgumentList.Add($"{v}={val}");
            psi.ArgumentList.Add("HOME=" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            psi.ArgumentList.Add(Paths.ExePath);
            psi.ArgumentList.Add("--no-elevate");
            psi.ArgumentList.Add("--replace");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(psi);
            return true;
        }
        catch (Exception ex) { Paths.LogError("RelaunchAsRoot", ex); return false; }
    }

    /// <summary>Arquivos criados pelo root em ~/.config/Pulso voltam a pertencer ao usuário (senão a versão sem root não consegue gravar).</summary>
    static void RestoreOwnership()
    {
        if (!OperatingSystem.IsLinux() || !OS.Current.IsElevated) return;
        if ((Environment.GetEnvironmentVariable("PKEXEC_UID") ?? Environment.GetEnvironmentVariable("SUDO_UID")) is not { Length: > 0 } uid) return;
        try { Process.Start(new ProcessStartInfo("chown", new[] { "-R", uid, Paths.AppData }) { UseShellExecute = false })?.WaitForExit(3000); }
        catch { }
    }

    void SetupTray()
    {
        try
        {
            var menu = new NativeMenu();
            var open = new NativeMenuItem(Loc.T("Abrir o Pulso", "Open Pulso"));
            open.Click += (_, _) => ShowMain();
            var quit = new NativeMenuItem(Loc.T("Sair", "Quit"));
            quit.Click += (_, _) => Exit();
            menu.Add(open);
            if (OperatingSystem.IsLinux() && !OS.Current.IsElevated)
            {
                var root = new NativeMenuItem(Loc.T("Reabrir como root", "Reopen as root"));
                root.Click += (_, _) => RelaunchAsRoot();
                menu.Add(root);
            }
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(quit);
            _tray = new TrayIcon
            {
                ToolTipText = "Pulso",
                Menu = menu,
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Pulso/Assets/logo.png"))),
                IsVisible = true,
            };
            _tray.Clicked += (_, _) => ShowMain();
            Notifier.Notified += (title, text, warning) => Dispatcher.UIThread.Post(() => Toast.Show(title, text, warning));
        }
        catch (Exception ex) { Paths.LogError("Tray", ex); }
    }

    public void ShowMain()
    {
        if (MainWin == null)
        {
            MainWin = new MainWindow();
            if (Args.Contains("--shot"))
            {
                // modo captura: janela fora da tela, sem foco e sem ícone na barra de tarefas
                MainWin.WindowStartupLocation = WindowStartupLocation.Manual;
                MainWin.Position = new PixelPoint(-20000, -20000);
                MainWin.ShowInTaskbar = false;
                MainWin.ShowActivated = false;
            }
            MainWin.Closed += (_, _) =>
            {
                MainWin = null;
                SensorHub.Instance.Minimized = true;
            };
        }
        SensorHub.Instance.Minimized = false;
        SensorHub.Instance.Poke();
        MainWin.Show();
        if (Args.Contains("--shot")) return;
        if (MainWin.WindowState == WindowState.Minimized) MainWin.WindowState = WindowState.Normal;
        MainWin.Activate();
    }

    /// <summary>Fecha a janela e continua rodando na bandeja (libera a memória da interface).</summary>
    public void HideToTray() => MainWin?.Close();

    public void Exit()
    {
        if (OperatingSystem.IsWindows()) StopWindowsOnlyServices();
        else { try { OverlayController.Instance.Stop(); } catch { } }
        try { SensorHub.Instance.Dispose(); } catch { }
        try { _pipeCts?.Cancel(); } catch { }
        RestoreOwnership();
        try { _tray?.Dispose(); } catch { }
        try { _lock?.Dispose(); } catch { }
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public static void ApplyTheme(string theme)
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = theme == "light" ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public static bool IsDark => Settings.Current.Theme != "light";
}
