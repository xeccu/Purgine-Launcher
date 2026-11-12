using System.Windows.Controls;

namespace Purgine.Views;

/// <summary>Perfil activo (foto + nombre) con su menú: cambiar, editar, añadir o borrar perfiles.</summary>
public partial class ProfileWidget : UserControl
{
    public ProfileWidget() => InitializeComponent();

    /// <summary>Cada widget abre su propio menú (hay uno en la cabecera de Steam y otro en la barra de Epic); se cierra al elegir algo.</summary>
    private void OnMenuClick(object sender, System.Windows.RoutedEventArgs e) => Toggle.IsChecked = false;
}
