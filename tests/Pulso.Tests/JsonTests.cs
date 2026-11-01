using System.IO;
using Pulso.Core;
using Xunit;

namespace Pulso.Tests;

public class JsonTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pulso-tests-" + Guid.NewGuid().ToString("N"));

    public JsonTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Load_ReturnsDefault_WhenFileMissing()
    {
        var s = Json.Load<AppSettings>(Path.Combine(_dir, "missing.json"));
        Assert.Equal("dark", s.Theme);
    }

    [Fact]
    public void Load_ReturnsDefault_WhenFileCorrupt()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal(1000, Json.Load<AppSettings>(path).IntervalMs);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(_dir, "settings.json");
        var original = new AppSettings { Theme = "light", Fahrenheit = true, Overlay = { Corner = OverlayCorner.BottomLeft } };
        Json.Save(path, original);
        var loaded = Json.Load<AppSettings>(path);
        Assert.Equal("light", loaded.Theme);
        Assert.True(loaded.Fahrenheit);
        Assert.Equal(OverlayCorner.BottomLeft, loaded.Overlay.Corner);
    }

    [Fact]
    public void Save_WritesEnumsAsStrings_AndLeavesNoTempFile()
    {
        var path = Path.Combine(_dir, "enum.json");
        Json.Save(path, new OverlaySettings { Corner = OverlayCorner.TopLeft });
        Assert.Contains("TopLeft", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
