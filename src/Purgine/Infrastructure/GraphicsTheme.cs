using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Purgine.Settings;

namespace Purgine.Infrastructure;

/// <summary>
/// Perfiles gráficos en tiempo real. Cada perfil reescribe un pequeño conjunto de recursos de la aplicación
/// (radios de esquina, bordes, fondos, degradados, sombras, pinceles del botón principal) que las vistas
/// consumen con <c>DynamicResource</c>, así que el cambio se ve al instante sin recrear ventanas.
/// <list type="bullet">
///   <item><b>Rendimiento</b>: plano y minimalista; cero animaciones, sombras y transparencias.</item>
///   <item><b>Ultra-moderno</b>: material acrílico de Windows 11 detrás de la ventana, paneles flotantes
///         translúcidos (#141721), bordes que se iluminan al pasar el cursor, sombras, botón con degradado y
///         brillo, brillo con el color dominante del juego, y animaciones (ver <see cref="Fx"/>).</item>
/// </list>
/// Todos los pinceles y efectos se congelan (Freeze): se comparten entre elementos sin coste de seguimiento.
/// </summary>
public static class GraphicsTheme
{
    public static GraphicsPreset Current { get; private set; } = GraphicsPreset.High;

    public static bool IsModern => Current != GraphicsPreset.Low;

    /// <summary>Animaciones XAML (portadas, transiciones, fundidos): solo en Ultra-moderno.</summary>
    public static bool AnimationsEnabled => IsModern;

    /// <summary>Se aplicó otro perfil (la ventana activa o desactiva el material acrílico).</summary>
    public static event Action<GraphicsPreset>? Changed;

    private static readonly Color Surface = Color.FromRgb(0x15, 0x18, 0x20);
    private static readonly Color Panel = Color.FromRgb(0x14, 0x17, 0x21);
    private static readonly Color Bg = Color.FromRgb(0x0E, 0x10, 0x14);
    private static readonly Color SubtleBorder = Color.FromRgb(0x2A, 0x2E, 0x3D);
    private static readonly Color SteamBlue = Color.FromRgb(0x1A, 0x9F, 0xFF);

    public static void Apply(GraphicsPreset preset)
    {
        if (preset == GraphicsPreset.Medium) preset = GraphicsPreset.High; // perfil retirado
        var r = Application.Current.Resources;
        var modern = preset == GraphicsPreset.High;

        // ----- Fondos: opacos en Rendimiento; translúcidos sobre el acrílico en Ultra-moderno -----
        r["ChromeBrush"] = Solid(modern ? WithAlpha(Surface, 0x99) : Surface);       // cabecera, lista lateral, barras
        r["ContentBrush"] = Solid(modern ? WithAlpha(Bg, 0xD9) : Bg);                // fondo de las vistas
        r["PanelBackgroundBrush"] = Solid(modern ? WithAlpha(Panel, 0xCC) : Surface); // paneles flotantes

        // ----- Formas y bordes -----
        r["CardCornerRadius"] = new CornerRadius(modern ? 10 : 0);
        r["CardBottomCornerRadius"] = new CornerRadius(0, 0, modern ? 10 : 0, modern ? 10 : 0);
        r["PanelCornerRadius"] = new CornerRadius(modern ? 12 : 0);
        r["PanelBorderBrush"] = Solid(modern ? SubtleBorder : Colors.Transparent);
        r["PanelHoverBorderBrush"] = Solid(modern ? WithAlpha(SteamBlue, 0x99) : Colors.Transparent);
        r["PanelBorderThickness"] = new Thickness(modern ? 1 : 0);
        r["PanelHoverEffect"] = modern ? Shadow(SteamBlue, blur: 18, depth: 0, opacity: 0.25) : null;

        // ----- Imagen panorámica de la ficha -----
        r["HeroGradientBrush"] = modern
            ? Frozen(new LinearGradientBrush(
                [
                    new GradientStop(WithAlpha(Bg, 0), 0.30),
                    new GradientStop(WithAlpha(Bg, 0xB3), 0.78),
                    new GradientStop(WithAlpha(Bg, 0xF2), 1),
                ], new Point(0, 0), new Point(0, 1)))
            : Solid(Colors.Transparent);
        r["LogoEffect"] = modern ? Shadow(Colors.Black, blur: 18, depth: 0, opacity: 0.85) : null;

        // ----- Barra de acción («JUGAR» + estadísticas): panel flotante bajo el banner en Ultra-moderno -----
        // Debajo del banner, sin invadirlo (antes subía 44 px sobre la imagen y tapaba logotipos del arte).
        r["ActionBarMargin"] = modern ? new Thickness(32, 16, 32, 0) : new Thickness(0);
        r["ActionBarCornerRadius"] = new CornerRadius(modern ? 12 : 0);
        r["ActionBarBorderThickness"] = new Thickness(modern ? 1 : 0);
        r["ActionBarBackground"] = Solid(modern ? WithAlpha(Panel, 0xE6) : Surface);
        r["ActionBarEffect"] = modern ? Shadow(Colors.Black, blur: 28, depth: 6, opacity: 0.55) : null;

        // ----- Portadas -----
        r["CoverEffect"] = modern ? Shadow(Colors.Black, blur: 16, depth: 5, opacity: 0.5) : null;

        // ----- Botón principal -----
        r["PlayButtonBrush"] = modern
            ? Frozen(new LinearGradientBrush(SteamBlue, Color.FromRgb(0x10, 0x72, 0xBA), new Point(0, 0), new Point(1, 1)))
            : Solid(SteamBlue);
        r["PlayButtonHoverBrush"] = modern
            ? Frozen(new LinearGradientBrush(Color.FromRgb(0x4D, 0xB6, 0xFF), Color.FromRgb(0x1A, 0x86, 0xD6), new Point(0, 0), new Point(1, 1)))
            : Solid(Color.FromRgb(0x47, 0xB2, 0xFF));
        r["PlayButtonCornerRadius"] = new CornerRadius(modern ? 8 : 0);
        r["PlayButtonGlow"] = modern ? Shadow(SteamBlue, blur: 24, depth: 0, opacity: 0.85) : null;

        Current = preset;
        Changed?.Invoke(preset);
        Log.Info($"Perfil gráfico: {(modern ? "Ultra-moderno" : "Rendimiento")}.");
    }

    /// <summary>Brillo con el color dominante de un juego (solo Ultra-moderno; null en Rendimiento).</summary>
    public static Effect? GlowFor(Color? color) =>
        !IsModern ? null
        : color is { } c ? Shadow(c, blur: 46, depth: 0, opacity: 0.6)
        : (Effect?)Application.Current.Resources["ActionBarEffect"];

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static SolidColorBrush Solid(Color c) => Frozen(new SolidColorBrush(c));

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
