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
}

/// <summary>Contenido de <c>owned-games.json</c> (caché para arrancar sin red).</summary>
public sealed class OwnedLibraryCache
{
    public string AccountName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<OwnedApp> Apps { get; set; } = [];
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

            var info = await _session.GetAppInfoAsync(appIds, ct).ConfigureAwait(false);
            var apps = new List<OwnedApp>();
            foreach (var (appId, kv) in info)
            {
                var common = kv["common"];
                var name = common["name"].Value;
                if (string.IsNullOrWhiteSpace(name)) continue;
                apps.Add(new OwnedApp
                {
                    AppId = appId,
                    Name = name.Trim(),
                    Type = (common["type"].Value ?? string.Empty).ToLowerInvariant(),
                    InstallDir = kv["config"]["installdir"].Value,
                    PublicBuildId = kv["depots"]["branches"]["public"]["buildid"].AsUnsignedInteger(),
                });
            }

            var cache = new OwnedLibraryCache
            {
                AccountName = _session.AccountName ?? string.Empty,
                UpdatedUtc = DateTimeOffset.UtcNow,
                Apps = apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            };
            lock (_gate) _cache = cache;
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
        lock (_gate) _cache = new OwnedLibraryCache();
        try { File.Delete(_cachePath); } catch (Exception) { /* no existía */ }
    }
}
