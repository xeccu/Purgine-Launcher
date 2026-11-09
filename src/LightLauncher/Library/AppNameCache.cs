using LightLauncher.Infrastructure;

namespace LightLauncher.Library;

/// <summary>Nombre y tipo de una app resueltos por la tienda (para las que no están en appinfo.vdf).</summary>
public sealed class CachedAppName
{
    public string Name { get; set; } = string.Empty;
    /// <summary>"game", "dlc"… o "unavailable" si la tienda no tiene ficha (retirado / región).</summary>
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset ResolvedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Caché en disco (<c>app-names.json</c>) de nombres resueltos por la tienda, para no repetir
/// peticiones de red en cada arranque. Las respuestas negativas se reintentan a los 30 días.
/// </summary>
public sealed class AppNameCache
{
    private static readonly TimeSpan RetryUnavailableAfter = TimeSpan.FromDays(30);
    private readonly string _path;
    private readonly Dictionary<string, CachedAppName> _items;
    private readonly object _gate = new();

    public AppNameCache(string path)
    {
        _path = path;
        _items = JsonFileStore.Load<Dictionary<string, CachedAppName>>(path);
    }

    public bool TryGet(string appId, out CachedAppName value)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(appId, out var v) &&
                (v.Type != "unavailable" || DateTimeOffset.UtcNow - v.ResolvedUtc < RetryUnavailableAfter))
            {
                value = v;
                return true;
            }
        }
        value = new CachedAppName();
        return false;
    }

    public void Set(string appId, string name, string type)
    {
        lock (_gate)
        {
            _items[appId] = new CachedAppName { Name = name, Type = type };
            JsonFileStore.Save(_path, _items);
        }
    }
}
