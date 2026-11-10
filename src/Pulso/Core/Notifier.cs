namespace Pulso.Core;

/// <summary>Ponte entre a lógica (alertas, rede...) e a interface: quem mostra a notificação é a UI.</summary>
public static class Notifier
{
    public static event Action<string, string, bool>? Notified;
    public static event Action? ExitRequested;

    public static void Notify(string title, string text, bool warning = true) => Notified?.Invoke(title, text, warning);
    public static void RequestExit() => ExitRequested?.Invoke();
}
