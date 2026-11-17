using Purgine.Infrastructure;
using SteamKit2;

namespace Purgine.SteamNet;

/// <summary>Un juego de la cuenta según los servidores de Steam.</summary>
public sealed class OwnedApp
{
    public uint AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>"game", "dlc", "tool", "application"… (minúsculas).</summary>
    public string Type { get; set; } = string.Empty;
    public string? InstallDir { get; set; }
    /// <summary>Build actual de la rama pública (para detectar actualizaciones).</summary>
    public uint PublicBuildId { get; set; }
    /// <summary>Hash del icono pequeño (PICS common/icon) para descargarlo de la CDN si no está en disco.</summary>
    public string? Icon { get; set; }
    /// <summary>
    /// Peso del juego instalado (bytes): suma de los depots que se descargarían por defecto en este equipo según
    /// PICS (Windows, 64 bits, idioma, solo los depots de tus licencias y tus DLC no opcionales). 0 = no disponible
    /// (Steam no publica el tamaño de algún depot): nunca una cifra a medias.
    /// </summary>
    public ulong InstallSize { get; set; }
    /// <summary>Lo que se descargaría (comprimido) para esa misma selección. 0 = no disponible.</summary>
    public ulong DownloadSize { get; set; }
    /// <summary>Hash del icono .ico del juego (PICS common/clienticon), para los accesos directos.</summary>
    public string? ClientIcon { get; set; }
    /// <summary>Tiene Steam Workshop (PICS common/workshop_visible). Null = desconocido (caché antigua).</summary>
    public bool? HasWorkshop { get; set; }
    /// <summary>
    /// Tiene objetos en la Tienda de puntos: fondos, emoticonos… que salen de su colección de cromos
    /// (PICS common/category/category_29). Null = desconocido.
    /// </summary>
    public bool? HasPointsShop { get; set; }
}

/// <summary>Contenido de <c>owned-games.json</c> (caché para arrancar sin red).</summary>
public sealed class OwnedLibraryCache
{
    /// <summary>
    /// Versión del cálculo de tamaños. Las cachés anteriores a la 2 sumaban depots de otras regiones y DLC
    /// opcionales (cifras infladas): sus tamaños se descartan al cargar hasta la siguiente consulta a Steam.
    /// </summary>
    public const int CurrentVersion = 3; // 3: el peso es solo el juego base (sin DLC)
    public int Version { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<OwnedApp> Apps { get; set; } = [];
    /// <summary>Depots que incluyen las licencias de la cuenta (para no sumar las copias de otras regiones).</summary>
    public List<uint> OwnedDepots { get; set; } = [];
}

/// <summary>
/// Lista exacta de lo que posee la cuenta, consultada en los servidores de Steam (no en cachés del cliente):
/// licencias → paquetes (PICS) → apps (PICS) → nombre, tipo, carpeta de instalación y build pública.
/// El resultado se guarda en disco para mostrar la biblioteca al instante y sin conexión.
/// </summary>
public sealed class SteamOwnershipService
{
    private static readonly HashSet<uint> IgnoredApps = [7, 228980]; // cliente de Steam, redistribuibles

    private readonly SteamSession _session;
    private readonly string _cachePath;
    private OwnedLibraryCache _cache;
    private readonly object _gate = new();

    public SteamOwnershipService(SteamSession session, string cachePath)
    {
        _session = session;
        _cachePath = cachePath;
        _cache = JsonFileStore.Load<OwnedLibraryCache>(cachePath);
        if (_cache.Version < OwnedLibraryCache.CurrentVersion)
            foreach (var app in _cache.Apps) { app.InstallSize = 0; app.DownloadSize = 0; } // cifras antiguas, no fiables
        _ownedDepots = _cache.OwnedDepots.ToHashSet();
    }

    private HashSet<uint> _ownedDepots;

    /// <summary>¿Incluye alguna licencia de la cuenta este depot? (Para elegir la copia de tu región.)</summary>
    public bool OwnsDepot(uint depotId)
    {
        lock (_gate) return _ownedDepots.Contains(depotId);
    }

    /// <summary>Último listado conocido (de la caché o de la última consulta).</summary>
    public OwnedLibraryCache Cache { get { lock (_gate) return _cache; } }

    public bool HasData { get { lock (_gate) return _cache.Apps.Count > 0; } }

    public bool IsOwned(uint appId)
    {
        lock (_gate) return _cache.Apps.Any(a => a.AppId == appId);
    }

    public OwnedApp? Find(uint appId)
    {
        lock (_gate) return _cache.Apps.FirstOrDefault(a => a.AppId == appId);
    }

    /// <summary>Consulta los servidores de Steam (requiere sesión iniciada) y actualiza la caché.</summary>
    public async Task<OwnedLibraryCache?> RefreshAsync(CancellationToken ct)
    {
        if (!await _session.LoginWithSavedSessionAsync(ct).ConfigureAwait(false)) return null;
        _session.Hold();
        try
        {
            var licenses = await _session.GetLicensesAsync(ct).ConfigureAwait(false);
            var usable = licenses.Where(l =>
                (l.LicenseFlags & (ELicenseFlags.Expired | ELicenseFlags.CancelledByUser | ELicenseFlags.CancelledByAdmin |
                                   ELicenseFlags.CancelledByPartner | ELicenseFlags.PendingRefund)) == 0);

            var packages = await _session.GetPackageInfoAsync(usable.Select(l => (l.PackageID, l.AccessToken)), ct).ConfigureAwait(false);
            var appIds = packages.Values
                .SelectMany(p => p["appids"].Children.Select(c => c.AsUnsignedInteger()))
                .Where(id => id != 0 && !IgnoredApps.Contains(id))
                .Distinct()
                .ToList();

            var ownedDepots = packages.Values
                .SelectMany(p => p["depotids"].Children.Select(c => c.AsUnsignedInteger()))
                .Where(id => id != 0)
                .ToHashSet();

            var info = await _session.GetAppInfoAsync(appIds, ct).ConfigureAwait(false);
            var ownedIds = appIds.ToHashSet();
            var language = DepotSelector.SteamLanguage();
            var apps = new List<OwnedApp>();
            foreach (var (appId, kv) in info)
            {
                var common = kv["common"];
                var name = common["name"].Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                // Peso de la tarjeta: solo el juego base (depots sin DLC, en tu idioma y región). Los DLC, mapas y
                // paquetes de texturas se desglosan en el selector de componentes al instalar.
                var size = DepotSelector.DefaultSize(DepotSelector.Select(kv["depots"], language, ownedIds.Contains, ownedDepots.Contains),
                    optionalDlc: _ => true);
                apps.Add(new OwnedApp
                {
                    AppId = appId,
                    Name = name.Trim(),
                    Type = (common["type"].Value ?? string.Empty).ToLowerInvariant(),
                    InstallDir = kv["config"]["installdir"].Value,
                    PublicBuildId = kv["depots"]["branches"]["public"]["buildid"].AsUnsignedInteger(),
                    Icon = common["icon"].Value,
                    ClientIcon = common["clienticon"].Value,
                    HasWorkshop = common["workshop_visible"].Value == "1",
                    HasPointsShop = common["category"]["category_29"] != KeyValue.Invalid,
                    InstallSize = size?.Install ?? 0,
                    DownloadSize = size?.Download ?? 0,
                });
            }

            var cache = new OwnedLibraryCache
            {
                Version = OwnedLibraryCache.CurrentVersion,
                OwnedDepots = ownedDepots.Order().ToList(),
                AccountName = _session.AccountName ?? string.Empty,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Apps = apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            };
            lock (_gate) { _cache = cache; _ownedDepots = ownedDepots; }
            JsonFileStore.Save(_cachePath, cache);
            Log.Info($"Steam (red): {licenses.Count} licencias → {apps.Count} apps ({apps.Count(a => a.Type == "game")} juegos).");
            return cache;
        }
        finally
        {
            _session.Release();
        }
    }

    /// <summary>Al cerrar sesión: se borra la caché (pertenece a esa cuenta).</summary>
    public void Clear()
    {
        lock (_gate) { _cache = new OwnedLibraryCache(); _ownedDepots = []; }
        try { File.Delete(_cachePath); } catch (Exception) { /* no existía */ }
    }
}
