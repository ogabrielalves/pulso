using Pulso.Core;
using Xunit;

namespace Pulso.Tests;

public class LocTests : IDisposable
{
    public void Dispose() => Loc.Init("pt-BR");

    [Fact]
    public void T_DefaultsToPortuguese()
    {
        Loc.Init(null);
        Assert.Equal("Olá", Loc.T("Olá", "Hello"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("EN-us")]
    public void T_ReturnsEnglish_WhenLanguageIsEnglish(string language)
    {
        Loc.Init(language);
        Assert.Equal("Hello", Loc.T("Olá", "Hello"));
    }

    [Fact]
    public void Init_UnknownLanguage_FallsBackToPortuguese()
    {
        Loc.Init("xx-XX");
        Assert.Equal("Olá", Loc.T("Olá", "Hello"));
    }
}
