using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Purgine.Views;

/// <summary>Ficha de un juego: héroe con logo, barra de acción, logros y enlaces rápidos.</summary>
public partial class GameDetailView : UserControl
{
    public GameDetailView() => InitializeComponent();

    /// <summary>El héroe mantiene una proporción panorámica parecida al original (≈ 3:1), con límites.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        HeroArea.Height = Math.Clamp(e.NewSize.Width / 3.1, 220, 460);

    /// <summary>El botón de opciones abre el mismo menú que el clic derecho sobre el juego.</summary>
    private void OnOptionsClick(object sender, RoutedEventArgs e)
    {
        if (OptionsButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = OptionsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = OptionsButton.DataContext;
        menu.IsOpen = true;
    }
}
