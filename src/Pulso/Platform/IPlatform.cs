namespace Pulso.Platform;

/// <summary>Leitor de sensores específico do sistema operacional. Roda sempre na thread do <see cref="Hardware.SensorHub"/>.</summary>
public interface ISensorProvider : IDisposable
{
    /// <summary>Abre drivers/arquivos. Pode lançar exceção: o hub segue com o que conseguir ler.</summary>
    void Open();

    /// <summary>Lê todos os sensores e devolve uma fotografia. <paramref name="tick"/> cresce a cada leitura.</summary>
    Hardware.Snapshot Read(long tick);

    bool CpuIsIntel { get; }
    bool PawnIoInstalled { get; }
}

/// <summary>Tudo o que muda de um sistema operacional para outro.</summary>
public interface IPlatform
{
    string Name { get; }
    bool IsWindows { get; }
    bool IsLinux { get; }
    /// <summary>Administrador (Windows) ou root (Linux).</summary>
    bool IsElevated { get; }
    ISensorProvider CreateSensorProvider();
    /// <summary>Memória física disponível agora, em bytes.</summary>
    long AvailablePhysicalBytes();
}

public static class OS
{
    static IPlatform? _current;

    public static IPlatform Current
    {
        get => _current ?? throw new InvalidOperationException("Platform not initialized");
        set => _current = value;
    }

    public static bool Ready => _current != null;
    public static bool IsWindows => System.OperatingSystem.IsWindows();
    public static bool IsLinux => System.OperatingSystem.IsLinux();
}

public static class PlatformFactory
{
    public static IPlatform Create() => OperatingSystem.IsWindows() ? new Windows.WindowsPlatform() : new Linux.LinuxPlatform();
}
