namespace Purgine.Library;

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

    /// <summary>StateFlags crudos del manifiesto (4 = instalado sin nada pendiente; 2 = actualización requerida).</summary>
    public int StateFlags { get; init; }

    /// <summary>Build instalada (campo <c>buildid</c> del manifiesto); se compara con la pública de Steam.</summary>
    public uint BuildId { get; init; }

    // Bits de EAppState de Steam relevantes para actualizaciones.
    private const int FlagUpdateRequired = 2, FlagFullyInstalled = 4, FlagUpdatePaused = 512, FlagUpdateStarted = 1024;

    /// <summary>
    /// Instalado y con una actualización pendiente, empezada o en pausa (StateFlags con los bits
    /// 2 = UpdateRequired, 512 = UpdatePaused o 1024 = UpdateStarted, además de 4 = FullyInstalled).
    /// </summary>
    public bool HasPendingUpdate =>
        IsInstalled && (StateFlags & FlagFullyInstalled) != 0 &&
        (StateFlags & (FlagUpdateRequired | FlagUpdatePaused | FlagUpdateStarted)) != 0;

    /// <summary>La actualización estaba en pausa en Steam.</summary>
    public bool IsUpdatePaused => HasPendingUpdate && (StateFlags & FlagUpdatePaused) != 0;

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
