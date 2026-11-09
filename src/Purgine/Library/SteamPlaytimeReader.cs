using Purgine.Core;
using Purgine.Infrastructure;

namespace Purgine.Library;

/// <summary>Uso de un juego según el propio Steam.</summary>
/// <param name="Playtime">Tiempo total acumulado en Steam.</param>
/// <param name="LastPlayed">Última partida registrada por Steam (UTC).</param>
public sealed record SteamAppUsage(string AppId, TimeSpan Playtime, DateTimeOffset? LastPlayed);

/// <summary>
/// Lee el tiempo jugado histórico que Steam guarda en local, sin red ni API:
/// <c>{Steam}\userdata\{accountId}\config\localconfig.vdf</c> →
/// <c>UserLocalConfigStore / Software / Valve / Steam / apps / {appid} / Playtime (min) · LastPlayed (unix)</c>.
/// <para>Steam vuelca este archivo al cerrarse (y de forma periódica), así que tras una partida el
/// valor puede ir por detrás: el <c>GameTimeTracker</c> completa la diferencia con sus propias sesiones.</para>
/// </summary>
public static class SteamPlaytimeReader
{
    /// <summary>Diferencia entre un SteamID64 y el AccountID de 32 bits (nombre de carpeta de userdata).</summary>
    private const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>Lee el uso de todos los juegos del usuario de Steam actual. Vacío si no hay datos.</summary>
    public static IReadOnlyDictionary<string, SteamAppUsage> Read(string? steamPath)
    {
        var file = FindLocalConfig(steamPath);
        if (file is null) return new Dictionary<string, SteamAppUsage>();

        try
        {
            var usage = Parse(VdfParser.ParseFile(file));
            Log.Info($"Tiempo de Steam leído: {usage.Count} juegos ({file}).");
            return usage;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer {file}: {ex.Message}");
            return new Dictionary<string, SteamAppUsage>();
        }
    }

    /// <summary>Extrae los juegos con tiempo o última partida del árbol de <c>localconfig.vdf</c>.</summary>
    public static Dictionary<string, SteamAppUsage> Parse(VdfNode root)
    {
        var result = new Dictionary<string, SteamAppUsage>();
        var apps = root["UserLocalConfigStore"]?["Software"]?["Valve"]?["Steam"]?["apps"];
        if (apps is null) return result;

        foreach (var (appId, node) in apps.Children)
        {
            _ = long.TryParse(node.GetValue("Playtime"), out var minutes);
            _ = long.TryParse(node.GetValue("LastPlayed"), out var unix);
            if (minutes <= 0 && unix <= 0) continue; // entradas solo de nube / ajustes: no son partidas

            DateTimeOffset? last = unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
            result[appId] = new SteamAppUsage(appId, TimeSpan.FromMinutes(minutes), last);
        }
        return result;
    }

    /// <summary>
    /// Elige el <c>localconfig.vdf</c> del usuario correcto: el que tiene sesión iniciada ahora
    /// (Registro), si no el último que inició sesión (<c>loginusers.vdf</c>), y si no el más reciente.
    /// </summary>
    public static string? FindLocalConfig(string? steamPath)
    {
        if (steamPath is null) return null;
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return null;

        string? ConfigFor(ulong accountId)
        {
            var path = Path.Combine(userdata, accountId.ToString(), "config", "localconfig.vdf");
            return File.Exists(path) ? path : null;
        }

        var active = SteamLocator.GetActiveAccountId();
        if (active != 0 && ConfigFor((ulong)active) is { } fromActive) return fromActive;

        if (MostRecentAccountId(steamPath) is { } recent && ConfigFor(recent) is { } fromRecent) return fromRecent;

        try
        {
            return Directory.EnumerateDirectories(userdata)
                .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    /// <summary>Carpeta <c>userdata\{accountId}</c> del usuario actual de Steam, o null.</summary>
    public static string? FindUserDir(string? steamPath)
    {
        var config = FindLocalConfig(steamPath);
        return config is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(config));
    }

    /// <summary>SteamID64 del usuario actual (para el campo <c>LastOwner</c> de los manifiestos), o null.</summary>
    public static ulong? CurrentSteamId64(string? steamPath)
    {
        var dir = FindUserDir(steamPath);
        return dir is not null && ulong.TryParse(Path.GetFileName(dir), out var accountId) && accountId > 0
            ? accountId + SteamId64Base
            : null;
    }

    private static ulong? MostRecentAccountId(string steamPath)
    {
        var file = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(file)) return null;
        try
        {
            var users = VdfParser.ParseFile(file)["users"];
            if (users is null) return null;
            foreach (var (steamId64, node) in users.Children)
            {
                if (node.GetValue("MostRecent") == "1" && ulong.TryParse(steamId64, out var id) && id > SteamId64Base)
                    return id - SteamId64Base;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer loginusers.vdf: {ex.Message}");
        }
        return null;
    }
}
