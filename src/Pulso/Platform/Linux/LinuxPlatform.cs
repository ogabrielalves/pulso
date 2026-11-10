using System.Globalization;
using System.IO;

namespace Pulso.Platform.Linux;

public sealed class LinuxPlatform : IPlatform
{
    public string Name => "Linux";
    public bool IsWindows => false;
    public bool IsLinux => true;
    public bool IsElevated => Environment.UserName == "root" || geteuid() == 0;
    public ISensorProvider CreateSensorProvider() => new LinuxSensorProvider();

    [System.Runtime.InteropServices.DllImport("libc")]
    static extern uint geteuid();

    public static string? ReadOsRelease(string key)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
                if (line.StartsWith(key + "=", StringComparison.Ordinal))
                    return line[(key.Length + 1)..].Trim('"');
        }
        catch { }
        return null;
    }

    public static string KernelVersion() => LinuxSensorProvider.Read("/proc/sys/kernel/osrelease") ?? "";

    public long AvailablePhysicalBytes()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
                if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
        }
        catch { }
        return 0;
    }
}
