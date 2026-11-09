namespace LightLauncher.Library;

/// <summary>Resultado de un escaneo completo de la biblioteca de Steam.</summary>
/// <param name="Installed">Juegos con manifiesto en alguna biblioteca.</param>
/// <param name="NotInstalled">Juegos de la cuenta sin manifiesto (con nombre conocido y tipo "game").</param>
/// <param name="Owned">Todos los AppIDs que constan en la cuenta (instalados o no, de cualquier tipo).</param>
/// <param name="Usage">Tiempo jugado de Steam por AppID.</param>
/// <param name="AppInfo">Nombre/tipo/carpeta por AppID (appinfo.vdf).</param>
/// <param name="UnresolvedAppIds">AppIDs propios sin nombre en la caché local (se resuelven por la tienda).</param>
/// <param name="LibraryFolders">Raíces de biblioteca (para el vigilante de manifiestos).</param>
public sealed record SteamLibrarySnapshot(
    IReadOnlyList<GameEntry> Installed,
    IReadOnlyList<GameEntry> NotInstalled,
    IReadOnlySet<string> Owned,
    IReadOnlyDictionary<string, SteamAppUsage> Usage,
    IReadOnlyDictionary<string, SteamAppBasics> AppInfo,
    IReadOnlyList<string> UnresolvedAppIds,
    IReadOnlyList<string> LibraryFolders);
