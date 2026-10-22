using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Pulso.Info;

namespace Pulso.Controls;

/// <summary>Painel que empilha filhos com espaçamento fixo (equivale ao "gap" do CSS).</summary>
public sealed class Stack : Panel
{
    public double Spacing { get; set; } = 18;
    public Orientation Orientation { get; set; } = Orientation.Vertical;

    public Stack() { }
    public Stack(double spacing, params UIElement?[] children)
    {
        Spacing = spacing;
        foreach (var c in children) if (c != null) Children.Add(c);
    }

    protected override Size MeasureOverride(Size available)
    {
        double main = 0, cross = 0;
        int visible = 0;
        bool v = Orientation == Orientation.Vertical;
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            c.Measure(v ? new Size(available.Width, double.PositiveInfinity) : new Size(double.PositiveInfinity, available.Height));
            var d = c.DesiredSize;
            main += v ? d.Height : d.Width;
            cross = Math.Max(cross, v ? d.Width : d.Height);
            visible++;
        }
        main += Math.Max(0, visible - 1) * Spacing;
        return v ? new Size(cross, main) : new Size(main, cross);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double pos = 0;
        bool v = Orientation == Orientation.Vertical;
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            var d = c.DesiredSize;
            if (v) { c.Arrange(new Rect(0, pos, final.Width, d.Height)); pos += d.Height + Spacing; }
            else { c.Arrange(new Rect(pos, 0, d.Width, final.Height)); pos += d.Width + Spacing; }
        }
        return final;
    }
}

/// <summary>Fábrica dos blocos visuais do design (cartões, linhas, blocos de estatística…).</summary>
public static class Ui
{
    public static FontFamily UiFont => (FontFamily)Application.Current.FindResource("UiFont");
    public static FontFamily MonoFont => (FontFamily)Application.Current.FindResource("MonoFont");

    public static T Res<T>(this T e, DependencyProperty p, string key) where T : FrameworkElement
    {
        e.SetResourceReference(p, key);
        return e;
    }

    public static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static string HealthKey(Health h) => h switch
    {
        Health.Good => "Good",
        Health.Warning => "Warn",
        Health.Critical => "Crit",
        _ => "Text",
    };

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string brush = "Text", bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        else t.LineHeight = size * 1.45;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock Num(string text, double size = 13, string brush = "Text", FontWeight? weight = null)
    {
        var t = Text(text, size, weight ?? FontWeights.Bold, brush);
        t.FontFamily = MonoFont;
        return t;
    }

    public static TextBlock Label(string text) => Text(text, 12, null, "TextMuted");

    public static TextBlock Caps(string text) => Text(text.ToUpperInvariant(), 11, FontWeights.Bold, "TextMuted");

    public static TextBlock Title(string text) => Text(text, 15, FontWeights.Bold);

    public static Border Card(UIElement child, double padH = 20, double padV = 20) =>
        new Border { Child = child, CornerRadius = new CornerRadius(18), Padding = new Thickness(padH, padV, padH, padV) }.Res(Border.BackgroundProperty, "Card");

    public static Border Tile(UIElement child, double padH = 12, double padV = 10, double radius = 12) =>
        new Border { Child = child, CornerRadius = new CornerRadius(radius), Padding = new Thickness(padH, padV, padH, padV) }.Res(Border.BackgroundProperty, "Tile");

    /// <summary>Badge "?" com explicação para leigos.</summary>
    public static Border Help(string tip)
    {
        var t = Text("?", 10, FontWeights.Bold, "Text3");
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        var b = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(8), Child = t, Cursor = System.Windows.Input.Cursors.Help,
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap },
        }.Res(Border.BackgroundProperty, "Help");
        ToolTipService.SetInitialShowDelay(b, 120);
        ToolTipService.SetShowDuration(b, 60000);
        return b;
    }

    /// <summary>Cabeçalho de página: legenda pequena + título grande, ações à direita.</summary>
    public static Grid Header(string eyebrow, string title, params UIElement[] actions) => Header(Text(eyebrow, 12, null, "TextMuted"), title, actions);

    public static Grid Header(TextBlock eyebrow, string title, params UIElement[] actions)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var h1 = Text(title, 28, FontWeights.ExtraBold);
        var left = new Stack(4, eyebrow, h1) { VerticalAlignment = VerticalAlignment.Bottom };
        g.Children.Add(left);
        if (actions.Length > 0)
        {
            var right = new Stack(8) { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var a in actions) right.Children.Add(a);
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
        }
        return g;
    }

    /// <summary>Linha "chave ........ valor" com divisória acima.</summary>
    public static Border Row(string key, string value, string? tip = null, string valueBrush = "Text", bool mono = true, double padV = 9)
    {
        var dock = new DockPanel { LastChildFill = true };
        var v = mono ? Num(value, 13, valueBrush, FontWeights.SemiBold) : Text(value, 13, FontWeights.SemiBold, valueBrush);
        v.TextAlignment = TextAlignment.Right;
        v.TextTrimming = TextTrimming.None;
        v.TextWrapping = TextWrapping.Wrap;
        v.MaxWidth = 380;
        v.Margin = new Thickness(16, 0, 0, 0);
        v.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(v, Dock.Right);
        dock.Children.Add(v);
        var k = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        k.Children.Add(Text(key, 13, null, "Text3"));
        if (!string.IsNullOrEmpty(tip)) k.Children.Add(Help(tip));
        dock.Children.Add(k);
        return new Border { Child = dock, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, padV, 0, padV) }.Res(Border.BorderBrushProperty, "Divider");
    }

    /// <summary>Bloco pequeno com rótulo e valor (ex.: Temperatura / Clock / Consumo).</summary>
    public static (Border tile, TextBlock value) Stat(string label, string value = "—", double size = 16, string brush = "Text")
    {
        var v = Num(value, size, brush);
        var tile = Tile(new Stack(3, Text(label, 11, null, "TextMuted"), v));
        return (tile, v);
    }

    /// <summary>Faixa de aviso amarela/vermelha com "!" redondo.</summary>
    public static Border Banner(string text, Health level = Health.Warning, bool bigIcon = true, Action? onClick = null)
    {
        bool crit = level == Health.Critical;
        var icon = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "!", FontWeight = FontWeights.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Res(TextBlock.ForegroundProperty, crit ? "CritBg" : "WarnBg"),
        }.Res(Border.BackgroundProperty, crit ? "Crit" : "Warn");
        if (!bigIcon) { icon.Width = icon.Height = 18; icon.CornerRadius = new CornerRadius(9); }
        var t = Text(text, 13, null, crit ? "CritText" : "WarnText", wrap: true);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(12, 0, 0, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(t);
        var b = new Border { Child = dock, CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 16, 14) }.Res(Border.BackgroundProperty, crit ? "CritBg" : "WarnBg");
        if (onClick != null)
        {
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.MouseLeftButtonUp += (_, _) => onClick();
        }
        return b;
    }

    /// <summary>Barra horizontal arredondada (0–100).</summary>
    public sealed class Bar : Grid
    {
        readonly ColumnDefinition _a = new(), _b = new();
        readonly Border _fill;

        public Bar(double height = 8, string fill = "AccentFill", string track = "Divider")
        {
            Height = height;
            ColumnDefinitions.Add(_a);
            ColumnDefinitions.Add(_b);
            var trackB = new Border { CornerRadius = new CornerRadius(height / 2) }.Res(Border.BackgroundProperty, track);
            SetColumnSpan(trackB, 2);
            Children.Add(trackB);
            _fill = new Border { CornerRadius = new CornerRadius(height / 2) }.Res(Border.BackgroundProperty, fill);
            Children.Add(_fill);
            Value = 0;
        }

        public string FillKey { set => _fill.SetResourceReference(Border.BackgroundProperty, value); }

        public double Value
        {
            set
            {
                double v = Math.Clamp(double.IsNaN(value) ? 0 : value, 0, 100);
                _a.Width = new GridLength(v, GridUnitType.Star);
                _b.Width = new GridLength(100 - v, GridUnitType.Star);
                _fill.Visibility = v <= 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    /// <summary>Grade de colunas proporcionais com espaçamento.</summary>
    public static Grid Cols(double gap, double[] stars, params UIElement[] children)
    {
        var g = new Grid();
        for (int i = 0; i < stars.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stars[i], GridUnitType.Star) });
        for (int i = 0; i < children.Length; i++)
        {
            var c = children[i];
            if (c is FrameworkElement fe) fe.Margin = new Thickness(i % stars.Length == 0 ? 0 : gap / 2, 0, i % stars.Length == stars.Length - 1 ? 0 : gap / 2, 0);
            Grid.SetColumn(c, i % stars.Length);
            g.Children.Add(c);
        }
        return g;
    }

    /// <summary>Grade com N colunas iguais e quebra de linha automática.</summary>
    public static Grid Wrap(int columns, double gap, IEnumerable<UIElement> items)
    {
        var g = new Grid();
        for (int i = 0; i < columns; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
        int k = 0;
        foreach (var it in items)
        {
            int r = k / columns, c = k % columns;
            if (g.RowDefinitions.Count <= r) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (it is FrameworkElement fe)
                fe.Margin = new Thickness(c == 0 ? 0 : gap / 2, r == 0 ? 0 : gap, c == columns - 1 ? 0 : gap / 2, 0);
            Grid.SetRow(it, r);
            Grid.SetColumn(it, c);
            g.Children.Add(it);
            k++;
        }
        return g;
    }

    /// <summary>Duas colunas de cartões alinhados ao topo, preenchidas alternadamente (como o grid do design).</summary>
    public static Grid Masonry(double gap, IEnumerable<UIElement> items)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Stack(gap) { Margin = new Thickness(0, 0, gap / 2, 0) };
        var right = new Stack(gap) { Margin = new Thickness(gap / 2, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        int i = 0;
        foreach (var it in items) (i++ % 2 == 0 ? left : right).Children.Add(it);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    public static RadioButton Segment(string text, string group, bool isChecked, Action onChecked)
    {
        var r = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked };
        r.SetResourceReference(FrameworkElement.StyleProperty, "Segment");
        r.Checked += (_, _) => onChecked();
        return r;
    }

    /// <summary>Grupo de pílulas dentro de uma cápsula (ex.: 0,5 s · 1 s · 2 s).</summary>
    public static Border Segments(string group, IEnumerable<(string label, bool on, Action act)> items, bool transparentItems = true, string host = "Tile")
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, on, act) in items)
        {
            var r = Segment(label, group, on, act);
            if (transparentItems) r.Background = Brushes.Transparent;
            r.Margin = new Thickness(0, 0, 4, 0);
            sp.Children.Add(r);
        }
        if (sp.Children.Count > 0) ((FrameworkElement)sp.Children[^1]).Margin = new Thickness(0);
        return new Border { Child = sp, CornerRadius = new CornerRadius(10), Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center }.Res(Border.BackgroundProperty, host);
    }

    public static Button Button(string text, Action onClick, string? style = null)
    {
        var b = new Button { Content = text };
        if (style != null) b.SetResourceReference(FrameworkElement.StyleProperty, style);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Linha de configuração com título, descrição e um controle à direita.</summary>
    public static Border SettingRow(string title, string? desc, UIElement control, bool first = false)
    {
        var dock = new DockPanel();
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(16, 0, 0, 0); }
        DockPanel.SetDock(control, Dock.Right);
        dock.Children.Add(control);
        var left = new Stack(3, Text(title, 14, FontWeights.SemiBold, wrap: true), desc != null ? Text(desc, 12, null, "TextMuted", wrap: true) : null) { VerticalAlignment = VerticalAlignment.Center };
        dock.Children.Add(left);
        return new Border { Child = dock, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, 16, 0, 16) }.Res(Border.BorderBrushProperty, "Divider");
    }

    public static CheckBox Toggle(bool on, Action<bool> changed, string? text = null)
    {
        var c = new CheckBox { IsChecked = on, Content = text };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        return c;
    }

    /// <summary>Ponto colorido de legenda.</summary>
    public static StackPanel Legend(string text, string brush, bool line = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
        var dot = new Border { Width = line ? 14 : 8, Height = line ? 2 : 8, CornerRadius = new CornerRadius(line ? 1 : 4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }.Res(Border.BackgroundProperty, brush);
        sp.Children.Add(dot);
        sp.Children.Add(Text(text, 12, null, "Text3"));
        return sp;
    }

    public static ScrollViewer Scroll(UIElement content, Thickness? padding = null) =>
        new() { Content = content, Padding = padding ?? new Thickness(28, 24, 28, 32), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
}
