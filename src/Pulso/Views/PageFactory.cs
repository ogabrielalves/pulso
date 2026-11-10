namespace Pulso.Views;

public static class PageFactory
{
    public static PageBase Create(string page, MainWindow main) => page switch
    {
        "painel" => new DashboardPage(main),
        "sistema" => new SystemPage(),
        "monitor" => new MonitorPage(),
        "bench" => new BenchmarkPage(),
        "stress" => new StressPage(),
        "gargalo" => new BottleneckPage(),
        "internet" => new InternetPage(),
        "processos" => new ProcessesPage(),
        "bateria" => new BatteryPage(),
        "rede" => new LanPage(),
        "overlay" => new OverlayPage(),
        "drivers" when OperatingSystem.IsWindows() => new DriversPage(),
        "inicializacao" when OperatingSystem.IsWindows() => new StartupPage(),
        "reparo" when OperatingSystem.IsWindows() => new MaintenancePage(),
        "programas" when OperatingSystem.IsWindows() => new ProgramsPage(),
        "reparo" => new LinuxMaintenancePage(LinuxMaintenancePage.Mode.Maintenance),
        "programas" => new LinuxMaintenancePage(LinuxMaintenancePage.Mode.Updates),
        "travamentos" when OperatingSystem.IsWindows() => new CrashesPage(),
        _ => new SettingsPage(),
    };
}
