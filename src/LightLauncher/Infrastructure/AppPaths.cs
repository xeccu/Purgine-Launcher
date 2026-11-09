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
