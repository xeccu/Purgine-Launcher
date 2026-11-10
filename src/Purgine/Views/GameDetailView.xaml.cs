using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Purgine.Infrastructure;
using Purgine.ViewModels;

namespace Purgine.Views;

/// <summary>
/// Ficha de un juego: héroe con logo, barra de acción, logros y enlaces rápidos.
/// En el perfil Ultra-moderno, al cambiar de juego la imagen panorámica hace un fundido cruzado: la del juego
/// anterior se queda debajo y se desvanece mientras aparece la nueva.
/// </summary>
public partial class GameDetailView : UserControl
{
    private static readonly Duration CrossfadeDuration = TimeSpan.FromMilliseconds(420);
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private GameDetailViewModel? _vm;
    /// <summary>Última imagen mostrada (para fundirla con la siguiente). Solo se guarda en Ultra-moderno.</summary>
    private ImageSource? _lastHero;

    public GameDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>El héroe mantiene una proporción panorámica parecida al original (≈ 3:1), con límites.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        HeroArea.Height = Math.Clamp(e.NewSize.Width / 3.1, 220, 460);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = e.NewValue as GameDetailViewModel;
        if (_vm is null)
        {
            _lastHero = null; // vuelta al inicio: nada que fundir después
            ResetLayers();
            return;
        }
        _vm.PropertyChanged += OnVmPropertyChanged;

        if (GraphicsTheme.AnimationsEnabled && _lastHero is not null)
        {
            // La anterior queda visible debajo hasta que llegue la nueva (la carga es asíncrona).
            HeroPrevious.BeginAnimation(OpacityProperty, null);
            HeroPrevious.Source = _lastHero;
            HeroPrevious.Opacity = 1;
            HeroImage.BeginAnimation(OpacityProperty, null);
            HeroImage.Opacity = 0;
            if (_vm.Hero is not null) Crossfade();
        }
        else
        {
            ResetLayers();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameDetailViewModel.Hero) || _vm is null) return;
        if (_vm.Hero is not { } hero)
        {
            // Imágenes soltadas (ventana en la bandeja): soltar también la copia. Al cerrar la ficha para abrir
            // otra (IsClosing) se conserva para el fundido.
            if (!_vm.IsClosing) { _lastHero = null; ResetLayers(); }
            return;
        }
        if (GraphicsTheme.AnimationsEnabled)
        {
            _lastHero = hero;
            if (HeroPrevious.Source is not null) Crossfade();
            else FadeIn(HeroImage);
        }
        else
        {
            _lastHero = null; // en Rendimiento no se retiene ninguna imagen extra
            ResetLayers();
        }
    }

    private void Crossfade()
    {
        var fadeOut = new DoubleAnimation(1, 0, CrossfadeDuration) { EasingFunction = Ease };
        fadeOut.Completed += (_, _) =>
        {
            HeroPrevious.BeginAnimation(OpacityProperty, null);
            HeroPrevious.Source = null; // soltar la imagen anterior
            HeroPrevious.Opacity = 1;
        };
        HeroPrevious.BeginAnimation(OpacityProperty, fadeOut);
        FadeIn(HeroImage);
    }

    private static void FadeIn(UIElement element)
    {
        var fadeIn = new DoubleAnimation(0, 1, CrossfadeDuration) { EasingFunction = Ease };
        fadeIn.Completed += (_, _) => element.BeginAnimation(OpacityProperty, null);
        element.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void ResetLayers()
    {
        HeroPrevious.BeginAnimation(OpacityProperty, null);
        HeroPrevious.Source = null;
        HeroImage.BeginAnimation(OpacityProperty, null);
        HeroImage.Opacity = 1;
    }

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
