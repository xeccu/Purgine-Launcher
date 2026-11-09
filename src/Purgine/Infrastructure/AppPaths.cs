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
