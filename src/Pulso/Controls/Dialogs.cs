using Avalonia;
using Pulso.Core;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using static Pulso.Controls.Ui;

namespace Pulso.Controls;

public enum MessageBoxButton { OK, OKCancel, YesNo }
public enum MessageBoxImage { None, Information, Warning, Question, Error }
public enum MessageBoxResult { None, OK, Cancel, Yes, No }

/// <summary>Caixa de mensagem modal com a mesma forma de uso do WPF (bloqueia até o usuário responder).</summary>
public static class MessageBox
{
    public static MessageBoxResult Show(string text, string caption = "Pulso") => Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(Window? owner, string text, string caption = "Pulso",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        var result = MessageBoxResult.None;
        var win = new Window
        {
            Title = caption, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        win.SetResourceReference(Window.BackgroundProperty, "Bg");
        var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        void Add(string label, MessageBoxResult r, bool primary)
        {
            var b = Ui.Button(label, () => { result = r; win.Close(); }, primary ? "Primary" : null);
            b.MinWidth = 84;
            buttonsRow.Children.Add(b);
        }
        switch (buttons)
        {
            case MessageBoxButton.OK: Add("OK", MessageBoxResult.OK, true); break;
            case MessageBoxButton.OKCancel: Add(Loc.T("Cancelar", "Cancel"), MessageBoxResult.Cancel, false); Add("OK", MessageBoxResult.OK, true); break;
            case MessageBoxButton.YesNo: Add(Loc.T("Não", "No"), MessageBoxResult.No, false); Add(Loc.T("Sim", "Yes"), MessageBoxResult.Yes, true); break;
        }
        var body = new Stack(20, Text(text, 13.5, null, "Text", wrap: true), buttonsRow) { Margin = new Thickness(24) };
        win.Content = body;
        Show(win, owner);
        return result;
    }

    /// <summary>Abre a janela como modal e só retorna quando ela fechar.</summary>
    internal static void Show(Window win, Window? owner)
    {
        var frame = new DispatcherFrame();
        win.Closed += (_, _) => frame.Continue = false;
        if (owner != null) _ = win.ShowDialog(owner); else win.Show();
        Dispatcher.UIThread.PushFrame(frame);
    }
}

public static class Dialogs
{
    public static async Task CopyAsync(Visual from, string text)
    {
        var top = TopLevel.GetTopLevel(from);
        if (top?.Clipboard != null) await top.Clipboard.SetTextAsync(text);
    }

    public static async Task<string?> SaveAsync(Visual from, string suggestedName, string filterName, string pattern, string title)
    {
        var top = TopLevel.GetTopLevel(from);
        if (top == null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = new[] { new FilePickerFileType(filterName) { Patterns = new[] { pattern } } },
        });
        return file?.TryGetLocalPath();
    }
}
