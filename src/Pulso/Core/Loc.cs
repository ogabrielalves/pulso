using System.Windows.Markup;

namespace Pulso.Core;

/// <summary>
/// Localização simples: cada texto é escrito nos dois idiomas no próprio ponto de uso.
/// Português do Brasil é o padrão; a troca de idioma vale após reiniciar o app.
/// </summary>
public static class Loc
{
    public static bool En { get; private set; }

    public static void Init(string? language) => En = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase);

    public static string T(string pt, string en) => En ? en : pt;
}

/// <summary>Uso no XAML: Text="{l:T Painel, En=Dashboard}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string pt) => Pt = pt;

    public string Pt { get; set; } = "";
    public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Loc.T(Pt, string.IsNullOrEmpty(En) ? Pt : En);
}
