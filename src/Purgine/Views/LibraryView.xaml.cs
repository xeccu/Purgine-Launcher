using System.Windows.Controls;
using System.Windows.Input;
using Purgine.ViewModels;

namespace Purgine.Views;

/// <summary>
/// Pestaña Biblioteca al estilo del cliente de Steam: lista lateral de juegos (búsqueda, filtros y orden),
/// inicio con cuadrícula/lista y ficha del juego seleccionado.
/// </summary>
public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    /// <summary>Al abrir una ficha desde la cuadrícula, la lista lateral muestra ese juego.</summary>
    private void OnSidebarSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameList.SelectedItem is { } item) GameList.ScrollIntoView(item);
    }

    /// <summary>Doble clic en la lista lateral = botón principal (jugar / instalar / actualizar).</summary>
    private void OnSidebarDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if ((e.OriginalSource as System.Windows.DependencyObject)?.FindAncestor<ListBoxItem>() is null) return;
        if (GameList.SelectedItem is GameCardViewModel card && card.PrimaryActionCommand.CanExecute(null))
            card.PrimaryActionCommand.Execute(null);
    }
}

internal static class VisualTreeExtensions
{
    public static T? FindAncestor<T>(this System.Windows.DependencyObject? node) where T : System.Windows.DependencyObject
    {
        while (node is not null and not T)
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}
