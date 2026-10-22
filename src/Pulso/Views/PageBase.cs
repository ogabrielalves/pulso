using System.Windows.Controls;
using Pulso.Hardware;

namespace Pulso.Views;

/// <summary>Base das telas: só a tela visível recebe atualizações (economia de CPU).</summary>
public abstract class PageBase : UserControl
{
    public bool Shown { get; private set; }

    public void Show()
    {
        Shown = true;
        OnShown();
        if (SensorHub.Instance.Last is Snapshot s) OnSnapshot(s);
    }

    public void Hide()
    {
        Shown = false;
        OnHidden();
    }

    protected virtual void OnShown() { }
    protected virtual void OnHidden() { }

    /// <summary>Chamado na thread da interface a cada nova leitura, apenas com a tela visível.</summary>
    public virtual void OnSnapshot(Snapshot s) { }

    /// <summary>Texto da tela para "Copiar" / relatório (opcional).</summary>
    public virtual string? CopyText() => null;
}
