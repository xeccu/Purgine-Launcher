namespace LightLauncher.Library;

/// <summary>Origen de un juego de la biblioteca.</summary>
public enum GamePlatform
{
    /// <summary>Juego instalado con Steam (detectado por sus manifiestos).</summary>
    Steam,
    /// <summary>Ejecutable añadido a mano por el usuario (proyectos propios, emuladores, juegos sin tienda).</summary>
    Local,
}

/// <summary>Un juego de la biblioteca, independiente de su origen.</summary>
public sealed record GameEntry
{
    /// <summary>Identificador único y estable, p. ej. <c>steam:570</c> o <c>local:3f2a…</c>.</summary>
    public required string Id { get; init; }
    public required GamePlatform Platform { get; init; }
    /// <summary>ID nativo: AppID de Steam o GUID para ejecutables locales.</summary>
    public required string AppId { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// False para juegos de la cuenta de Steam que no están en disco (no hay manifiesto).
    /// </summary>
    public bool IsInstalled { get; init; } = true;

    /// <summary>Nombre de carpeta que usará Steam al instalarlo (de appinfo.vdf). Solo juegos no instalados.</summary>
    public string? InstallDirName { get; init; }

    /// <summary>Carpeta de instalación; null si se desconoce.</summary>
    public string? InstallDir { get; init; }
    public string? LibraryPath { get; init; }
    public long SizeOnDisk { get; init; }
    /// <summary>False si Steam lo marca como descargándose / actualizándose.</summary>
    public bool IsFullyInstalled { get; init; } = true;
    /// <summary>Portada local (caché de Steam o imagen elegida por el usuario). Puede ser null.</summary>
    public string? CoverImagePath { get; init; }

    // ----- Progreso de descarga (Steam) -----
    public long BytesToDownload { get; init; }
    public long BytesDownloaded { get; init; }

    // ----- Ejecutables locales -----
    public string? ExecutablePath { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }

    /// <summary>Hay manifiesto pero la instalación no ha terminado (en cola, descargando o actualizando).</summary>
    public bool IsInstalling => IsInstalled && !IsFullyInstalled;

    /// <summary>Progreso 0..1 si hay una descarga en curso; null si no.</summary>
    public double? DownloadProgress =>
        !IsFullyInstalled && BytesToDownload > 0
            ? Math.Clamp((double)BytesDownloaded / BytesToDownload, 0, 1)
            : null;

    public static string MakeSteamId(string appId) => $"steam:{appId}";
    public static string MakeLocalId(string guid) => $"local:{guid}";
}
