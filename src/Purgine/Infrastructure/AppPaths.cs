namespace Purgine.Infrastructure;

/// <summary>
/// Rutas de almacenamiento local. Todo vive en la máquina del usuario; no hay servidores intermedios.
/// Modo portable: si existe un archivo <c>portable.txt</c> junto al ejecutable, los datos se guardan
/// en la subcarpeta <c>data</c> al lado del .exe en vez de en %LocalAppData%.
/// </summary>
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string PlaytimeFile => Path.Combine(DataDir, "playtime.json");
    public static string LibraryFile => Path.Combine(DataDir, "library.json");
    public static string CoversDir => Path.Combine(DataDir, "covers");
    /// <summary>Sesión de Steam (refresh token) cifrada con DPAPI. Nunca contiene la contraseña.</summary>
    public static string SteamSessionFile => Path.Combine(DataDir, "steam-session.bin");
    /// <summary>Juegos de la cuenta según los servidores de Steam (caché para arrancar sin red).</summary>
    public static string OwnedGamesFile => Path.Combine(DataDir, "owned-games.json");
    /// <summary>Cola de descargas pendientes (se recupera en pausa al reiniciar).</summary>
    public static string DownloadQueueFile => Path.Combine(DataDir, "download-queue.json");
    public static string LogFile => Path.Combine(DataDir, "purgine.log");
    /// <summary>Refresh token de Epic Games cifrado con DPAPI (nunca la contraseña).</summary>
    public static string EpicSessionFile => Path.Combine(DataDir, "epic-session.bin");
    /// <summary>Tokens de propiedad temporales de Epic (juegos con DRM de Epic).</summary>
    /// <summary>Juegos de Epic instalados o actualizados por Purgine.</summary>
    public static string EpicInstallsFile => Path.Combine(DataDir, "epic-installs.json");
    /// <summary>Cola de descargas de Epic.</summary>
    public static string EpicQueueFile => Path.Combine(DataDir, "epic-download-queue.json");
    /// <summary>Caché de builds y tamaños de Epic (para no bajar manifiestos en cada arranque).</summary>
    public static string EpicBuildsFile => Path.Combine(DataDir, "epic-builds.json");
    /// <summary>Carpetas de partidas elegidas a mano en la ficha (id de juego → ruta).</summary>
    public static string SavePathsFile => Path.Combine(DataDir, "save-paths.json");
    /// <summary>Copias comprimidas de las partidas (antes de bajar de la nube y al cerrar cada juego).</summary>
    /// <summary>Estado de la última sincronización con la nube de Epic de cada juego (para detectar conflictos).</summary>
    public static string CloudSyncFile => Path.Combine(DataDir, "cloud-sync.json");
    /// <summary>Copias de localconfig.vdf de Steam antes de anotar un contrato aceptado.</summary>
    public static string SteamConfigBackupDir => Path.Combine(DataDir, "steam-config-backups");
    public static string SaveBackupsDir => Path.Combine(DataDir, "save-backups");
    public static string EpicOwnershipDir => Path.Combine(DataDir, "epic-ovt");
    /// <summary>Notas y reseñas personales por juego (solo locales).</summary>
    public static string GameNotesFile => Path.Combine(DataDir, "game-notes.json");
    /// <summary>Petición de jugar de una segunda instancia (acceso directo con --run) a la que ya está abierta.</summary>
    public static string LaunchRequestFile => Path.Combine(DataDir, "launch-request.txt");
    /// <summary>Caché de arte de Steam que no estaba en el disco (iconos, héroes, logos, logros).</summary>
    public static string ArtCacheDir => Path.Combine(DataDir, "art");
    /// <summary>Perfil del navegador de la tienda (WebView2): cookies, caché y sesión web de Steam.</summary>
    public static string WebViewDir => Path.Combine(DataDir, "webview");

    public static void EnsureCreated() => Directory.CreateDirectory(DataDir);

    private static string ResolveDataDir()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.txt")))
            return Path.Combine(exeDir, "data");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(localAppData, "Purgine");
        MigrateLegacyDataDir(Path.Combine(localAppData, "LightLauncher"), current);
        return current;
    }

    /// <summary>
    /// La app se llamó «LightLauncher» durante su desarrollo. Si existe esa carpeta de datos y todavía no hay
    /// una de Purgine, se mueve entera (ajustes, etiquetas, tiempo jugado, caché de la biblioteca y la sesión de
    /// Steam, que sigue siendo válida: DPAPI cifra por usuario de Windows, no por ruta). Si no se puede mover
    /// (p. ej. otra instancia antigua abierta), se sigue usando la carpeta antigua para no perder nada.
    /// </summary>
    private static void MigrateLegacyDataDir(string legacy, string current)
    {
        try
        {
            if (Directory.Exists(current) || !Directory.Exists(legacy)) return;
            Directory.Move(legacy, current);

            var oldLog = Path.Combine(current, "lightlauncher.log");
            var newLog = Path.Combine(current, "purgine.log");
            if (File.Exists(oldLog) && !File.Exists(newLog)) File.Move(oldLog, newLog);
        }
        catch (Exception)
        {
            // El registro todavía no está inicializado; el siguiente arranque lo vuelve a intentar.
        }
    }
}
