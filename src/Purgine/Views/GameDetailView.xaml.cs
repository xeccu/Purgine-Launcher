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

    /// <summary>
    /// Generación de la transición en curso. Al cambiar de juego rápido, una animación anterior puede terminar
    /// (evento Completed) después de empezar la nueva: sin este número, ese Completed tardío borraba la imagen
    /// anterior y cortaba el fundido nuevo, y el banner se quedaba vacío unos instantes.
    /// </summary>
    private int _fadeGeneration;

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
            // La imagen anterior se queda visible, opaca, hasta que la nueva esté decodificada (la carga es
            // asíncrona y el BitmapImage llega ya congelado y listo para dibujar): nunca se ve el fondo vacío.
            _fadeGeneration++;
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

    /// <summary>
    /// La nueva aparece encima de la anterior, que sigue opaca debajo; solo cuando la nueva ya cubre todo se
    /// retira la anterior. Así no hay ningún instante con el banner vacío.
    /// </summary>
    private void Crossfade()
    {
        var generation = ++_fadeGeneration;
        var fadeIn = new DoubleAnimation(0, 1, CrossfadeDuration) { EasingFunction = Ease };
        fadeIn.Completed += (_, _) =>
        {
            if (generation != _fadeGeneration) return; // transición ya sustituida por otra
            HeroImage.Opacity = 1;
            HeroImage.BeginAnimation(OpacityProperty, null);
            HeroPrevious.Source = null; // soltar la imagen anterior
        };
        HeroImage.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void FadeIn(UIElement element)
    {
        var generation = ++_fadeGeneration;
        var fadeIn = new DoubleAnimation(0, 1, CrossfadeDuration) { EasingFunction = Ease };
        fadeIn.Completed += (_, _) =>
        {
            if (generation != _fadeGeneration) return;
            element.Opacity = 1;
            element.BeginAnimation(OpacityProperty, null);
        };
        element.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void ResetLayers()
    {
        _fadeGeneration++;
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

    /// <summary>La nube abre su menú con un clic normal (no solo con el derecho).</summary>
    private void OnCloudClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element) return;
        menu.DataContext = element.DataContext;
        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
