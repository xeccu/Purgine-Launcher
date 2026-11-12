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
        [GamePlatform.Itch] = new(GamePlatform.Itch, "itch.io", "ITCH", Frozen("#E6FA5C5C")),
        [GamePlatform.Local] = new(GamePlatform.Local, "Local", "LOCAL", Frozen("#CC0E1014")),
    };

    /// <summary>Todas las plataformas de <see cref="GamePlatform"/>, con los ejecutables locales al final.</summary>
    public static IReadOnlyList<PlatformInfo> All { get; } =
        Enum.GetValues<GamePlatform>().Select(Get).OrderBy(p => p.Platform == GamePlatform.Local).ToList();

    public static PlatformInfo Get(GamePlatform platform) =>
        Platforms.TryGetValue(platform, out var info) ? info : new(platform, platform.ToString(), platform.ToString().ToUpperInvariant(), Frozen("#CC0E1014"));

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
