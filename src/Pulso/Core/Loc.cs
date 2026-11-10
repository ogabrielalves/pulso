using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pulso.Core;

public enum Lang { Pt, En, Ru }

/// <summary>
/// Simple localization: every text is written in Portuguese and English at the point of use.
/// Russian is looked up in <see cref="RuStrings"/> by the English text (or by its template, for
/// interpolated strings), falling back to English when no translation exists.
/// Brazilian Portuguese is the default; a language change takes effect after restarting the app.
/// </summary>
public static partial class Loc
{
    public static Lang Current { get; private set; } = Lang.Pt;

    public static void Init(string? language) => Current = ParseLanguage(language);

    public static Lang ParseLanguage(string? language) => language?.ToLowerInvariant() switch
    {
        "en-us" or "en" => Lang.En,
        "ru-ru" or "ru" => Lang.Ru,
        _ => Lang.Pt,
    };

    public static string Code(Lang lang) => lang switch { Lang.En => "en-US", Lang.Ru => "ru-RU", _ => "pt-BR" };

    public static string T(LocText pt, LocText en) => Current switch
    {
        Lang.En => en.Format(),
        Lang.Ru => RuStrings.TryGetValue(en.Template, out var ru) ? LocText.Apply(ru, en.Args) : en.Format(),
        _ => pt.Format(),
    };
}

/// <summary>
/// A text that is either a plain string or an interpolated string. Interpolated strings keep their
/// template (<c>"... {0:N1} ..."</c>) and arguments so the translation can be looked up by template.
/// </summary>
[InterpolatedStringHandler]
public readonly struct LocText
{
    readonly StringBuilder? _template;
    readonly List<object?>? _args;
    readonly string? _plain;

    public LocText(string plain) { _plain = plain; _template = null; _args = null; }

    public LocText(int literalLength, int formattedCount)
    {
        _plain = null;
        _template = new StringBuilder(literalLength + formattedCount * 4);
        _args = new List<object?>(formattedCount);
    }

    public static implicit operator LocText(string plain) => new(plain);

    public string Template => _plain ?? _template!.ToString();
    public object?[] Args => _args?.ToArray() ?? Array.Empty<object?>();

    public void AppendLiteral(string s) => _template!.Append(s.Replace("{", "{{").Replace("}", "}}"));

    public void AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);
    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value, 0, format);
    public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value, alignment, null);

    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        _template!.Append('{').Append(_args!.Count);
        if (alignment != 0) _template.Append(',').Append(alignment);
        if (format != null) _template.Append(':').Append(format);
        _template.Append('}');
        _args.Add(value);
    }

    public string Format() => _plain ?? Apply(_template!.ToString(), Args);

    public static string Apply(string template, object?[] args) =>
        args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
}
