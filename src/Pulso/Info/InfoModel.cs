using System.Text;
using Pulso.Core;

namespace Pulso.Info;

public enum Health { Unknown, Good, Warning, Critical }

public sealed class InfoItem
{
    public string Key { get; init; } = "";
    public string Value { get; set; } = "";
    public string? Tip { get; init; }
    public Health Status { get; init; }
    public bool HasTip => !string.IsNullOrEmpty(Tip);
}

public sealed class InfoTable
{
    public string Title { get; init; } = "";
    public string[] Columns { get; init; } = Array.Empty<string>();
    public List<string[]> Rows { get; } = new();
    /// <summary>Explicação por linha (tooltip), opcional.</summary>
    public List<string?> RowTips { get; } = new();
    public List<Health> RowStatus { get; } = new();

    public void Add(string? tip, Health status, params string[] cells)
    {
        Rows.Add(cells);
        RowTips.Add(tip);
        RowStatus.Add(status);
    }
}

public sealed class InfoGroup
{
    public string Title { get; init; } = "";
    public List<InfoItem> Items { get; } = new();
    public List<InfoTable> Tables { get; } = new();
    public Health Status { get; set; }
    /// <summary>Avisos de destaque (ex.: memória abaixo da velocidade nominal).</summary>
    public List<(Health level, string text)> Notes { get; } = new();

    public InfoGroup Add(string key, string? value, string? tip = null, Health status = Health.Unknown)
    {
        Items.Add(new InfoItem { Key = key, Value = string.IsNullOrWhiteSpace(value) ? Fmt.NA : value!, Tip = tip, Status = status });
        return this;
    }
}

public sealed class InfoPage
{
    public string Title { get; init; } = "";
    public List<InfoGroup> Groups { get; } = new();

    public InfoGroup Group(string title)
    {
        var g = new InfoGroup { Title = title };
        Groups.Add(g);
        return g;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title.ToUpperInvariant());
        sb.AppendLine(new string('=', Math.Max(8, Title.Length)));
        foreach (var g in Groups)
        {
            sb.AppendLine();
            sb.AppendLine("[" + g.Title + "]");
            foreach (var (lvl, text) in g.Notes) sb.AppendLine("  ! " + text);
            int w = g.Items.Count > 0 ? g.Items.Max(i => i.Key.Length) + 2 : 0;
            foreach (var i in g.Items) sb.AppendLine("  " + (i.Key + ":").PadRight(w) + " " + i.Value);
            foreach (var t in g.Tables)
            {
                if (!string.IsNullOrEmpty(t.Title)) sb.AppendLine("  " + t.Title);
                var widths = new int[t.Columns.Length];
                for (int c = 0; c < t.Columns.Length; c++)
                    widths[c] = Math.Max(t.Columns[c].Length, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => c < r.Length ? r[c].Length : 0));
                sb.AppendLine("  " + string.Join(" | ", t.Columns.Select((c, k) => c.PadRight(widths[k]))));
                sb.AppendLine("  " + string.Join("-+-", widths.Select(x => new string('-', x))));
                foreach (var r in t.Rows)
                    sb.AppendLine("  " + string.Join(" | ", r.Select((c, k) => c.PadRight(k < widths.Length ? widths[k] : 0))));
            }
        }
        return sb.ToString();
    }
}

public static class HealthText
{
    public static string Of(Health h) => h switch
    {
        Health.Good => Loc.T("Bom", "Good"),
        Health.Warning => Loc.T("Atenção", "Warning"),
        Health.Critical => Loc.T("Crítico", "Critical"),
        _ => Loc.T("Desconhecido", "Unknown"),
    };
}
