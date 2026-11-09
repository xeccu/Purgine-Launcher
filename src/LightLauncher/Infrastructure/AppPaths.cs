namespace LightLauncher.Infrastructure;

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
    public static string AppNamesFile => Path.Combine(DataDir, "app-names.json");
    /// <summary>Sesión de Steam (refresh token) cifrada con DPAPI. Nunca contiene la contraseña.</summary>
    public static string SteamSessionFile => Path.Combine(DataDir, "steam-session.bin");
    /// <summary>Juegos de la cuenta según los servidores de Steam (caché para arrancar sin red).</summary>
    public static string OwnedGamesFile => Path.Combine(DataDir, "owned-games.json");
    /// <summary>Cola de descargas pendientes (se recupera en pausa al reiniciar).</summary>
    public static string DownloadQueueFile => Path.Combine(DataDir, "download-queue.json");
    public static string LogFile => Path.Combine(DataDir, "lightlauncher.log");

    public static void EnsureCreated() => Directory.CreateDirectory(DataDir);

    private static string ResolveDataDir()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.txt")))
            return Path.Combine(exeDir, "data");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LightLauncher");
    }
}
