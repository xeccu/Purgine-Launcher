using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Purgine.Settings;

namespace Purgine.Infrastructure;

/// <summary>
/// Perfiles gráficos en tiempo real. Cada perfil reescribe un pequeño conjunto de recursos de la aplicación
/// (radios de esquina, bordes, degradados, sombras, pinceles del botón principal, translucidez de la barra
/// lateral y la cabecera) que las vistas consumen con <c>DynamicResource</c>, así que el cambio se ve al
/// instante sin recrear ventanas.
/// <list type="bullet">
///   <item><b>Bajo</b>: plano, sin sombras, sin degradados, sin animaciones ni desenfoque.</item>
///   <item><b>Medio</b>: esquinas redondeadas, bordes sutiles y degradado en la imagen panorámica.</item>
///   <item><b>Alto</b>: lo anterior + sombras, animaciones (ver <see cref="Fx"/>), botón con degradado y brillo,
///         y barra lateral/cabecera translúcidas sobre el material Mica de Windows 11 (desenfoque del fondo).</item>
/// </list>
/// Todos los pinceles y efectos se congelan (Freeze): se comparten entre elementos sin coste de seguimiento.
/// </summary>
public static class GraphicsTheme
{
    public static GraphicsPreset Current { get; private set; } = GraphicsPreset.Medium;

    /// <summary>Animaciones XAML (elevación de portadas, transiciones de entrada): solo en Alto.</summary>
    public static bool AnimationsEnabled => Current == GraphicsPreset.High;

    /// <summary>Se aplicó otro perfil (la ventana activa o desactiva el material Mica).</summary>
    public static event Action<GraphicsPreset>? Changed;

    private static readonly Color Surface = Color.FromRgb(0x15, 0x18, 0x20);
    private static readonly Color Bg = Color.FromRgb(0x0E, 0x10, 0x14);
    private static readonly Color SubtleBorder = Color.FromRgb(0x2A, 0x2E, 0x3D);
    private static readonly Color SteamBlue = Color.FromRgb(0x1A, 0x9F, 0xFF);

    public static void Apply(GraphicsPreset preset)
    {
        var r = Application.Current.Resources;
        var low = preset == GraphicsPreset.Low;
        var high = preset == GraphicsPreset.High;

        // ----- Formas y bordes -----
        r["CardCornerRadius"] = new CornerRadius(low ? 2 : 8);
        r["CardBottomCornerRadius"] = new CornerRadius(0, 0, low ? 2 : 8, low ? 2 : 8);
        r["PanelCornerRadius"] = new CornerRadius(low ? 2 : 8);
        r["PanelBorderBrush"] = Frozen(new SolidColorBrush(low ? Colors.Transparent : SubtleBorder));
        r["PanelBorderThickness"] = new Thickness(low ? 0 : 1);

        // ----- Imagen panorámica de la ficha -----
        r["HeroGradientBrush"] = low ? Frozen(new SolidColorBrush(Colors.Transparent)) : Frozen(new LinearGradientBrush(
            [
                new GradientStop(Color.FromArgb(0, Bg.R, Bg.G, Bg.B), high ? 0.30 : 0.40),
                new GradientStop(Color.FromArgb(high ? (byte)0xB3 : (byte)0x99, Bg.R, Bg.G, Bg.B), 0.78),
                new GradientStop(Color.FromArgb(0xFF, Bg.R, Bg.G, Bg.B), 1),
            ], new Point(0, 0), new Point(0, 1)));
        r["LogoEffect"] = high ? Shadow(Colors.Black, blur: 18, depth: 0, opacity: 0.85) : null;

        // ----- Barra de acción («JUGAR» + estadísticas) -----
        r["ActionBarMargin"] = high ? new Thickness(24, -44, 24, 0) : new Thickness(0);
        r["ActionBarCornerRadius"] = new CornerRadius(high ? 10 : 0);
        r["ActionBarBorderThickness"] = high ? new Thickness(1) : new Thickness(0, 0, 0, low ? 0 : 1);
        r["ActionBarBackground"] = Frozen(new SolidColorBrush(high ? Color.FromArgb(0xF2, Surface.R, Surface.G, Surface.B) : Surface));
        r["ActionBarEffect"] = high ? Shadow(Colors.Black, blur: 28, depth: 6, opacity: 0.55) : null;

        // ----- Portadas -----
        r["CoverEffect"] = high ? Shadow(Colors.Black, blur: 14, depth: 4, opacity: 0.5) : null;

        // ----- Botón principal -----
        r["PlayButtonBrush"] = high
            ? Frozen(new LinearGradientBrush(SteamBlue, Color.FromRgb(0x10, 0x72, 0xBA), new Point(0, 0), new Point(1, 1)))
            : Frozen(new SolidColorBrush(SteamBlue));
        r["PlayButtonHoverBrush"] = high
            ? Frozen(new LinearGradientBrush(Color.FromRgb(0x4D, 0xB6, 0xFF), Color.FromRgb(0x1A, 0x86, 0xD6), new Point(0, 0), new Point(1, 1)))
            : Frozen(new SolidColorBrush(Color.FromRgb(0x47, 0xB2, 0xFF)));
        r["PlayButtonCornerRadius"] = new CornerRadius(low ? 2 : high ? 6 : 4);
        r["PlayButtonGlow"] = high ? Shadow(SteamBlue, blur: 22, depth: 0, opacity: 0.85) : null;

        // ----- Barra lateral y cabecera (translúcidas sobre Mica solo en Alto) -----
        r["ChromeBrush"] = Frozen(new SolidColorBrush(high ? Color.FromArgb(0xA6, Surface.R, Surface.G, Surface.B) : Surface));

        Current = preset;
        Changed?.Invoke(preset);
        Log.Info($"Perfil gráfico: {preset}.");
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static DropShadowEffect Shadow(Color color, double blur, double depth, double opacity) => Frozen(new DropShadowEffect
    {
        Color = color,
        BlurRadius = blur,
        ShadowDepth = depth,
        Direction = 270,
        Opacity = opacity,
        RenderingBias = RenderingBias.Performance,
    });
}
