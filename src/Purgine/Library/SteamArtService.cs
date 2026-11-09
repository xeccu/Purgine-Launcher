using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Purgine.Core;
using Purgine.Infrastructure;

namespace Purgine.Library;

/// <summary>
/// Arte de los juegos para la biblioteca (icono pequeño, imagen panorámica «héroe», logo e iconos de logros).
/// Primero se busca en la caché que ya mantiene el cliente de Steam (<c>appcache\librarycache</c>, sin red);
/// si no está, se descarga una vez de la CDN pública de Steam y se guarda en <c>%LocalAppData%\Purgine\art</c>.
/// Las imágenes se decodifican al tamaño en que se muestran para gastar la mínima memoria.
/// </summary>
public sealed partial class SteamArtService : IDisposable
{
    private const string StoreAssets = "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps";
    private const string CommunityImages = "https://cdn.fastly.steamstatic.com/steamcommunity/public/images/apps";

    private readonly SteamLocator _locator;
    private readonly string _cacheDir;
    private readonly Lazy<HttpClient> _http;
    private readonly SemaphoreSlim _downloadGate = new(4, 4);
    /// <summary>URLs que la CDN no tiene (404): no se vuelven a pedir en esta ejecución.</summary>
    private readonly ConcurrentDictionary<string, byte> _missing = new();

    public SteamArtService(SteamLocator locator, string cacheDir)
    {
        _locator = locator;
        _cacheDir = cacheDir;
        _http = new Lazy<HttpClient>(() =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Purgine/1.0");
            return http;
        });
    }

    [GeneratedRegex("^[0-9a-f]{40}\\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex IconFileName();

    // =====================================================================================
    // API
    // =====================================================================================

    /// <summary>Icono cuadrado del juego (lista lateral). <paramref name="iconHash"/> viene de PICS (common/icon).</summary>
    public Task<ImageSource?> LoadIconAsync(string appId, string? iconHash, int decodeWidth, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var local = FindLocalIcon(appId, iconHash);
            if (local is not null && ImageLoader.FromFile(local, decodeWidth) is { } img) return (ImageSource?)img;
            if (string.IsNullOrEmpty(iconHash)) return null;
            return await FromCacheOrCdnAsync($"{appId}_icon.jpg", $"{CommunityImages}/{appId}/{iconHash}.jpg", decodeWidth, ct)
                .ConfigureAwait(false);
        }, ct);

    /// <summary>Imagen panorámica de cabecera (library_hero, 3840x1240 en origen).</summary>
    public Task<ImageSource?> LoadHeroAsync(string appId, int decodeWidth, CancellationToken ct = default) =>
        LoadLibraryAssetAsync(appId, "library_hero.jpg", decodeWidth, ct);

    /// <summary>Logo transparente que se superpone al héroe.</summary>
    public Task<ImageSource?> LoadLogoAsync(string appId, int decodeWidth, CancellationToken ct = default) =>
        LoadLibraryAssetAsync(appId, "logo.png", decodeWidth, ct);

    /// <summary>Imagen remota arbitraria (iconos de logros), con caché en disco por nombre de archivo.</summary>
    public Task<ImageSource?> LoadRemoteAsync(string url, string cacheName, int decodeWidth, CancellationToken ct = default) =>
        Task.Run(() => FromCacheOrCdnAsync(cacheName, url, decodeWidth, ct), ct);

    // =====================================================================================
    // Búsqueda local (caché del cliente de Steam)
    // =====================================================================================

    private Task<ImageSource?> LoadLibraryAssetAsync(string appId, string fileName, int decodeWidth, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var local = FindLocalAsset(appId, fileName);
            if (local is not null && ImageLoader.FromFile(local, decodeWidth) is { } img) return (ImageSource?)img;
            return await FromCacheOrCdnAsync($"{appId}_{fileName}", $"{StoreAssets}/{appId}/{fileName}", decodeWidth, ct)
                .ConfigureAwait(false);
        }, ct);

    private string? LibraryCacheRoot()
    {
        var steam = _locator.SteamPath;
        if (steam is null) return null;
        var dir = Path.Combine(steam, "appcache", "librarycache");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// <c>librarycache\{appid}\{archivo}</c> (a veces dentro de una subcarpeta con hash) o, en clientes
    /// antiguos, <c>librarycache\{appid}_{archivo}</c>.
    /// </summary>
    private string? FindLocalAsset(string appId, string fileName)
    {
        var root = LibraryCacheRoot();
        if (root is null) return null;
        try
        {
            var dir = Path.Combine(root, appId);
            if (Directory.Exists(dir))
            {
                var direct = Path.Combine(dir, fileName);
                if (File.Exists(direct)) return direct;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var nested = Path.Combine(sub, fileName);
                    if (File.Exists(nested)) return nested;
                }
            }
            var flat = Path.Combine(root, $"{appId}_{fileName}");
            return File.Exists(flat) ? flat : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>El icono es el único .jpg con nombre de 40 hex en <c>librarycache\{appid}</c> (= hash de PICS).</summary>
    private string? FindLocalIcon(string appId, string? iconHash)
    {
        var root = LibraryCacheRoot();
        if (root is null) return null;
        try
        {
            var dir = Path.Combine(root, appId);
            if (!Directory.Exists(dir))
            {
                var flat = Path.Combine(root, $"{appId}_icon.jpg");
                return File.Exists(flat) ? flat : null;
            }
            if (!string.IsNullOrEmpty(iconHash))
            {
                var exact = Path.Combine(dir, iconHash + ".jpg");
                if (File.Exists(exact)) return exact;
            }
            return Directory.EnumerateFiles(dir, "*.jpg").FirstOrDefault(f => IconFileName().IsMatch(Path.GetFileName(f)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // =====================================================================================
    // Caché propia + CDN
    // =====================================================================================

    private async Task<ImageSource?> FromCacheOrCdnAsync(string cacheName, string url, int decodeWidth, CancellationToken ct)
    {
        var cached = Path.Combine(_cacheDir, SanitizeFileName(cacheName));
        if (File.Exists(cached) && ImageLoader.FromFile(cached, decodeWidth) is { } img) return img;
        if (_missing.ContainsKey(url)) return null;

        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var response = await _http.Value.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _missing.TryAdd(url, 0);
                return null;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var image = ImageLoader.FromBytes(bytes, decodeWidth);
            if (image is null) return null;
            try
            {
                Directory.CreateDirectory(_cacheDir);
                var tmp = cached + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                File.Move(tmp, cached, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"No se pudo guardar en caché {cacheName}: {ex.Message}");
            }
            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null; // sin red: se muestra el marcador de posición
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    public void Dispose()
    {
        if (_http.IsValueCreated) _http.Value.Dispose();
        _downloadGate.Dispose();
    }
}
