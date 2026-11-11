using Purgine.Infrastructure;

namespace Purgine.Library;

/// <summary>De dónde sale la carpeta de partidas de un juego.</summary>
public enum SavePathSource
{
    /// <summary>Elegida por el usuario en la ficha.</summary>
    Manual,
    /// <summary>Plantilla CloudSaveFolder del catálogo de Epic, expandida.</summary>
    EpicCatalog,
    /// <summary>Deducida buscando una carpeta con el nombre del juego en las rutas habituales.</summary>
    Detected,
}

public sealed record SavePathInfo(string Path, SavePathSource Source);

/// <summary>
/// Carpetas de partidas guardadas: las elegidas a mano (<c>save-paths.json</c>, por id de juego), las plantillas
/// de Epic (<c>{AppData}</c>, <c>{UserDir}</c>, <c>{UserSavedGames}</c>, <c>{InstallDir}</c>, <c>{EpicID}</c>…) y,
/// si no hay otra cosa, una carpeta con el nombre del juego en las rutas habituales de Windows.
/// </summary>
public sealed class SavePathResolver
{
    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, string> _manual;

    public SavePathResolver(string file)
    {
        _file = file;
        _manual = JsonFileStore.Load<Dictionary<string, string>>(file);
    }

    public string? GetManual(string gameId)
    {
        lock (_gate) return _manual.GetValueOrDefault(gameId);
    }

    /// <summary>Guarda (o borra, con null) la carpeta elegida a mano para un juego.</summary>
    public void SetManual(string gameId, string? path)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(path)) _manual.Remove(gameId);
            else _manual[gameId] = Path.GetFullPath(path);
            JsonFileStore.Save(_file, _manual);
        }
    }

    /// <summary>Carpeta de partidas del juego (la manual manda; después la del catálogo; después la deducida).</summary>
    public SavePathInfo? Resolve(GameEntry game, string? epicTemplate, string? epicAccountId)
    {
        if (GetManual(game.Id) is { } manual) return new SavePathInfo(manual, SavePathSource.Manual);
        if (!string.IsNullOrWhiteSpace(epicTemplate) && ExpandEpicTemplate(epicTemplate, game.InstallDir, epicAccountId) is { } expanded)
            return new SavePathInfo(expanded, SavePathSource.EpicCatalog);
        return Detect(game) is { } detected ? new SavePathInfo(detected, SavePathSource.Detected) : null;
    }

    /// <summary>
    /// Expande una plantilla CloudSaveFolder de Epic como Epic Games Launcher en Windows. Los marcadores no
    /// distinguen mayúsculas; uno desconocido (o {InstallDir}/{EpicID} sin dato) hace que no se pueda resolver.
    /// </summary>
    public static string? ExpandEpicTemplate(string template, string? installDir, string? accountId)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var vars = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["{appdata}"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["{userdir}"] = Path.Combine(profile, "Documents"),
            ["{userprofile}"] = profile,
            ["{usersavedgames}"] = Path.Combine(profile, "Saved Games"),
            ["{installdir}"] = installDir,
            ["{epicid}"] = accountId,
        };
        var segments = new List<string>();
        foreach (var segment in template.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('{') && segment.EndsWith('}'))
            {
                if (!vars.TryGetValue(segment, out var value) || string.IsNullOrEmpty(value)) return null;
                segments.Add(value);
            }
            else if (segment.Contains('{'))
            {
                // Marcador dentro de un segmento (p. ej. «Save_{EpicID}»).
                var s = segment;
                foreach (var (k, v) in vars)
                    if (v is not null) s = s.Replace(k, v, StringComparison.OrdinalIgnoreCase);
                if (s.Contains('{')) return null;
                segments.Add(s);
            }
            else segments.Add(segment);
        }
        if (segments.Count == 0) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine([.. segments]));
            return Path.IsPathFullyQualified(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Rutas habituales donde los juegos de Windows guardan partidas.</summary>
    public static IReadOnlyList<string> CommonRoots
    {
        get
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return
            [
                Path.Combine(profile, "Saved Games"),
                Path.Combine(docs, "My Games"),
                docs,
                local,
                Path.Combine(profile, "AppData", "LocalLow"),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ];
        }
    }

    /// <summary>
    /// Busca, a un nivel o dos de las rutas habituales, una carpeta con el nombre del juego (o su AppName de
    /// Epic). Solo es una sugerencia: la ficha la marca como «deducida» para que el usuario la compruebe.
    /// </summary>
    public static string? Detect(GameEntry game)
    {
        var names = new[] { game.Name, game.AppId }
            .Select(Normalize).Where(n => n.Length >= 4).Distinct().ToList();
        if (names.Count == 0) return null;
        foreach (var root in CommonRoots)
        {
            foreach (var dir in SafeDirs(root))
            {
                if (names.Contains(Normalize(Path.GetFileName(dir)))) return dir;
            }
            foreach (var publisher in SafeDirs(root))
                foreach (var dir in SafeDirs(publisher))
                    if (names.Contains(Normalize(Path.GetFileName(dir)))) return dir;
        }
        return null;
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
