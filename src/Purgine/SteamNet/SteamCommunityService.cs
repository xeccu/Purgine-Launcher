using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Purgine.Infrastructure;
using SteamKit2.Internal;

namespace Purgine.SteamNet;

/// <summary>Un amigo que ha jugado a un juego.</summary>
public sealed record FriendPlay(ulong SteamId, string Name, string AvatarUrl, string Status, uint MinutesTotal, bool IsRecent);

/// <summary>Amigos y un juego: quién juega ahora, quién jugó hace poco y quién lo ha jugado alguna vez.</summary>
public sealed record FriendsForApp(IReadOnlyList<FriendPlay> Friends, int InGame, int Recent, int Ever, int Owners);

/// <summary>Un cromo del juego: obtenido (con cuántos) o pendiente.</summary>
public sealed record TradingCard(string Name, string IconUrl, int Owned);

/// <summary>Cromos del juego: la colección normal (no brillante), cuántos tienes y el nivel de insignia.</summary>
public sealed record TradingCardSet(IReadOnlyList<TradingCard> Cards, int BadgeLevel, bool SetKnown);

/// <summary>Elemento del feed de comunidad (guía o noticia/anuncio).</summary>
public sealed record CommunityItem(string Kind, string Title, string Subtitle, string Url);

/// <summary>
/// Datos sociales de la ficha de un juego, leídos de Steam con la sesión de Purgine (SteamKit2) y de endpoints
/// públicos de Steam. Todo es de solo lectura: no se publica nada ni cambia el estado en línea del usuario.
/// <list type="bullet">
///   <item>Amigos: servicio Player.GetFriendsGameplayInfo + nombres y avatares (PersonaState).</item>
///   <item>Cromos: colección completa del Mercado de la Comunidad (público), los tuyos del inventario
///         (servicio Econ, appid 753 / contexto 6) y el nivel de insignia (Player.GetGameBadgeLevels).</item>
///   <item>Comunidad: guías más populares (PublishedFile.QueryFiles) y últimas noticias/anuncios
///         (ISteamNews, público).</item>
/// </list>
/// Los resultados se guardan 10 minutos en memoria para no repetir consultas al volver a una ficha.
/// </summary>
public sealed class SteamCommunityService : IDisposable
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private const uint SteamCommunityApp = 753;
    private const ulong CommunityContext = 6;
    private const uint AllGuidesFileType = 10; // k_PFI_MatchingFileType_AllGuides

    private readonly SteamSession _session;
    private readonly Lazy<HttpClient> _http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Purgine/1.0");
        return http;
    });
    private readonly ConcurrentDictionary<string, (DateTime At, object Value)> _cache = new();
    private (DateTime At, Dictionary<uint, Dictionary<string, int>> ByApp)? _inventory;
    private readonly SemaphoreSlim _inventoryGate = new(1, 1);

    public SteamCommunityService(SteamSession session) => _session = session;

    // =====================================================================================
    // Amigos
    // =====================================================================================

    public Task<FriendsForApp?> GetFriendsAsync(uint appId, CancellationToken ct) => Cached($"friends:{appId}", async () =>
    {
        var info = await _session.UseServiceAsync<Player, CPlayer_GetFriendsGameplayInfo_Response>(async (player, _) =>
        {
            var r = await player.GetFriendsGameplayInfo(new CPlayer_GetFriendsGameplayInfo_Request { appid = appId }).ToTask();
            return r.Result == SteamKit2.EResult.OK ? r.Body : null;
        }, "los amigos que juegan", ct).ConfigureAwait(false);
        if (info is null) return null;

        // Orden: jugando ahora → hace poco → alguna vez (sin repetir).
        var rows = new List<(ulong Id, string Status, uint Total, bool Recent)>();
        var seen = new HashSet<ulong>();
        foreach (var f in info.in_game) if (seen.Add(f.steamid)) rows.Add((f.steamid, "Jugando ahora", f.minutes_played_forever, true));
        foreach (var f in info.played_recently) if (seen.Add(f.steamid)) rows.Add((f.steamid, $"Jugó hace poco · {Formatting.Duration(TimeSpan.FromMinutes(f.minutes_played))} (2 sem.)", f.minutes_played_forever, true));
        foreach (var f in info.played_ever) if (seen.Add(f.steamid)) rows.Add((f.steamid, $"{Formatting.Duration(TimeSpan.FromMinutes(f.minutes_played_forever))} en total", f.minutes_played_forever, false));

        var shown = rows.Take(12).ToList();
        var personas = await _session.GetPersonasAsync(shown.Select(r => r.Id).ToList(), TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        var friends = shown.Select(r => personas.TryGetValue(r.Id, out var p)
                ? new FriendPlay(r.Id, p.Name, p.AvatarUrl, r.Status, r.Total, r.Recent)
                : new FriendPlay(r.Id, "Amigo", new PersonaInfo("", null).AvatarUrl, r.Status, r.Total, r.Recent))
            .ToList();
        return new FriendsForApp(friends, info.in_game.Count, info.played_recently.Count, info.played_ever.Count, info.owns.Count);
    });

    // =====================================================================================
    // Cromos
    // =====================================================================================

    public Task<TradingCardSet?> GetTradingCardsAsync(uint appId, CancellationToken ct) => Cached($"cards:{appId}", async () =>
    {
        var setTask = GetCardSetFromMarketAsync(appId, ct);
        var ownedTask = GetOwnedCardsAsync(ct);
        var badgeTask = _session.UseServiceAsync<Player, CPlayer_GetGameBadgeLevels_Response>(async (player, _) =>
        {
            var r = await player.GetGameBadgeLevels(new CPlayer_GetGameBadgeLevels_Request { appid = appId }).ToTask();
            return r.Result == SteamKit2.EResult.OK ? r.Body : null;
        }, "la insignia del juego", ct);

        var set = await setTask.ConfigureAwait(false);
        var ownedAll = await ownedTask.ConfigureAwait(false);
        var badge = await badgeTask.ConfigureAwait(false);
        var owned = ownedAll?.GetValueOrDefault(appId) ?? new Dictionary<string, int>();
        var level = badge?.badges.Where(b => b.border_color == 0).Select(b => b.level).DefaultIfEmpty(0).Max() ?? 0;

        if (set is null || set.Count == 0)
        {
            // Sin datos del Mercado: al menos los que tienes.
            if (owned.Count == 0) return set is null ? null : new TradingCardSet([], level, SetKnown: true);
            return new TradingCardSet(owned.Select(o => new TradingCard(CardName(o.Key), string.Empty, o.Value)).ToList(), level, SetKnown: false);
        }
        var cards = set.Select(c => new TradingCard(c.Name, c.IconUrl, owned.GetValueOrDefault(c.HashName))).ToList();
        return new TradingCardSet(cards, level, SetKnown: true);
    });

    /// <summary>Colección completa (cromos normales) del Mercado de la Comunidad: endpoint público, sin sesión.</summary>
    private async Task<List<(string Name, string HashName, string IconUrl)>?> GetCardSetFromMarketAsync(uint appId, CancellationToken ct)
    {
        var url = "https://steamcommunity.com/market/search/render/?norender=1&count=50&start=0&appid=753&l=spanish" +
                  $"&category_753_Game[]=tag_app_{appId}&category_753_item_class[]=tag_item_class_2&category_753_cardborder[]=tag_cardborder_0";
        try
        {
            using var doc = JsonDocument.Parse(await _http.Value.GetStringAsync(url, ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("results", out var results)) return null;
            var list = new List<(string, string, string)>();
            foreach (var r in results.EnumerateArray())
            {
                var hash = r.GetProperty("hash_name").GetString() ?? string.Empty;
                var name = r.TryGetProperty("name", out var n) ? n.GetString() ?? hash : hash;
                var icon = r.TryGetProperty("asset_description", out var d) && d.TryGetProperty("icon_url", out var i) ? i.GetString() : null;
                list.Add((name, hash, icon is null ? string.Empty : $"https://community.fastly.steamstatic.com/economy/image/{icon}/96fx96f"));
            }
            return list.OrderBy(c => c.Item1, StringComparer.CurrentCulture).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn($"Mercado de la Comunidad: no se pudo leer la colección de cromos de {appId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Cromos normales del inventario del usuario, por juego y nombre de mercado (una consulta para todos).</summary>
    private async Task<Dictionary<uint, Dictionary<string, int>>?> GetOwnedCardsAsync(CancellationToken ct)
    {
        await _inventoryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inventory is { } inv && DateTime.UtcNow - inv.At < CacheFor) return inv.ByApp;
            var byApp = await _session.UseServiceAsync<Econ, Dictionary<uint, Dictionary<string, int>>>(async (econ, steamId) =>
            {
                var result = new Dictionary<uint, Dictionary<string, int>>();
                ulong start = 0;
                for (var page = 0; page < 6; page++)
                {
                    var r = await econ.GetInventoryItemsWithDescriptions(new CEcon_GetInventoryItemsWithDescriptions_Request
                    {
                        steamid = steamId,
                        appid = SteamCommunityApp,
                        contextid = CommunityContext,
                        get_descriptions = true,
                        language = "spanish",
                        count = 2000,
                        start_assetid = start,
                    }).ToTask();
                    if (r.Result != SteamKit2.EResult.OK) return page == 0 ? null : result;

                    var cards = r.Body.descriptions
                        .Where(d => d.tags.Any(t => t.internal_name == "item_class_2") && d.tags.Any(t => t.internal_name == "cardborder_0"))
                        .GroupBy(d => d.classid).ToDictionary(g => g.Key, g => g.First());
                    foreach (var asset in r.Body.assets)
                    {
                        if (!cards.TryGetValue(asset.classid, out var d) || d.market_fee_app <= 0) continue;
                        var app = (uint)d.market_fee_app;
                        if (!result.TryGetValue(app, out var perApp)) result[app] = perApp = new Dictionary<string, int>();
                        perApp[d.market_hash_name] = perApp.GetValueOrDefault(d.market_hash_name) + (int)Math.Max(1, asset.amount);
                    }
                    if (!r.Body.more_items) break;
                    start = r.Body.last_assetid;
                }
                return result;
            }, "tu inventario de cromos", ct).ConfigureAwait(false);
            if (byApp is not null) _inventory = (DateTime.UtcNow, byApp);
            return byApp;
        }
        finally
        {
            _inventoryGate.Release();
        }
    }

    /// <summary>«730-Nombre del cromo» → «Nombre del cromo».</summary>
    private static string CardName(string marketHashName)
    {
        var dash = marketHashName.IndexOf('-');
        return dash > 0 && marketHashName[..dash].All(char.IsAsciiDigit) ? marketHashName[(dash + 1)..] : marketHashName;
    }

    // =====================================================================================
    // Comunidad: guías y noticias
    // =====================================================================================

    public Task<IReadOnlyList<CommunityItem>?> GetCommunityFeedAsync(uint appId, CancellationToken ct) => Cached($"feed:{appId}", async () =>
    {
        var guidesTask = _session.UseServiceAsync<PublishedFile, List<CommunityItem>>(async (pf, _) =>
        {
            var r = await pf.QueryFiles(new CPublishedFile_QueryFiles_Request
            {
                query_type = 3, // RankedByTrend
                appid = appId,
                filetype = AllGuidesFileType,
                numperpage = 4,
                days = 365,
                return_short_description = true,
                return_vote_data = true,
            }).ToTask();
            if (r.Result != SteamKit2.EResult.OK) return null;
            return r.Body.publishedfiledetails
                .Where(d => !string.IsNullOrWhiteSpace(d.title))
                .Select(d => new CommunityItem("Guía", d.title.Trim(),
                    d.vote_data is { } v && v.votes_up + v.votes_down > 0 ? $"{v.votes_up:N0} valoraciones positivas" : "Guía de la comunidad",
                    $"https://steamcommunity.com/sharedfiles/filedetails/?id={d.publishedfileid}"))
                .ToList();
        }, "las guías de la comunidad", ct);
        var newsTask = GetNewsAsync(appId, ct);

        var items = new List<CommunityItem>();
        if (await guidesTask.ConfigureAwait(false) is { } guides) items.AddRange(guides);
        if (await newsTask.ConfigureAwait(false) is { } news) items.AddRange(news);
        return items.Count == 0 ? null : (IReadOnlyList<CommunityItem>)items;
    });

    /// <summary>Últimas noticias y anuncios de la comunidad (ISteamNews, público y sin clave).</summary>
    private async Task<List<CommunityItem>?> GetNewsAsync(uint appId, CancellationToken ct)
    {
        var url = $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={appId}&count=5&maxlength=1&format=json";
        try
        {
            using var doc = JsonDocument.Parse(await _http.Value.GetStringAsync(url, ct).ConfigureAwait(false));
            var list = new List<CommunityItem>();
            foreach (var n in doc.RootElement.GetProperty("appnews").GetProperty("newsitems").EnumerateArray())
            {
                var title = n.GetProperty("title").GetString();
                var link = n.GetProperty("url").GetString();
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
                var label = n.TryGetProperty("feedlabel", out var fl) ? fl.GetString() : null;
                var date = DateTimeOffset.FromUnixTimeSeconds(n.GetProperty("date").GetInt64());
                var kind = label?.Contains("Community", StringComparison.OrdinalIgnoreCase) == true ? "Anuncio" : "Noticia";
                list.Add(new CommunityItem(kind, title.Trim(), $"{Formatting.RelativeDate(date)}{(string.IsNullOrEmpty(label) ? "" : " · " + label)}", link));
            }
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Warn($"Noticias de {appId}: {ex.Message}");
            return null;
        }
    }

    // =====================================================================================
    // Workshop: títulos de mods
    // =====================================================================================

    /// <summary>Título de varios elementos de Workshop (PublishedFile.GetDetails). Vacío si no hay conexión.</summary>
    public async Task<IReadOnlyDictionary<ulong, string>> GetWorkshopTitlesAsync(IReadOnlyCollection<ulong> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<ulong, string>();
        var titles = await _session.UseServiceAsync<PublishedFile, Dictionary<ulong, string>>(async (pf, _) =>
        {
            var result = new Dictionary<ulong, string>();
            foreach (var chunk in ids.Chunk(100))
            {
                var request = new CPublishedFile_GetDetails_Request();
                request.publishedfileids.AddRange(chunk);
                var r = await pf.GetDetails(request).ToTask();
                if (r.Result != SteamKit2.EResult.OK) continue;
                foreach (var d in r.Body.publishedfiledetails)
                    if (!string.IsNullOrWhiteSpace(d.title)) result[d.publishedfileid] = d.title.Trim();
            }
            return result;
        }, "los nombres de los mods", ct).ConfigureAwait(false);
        return titles ?? new Dictionary<ulong, string>();
    }

    // =====================================================================================

    private async Task<T?> Cached<T>(string key, Func<Task<T?>> load) where T : class
    {
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return (T)hit.Value;
        var value = await load().ConfigureAwait(false);
        if (value is not null) _cache[key] = (DateTime.UtcNow, value);
        return value;
    }

    public void Dispose()
    {
        if (_http.IsValueCreated) _http.Value.Dispose();
        _inventoryGate.Dispose();
    }
}
