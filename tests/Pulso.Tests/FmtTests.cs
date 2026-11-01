using Pulso.Core;
using Xunit;

namespace Pulso.Tests;

public class FmtTests
{
    public FmtTests() => Loc.Init("en-US");

    [Fact]
    public void Value_ReturnsDash_WhenNullOrNaN()
    {
        Assert.Equal(Fmt.Dash, Fmt.Value(null, Unit.Percent));
        Assert.Equal(Fmt.Dash, Fmt.Value(double.NaN, Unit.Percent));
    }

    [Theory]
    [InlineData(45.4, Unit.Percent, "45%")]
    [InlineData(2.5, Unit.Watt, "2.5 W")]
    [InlineData(65, Unit.Watt, "65 W")]
    [InlineData(1.25, Unit.Volt, "1.250 V")]
    [InlineData(144, Unit.Fps, "144 FPS")]
    [InlineData(3500, Unit.MHz, "3.50 GHz")]
    [InlineData(800, Unit.MHz, "800 MHz")]
    [InlineData(2048, Unit.MB, "2.0 GB")]
    [InlineData(512, Unit.MB, "512 MB")]
    public void Value_FormatsWithUnit(double value, Unit unit, string expected) =>
        Assert.Equal(expected, Fmt.Value(value, unit));

    [Fact]
    public void Value_CompactMHz_NeverUsesGHz() =>
        Assert.Equal("3,500 MHz", Fmt.Value(3500, Unit.MHz, compact: true));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    public void Bytes_UsesBase1024(double bytes, string expected) =>
        Assert.Equal(expected, Fmt.Bytes(bytes));

    [Fact]
    public void Capacity_UsesBase1000() =>
        Assert.Equal("500.0 GB", Fmt.Capacity(500_000_000_000));

    [Theory]
    [InlineData(512 * 1024, "512 KB/s")]
    [InlineData(5 * 1024 * 1024, "5.0 MB/s")]
    public void Rate_PicksUnit(double bytesPerSec, string expected) =>
        Assert.Equal(expected, Fmt.Rate(bytesPerSec));

    [Fact]
    public void Mbps_UsesMoreDecimalsForSlowLinks()
    {
        Assert.Equal("5.00 Mbps", Fmt.Mbps(5_000_000));
        Assert.Equal("100.0 Mbps", Fmt.Mbps(100_000_000));
    }

    [Theory]
    [InlineData(65, "01:05")]
    [InlineData(3725, "1h 02min")]
    public void Duration_FormatsShortSpans(int seconds, string expected) =>
        Assert.Equal(expected, Fmt.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Duration_FormatsDays() =>
        Assert.Equal("2d 3h 4m", Fmt.Duration(new TimeSpan(2, 3, 4, 0)));

    [Fact]
    public void Or_FallsBackToNotAvailable()
    {
        Assert.Equal(Fmt.NA, Fmt.Or("  "));
        Assert.Equal(Fmt.NA, Fmt.Or((string?)null));
        Assert.Equal("abc", Fmt.Or("  abc "));
    }

    [Fact]
    public void YesNo_FollowsLanguage()
    {
        Assert.Equal("Yes", Fmt.YesNo(true));
        Assert.Equal("No", Fmt.YesNo(false));
        Assert.Equal(Fmt.NA, Fmt.YesNo((bool?)null));
    }
}
