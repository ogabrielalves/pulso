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

    [Theory]
    [InlineData("ru-RU", Lang.Ru)]
    [InlineData("RU", Lang.Ru)]
    [InlineData("en-US", Lang.En)]
    [InlineData("pt-BR", Lang.Pt)]
    [InlineData(null, Lang.Pt)]
    public void ParseLanguage_MapsCodes(string? code, Lang expected) =>
        Assert.Equal(expected, Loc.ParseLanguage(code));

    [Fact]
    public void Code_RoundTripsWithParseLanguage()
    {
        foreach (var lang in Enum.GetValues<Lang>())
            Assert.Equal(lang, Loc.ParseLanguage(Loc.Code(lang)));
    }

    [Fact]
    public void T_Russian_UsesTranslationByEnglishText()
    {
        Loc.Init("ru-RU");
        Assert.Equal("Закрыть", Loc.T("Fechar", "Close"));
    }

    [Fact]
    public void T_Russian_FallsBackToEnglish_WhenMissing()
    {
        Loc.Init("ru-RU");
        Assert.Equal("Not translated yet", Loc.T("Ainda sem tradução", "Not translated yet"));
    }

    [Fact]
    public void T_Russian_TranslatesInterpolatedStrings()
    {
        Loc.Init("ru-RU");
        int cores = 8, threads = 16;
        Assert.Equal("Ядер: 8 · потоков: 16", Loc.T($"{cores} núcleos · {threads} threads", $"{cores} cores · {threads} threads"));
    }

    [Fact]
    public void T_Interpolated_FormatsNormally_InPortugueseAndEnglish()
    {
        int n = 3;
        Loc.Init("pt-BR");
        Assert.Equal("3 dias", Loc.T($"{n} dias", $"{n} days"));
        Loc.Init("en-US");
        Assert.Equal("3 days", Loc.T($"{n} dias", $"{n} days"));
    }
}
