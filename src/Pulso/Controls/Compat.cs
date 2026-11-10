using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

// Camada de compatibilidade: deixa o código de telas escrito para WPF compilar no Avalonia
// (mesmos nomes de tipos/propriedades usados em todo o projeto).

namespace Pulso.Controls;

public enum Visibility { Visible, Hidden, Collapsed }

public static class FontWeights
{
    public static FontWeight Normal => FontWeight.Normal;
    public static FontWeight Medium => FontWeight.Medium;
    public static FontWeight SemiBold => FontWeight.SemiBold;
    public static FontWeight Bold => FontWeight.Bold;
    public static FontWeight ExtraBold => FontWeight.ExtraBold;
    public static FontWeight Black => FontWeight.Black;
    public static FontWeight Light => FontWeight.Light;
}

public static class Compat
{
    extension(Window)
    {
        public static Window? GetWindow(Visual c) => c.GetVisualRoot() as Window;
    }

    extension(Control c)
    {
        public Visibility Visibility
        {
            get => c.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            set => c.IsVisible = value == Visibility.Visible;
        }

        public object? ToolTip
        {
            get => Avalonia.Controls.ToolTip.GetTip(c);
            set => Avalonia.Controls.ToolTip.SetTip(c, value);
        }
    }

    /// <summary>Equivale a SetResourceReference do WPF: acompanha troca de tema.</summary>
    public static T SetResourceReference<T>(this T e, AvaloniaProperty p, string key) where T : StyledElement
    {
        e.Bind(p, e.GetResourceObservable(key));
        return e;
    }

    public static T Res<T>(this T e, AvaloniaProperty p, string key) where T : StyledElement => e.SetResourceReference(p, key);

    public static void BeginInvoke(this Dispatcher d, Action a) => d.Post(a);
    public static void BeginInvoke(this Dispatcher d, DispatcherPriority p, Action a) => d.Post(a, p);
    public static void BeginInvoke(this Dispatcher d, Action a, DispatcherPriority p) => d.Post(a, p);
    public static void Invoke(this Dispatcher d, Action a) { if (d.CheckAccess()) a(); else d.InvokeAsync(a).GetAwaiter().GetResult(); }

    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b) return b;
        return Brushes.Gray;
    }
}

/// <summary>Acesso fácil ao dispatcher da interface.</summary>
public static class UiThread
{
    public static void Post(Action a) => Dispatcher.UIThread.Post(a);
    public static Task InvokeAsync(Action a) => Dispatcher.UIThread.InvokeAsync(a).GetTask();
}
