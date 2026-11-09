using LightLauncher.Core;
using LightLauncher.Infrastructure;

namespace LightLauncher.Library;

/// <summary>
/// Detecta los juegos de Steam instalados leyendo únicamente archivos locales:
/// <list type="number">
///   <item><c>steamapps/libraryfolders.vdf</c> → todas las carpetas de biblioteca (todos los discos).</item>
///   <item><c>appmanifest_{appid}.acf</c> de cada biblioteca → AppID, nombre, carpeta, estado.</item>
///   <item><c>appcache/librarycache</c> → portadas ya descargadas por el propio cliente.</item>
/// </list>
/// No hace ninguna petición de red.
/// </summary>
public sealed class SteamLibraryScanner
{
    /// <summary>AppIDs que no son juegos (redistribuibles, herramientas).</summary>
    private static readonly HashSet<string> IgnoredAppIds =
    [
        "228980", // Steamworks Common Redistributables
        "1070560", "1391110", "1628350", // Steam Linux Runtime (no aplica en Windows)
    ];

    /// <summary>Bit "FullyInstalled" del campo StateFlags del manifiesto.</summary>
    private const int StateFullyInstalled = 4;

    private readonly SteamLocator _locator;

    public SteamLibraryScanner(SteamLocator locator) => _locator = locator;

    /// <summary>Rutas raíz de todas las bibliotecas de Steam (sin duplicados).</summary>
    public IReadOnlyList<string> GetLibraryFolders()
    {
        var steamPath = _locator.SteamPath;
        if (steamPath is null) return [];

        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            try
            {
                var full = Path.GetFullPath(raw.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar);
                if (Directory.Exists(Path.Combine(full, "steamapps")) && seen.Add(full))
                    folders.Add(full);
            }
            catch (Exception) { /* ruta inválida */ }
        }

        Add(steamPath); // la biblioteca principal siempre existe

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
            vdfPath = Path.Combine(steamPath, "config", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
            return folders;

        try
        {
            var root = VdfParser.ParseFile(vdfPath);
            var libs = root["libraryfolders"] ?? root["LibraryFolders"];
            if (libs is null) return folders;

            // Formato moderno: "0" { "path" "D:\\SteamLibrary" "apps" { ... } }
            foreach (var child in libs.Children.Values)
                Add(child.GetValue("path"));

            // Formato antiguo: "1" "D:\\SteamLibrary"
            foreach (var (key, value) in libs.Values)
                if (int.TryParse(key, out _)) Add(value);
        }
        catch (Exception ex)
        {
            Log.Error($"Error leyendo {vdfPath}", ex);
        }

        return folders;
    }

    /// <summary>Escanea todas las bibliotecas y devuelve los juegos instalados ordenados por nombre.</summary>
    public IReadOnlyList<GameEntry> Scan()
    {
        var games = new Dictionary<string, GameEntry>();
        foreach (var library in GetLibraryFolders())
        {
            var steamapps = Path.Combine(library, "steamapps");
            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"); }
            catch (Exception ex) { Log.Warn($"No se puede leer {steamapps}: {ex.Message}"); continue; }

            foreach (var manifest in manifests)
            {
                var game = ParseManifest(manifest, library);
                if (game is not null) games.TryAdd(game.AppId, game);
            }
        }

        Log.Info($"Escaneo de Steam: {games.Count} juegos.");
        return games.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Tipos de app de Steam que se muestran como juegos no instalados.</summary>
    private static readonly HashSet<string> GameTypes = new(StringComparer.OrdinalIgnoreCase) { "game" };

    /// <summary>
    /// Escaneo completo: juegos instalados + juegos de la cuenta que no están en disco, todo desde
    /// archivos locales de Steam:
    /// <list type="bullet">
    ///   <item>propiedad: <c>userdata\{id}\config\librarycache\{appid}.json</c> (Steam crea uno por cada juego
    ///         de tu biblioteca) + juegos con tiempo en <c>localconfig.vdf</c> + instalados;</item>
    ///   <item>nombre, tipo y carpeta: <c>appcache\appinfo.vdf</c>;</item>
    ///   <item>portadas: <c>appcache\librarycache</c>.</item>
    /// </list>
    /// <c>licensecache</c> está cifrado y no se toca: la lista es una aproximación local fiable, no la API oficial.
    /// </summary>
    public SteamLibrarySnapshot ScanFull()
    {
        var installed = Scan();
        var usage = ReadSteamPlaytime();
        var appInfo = SteamAppInfoReader.Read(_locator.SteamPath);
        var owned = ReadOwnedAppIds(usage);
        foreach (var g in installed) owned.Add(g.AppId);

        var installedIds = installed.Select(g => g.AppId).ToHashSet();
        var notInstalled = new List<GameEntry>();
        var unresolved = new List<string>();
        foreach (var appId in owned)
        {
            if (installedIds.Contains(appId) || IgnoredAppIds.Contains(appId)) continue;
            if (!appInfo.TryGetValue(appId, out var basics)) { unresolved.Add(appId); continue; }
            if (!GameTypes.Contains(basics.Type)) continue; // herramientas, demos, configs, betas…
            notInstalled.Add(CreateNotInstalledEntry(appId, basics.Name, basics.InstallDir));
        }

        Log.Info($"Biblioteca Steam: {installed.Count} instalados, {notInstalled.Count} no instalados, {unresolved.Count} sin nombre local.");
        return new SteamLibrarySnapshot(installed, notInstalled, owned, usage, appInfo, unresolved, GetLibraryFolders());
    }

    /// <summary>AppIDs que constan en la cuenta según la caché local (sin los instalados).</summary>
    public HashSet<string> ReadOwnedAppIds(IReadOnlyDictionary<string, SteamAppUsage> usage)
    {
        var owned = new HashSet<string>(usage.Keys);
        var userDir = SteamPlaytimeReader.FindUserDir(_locator.SteamPath);
        if (userDir is null) return owned;

        var cache = Path.Combine(userDir, "config", "librarycache");
        if (!Directory.Exists(cache)) return owned;
        try
        {
            foreach (var file in Directory.EnumerateFiles(cache, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > 0 && name.All(char.IsAsciiDigit)) owned.Add(name);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer {cache}: {ex.Message}");
        }
        return owned;
    }

    /// <summary>Entrada de un juego de la cuenta que no está en disco.</summary>
    public GameEntry CreateNotInstalledEntry(string appId, string name, string? installDirName) => new()
    {
        Id = GameEntry.MakeSteamId(appId),
        Platform = GamePlatform.Steam,
        AppId = appId,
        Name = name,
        IsInstalled = false,
        IsFullyInstalled = false,
        InstallDirName = installDirName,
        CoverImagePath = FindCoverImage(appId),
    };

    /// <summary>
    /// Tiempo jugado histórico de Steam (de <c>localconfig.vdf</c>), indexado por AppID.
    /// Incluye también juegos no instalados: sirve de señal local de propiedad para la Tienda.
    /// </summary>
    public IReadOnlyDictionary<string, SteamAppUsage> ReadSteamPlaytime() =>
        SteamPlaytimeReader.Read(_locator.SteamPath);

    /// <summary>Busca un juego concreto por AppID (lee solo los manifiestos necesarios).</summary>
    public GameEntry? FindGame(string appId)
    {
        foreach (var library in GetLibraryFolders())
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (File.Exists(manifest)) return ParseManifest(manifest, library);
        }
        return null;
    }

    /// <summary>
    /// Parsea un único <c>appmanifest_*.acf</c> (lo usa el vigilante de bibliotecas para reflejar
    /// instalaciones y progreso de descargas en vivo).
    /// </summary>
    public GameEntry? ParseManifestFile(string manifestPath)
    {
        // {biblioteca}\steamapps\appmanifest_X.acf → {biblioteca}
        var steamapps = Path.GetDirectoryName(manifestPath);
        var library = steamapps is null ? null : Path.GetDirectoryName(steamapps);
        return library is null ? null : ParseManifest(manifestPath, library);
    }

    /// <summary>Extrae el AppID del nombre de archivo <c>appmanifest_{id}.acf</c>.</summary>
    public static string? AppIdFromManifestPath(string manifestPath)
    {
        var name = Path.GetFileNameWithoutExtension(manifestPath);
        const string prefix = "appmanifest_";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : null;
    }

    private GameEntry? ParseManifest(string manifestPath, string libraryPath)
    {
        try
        {
            var state = VdfParser.ParseFile(manifestPath)["AppState"];
            if (state is null) return null;

            var appId = state.GetValue("appid");
            var name = state.GetValue("name");
            var installDirName = state.GetValue("installdir");
            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name) || IgnoredAppIds.Contains(appId))
                return null;
            if (string.Equals(installDirName, "Steamworks Shared", StringComparison.OrdinalIgnoreCase))
                return null;

            _ = int.TryParse(state.GetValue("StateFlags"), out var flags);
            _ = long.TryParse(state.GetValue("SizeOnDisk"), out var size);
            _ = long.TryParse(state.GetValue("BytesToDownload"), out var toDownload);
            _ = long.TryParse(state.GetValue("BytesDownloaded"), out var downloaded);

            return new GameEntry
            {
                Id = GameEntry.MakeSteamId(appId),
                Platform = GamePlatform.Steam,
                AppId = appId,
                Name = name,
                InstallDir = string.IsNullOrEmpty(installDirName)
                    ? null
                    : Path.Combine(libraryPath, "steamapps", "common", installDirName),
                LibraryPath = libraryPath,
                SizeOnDisk = size,
                IsFullyInstalled = (flags & StateFullyInstalled) != 0,
                BytesToDownload = toDownload,
                BytesDownloaded = downloaded,
                CoverImagePath = FindCoverImage(appId),
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Manifiesto ilegible {manifestPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Busca la portada vertical (600x900) en la caché local de Steam. Soporta el formato antiguo
    /// (<c>{appid}_library_600x900.jpg</c>) y el nuevo (<c>{appid}/[hash/]library_600x900.jpg</c>).
    /// </summary>
    private string? FindCoverImage(string appId)
    {
        var steamPath = _locator.SteamPath;
        if (steamPath is null) return null;
        var cache = Path.Combine(steamPath, "appcache", "librarycache");
        if (!Directory.Exists(cache)) return null;

        foreach (var flat in new[] { $"{appId}_library_600x900.jpg", $"{appId}_library_600x900_2x.jpg" })
        {
            var p = Path.Combine(cache, flat);
            if (File.Exists(p)) return p;
        }

        var dir = Path.Combine(cache, appId);
        if (Directory.Exists(dir))
        {
            try
            {
                foreach (var name in new[] { "library_600x900.jpg", "library_600x900_2x.jpg", "library_capsule.jpg" })
                {
                    var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit is not null) return hit;
                }
            }
            catch (Exception) { /* permisos */ }
        }

        // Último recurso: cabecera horizontal (se recorta en la tarjeta).
        var header = Path.Combine(cache, $"{appId}_header.jpg");
        if (File.Exists(header)) return header;
        var headerNew = Path.Combine(dir, "header.jpg");
        return File.Exists(headerNew) ? headerNew : null;
    }
}
