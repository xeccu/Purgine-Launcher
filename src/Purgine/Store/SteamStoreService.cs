using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Purgine.Infrastructure;
using Purgine.Settings;

namespace Purgine.Store;

/// <summary>
/// Cliente de solo lectura de los endpoints JSON públicos de la tienda de Steam
/// (los mismos que usa la web). Sin API key, sin cuenta, sin cookies: solo búsqueda, ofertas y fichas.
/// La compra e instalación se delegan al cliente oficial mediante <c>steam://</c>.
/// <para>El <see cref="HttpClient"/> se crea perezosamente: si el usuario nunca abre la Tienda,
/// no se carga la pila de red.</para>
/// </summary>
public sealed partial class SteamStoreService : IDisposable
{
    private const string StoreBase = "https://store.steampowered.com";
    private readonly LauncherSettings _settings;
    private HttpClient? _http;

    public SteamStoreService(LauncherSettings settings) => _settings = settings;

    private HttpClient Http => _http ??= CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30), // suelta sockets pronto
            UseCookies = false,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Purgine/0.2 (+open source)");
        return client;
    }

    /// <summary>País para precios: el de ajustes o el de la región de Windows.</summary>
    public string CountryCode
    {
        get
        {
            var cc = _settings.StoreCountryCode?.Trim();
            if (!string.IsNullOrEmpty(cc) && cc.Length == 2) return cc.ToUpperInvariant();
            try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
            catch (Exception) { return "US"; }
        }
    }

    private static string Language => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "es" => "spanish",
        "pt" => "portuguese",
        "fr" => "french",
        "de" => "german",
        "it" => "italian",
        _ => "english",
    };

    /// <summary>Búsqueda por nombre (endpoint <c>/api/storesearch</c>).</summary>
    public async Task<IReadOnlyList<StoreItem>> SearchAsync(string term, CancellationToken ct)
    {
        var url = $"{StoreBase}/api/storesearch/?term={Uri.EscapeDataString(term)}&l={Language}&cc={CountryCode}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<StoreItem>();
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var it in items.EnumerateArray())
        {
            if (Str(it, "type") is { } type && type != "app") continue;
            var id = it.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            var name = Str(it, "name");
            if (id is null || name is null) continue;

            string? price = null, original = null;
            int discount = 0;
            bool free = true;
            if (it.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                free = false;
                var currency = Str(p, "currency") ?? "EUR";
                var final = Long(p, "final");
                var initial = Long(p, "initial");
                price = FormatPrice(final, currency);
                if (initial > final && initial > 0)
                {
                    original = FormatPrice(initial, currency);
                    discount = (int)Math.Round(100.0 * (initial - final) / initial);
                }
            }

            list.Add(new StoreItem(id, name, Str(it, "tiny_image"), free ? "Gratis / sin precio" : price, original, discount, free));
        }
        return list;
    }

    /// <summary>Ofertas destacadas (endpoint <c>/api/featuredcategories</c>, sección <c>specials</c>).</summary>
    public async Task<IReadOnlyList<StoreItem>> GetSpecialsAsync(CancellationToken ct)
    {
        var url = $"{StoreBase}/api/featuredcategories?l={Language}&cc={CountryCode}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<StoreItem>();
        var seen = new HashSet<string>();
        if (!doc.RootElement.TryGetProperty("specials", out var specials) ||
            !specials.TryGetProperty("items", out var items))
            return list;

        foreach (var it in items.EnumerateArray())
        {
            var id = it.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            var name = Str(it, "name");
            if (id is null || name is null || !seen.Add(id)) continue;

            var currency = Str(it, "currency") ?? "EUR";
            var final = Long(it, "final_price");
            var original = Long(it, "original_price");
            var discount = (int)Long(it, "discount_percent");
            list.Add(new StoreItem(
                id, name,
                Str(it, "small_capsule_image") ?? Str(it, "header_image"),
                final > 0 ? FormatPrice(final, currency) : "Gratis",
                original > final ? FormatPrice(original, currency) : null,
                discount,
                final == 0));
        }
        return list;
    }

    /// <summary>Ficha de producto (endpoint <c>/api/appdetails</c>).</summary>
    public async Task<StoreAppDetails?> GetDetailsAsync(string appId, CancellationToken ct)
    {
        var url = $"{StoreBase}/api/appdetails?appids={Uri.EscapeDataString(appId)}&l={Language}&cc={CountryCode}";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty(appId, out var wrapper) ||
            !wrapper.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True ||
            !wrapper.TryGetProperty("data", out var d))
            return null;

        string? price = null, original = null;
        int discount = 0;
        var isFree = d.TryGetProperty("is_free", out var freeEl) && freeEl.ValueKind == JsonValueKind.True;
        if (d.TryGetProperty("price_overview", out var po))
        {
            price = Str(po, "final_formatted");
            discount = (int)Long(po, "discount_percent");
            if (discount > 0) original = Str(po, "initial_formatted");
        }

        bool comingSoon = false;
        string? release = null;
        if (d.TryGetProperty("release_date", out var rd))
        {
            comingSoon = rd.TryGetProperty("coming_soon", out var cs) && cs.ValueKind == JsonValueKind.True;
            release = Str(rd, "date");
        }

        int? metacritic = d.TryGetProperty("metacritic", out var mc) && mc.TryGetProperty("score", out var sc) && sc.TryGetInt32(out var score)
            ? score : null;
        bool windows = !d.TryGetProperty("platforms", out var pl) ||
                       (pl.TryGetProperty("windows", out var w) && w.ValueKind == JsonValueKind.True);

        return new StoreAppDetails(
            appId,
            Str(d, "name") ?? $"App {appId}",
            Str(d, "type") ?? "game",
            CleanHtml(Str(d, "short_description")),
            Str(d, "header_image"),
            isFree ? "Gratis" : price,
            original,
            discount,
            isFree,
            comingSoon,
            release,
            JoinStrings(d, "developers"),
            JoinStrings(d, "publishers"),
            JoinDescriptions(d, "genres"),
            metacritic,
            windows);
    }

    /// <summary>Descarga una imagen (portada/cápsula). Devuelve null si falla.</summary>
    public async Task<byte[]?> GetImageBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            return await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Libera sockets y buffers cuando la app se oculta en la bandeja.</summary>
    public void ReleaseNetworkResources()
    {
        _http?.Dispose();
        _http = null;
    }

    public void Dispose() => ReleaseNetworkResources();

    // ------------------------------------------------------------------------------------

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == (HttpStatusCode)429)
            throw new HttpRequestException("La tienda de Steam está limitando peticiones. Espera unos segundos.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string FormatPrice(long cents, string currency)
    {
        var amount = (cents / 100m).ToString("N2", CultureInfo.CurrentCulture);
        return currency switch
        {
            "EUR" => $"{amount} €",
            "USD" => $"${amount}",
            "GBP" => $"£{amount}",
            _ => $"{amount} {currency}",
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s) ? s : 0;
    }

    private static string? JoinStrings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? string.Join(", ", arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()))
            : null;

    private static string? JoinDescriptions(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? string.Join(", ", arr.EnumerateArray().Select(x => Str(x, "description")).Where(x => x is not null))
            : null;

    private static string? CleanHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = HtmlTag().Replace(html, " ");
        return WebUtility.HtmlDecode(MultiSpace().Replace(text, " ")).Trim();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
