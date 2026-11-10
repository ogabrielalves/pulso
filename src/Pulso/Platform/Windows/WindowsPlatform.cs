using Pulso.Core;

namespace Pulso.Platform.Windows;

public sealed class WindowsPlatform : IPlatform
{
    public string Name => "Windows";
    public bool IsWindows => true;
    public bool IsLinux => false;
    public bool IsElevated => Native.IsAdmin;
    public ISensorProvider CreateSensorProvider() => new WindowsSensorProvider();

    public long AvailablePhysicalBytes()
    {
        var pi = new Native.PERFORMANCE_INFORMATION { cb = System.Runtime.InteropServices.Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>() };
        return Native.GetPerformanceInfo(ref pi, pi.cb) ? (long)(pi.PhysicalAvailable * pi.PageSize) : 0;
    }
}
