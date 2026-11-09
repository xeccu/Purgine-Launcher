using Microsoft.Win32;

namespace Purgine.Core;

/// <summary>
/// Localiza la instalación de Steam y lee su estado desde el Registro de Windows.
/// Steam publica en <c>HKCU\Software\Valve\Steam</c> la ruta de instalación, el PID activo,
/// el usuario con sesión iniciada y el AppID que se está ejecutando.
/// </summary>
public sealed class SteamLocator
{
    private const string SteamKey = @"Software\Valve\Steam";
    private string? _steamPath;

    /// <summary>Carpeta raíz de Steam (p. ej. <c>C:\Program Files (x86)\Steam</c>) o null si no está instalado.</summary>
    public string? SteamPath => _steamPath ??= ResolveSteamPath();

    /// <summary>Ruta a <c>steam.exe</c> o null.</summary>
    public string? SteamExe
    {
        get
        {
            var root = SteamPath;
            if (root is null) return null;
            var exe = Path.Combine(root, "steam.exe");
            return File.Exists(exe) ? exe : null;
        }
    }

    /// <summary>Fuerza a volver a leer la ruta (p. ej. tras instalar Steam con el launcher abierto).</summary>
    public void Refresh() => _steamPath = null;

    private static string? ResolveSteamPath()
    {
        // 1) HKCU\Software\Valve\Steam\SteamPath  (usa barras '/', hay que normalizar)
        var candidates = new[]
        {
            ReadString(Registry.CurrentUser, SteamKey, "SteamPath"),
            Path.GetDirectoryName(ReadString(Registry.CurrentUser, SteamKey, "SteamExe") ?? string.Empty),
            // 2) Fallback a HKLM (instalaciones antiguas / multiusuario)
            ReadString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            ReadString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                var normalized = Path.GetFullPath(c.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(normalized)) return normalized;
            }
            catch (Exception) { /* ruta inválida en el registro */ }
        }
        return null;
    }

    /// <summary>AppID que Steam marca como en ejecución (0 si ninguno).</summary>
    public static int GetRunningAppId() => ReadDword(Registry.CurrentUser, SteamKey, "RunningAppID");

    /// <summary>Steam marca <c>Apps\{appId}\Running = 1</c> mientras el juego está abierto.</summary>
    public static bool IsAppFlaggedRunning(string appId) =>
        ReadDword(Registry.CurrentUser, $@"{SteamKey}\Apps\{appId}", "Running") == 1;

    public static bool IsClientRunning() => ProcessUtils.IsRunning("steam");

    /// <summary>AccountID (32 bits) del usuario con sesión iniciada en Steam; 0 si no hay.</summary>
    public static int GetActiveAccountId() =>
        ReadDword(Registry.CurrentUser, $@"{SteamKey}\ActiveProcess", "ActiveUser");

    /// <summary>
    /// El cliente está "listo" cuando ha publicado su PID activo y hay un usuario con sesión iniciada.
    /// A partir de ahí acepta comandos <c>steam://</c> sin perderlos.
    /// </summary>
    public static bool IsClientReady()
    {
        var pid = ReadDword(Registry.CurrentUser, $@"{SteamKey}\ActiveProcess", "pid");
        var user = ReadDword(Registry.CurrentUser, $@"{SteamKey}\ActiveProcess", "ActiveUser");
        return pid != 0 && user != 0 && ProcessUtils.IsAlive(pid);
    }

    private static string? ReadString(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception) { return null; }
    }

    private static int ReadDword(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : 0;
        }
        catch (Exception) { return 0; }
    }
}
