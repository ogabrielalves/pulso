using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pulso.Controls;
using Pulso.Core;
using Pulso.Features;
using Pulso.Hardware;
using static Pulso.Controls.Ui;

namespace Pulso.Views;

public sealed class OverlayPage : PageBase
{
    readonly CheckBox _toggle = new();
    readonly Grid _preview = new();
    readonly OverlayPanel _panel = new() { IsHitTestVisible = false };
    readonly TextBlock _detected = Text("", 11.5, null, "TextMuted");
    readonly Button _hotkey = new();
    readonly Border[] _corners = new Border[4];
    bool _capturing;

    static OverlaySettings O => Settings.Current.Overlay;

    public OverlayPage()
    {
        _toggle.SetResourceReference(StyleProperty, "BigToggle");
        _toggle.IsChecked = O.Enabled;
        _toggle.Content = O.Enabled ? Loc.T("Overlay ativo", "Overlay on") : Loc.T("Overlay desativado", "Overlay off");
        _toggle.Checked += (_, _) => { OverlayController.Instance.SetEnabled(true); _toggle.Content = Loc.T("Overlay ativo", "Overlay on"); };
        _toggle.Unchecked += (_, _) => { OverlayController.Instance.SetEnabled(false); _toggle.Content = Loc.T("Overlay desativado", "Overlay off"); };
        _toggle.VerticalAlignment = VerticalAlignment.Bottom;

        // pré-visualização 16:9 com fundo listrado
        var stripes = new DrawingBrush
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 28, 28), ViewportUnits = BrushMappingMode.Absolute,
            Transform = new RotateTransform(45),
            Drawing = new DrawingGroup
            {
                Children =
                {
                    new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x1D, 0x21, 0x1F)), null, new RectangleGeometry(new Rect(0, 0, 14, 28))),
                    new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x20, 0x25, 0x23)), null, new RectangleGeometry(new Rect(14, 0, 14, 28))),
                },
            },
        };
        var screen = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Background = stripes, ClipToBounds = true }.Res(Border.BorderBrushProperty, "Divider");
        var placeholder = Num(Loc.T("pré-visualização · captura do jogo", "preview · game capture"), 13, "Placeholder", FontWeights.Normal);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        _preview.Children.Add(placeholder);
        _preview.Children.Add(_panel);
        screen.Child = _preview;
        var ratio = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
        var host = new Grid();
        host.SizeChanged += (_, e) => screen.Height = e.NewSize.Width * 9 / 16;
        host.Children.Add(screen);
        var leftCol = new Stack(10, host, Text(Loc.T("O overlay ignora cliques do mouse e não recebe foco, então não interfere no jogo. Funciona em tela cheia sem bordas e em janela; em tela cheia exclusiva alguns jogos podem escondê-lo.",
                                                  "The overlay ignores mouse clicks and never takes focus, so it doesn't interfere with the game. Works in borderless fullscreen and windowed; some games in exclusive fullscreen may hide it."), 12, null, "TextMuted", wrap: true));

        // itens
        var chips = new WrapPanel();
        foreach (var (id, pt, en) in OverlayItems.All)
        {
            var c = new ToggleButton { Content = Loc.T(pt, en), IsChecked = O.Items.Contains(id) };
            c.SetResourceReference(StyleProperty, "Chip");
            string iid = id;
            c.Checked += (_, _) => { if (!O.Items.Contains(iid)) O.Items.Add(iid); Save(); };
            c.Unchecked += (_, _) => { O.Items.Remove(iid); Save(); };
            chips.Children.Add(c);
        }
        var itemsCard = Card(new Stack(10, Label(Loc.T("Itens exibidos", "Items shown")), chips), 16, 16);

        // posição
        var cornerGrid = new Grid { Height = 92 };
        cornerGrid.ColumnDefinitions.Add(new ColumnDefinition()); cornerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        cornerGrid.RowDefinitions.Add(new RowDefinition()); cornerGrid.RowDefinitions.Add(new RowDefinition());
        var cornersDef = new[] { (OverlayCorner.TopLeft, 0, 0), (OverlayCorner.TopRight, 0, 1), (OverlayCorner.BottomLeft, 1, 0), (OverlayCorner.BottomRight, 1, 1) };
        for (int i = 0; i < 4; i++)
        {
            var (corner, row, col) = cornersDef[i];
            var dot = new Border { Width = 16, Height = 10, CornerRadius = new CornerRadius(3), HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = row == 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom };
            _corners[i] = dot;
            var cell = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(5), Margin = new Thickness(3), Child = dot, Cursor = Cursors.Hand }.Res(Border.BackgroundProperty, "Well");
            var cc = corner;
            cell.MouseLeftButtonUp += (_, _) => { O.Corner = cc; Save(); };
            Grid.SetRow(cell, row); Grid.SetColumn(cell, col);
            cornerGrid.Children.Add(cell);
        }
        var posBox = Tile(cornerGrid, 3, 3);
        var sizes = new Stack(6);
        foreach (var (label, size) in new[] { (Loc.T("Pequeno", "Small"), 11.0), (Loc.T("Médio", "Medium"), 13.0), (Loc.T("Grande", "Large"), 16.0) })
        {
            double sz = size;
            var r = Segment(label, "ovsize", Math.Abs(O.FontSize - sz) < 0.5, () => { O.FontSize = sz; Save(); });
            r.HorizontalContentAlignment = HorizontalAlignment.Left;
            r.Padding = new Thickness(12, 7, 12, 7);
            sizes.Children.Add(r);
        }
        var layoutCard = Card(Cols(14, new[] { 1.0, 1.0 }, new Stack(10, Label(Loc.T("Posição", "Position")), posBox), new Stack(10, Label(Loc.T("Tamanho", "Size")), sizes)), 16, 16);

        // aparência
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hex in new[] { "#FFFFFF", "#C5F36A", "#F2B84B", "#7AD3FF", "#FF8FA3" })
        {
            string h = hex;
            var sw = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(2), Background = (Brush)new BrushConverter().ConvertFromString(hex)!, ToolTip = hex };
            sw.MouseLeftButtonUp += (_, _) => { O.TextColor = h; Save(); };
            swatches.Children.Add(sw);
        }
        var opacity = new Slider { Minimum = 0, Maximum = 1, Value = O.BackgroundOpacity, SmallChange = 0.05, LargeChange = 0.1 };
        var opLabel = Num("", 12, "Text2", FontWeights.Normal);
        opacity.ValueChanged += (_, e) => { O.BackgroundOpacity = Math.Round(e.NewValue, 2); opLabel.Text = Fmt.N(O.BackgroundOpacity * 100) + "%"; Save(); };
        opLabel.Text = Fmt.N(O.BackgroundOpacity * 100) + "%";
        var opRow = new DockPanel();
        DockPanel.SetDock(opLabel, Dock.Right);
        opLabel.Margin = new Thickness(10, 0, 0, 0); opLabel.VerticalAlignment = VerticalAlignment.Center;
        opRow.Children.Add(opLabel); opRow.Children.Add(opacity);
        var lookCard = Card(new Stack(10, Label(Loc.T("Cor do texto", "Text color")), swatches, Label(Loc.T("Opacidade do fundo", "Background opacity")), opRow), 16, 16);
        _swatches = swatches;

        // atalho e detecção
        _hotkey.SetResourceReference(StyleProperty, "StepButton");
        _hotkey.Width = double.NaN; _hotkey.Height = double.NaN;
        _hotkey.Padding = new Thickness(10, 6, 10, 6);
        _hotkey.FontFamily = MonoFont; _hotkey.FontSize = 12;
        _hotkey.Content = HotkeyText(O.Hotkey);
        _hotkey.ToolTip = Loc.T("Clique e pressione a nova combinação de teclas.", "Click and press the new key combination.");
        _hotkey.Click += (_, _) => { _capturing = true; _hotkey.Content = Loc.T("Pressione as teclas…", "Press the keys…"); _hotkey.Focus(); };
        _hotkey.PreviewKeyDown += OnHotkeyKey;
        var hkBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = _hotkey }.Res(Border.BorderBrushProperty, "BorderStrong");
        var auto = Toggle(O.OnlyInGames, on => { O.OnlyInGames = on; Save(); });
        var autoLabel = new Stack(2, Text(Loc.T("Só exibir com jogo aberto", "Only show while a game is running"), 13), _detected);
        var rowsCard = Card(new Stack(0,
            SettingRow(Loc.T("Mostrar/ocultar", "Show/hide"), null, hkBorder, first: true),
            new Border { Child = WithToggle(autoLabel, auto), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 12) }.Res(Border.BorderBrushProperty, "Divider")), 16, 6);
        ((Border)((Stack)rowsCard.Child).Children[0]).Padding = new Thickness(0, 12, 0, 12);

        var right = new Stack(14, itemsCard, layoutCard, lookCard, rowsCard);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        right.Margin = new Thickness(18, 0, 0, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(leftCol); grid.Children.Add(right);

        Content = Scroll(new Stack(18, Header(Loc.T("Impacto estimado no jogo: menos de 0,5% de CPU", "Estimated in-game impact: under 0.5% CPU"), "Overlay", _toggle), grid));
        OverlayController.Instance.StateChanged += () => Dispatcher.BeginInvoke(UpdateDetected);
        UpdateLook();
    }

    StackPanel _swatches;

    static DockPanel WithToggle(UIElement left, CheckBox toggle)
    {
        var d = new DockPanel();
        DockPanel.SetDock(toggle, Dock.Right);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        d.Children.Add(toggle); d.Children.Add(left);
        return d;
    }

    static string HotkeyText(string hk) => hk.Replace("+", " + ");

    void OnHotkeyKey(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        if (key == Key.Escape) { _capturing = false; _hotkey.Content = HotkeyText(O.Hotkey); return; }
        var mods = Keyboard.Modifiers;
        bool fkey = key >= Key.F1 && key <= Key.F24;
        if (mods == ModifierKeys.None && !fkey) { _hotkey.Content = Loc.T("Use Ctrl, Alt ou Shift + tecla", "Use Ctrl, Alt or Shift + key"); return; }
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(key.ToString());
        string prev = O.Hotkey;
        O.Hotkey = string.Join("+", parts);
        _capturing = false;
        if (!OverlayController.Instance.RegisterHotkey())
        {
            O.Hotkey = prev;
            OverlayController.Instance.RegisterHotkey();
            _hotkey.Content = Loc.T("Combinação em uso por outro programa", "Combination used by another program");
            return;
        }
        Settings.Save();
        _hotkey.Content = HotkeyText(O.Hotkey);
    }

    void Save()
    {
        Settings.Save();
        OverlayController.Instance.Refresh();
        UpdateLook();
    }

    void UpdateLook()
    {
        _panel.ApplyStyle();
        var c = O.Corner;
        _panel.HorizontalAlignment = c is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _panel.VerticalAlignment = c is OverlayCorner.TopLeft or OverlayCorner.TopRight ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _panel.Margin = new Thickness(16);
        var order = new[] { OverlayCorner.TopLeft, OverlayCorner.TopRight, OverlayCorner.BottomLeft, OverlayCorner.BottomRight };
        for (int i = 0; i < 4; i++) _corners[i].SetResourceReference(Border.BackgroundProperty, order[i] == c ? "AccentFill" : "BorderStrong");
        foreach (Border sw in _swatches.Children)
            sw.BorderBrush = string.Equals((string)sw.ToolTip, O.TextColor, StringComparison.OrdinalIgnoreCase) ? Ui.Brush("Accent") : Brushes.Transparent;
        if (SensorHub.Instance.Last is Snapshot s) OnSnapshot(s);
    }

    void UpdateDetected()
    {
        var oc = OverlayController.Instance;
        _detected.Text = !O.Enabled ? Loc.T("Ative o overlay para detectar jogos", "Turn on the overlay to detect games")
            : FpsMonitor.Instance.Error ?? (oc.CurrentGame != null ? Loc.T($"Detectado agora: {oc.CurrentGame}.exe", $"Detected now: {oc.CurrentGame}.exe") : Loc.T("Nenhum jogo detectado agora", "No game detected right now"));
    }

    public override void OnSnapshot(Snapshot s)
    {
        // Na pré-visualização mostra valores de exemplo quando não há jogo (FPS/frametime).
        var rows = OverlayItems.Rows(s).Select(r => r.value == "-" && r.key is "FPS" or "1%" ? (r.key, "144") : r.value == "-" && r.key == "FT" ? (r.key, Fmt.N(6.9, 1) + " ms") : r).ToList();
        _panel.Update(rows);
        _panel.Visibility = _toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetected();
    }

    protected override void OnShown() => UpdateDetected();
}
