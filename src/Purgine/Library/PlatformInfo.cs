using System.Windows.Media;

namespace Purgine.Library;

/// <summary>Presentación de una plataforma: nombre, distintivo corto y color del distintivo.</summary>
public sealed record PlatformInfo(GamePlatform Platform, string DisplayName, string Badge, Brush BadgeBrush);

/// <summary>
/// Registro de plataformas de la biblioteca. Añadir un launcher nuevo (GOG, Ubisoft, EA…) es añadir un valor a
/// <see cref="GamePlatform"/> y una entrada aquí; ninguna plataforma tiene prioridad sobre las demás.
/// </summary>
public static class PlatformRegistry
{
    private static readonly Dictionary<GamePlatform, PlatformInfo> Platforms = new()
    {
        [GamePlatform.Steam] = new(GamePlatform.Steam, "Steam", "STEAM", Frozen("#CC10263F")),
        [GamePlatform.Epic] = new(GamePlatform.Epic, "Epic Games", "EPIC", Frozen("#E6202020")),
        [GamePlatform.Local] = new(GamePlatform.Local, "Local", "LOCAL", Frozen("#CC0E1014")),
    };

    public static PlatformInfo Get(GamePlatform platform) =>
        Platforms.TryGetValue(platform, out var info) ? info : new(platform, platform.ToString(), platform.ToString().ToUpperInvariant(), Frozen("#CC0E1014"));

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
