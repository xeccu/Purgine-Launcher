using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Purgine.ViewModels;

namespace Purgine.Views;

/// <summary>Diálogo de instalar como superposición dentro de la ventana (Esc = cancelar).</summary>
public partial class InstallDialogView : UserControl
{
    public InstallDialogView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(() => Card.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
        Card.Focusable = true;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not InstallDialogViewModel vm) return;
        vm.CancelCommand.Execute(null);
        e.Handled = true;
    }
}
