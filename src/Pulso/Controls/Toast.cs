using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using static Pulso.Controls.Ui;

namespace Pulso.Controls;

/// <summary>Aviso rápido no canto da tela (alertas de temperatura, aparelho novo na rede).</summary>
public static class Toast
{
    public static void Show(string title, string text, bool warning)
    {
        try
        {
            var body = new Stack(4, Text(title, 13, FontWeights.Bold, warning ? "Warn" : "Accent"), Text(text, 12.5, null, "Text2", wrap: true));
            var card = new Border { Child = body, CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), BorderThickness = new Thickness(1) }
                .Res(Border.BackgroundProperty, "Card").Res(Border.BorderBrushProperty, "BorderStrong");
            var w = new Window
            {
                Content = card, Width = 340, SizeToContent = SizeToContent.Height, SystemDecorations = SystemDecorations.None,
                Topmost = true, ShowInTaskbar = false, CanResize = false, Background = Avalonia.Media.Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            var screen = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Screens?.Primary;
            var wa = screen?.WorkingArea ?? new Avalonia.PixelRect(0, 0, 1280, 720);
            w.Position = new PixelPoint(wa.Right - 360, wa.Bottom - 140);
            w.Show();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (_, _) => { t.Stop(); w.Close(); };
            t.Start();
        }
        catch { }
    }
}
