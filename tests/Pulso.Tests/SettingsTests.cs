using Pulso.Core;
using Xunit;

namespace Pulso.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        var s = new AppSettings();
        Assert.Equal("pt-BR", s.Language);
        Assert.Equal(1000, s.IntervalMs);
        Assert.True(s.AlertsEnabled);
        Assert.Contains(s.Alerts, a => a.MetricId == "cpu.temp" && a.Enabled);
        Assert.Contains("fps", s.Overlay.Items);
    }

    [Fact]
    public void StressLimits_AreBelowCriticalTemperatures()
    {
        var s = new AppSettings();
        Assert.InRange(s.StressCpuLimit, 60, 100);
        Assert.InRange(s.StressGpuLimit, 60, 100);
    }
}
