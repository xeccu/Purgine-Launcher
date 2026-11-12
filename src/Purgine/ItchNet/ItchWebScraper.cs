using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Purgine.Infrastructure;

namespace Purgine.ItchNet;

/// <summary>Archivos de un juego leídos de su página de descargas de itch.io (flujo «No thanks, just take me to the downloads»).</summary>
public sealed record ItchWebDownloads(string GameUrl, string DownloadPageUrl, string Title, string? CoverUrl, IReadOnlyList<ItchUpload> Uploads);

/// <summary>
/// Lector de la web de itch.io para los juegos gratuitos o «paga lo que quieras» (con pantalla de donación), cuando
/// no hay clave de API o la API no da sus archivos. Hace lo mismo que el botón de la web: pide la página de
/// descargas (<c>/download_url</c> → <c>/download/&lt;token&gt;</c>), lee sus archivos (<c>data-upload_id</c>,
/// nombre, tamaño y plataformas) y, al elegir uno, pide su enlace firmado (<c>/file/&lt;id&gt;</c>). Las cookies y
/// el token CSRF son los de una sesión propia de Purgine.
/// </summary>
public sealed class ItchWebScraper
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _csrfByGame = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Clave de compra de la página de descargas (juegos de pago), como la pasa la web en <c>init_GameDownload</c>.</summary>
    private readonly Dictionary<string, string> _keyByPage = new(StringComparer.OrdinalIgnoreCase);

    public ItchWebScraper()
    {
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Purgine/1.0");
    }

    /// <summary>Página del juego normalizada (https://autor.itch.io/juego), o null si no es una página de juego.</summary>
    public static string? GameUrlOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Host.EndsWith(".itch.io", StringComparison.OrdinalIgnoreCase)) return null;
        var slug = u.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
        return string.IsNullOrEmpty(slug) ? null : $"https://{u.Host.ToLowerInvariant()}/{slug.ToLowerInvariant()}";
    }

    /// <summary>«mi-juego-genial» → «Mi Juego Genial».</summary>
    public static string PrettySlug(string slug) =>
        string.Join(' ', slug.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    public async Task<ItchWebDownloads?> GetDownloadsAsync(string gameUrl, CancellationToken ct)
    {
        var game = GameUrlOf(gameUrl) ?? gameUrl;
        var page = await GetStringAsync(game, ct).ConfigureAwait(false);
        if (page is null) return null;
        var csrf = Meta(page, "csrf_token", "name", "value");
        var title = Decode(Regex.Match(page, @"<h1[^>]*class=""[^""]*game_title[^""]*""[^>]*>(.*?)</h1>", RegexOptions.Singleline).Groups[1].Value)
                    ?? Meta(page, "og:title", "property", "content") ?? PrettySlug(game.Split('/').Last());
        var cover = Meta(page, "og:image", "property", "content");

        // La propia página puede tener ya los archivos (juegos gratis sin pantalla de donación).
        var uploads = ParseUploads(page);
        var downloadPage = game;
        if (uploads.Count == 0 && csrf is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{game}/download_url")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["csrf_token"] = csrf }),
            };
            req.Headers.Referrer = new Uri(game);
            req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            var json = await SendStringAsync(req, ct).ConfigureAwait(false);
            if (json is not null && TryJson(json, "url") is { } url)
            {
                downloadPage = url;
                var dl = await GetStringAsync(url, ct).ConfigureAwait(false);
                if (dl is not null)
                {
                    uploads = ParseUploads(dl);
                    csrf = Meta(dl, "csrf_token", "name", "value") ?? csrf;
                    var init = Regex.Match(dl, @"init_GameDownload\('[^']*',\s*(\{.*?\})\);", RegexOptions.Singleline).Groups[1].Value;
                    var key = Regex.Match(init, "\"key\":\"([^\"]+)\"");
                    if (key.Success) lock (_csrfByGame) _keyByPage[url] = Regex.Unescape(key.Groups[1].Value);
                }
            }
        }
        if (csrf is not null) lock (_csrfByGame) _csrfByGame[downloadPage] = csrf;
        return new ItchWebDownloads(game, downloadPage, title.Trim(), cover, uploads);
    }

    /// <summary>Enlace firmado (temporal) de un archivo de la página de descargas.</summary>
    public async Task<string?> ResolveFileUrlAsync(string downloadPageUrl, long uploadId, CancellationToken ct)
    {
        string? csrf;
        lock (_csrfByGame) _csrfByGame.TryGetValue(downloadPageUrl, out csrf);
        if (csrf is null) return null;
        var u = new Uri(downloadPageUrl);
        // Como el botón de la web: en la página de descargas, source=game_download (+ la clave de compra si la hay);
        // en la página del juego (gratis sin donación), source=view_game.
        var parts = u.AbsolutePath.Trim('/').Split('/');
        var slug = parts[0];
        var onDownloadPage = parts.Length >= 2 && parts[1] == "download";
        string? purchaseKey;
        lock (_csrfByGame) _keyByPage.TryGetValue(downloadPageUrl, out purchaseKey);
        var fileUrl = onDownloadPage
            ? $"https://{u.Host}/{slug}/file/{uploadId}?source=game_download&after_download_lightbox=1&as_props=1" +
              (purchaseKey is null ? string.Empty : $"&key={Uri.EscapeDataString(purchaseKey)}")
            : $"https://{u.Host}/{slug}/file/{uploadId}?source=view_game&as_props=1&after_download_lightbox=true";
        using var req = new HttpRequestMessage(HttpMethod.Post, fileUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["csrf_token"] = csrf }),
        };
        req.Headers.Referrer = u;
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var json = await SendStringAsync(req, ct).ConfigureAwait(false);
        var url = json is null ? null : TryJson(json, "url");
        if (url is null && json is not null) Log.Warn($"itch.io (web): sin enlace para el archivo {uploadId}: {json[..Math.Min(json.Length, 200)]}");
        return url;
    }

    /// <summary>Archivos de una página de descargas: cada <c>.upload</c> con <c>data-upload_id</c>.</summary>
    public static List<ItchUpload> ParseUploads(string html)
    {
        var list = new List<ItchUpload>();
        var seen = new HashSet<long>();
        foreach (Match m in Regex.Matches(html, @"data-upload_id=""(\d+)"""))
        {
            var id = long.Parse(m.Groups[1].Value);
            if (!seen.Add(id)) continue;
            // El bloque del archivo: desde el id hasta el siguiente archivo (nombre, tamaño, iconos de plataforma).
            var next = html.IndexOf("data-upload_id=", m.Index + m.Length, StringComparison.Ordinal);
            var start = Math.Max(0, html.LastIndexOf("<div class=\"upload", m.Index, StringComparison.Ordinal));
            var block = html[start..(next < 0 ? Math.Min(html.Length, m.Index + 4000) : next)];
            var name = Decode(Regex.Match(block, @"class=""name""[^>]*title=""([^""]+)""").Groups[1].Value)
                       ?? Decode(Regex.Match(block, @"<strong[^>]*class=""name""[^>]*>(.*?)</strong>", RegexOptions.Singleline).Groups[1].Value)
                       ?? $"archivo-{id}";
            var sizeText = Regex.Match(block, @"class=""file_size""[^>]*>\s*<span>([^<]+)</span>").Groups[1].Value;
            list.Add(new ItchUpload(id, name, null, ParseSize(sizeText),
                Windows: block.Contains("icon-windows", StringComparison.Ordinal),
                Linux: block.Contains("icon-tux", StringComparison.Ordinal),
                Mac: block.Contains("icon-apple", StringComparison.Ordinal),
                Type: null, Updated: null));
        }
        return list;
    }

    private static long ParseSize(string text)
    {
        var m = Regex.Match(text.Trim(), @"([\d.,]+)\s*(B|kB|KB|MB|GB)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "GB" => (long)(v * 1024 * 1024 * 1024),
            "MB" => (long)(v * 1024 * 1024),
            "KB" => (long)(v * 1024),
            _ => (long)v,
        };
    }

    private static string? Meta(string html, string key, string attr, string valueAttr)
    {
        var m = Regex.Match(html, $@"<meta[^>]*{attr}=""{Regex.Escape(key)}""[^>]*{valueAttr}=""([^""]*)""", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(html, $@"<meta[^>]*{valueAttr}=""([^""]*)""[^>]*{attr}=""{Regex.Escape(key)}""", RegexOptions.IgnoreCase);
        return m.Success ? Decode(m.Groups[1].Value) : null;
    }

    private static string? Decode(string s)
    {
        s = WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", string.Empty)).Trim();
        return s.Length == 0 ? null : s;
    }

    private static string? TryJson(string json, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(prop, out var v) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendStringAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<string?> SendStringAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"itch.io (web) {req.Method} {req.RequestUri?.AbsolutePath}: {(int)resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"itch.io (web): {ex.Message}");
            return null;
        }
    }
}
