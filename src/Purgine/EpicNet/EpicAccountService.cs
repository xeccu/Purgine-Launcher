using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Purgine.Infrastructure;

namespace Purgine.EpicNet;

/// <summary>Sesión de Epic recordada. Nunca contiene la contraseña: solo el refresh token que emite Epic.</summary>
public sealed class SavedEpicSession
{
    public string AccountId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset RefreshExpiresUtc { get; set; }
}

/// <summary>Arte y datos de un artículo del catálogo de Epic.</summary>
public sealed record EpicCatalogInfo(string Title, string? CoverUrl, string? HeroUrl, string? LogoUrl, bool RequiresOwnershipToken);

/// <summary>Credenciales de un lanzamiento directo (sin Epic Games Launcher).</summary>
public sealed record EpicLaunchAuth(string AccountId, string DisplayName, string ExchangeCode, string? OwnershipTokenPath);

/// <summary>
/// Cuenta de Epic Games en Purgine, sin el cliente oficial (mismo enfoque que Legendary / Heroic):
/// <list type="bullet">
///   <item>Inicio de sesión en la página oficial de Epic (ventana con WebView2: el usuario escribe sus datos
///         allí) → código de autorización → tokens OAuth. La contraseña nunca pasa por Purgine.</item>
///   <item>El refresh token se guarda cifrado con DPAPI en <c>epic-session.bin</c>; se renueva solo.</item>
///   <item>Lanzar: código de intercambio de un solo uso (y, si el juego lo pide, token de propiedad) que se
///         pasa al ejecutable del juego, igual que hace Epic Games Launcher.</item>
///   <item>Tiempo jugado de la cuenta y arte del catálogo para la biblioteca.</item>
/// </list>
/// Epic no da soporte oficial a clientes de terceros: se usan los mismos endpoints que su launcher.
/// </summary>
public sealed class EpicAccountService : IDisposable
{
    // Cliente OAuth del launcher de Epic (público; es el que usan Legendary y Heroic).
    private const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string ClientSecret = "daafbccc737745039dffe53d94fc76cf";
    private const string OAuthHost = "https://account-public-service-prod03.ol.epicgames.com";
    private const string LibraryHost = "https://library-service.live.use1a.on.epicgames.com";
    private const string CatalogHost = "https://catalog-public-service-prod06.ol.epicgames.com";
    private const string EcommerceHost = "https://ecommerceintegration-public-service-ecomprod02.ol.epicgames.com";

    /// <summary>Página oficial de inicio de sesión; al terminar, Epic muestra un JSON con el código de autorización.</summary>
    public const string LoginUrl = "https://www.epicgames.com/id/login?lang=es-ES&redirectUrl=" +
        "https%3A%2F%2Fwww.epicgames.com%2Fid%2Fapi%2Fredirect%3FclientId%3D" + ClientId + "%26responseType%3Dcode";

    /// <summary>Prefijo de la página de redirección con el código (fin del inicio de sesión).</summary>
    public const string RedirectPrefix = "https://www.epicgames.com/id/api/redirect";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Purgine.EpicSession.v1");

    private readonly string _path;
    private readonly string _ovtDir;
    private readonly Lazy<HttpClient> _http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Purgine/1.0");
        return http;
    });
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessExpiresUtc;

    public EpicAccountService(string sessionPath, string ownershipTokenDir)
    {
        _path = sessionPath;
        _ovtDir = ownershipTokenDir;
        var saved = Load();
        DisplayName = saved?.DisplayName;
        AccountId = saved?.AccountId;
    }

    public bool HasSession => File.Exists(_path) && AccountId is not null;
    public string? DisplayName { get; private set; }
    public string? AccountId { get; private set; }

    /// <summary>Cambió la sesión (inicio o cierre).</summary>
    public event Action? SessionChanged;

    // =====================================================================================
    // Sesión
    // =====================================================================================

    /// <summary>Canjea el código de autorización de la página de inicio de sesión por tokens y guarda la sesión.</summary>
    public async Task LoginWithAuthorizationCodeAsync(string code, CancellationToken ct)
    {
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["token_type"] = "eg1",
        }, ct).ConfigureAwait(false);
        Log.Info($"Epic: sesión iniciada como {DisplayName}.");
        SessionChanged?.Invoke();
        _ = token;
    }

    public async Task LogOutAsync()
    {
        try
        {
            if (_accessToken is { } at)
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{OAuthHost}/account/api/oauth/sessions/kill/{at}");
                req.Headers.Authorization = new AuthenticationHeaderValue("bearer", at);
                using var _ = await _http.Value.SendAsync(req).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* sin red: se borra igualmente */ }
        _accessToken = null;
        try { File.Delete(_path); } catch (Exception) { /* no existía */ }
        DisplayName = AccountId = null;
        Log.Info("Epic: sesión cerrada.");
        SessionChanged?.Invoke();
    }

    /// <summary>Token de acceso válido (renovándolo con el refresh token si hace falta). Null si no hay sesión.</summary>
    private async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpiresUtc - TimeSpan.FromMinutes(2)) return _accessToken;
            var saved = Load();
            if (saved is null || string.IsNullOrEmpty(saved.RefreshToken)) return null;
            if (saved.RefreshExpiresUtc != default && saved.RefreshExpiresUtc < DateTimeOffset.UtcNow)
            {
                Log.Warn("Epic: la sesión guardada ha caducado.");
                return null;
            }
            try
            {
                await RequestTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = saved.RefreshToken,
                    ["token_type"] = "eg1",
                }, ct).ConfigureAwait(false);
                return _accessToken;
            }
            catch (EpicApiException ex) when (ex.IsAuthError)
            {
                Log.Warn($"Epic: la sesión ya no es válida ({ex.Message}); hay que volver a iniciar sesión.");
                try { File.Delete(_path); } catch (Exception) { /* idem */ }
                DisplayName = AccountId = null;
                SessionChanged?.Invoke();
                return null;
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<JsonElement> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{OAuthHost}/account/api/oauth/token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}")));
        var json = await SendJsonAsync(req, ct).ConfigureAwait(false);

        _accessToken = json.GetProperty("access_token").GetString();
        _accessExpiresUtc = json.TryGetProperty("expires_at", out var e) && e.TryGetDateTimeOffset(out var exp) ? exp : DateTimeOffset.UtcNow.AddHours(2);
        AccountId = json.GetProperty("account_id").GetString();
        DisplayName = json.TryGetProperty("displayName", out var d) ? d.GetString() : DisplayName;
        Save(new SavedEpicSession
        {
            AccountId = AccountId ?? string.Empty,
            DisplayName = DisplayName ?? string.Empty,
            RefreshToken = json.GetProperty("refresh_token").GetString() ?? string.Empty,
            RefreshExpiresUtc = json.TryGetProperty("refresh_expires_at", out var r) && r.TryGetDateTimeOffset(out var rexp) ? rexp : default,
        });
        return json;
    }

    // =====================================================================================
    // Lanzamiento directo
    // =====================================================================================

    /// <summary>
    /// Credenciales para lanzar un juego sin Epic Games Launcher: código de intercambio de un solo uso y, si el
    /// juego lo exige (DRM de Epic), el token de propiedad en un archivo temporal. Null si no hay sesión.
    /// </summary>
    public async Task<EpicLaunchAuth?> GetLaunchAuthAsync(string? catalogNamespace, string? catalogItemId, CancellationToken ct)
    {
        var token = await AccessTokenAsync(ct).ConfigureAwait(false);
        if (token is null || AccountId is null) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{OAuthHost}/account/api/oauth/exchange");
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
        var json = await SendJsonAsync(req, ct).ConfigureAwait(false);
        var code = json.GetProperty("code").GetString() ?? throw new EpicApiException("Epic no devolvió el código de intercambio.", false);

        string? ovt = null;
        if (catalogNamespace is not null && catalogItemId is not null &&
            (await GetCatalogInfoAsync(catalogNamespace, catalogItemId, ct).ConfigureAwait(false))?.RequiresOwnershipToken == true)
        {
            ovt = await GetOwnershipTokenAsync(token, catalogNamespace, catalogItemId, ct).ConfigureAwait(false);
        }
        return new EpicLaunchAuth(AccountId, DisplayName ?? string.Empty, code, ovt);
    }

    private async Task<string?> GetOwnershipTokenAsync(string token, string ns, string itemId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{EcommerceHost}/ecommerceintegration/api/public/platforms/EPIC/identities/{AccountId}/ownershipToken")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["nsCatalogItemId"] = $"{ns}:{itemId}" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
        using var resp = await _http.Value.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) { Log.Warn($"Epic: token de propiedad no disponible ({(int)resp.StatusCode})."); return null; }
        Directory.CreateDirectory(_ovtDir);
        var path = Path.Combine(_ovtDir, $"{ns}{itemId}.ovt");
        await File.WriteAllBytesAsync(path, await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        return path;
    }

    // =====================================================================================
    // Tiempo jugado y catálogo
    // =====================================================================================

    /// <summary>Tiempo jugado registrado en la cuenta de Epic, por AppName.</summary>
    public async Task<Dictionary<string, TimeSpan>?> GetPlaytimeAsync(CancellationToken ct)
    {
        var token = await AccessTokenAsync(ct).ConfigureAwait(false);
        if (token is null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{LibraryHost}/library/api/public/playtime/account/{AccountId}/all");
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
        var json = await SendJsonAsync(req, ct).ConfigureAwait(false);
        var map = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in json.EnumerateArray())
        {
            if (item.TryGetProperty("artifactId", out var a) && a.GetString() is { } app &&
                item.TryGetProperty("totalTime", out var t) && t.TryGetInt64(out var seconds))
                map[app] = TimeSpan.FromSeconds(seconds);
        }
        return map;
    }

    private readonly Dictionary<string, EpicCatalogInfo?> _catalog = new();

    /// <summary>Título, arte y si exige token de propiedad, del catálogo de Epic (se recuerda en memoria).</summary>
    public async Task<EpicCatalogInfo?> GetCatalogInfoAsync(string ns, string itemId, CancellationToken ct)
    {
        var key = $"{ns}:{itemId}";
        lock (_catalog) if (_catalog.TryGetValue(key, out var cached)) return cached;
        var token = await AccessTokenAsync(ct).ConfigureAwait(false);
        if (token is null) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{CatalogHost}/catalog/api/shared/namespace/{ns}/bulk/items?id={itemId}&includeDLCDetails=false&includeMainGameDetails=false&country=ES&locale=es-ES");
        req.Headers.Authorization = new AuthenticationHeaderValue("bearer", token);
        EpicCatalogInfo? info = null;
        try
        {
            var json = await SendJsonAsync(req, ct).ConfigureAwait(false);
            if (json.TryGetProperty(itemId, out var item))
            {
                string? Img(params string[] types)
                {
                    if (!item.TryGetProperty("keyImages", out var imgs)) return null;
                    foreach (var type in types)
                        foreach (var img in imgs.EnumerateArray())
                            if (img.GetProperty("type").GetString() == type) return img.GetProperty("url").GetString();
                    return null;
                }
                var ovt = item.TryGetProperty("customAttributes", out var attrs) &&
                          attrs.TryGetProperty("OwnershipToken", out var o) &&
                          o.TryGetProperty("value", out var v) && v.GetString() == "true";
                info = new EpicCatalogInfo(
                    item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                    Img("DieselGameBoxTall", "OfferImageTall", "Thumbnail"),
                    Img("DieselGameBox", "OfferImageWide", "DieselStoreFrontWide"),
                    Img("DieselGameBoxLogo"),
                    ovt);
            }
        }
        catch (EpicApiException ex)
        {
            Log.Warn($"Epic: catálogo de {key} no disponible: {ex.Message}");
        }
        lock (_catalog) _catalog[key] = info;
        return info;
    }

    // =====================================================================================

    private async Task<JsonElement> SendJsonAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.Value.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new EpicApiException($"Sin conexión con Epic: {ex.Message}", false); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string? error = null;
                try { using var e = JsonDocument.Parse(body); error = e.RootElement.TryGetProperty("errorMessage", out var m) ? m.GetString() : null; }
                catch (JsonException) { /* cuerpo no JSON */ }
                var auth = resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest
                           && req.RequestUri?.AbsolutePath.Contains("/oauth/token") == true;
                throw new EpicApiException(error ?? $"Epic respondió {(int)resp.StatusCode}.", auth || resp.StatusCode == System.Net.HttpStatusCode.Unauthorized);
            }
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
    }

    private SavedEpicSession? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<SavedEpicSession>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            Log.Warn($"Sesión de Epic ilegible: {ex.Message}");
            return null;
        }
    }

    private void Save(SavedEpicSession session)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Dispose()
    {
        if (_http.IsValueCreated) _http.Value.Dispose();
        _tokenGate.Dispose();
    }
}

/// <summary>Error de la API de Epic. <see cref="IsAuthError"/>: la sesión ya no vale.</summary>
public sealed class EpicApiException(string message, bool isAuthError) : Exception(message)
{
    public bool IsAuthError { get; } = isAuthError;
}
