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

    /// <summary>Estilo visual (Steam / Epic). Se combina con el perfil: 4 apariencias.</summary>
    public static UiSkin Skin { get; private set; } = UiSkin.Steam;

    public static bool IsEpic => Skin == UiSkin.Epic;

    /// <summary>Acrílico de Windows 11 detrás de la ventana: solo Steam moderno (el estilo Epic es negro opaco).</summary>
    public static bool UseAcrylic => IsModern && !IsEpic;

    public static bool IsModern => Current != GraphicsPreset.Low;

    /// <summary>Animaciones XAML (portadas, transiciones, fundidos): solo en Ultra-moderno.</summary>
    public static bool AnimationsEnabled => IsModern;

    /// <summary>Se aplicó otro perfil (la ventana activa o desactiva el material acrílico).</summary>
    public static event Action<GraphicsPreset>? Changed;

    /// <summary>Paleta de un estilo. Los pinceles de la app se recolorean en su sitio, así que el cambio es en caliente.</summary>
    private sealed record Palette(
        Color Bg, Color Surface, Color Card, Color Border, Color Text, Color Muted, Color Accent, Color AccentHover,
        Color AccentSoft, Color Hover, Color ScrollThumb, Color SidebarSelected, Color Panel, Color SubtleBorder,
        Color Primary, Color PrimaryHover, Color PrimaryPressed, Color PrimaryEnd);

    private static readonly Palette SteamPalette = new(
        Bg: Rgb(0x0E, 0x10, 0x14), Surface: Rgb(0x15, 0x18, 0x20), Card: Rgb(0x1C, 0x20, 0x29), Border: Rgb(0x26, 0x2B, 0x36),
        Text: Rgb(0xE6, 0xE8, 0xEE), Muted: Rgb(0x8A, 0x91, 0xA3), Accent: Rgb(0x4F, 0x8C, 0xFF), AccentHover: Rgb(0x6A, 0x9E, 0xFF),
        AccentSoft: Rgb(0x1A, 0x2B, 0x4D), Hover: Rgb(0x23, 0x28, 0x36), ScrollThumb: Rgb(0x34, 0x3A, 0x48),
        SidebarSelected: Rgb(0x2A, 0x35, 0x50), Panel: Rgb(0x14, 0x17, 0x21), SubtleBorder: Rgb(0x2A, 0x2E, 0x3D),
        Primary: Rgb(0x1A, 0x9F, 0xFF), PrimaryHover: Rgb(0x47, 0xB2, 0xFF), PrimaryPressed: Rgb(0x14, 0x88, 0xD8), PrimaryEnd: Rgb(0x10, 0x72, 0xBA));

    /// <summary>Negro neutro, gris carbón, texto blanco y acento cian, como la tienda de Epic.</summary>
    private static readonly Palette EpicPalette = new(
        Bg: Rgb(0x10, 0x10, 0x14), Surface: Rgb(0x10, 0x10, 0x14), Card: Rgb(0x20, 0x20, 0x24), Border: Rgb(0x2C, 0x2C, 0x30),
        Text: Rgb(0xFF, 0xFF, 0xFF), Muted: Rgb(0xA4, 0xA4, 0xA8), Accent: Rgb(0x26, 0xBB, 0xFF), AccentHover: Rgb(0x61, 0xCD, 0xFF),
        AccentSoft: Rgb(0x2A, 0x2A, 0x2F), Hover: Rgb(0x23, 0x23, 0x27), ScrollThumb: Rgb(0x3A, 0x3A, 0x40),
        SidebarSelected: Rgb(0x2A, 0x2A, 0x2F), Panel: Rgb(0x18, 0x18, 0x1C), SubtleBorder: Rgb(0x2C, 0x2C, 0x30),
        Primary: Rgb(0x00, 0x74, 0xE4), PrimaryHover: Rgb(0x26, 0x8F, 0xF5), PrimaryPressed: Rgb(0x00, 0x5E, 0xBC), PrimaryEnd: Rgb(0x00, 0x5A, 0xC0));

    private static Palette P => IsEpic ? EpicPalette : SteamPalette;

    private static Color Surface => P.Surface;
    private static Color Panel => P.Panel;
    private static Color Bg => P.Bg;
    private static Color SubtleBorder => P.SubtleBorder;
    private static Color SteamBlue => P.Primary;

    /// <summary>Cambia el estilo (Steam / Epic) en caliente y vuelve a aplicar el perfil con sus colores.</summary>
    public static void ApplySkin(UiSkin skin)
    {
        Skin = skin;
        var r = Application.Current.Resources;
        var p = P;
        Recolor(r, "BgBrush", p.Bg);
        Recolor(r, "SurfaceBrush", p.Surface);
        Recolor(r, "CardBrush", p.Card);
        Recolor(r, "BorderBrush", p.Border);
        Recolor(r, "TextBrush", p.Text);
        Recolor(r, "MutedBrush", p.Muted);
        Recolor(r, "AccentBrush", p.Accent);
        Recolor(r, "AccentHoverBrush", p.AccentHover);
        Recolor(r, "AccentSoftBrush", p.AccentSoft);
        Recolor(r, "HoverBrush", p.Hover);
        Recolor(r, "ScrollThumbBrush", p.ScrollThumb);
        Recolor(r, "SidebarSelectedBrush", p.SidebarSelected);
        Recolor(r, "SteamBlueBrush", p.Primary);
        Recolor(r, "SteamBlueHoverBrush", p.PrimaryHover);
        Recolor(r, "SteamBluePressedBrush", p.PrimaryPressed);
        Recolor(r, "OverlayBrush", WithAlpha(p.Bg, 0xB3));
        Apply(Current);
    }

    /// <summary>
    /// Recolorea el pincel compartido en su sitio: las vistas lo usan con StaticResource y se repintan solas. Si
    /// alguno estuviera congelado se sustituye (lo verán las vistas que usan DynamicResource).
    /// </summary>
    private static void Recolor(ResourceDictionary r, string key, Color color)
    {
        if (r[key] is SolidColorBrush { IsFrozen: false } brush) brush.Color = color;
        else r[key] = new SolidColorBrush(color);
    }

    private static Color Rgb(byte red, byte green, byte blue) => Color.FromRgb(red, green, blue);

    public static void Apply(GraphicsPreset preset)
    {
        if (preset == GraphicsPreset.Medium) preset = GraphicsPreset.High; // perfil retirado
        var r = Application.Current.Resources;
        var modern = preset == GraphicsPreset.High;
        var epic = IsEpic;

        // ----- Fondos: opacos en Rendimiento; translúcidos sobre el acrílico en Ultra-moderno -----
        // Epic: negro opaco en los dos perfiles (sin acrílico), con paneles gris carbón como su tienda.
        var translucent = modern && !epic;
        r["ChromeBrush"] = Solid(translucent ? WithAlpha(Surface, 0x99) : Surface);       // cabecera, lista lateral, barras
        r["ContentBrush"] = Solid(translucent ? WithAlpha(Bg, 0xD9) : Bg);                // fondo de las vistas
        r["PanelBackgroundBrush"] = Solid(translucent ? WithAlpha(Panel, 0xCC) : epic ? P.Panel : Surface); // paneles flotantes
        r["SidebarBrush"] = Solid(epic ? Rgb(0x12, 0x12, 0x16) : Surface);              // barra lateral del estilo Epic

        // ----- Formas y bordes -----
        // Epic: esquinas redondeadas también en Optimizada (no cuestan nada: no son efectos).
        var card = epic ? (modern ? 12 : 8) : modern ? 10 : 0;
        r["CardCornerRadius"] = new CornerRadius(card);
        r["CardBottomCornerRadius"] = new CornerRadius(0, 0, card, card);
        r["PanelCornerRadius"] = new CornerRadius(epic ? (modern ? 14 : 10) : modern ? 12 : 0);
        r["PanelBorderBrush"] = Solid(modern || epic ? SubtleBorder : Colors.Transparent);
        r["PanelHoverBorderBrush"] = Solid(modern ? WithAlpha(SteamBlue, 0x99) : Colors.Transparent);
        r["PanelBorderThickness"] = new Thickness(modern || epic ? 1 : 0);
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
        r["ActionBarCornerRadius"] = new CornerRadius(epic ? (modern ? 14 : 10) : modern ? 12 : 0);
        r["ActionBarBorderThickness"] = new Thickness(modern ? 1 : 0);
        r["ActionBarBackground"] = Solid(modern ? WithAlpha(Panel, 0xE6) : Surface);
        r["ActionBarEffect"] = modern ? Shadow(Colors.Black, blur: 28, depth: 6, opacity: 0.55) : null;

        // ----- Portadas -----
        r["CoverEffect"] = modern ? Shadow(Colors.Black, blur: 16, depth: 5, opacity: 0.5) : null;

        // ----- Botón principal -----
        r["PlayButtonBrush"] = modern
            ? Frozen(new LinearGradientBrush(SteamBlue, P.PrimaryEnd, new Point(0, 0), new Point(1, 1)))
            : Solid(SteamBlue);
        r["PlayButtonHoverBrush"] = modern
            ? Frozen(new LinearGradientBrush(P.PrimaryHover, P.Primary, new Point(0, 0), new Point(1, 1)))
            : Solid(P.PrimaryHover);
        r["PlayButtonCornerRadius"] = new CornerRadius(epic ? 10 : modern ? 8 : 0);
        r["PlayButtonGlow"] = modern ? Shadow(SteamBlue, blur: 24, depth: 0, opacity: 0.85) : null;

        Current = preset;
        Changed?.Invoke(preset);
        Log.Info($"Apariencia: {(epic ? "Epic Games" : "Steam")} {(modern ? "moderna" : "optimizada")}.");
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
