using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Purgine.Infrastructure;

namespace Purgine.ItchNet;

/// <summary>Juego de la cuenta de itch.io (comprado, reclamado o en una colección con clave de descarga).</summary>
public sealed record ItchOwnedGame(long GameId, long? DownloadKeyId, string Title, string Url, string? CoverUrl, string? Classification);

/// <summary>Un archivo descargable de un juego («upload»): Windows 64 bits, 32 bits, portable, Linux…</summary>
public sealed record ItchUpload(long Id, string FileName, string? DisplayName, long Size, bool Windows, bool Linux, bool Mac,
    string? Type, DateTimeOffset? Updated)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? FileName : DisplayName!;
}

/// <summary>
/// API oficial de itch.io (<c>api.itch.io</c>) con una clave de API del usuario (itch.io → Ajustes → Claves de API).
/// La clave se guarda cifrada con DPAPI; nunca se piden ni guardan contraseñas.
/// </summary>
public sealed class ItchApiClient : IDisposable
{
    private const string Api = "https://api.itch.io";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Purgine.ItchKey.v1");
    private readonly string _keyFile;
    private readonly Lazy<HttpClient> _http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Purgine/1.0");
        return http;
    });
    private string? _key;

    public ItchApiClient(string keyFile)
    {
        _keyFile = keyFile;
        _key = LoadKey();
    }

    public bool HasKey => !string.IsNullOrEmpty(_key);

    /// <summary>Nombre de la cuenta (tras comprobar la clave).</summary>
    public string? UserName { get; private set; }

    /// <summary>Guarda la clave (cifrada) tras comprobar que funciona. Devuelve null o el motivo del fallo.</summary>
    public async Task<string?> SetKeyAsync(string key, CancellationToken ct)
    {
        key = key.Trim();
        if (key.Length < 20) return "La clave de API no parece válida.";
        var previous = _key;
        _key = key;
        if (await GetProfileAsync(ct).ConfigureAwait(false) is null)
        {
            _key = previous;
            return "itch.io no aceptó la clave de API.";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_keyFile)!);
        File.WriteAllBytes(_keyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
        return null;
    }

    public void ClearKey()
    {
        _key = null;
        UserName = null;
        try { if (File.Exists(_keyFile)) File.Delete(_keyFile); }
        catch (IOException ex) { Log.Warn($"itch.io: no se pudo borrar la clave: {ex.Message}"); }
    }

    /// <summary>Perfil de la cuenta (y comprobación de la clave). Null si la clave no vale o no hay conexión.</summary>
    public async Task<string?> GetProfileAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync("/profile", ct).ConfigureAwait(false);
        if (json is not { } j || !j.TryGetProperty("user", out var user)) return null;
        UserName = user.TryGetProperty("display_name", out var dn) && dn.GetString() is { Length: > 0 } d ? d
                 : user.TryGetProperty("username", out var un) ? un.GetString() : null;
        return UserName;
    }

    /// <summary>Juegos de la cuenta (claves de descarga), todas las páginas.</summary>
    public async Task<List<ItchOwnedGame>?> GetOwnedGamesAsync(CancellationToken ct)
    {
        if (!HasKey) return null;
        var list = new List<ItchOwnedGame>();
        var seen = new HashSet<long>();
        for (var page = 1; page <= 50; page++)
        {
            var json = await GetJsonAsync($"/profile/owned-keys?page={page}", ct).ConfigureAwait(false);
            if (json is not { } j || !j.TryGetProperty("owned_keys", out var keys) || keys.ValueKind != JsonValueKind.Array) break;
            var count = 0;
            foreach (var k in keys.EnumerateArray())
            {
                count++;
                if (!k.TryGetProperty("game", out var g)) continue;
                var id = g.TryGetProperty("id", out var gid) ? gid.GetInt64() : 0;
                if (id == 0 || !seen.Add(id)) continue;
                list.Add(new ItchOwnedGame(id,
                    k.TryGetProperty("id", out var kid) ? kid.GetInt64() : null,
                    Str(g, "title") ?? $"Juego {id}",
                    Str(g, "url") ?? string.Empty,
                    Str(g, "cover_url") ?? Str(g, "still_cover_url"),
                    Str(g, "classification")));
            }
            var perPage = j.TryGetProperty("per_page", out var pp) && pp.TryGetInt32(out var n) ? n : 50;
            if (count < perPage) break;
        }
        return list;
    }

    /// <summary>Archivos descargables de un juego.</summary>
    public async Task<List<ItchUpload>> GetUploadsAsync(long gameId, long? downloadKeyId, CancellationToken ct)
    {
        var query = downloadKeyId is { } k ? $"?download_key_id={k}" : string.Empty;
        var json = await GetJsonAsync($"/games/{gameId}/uploads{query}", ct).ConfigureAwait(false);
        var list = new List<ItchUpload>();
        if (json is not { } j || !j.TryGetProperty("uploads", out var ups) || ups.ValueKind != JsonValueKind.Array) return list;
        foreach (var u in ups.EnumerateArray())
        {
            bool Has(string p) => u.TryGetProperty("traits", out var t) && t.ValueKind == JsonValueKind.Array
                                     ? t.EnumerateArray().Any(x => x.GetString() == p)
                                     : u.TryGetProperty(p, out var b) && b.ValueKind == JsonValueKind.True;
            list.Add(new ItchUpload(
                u.GetProperty("id").GetInt64(),
                Str(u, "filename") ?? "archivo",
                Str(u, "display_name"),
                u.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var size) ? size : 0,
                Has("p_windows"), Has("p_linux"), Has("p_osx"),
                Str(u, "type"),
                u.TryGetProperty("updated_at", out var up) && DateTimeOffset.TryParse(up.GetString(), out var when) ? when : null));
        }
        return list;
    }

    /// <summary>Petición de descarga de un archivo (la API redirige a la CDN; la clave no viaja a la CDN).</summary>
    public HttpRequestMessage CreateDownloadRequest(long uploadId, long? downloadKeyId)
    {
        var query = downloadKeyId is { } k ? $"?download_key_id={k}" : string.Empty;
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/uploads/{uploadId}/download{query}");
        if (_key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return req;
    }

    private async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        if (_key is null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            using var resp = await _http.Value.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!resp.IsSuccessStatusCode || doc.RootElement.TryGetProperty("errors", out _))
            {
                Log.Warn($"itch.io {path}: {(int)resp.StatusCode} {body[..Math.Min(body.Length, 160)]}");
                return null;
            }
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"itch.io {path}: {ex.Message}");
            return null;
        }
    }

    private string? LoadKey()
    {
        try
        {
            if (!File.Exists(_keyFile)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_keyFile), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            Log.Warn($"itch.io: clave guardada ilegible: {ex.Message}");
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public void Dispose()
    {
        if (_http.IsValueCreated) _http.Value.Dispose();
    }
}
