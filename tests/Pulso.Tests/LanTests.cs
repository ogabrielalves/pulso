using System.Net;
using Pulso.Features;
using Xunit;

namespace Pulso.Tests;

public class LanTests
{
    [Fact]
    public void LanTypes_HaveUniqueIds_AndFallbackIsLast()
    {
        var ids = LanTypes.All.Select(t => t.id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal("unknown", ids[^1]);
    }

    [Fact]
    public void Icon_UnknownId_FallsBackToLastIcon() =>
        Assert.Equal(LanTypes.All[^1].icon, LanTypes.Icon("does-not-exist"));

    [Theory]
    [InlineData("iPhone de Maria", null, "iphone")]
    [InlineData(null, "Sony PlayStation", "console")]
    [InlineData(null, "Hikvision Digital", "camera")]
    [InlineData(null, "TP-Link Technologies", "router")]
    [InlineData("HP LaserJet 400", null, "printer")]
    public void Guess_DetectsTypeFromNameOrVendor(string? hostname, string? vendor, string expected) =>
        Assert.Equal(expected, LanTypes.Guess(new LanDevice { Mac = "00:11:22:33:44:55", Hostname = hostname, Vendor = vendor }));

    [Fact]
    public void Guess_Gateway_IsRouter() =>
        Assert.Equal("router", LanTypes.Guess(new LanDevice { Mac = "00:11:22:33:44:55", IsGateway = true }));

    [Fact]
    public void Guess_RandomMacWithoutHints_IsPhone() =>
        Assert.Equal("phone", LanTypes.Guess(new LanDevice { Mac = "DA:11:22:33:44:55" }));

    [Fact]
    public void Guess_NoHints_IsUnknown() =>
        Assert.Equal("unknown", LanTypes.Guess(new LanDevice { Mac = "00:11:22:33:44:55" }));

    [Fact]
    public void EffectiveType_PrefersUserChoice()
    {
        var d = new LanDevice { Mac = "00:11:22:33:44:55", Hostname = "iphone", Type = "tv" };
        Assert.Equal("tv", d.EffectiveType);
    }

    [Fact]
    public void OnlineWithin_CountsOnlyTheWindow()
    {
        var now = DateTime.Now;
        var d = new LanDevice
        {
            Mac = "00:11:22:33:44:55",
            Sessions =
            {
                new LanSession { Start = now.AddHours(-5), End = now.AddHours(-4) },
                new LanSession { Start = now.AddMinutes(-30), End = now.AddMinutes(-10) },
            },
        };
        var online = d.OnlineWithin(TimeSpan.FromHours(1));
        Assert.InRange(online.TotalMinutes, 19.9, 20.1);
    }

    [Fact]
    public void LanNet_Cidr_UsesNetworkAddress()
    {
        var ip = IPAddress.Parse("192.168.1.57");
        var net = new LanNet("Wi-Fi", "wifi", ip, "00:11:22:33:44:55", IPAddress.Parse("192.168.1.1"), 24, Array.Empty<IPAddress>());
        Assert.Equal("192.168.1.0/24", net.Cidr);
    }
}
