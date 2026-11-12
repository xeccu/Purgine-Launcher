using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.SteamNet;

namespace Purgine.ViewModels;

/// <summary>Un amigo en el módulo de amigos de la ficha.</summary>
public sealed class FriendItemViewModel(FriendPlay friend) : ObservableObject
{
    private ImageSource? _avatar;
    public string Name { get; } = friend.Name;
    public string Status { get; } = friend.Status;
    public bool IsRecent { get; } = friend.IsRecent;
    public string AvatarUrl { get; } = friend.AvatarUrl;
    public ImageSource? Avatar { get => _avatar; set => SetProperty(ref _avatar, value); }
}

/// <summary>Un cromo (obtenido o pendiente).</summary>
public sealed class CardItemViewModel(TradingCard card) : ObservableObject
{
    private ImageSource? _icon;
    public string Name { get; } = card.Name;
    public string IconUrl { get; } = card.IconUrl;
    public int Owned { get; } = card.Owned;
    public bool IsOwned => Owned > 0;
    public string Tooltip => Owned switch { 0 => $"{Name}\nPendiente", 1 => $"{Name}\nObtenido", _ => $"{Name}\nObtenido (x{Owned})" };
    public ImageSource? Icon { get => _icon; set => SetProperty(ref _icon, value); }
}

/// <summary>Miniatura de una captura de pantalla local.</summary>
public sealed class ScreenshotItemViewModel(string path) : ObservableObject
{
    private ImageSource? _thumb;
    public string Path { get; } = path;
    public ImageSource? Thumb { get => _thumb; set => SetProperty(ref _thumb, value); }
}

/// <summary>
/// Secciones sociales y personales de la ficha: amigos, cromos, capturas y grabaciones, notas, reseña y
/// contenido de la comunidad. Las notas y la reseña son locales (<c>game-notes.json</c>); publicar la reseña
/// se hace en la página oficial del juego (Purgine copia el texto al portapapeles), nunca en nombre del usuario.
/// </summary>
public sealed partial class GameDetailViewModel
{
    private const int AvatarSize = 64;
    private const int CardIconSize = 96;
    private const int ScreenshotThumbWidth = 220;
    private const int MaxScreenshots = 6;

    private SteamCommunityService _community = null!;
    private GameNotesStore _notes = null!;
    private string? _steamPath;
    private bool _extrasLoaded;

    private string _notesText = string.Empty;
    private string _reviewText = string.Empty;
    private bool? _recommended;
    private string _reviewStatus = string.Empty;
    private string _friendsText = string.Empty;
    private bool _isLoadingFriends;
    private string _cardsText = string.Empty;
    private double _cardsPercent;
    private bool _isLoadingCards;
    private string _mediaText = string.Empty;
    private string? _screenshotsDir;
    private string? _recordingsDir;
    private int _recordingsCount;
    private bool _isLoadingCommunity;
    private string _communityText = string.Empty;

    private void InitExtras(SteamCommunityService community, GameNotesStore notes, string? steamPath)
    {
        _community = community;
        _notes = notes;
        _steamPath = steamPath;

        var saved = notes.Get(Card.Entry.Id);
        _notesText = saved.Notes;
        _reviewText = saved.Review;
        _recommended = saved.Recommended;
        _reviewStatus = saved.Review.Length > 0 || saved.Recommended is not null
            ? $"Guardada en este equipo · {Formatting.RelativeDate(saved.UpdatedUtc)}"
            : "Solo se guarda en este equipo.";

        var id = Card.Entry.AppId;
        OpenScreenshotsCommand = new RelayCommand(_ => OpenPath(_screenshotsDir), _ => _screenshotsDir is not null);
        OpenRecordingsCommand = new RelayCommand(_ => OpenPath(_recordingsDir), _ => _recordingsDir is not null);
        OpenScreenshotCommand = new RelayCommand(p => OpenPath(p as string));
        OpenCommunityItemCommand = new RelayCommand(p => { if (p is string url) _openWeb(url); });
        OpenCardsCommand = new RelayCommand(_ => _openWeb(_session.SteamId is { } sid
            ? $"https://steamcommunity.com/profiles/{sid.ConvertToUInt64()}/gamecards/{id}/?l=spanish"
            : $"https://steamcommunity.com/market/search?appid=753&category_753_Game[]=tag_app_{id}"), _ => Card.IsSteam);
        SaveReviewCommand = new RelayCommand(_ => SaveReview());
        PublishReviewCommand = new RelayCommand(_ => PublishReview(), _ => Card.IsSteam && _recommended is not null);
    }

    // ===================== Notas =====================

    /// <summary>Notas personales del juego; se guardan solas (con un pequeño retardo) en game-notes.json.</summary>
    public string NotesText
    {
        get => _notesText;
        set
        {
            if (!SetProperty(ref _notesText, value ?? string.Empty)) return;
            var text = _notesText;
            _notes.Update(Card.Entry.Id, n => n.Notes = text);
        }
    }

    // ===================== Reseña =====================

    public string ReviewText
    {
        get => _reviewText;
        set => SetProperty(ref _reviewText, value ?? string.Empty);
    }

    public bool IsRecommended
    {
        get => _recommended == true;
        set { if (value) SetRecommended(true); }
    }

    public bool IsNotRecommended
    {
        get => _recommended == false;
        set { if (value) SetRecommended(false); }
    }

    private void SetRecommended(bool value)
    {
        _recommended = value;
        OnPropertyChanged(nameof(IsRecommended));
        OnPropertyChanged(nameof(IsNotRecommended));
        CommandManager.InvalidateRequerySuggested();
    }

    public string ReviewStatus
    {
        get => _reviewStatus;
        private set => SetProperty(ref _reviewStatus, value);
    }

    public ICommand SaveReviewCommand { get; private set; } = null!;
    public ICommand PublishReviewCommand { get; private set; } = null!;

    private void SaveReview()
    {
        var (text, rec) = (_reviewText.Trim(), _recommended);
        _notes.Update(Card.Entry.Id, n => { n.Review = text; n.Recommended = rec; });
        _notes.Flush();
        ReviewStatus = "Reseña guardada en este equipo.";
    }

    /// <summary>
    /// Publicar se hace en la página oficial (con tu sesión web de Steam): se guarda, se copia el texto al
    /// portapapeles y se abre la sección de reseñas del juego en la pestaña Tienda para pegarlo y enviarlo.
    /// </summary>
    private void PublishReview()
    {
        SaveReview();
        try { if (_reviewText.Trim().Length > 0) Clipboard.SetText(_reviewText.Trim()); }
        catch (Exception ex) { Log.Warn($"No se pudo copiar la reseña: {ex.Message}"); }
        ReviewStatus = "Texto copiado. Pégalo en la página de Steam y pulsa «Publicar reseña» allí.";
        _openWeb($"https://store.steampowered.com/app/{Uri.EscapeDataString(Card.Entry.AppId)}/?l=spanish#review_create");
    }

    // ===================== Amigos =====================

    public bool ShowSteamExtras => Card.IsSteam;
    public ObservableCollection<FriendItemViewModel> Friends { get; } = [];
    public string FriendsText { get => _friendsText; private set => SetProperty(ref _friendsText, value); }
    public bool IsLoadingFriends { get => _isLoadingFriends; private set => SetProperty(ref _isLoadingFriends, value); }

    // ===================== Cromos =====================

    public ObservableCollection<CardItemViewModel> Cards { get; } = [];
    public string CardsText { get => _cardsText; private set => SetProperty(ref _cardsText, value); }
    public double CardsPercent { get => _cardsPercent; private set => SetProperty(ref _cardsPercent, value); }
    public bool IsLoadingCards { get => _isLoadingCards; private set => SetProperty(ref _isLoadingCards, value); }
    public ICommand OpenCardsCommand { get; private set; } = null!;

    // ===================== Capturas y grabaciones =====================

    public ObservableCollection<ScreenshotItemViewModel> Screenshots { get; } = [];
    public string MediaText { get => _mediaText; private set => SetProperty(ref _mediaText, value); }
    public int RecordingsCount { get => _recordingsCount; private set => SetProperty(ref _recordingsCount, value); }
    public ICommand OpenScreenshotsCommand { get; private set; } = null!;
    public ICommand OpenRecordingsCommand { get; private set; } = null!;
    public ICommand OpenScreenshotCommand { get; private set; } = null!;

    // ===================== Comunidad =====================

    public ObservableCollection<CommunityItem> CommunityItems { get; } = [];
    public bool IsLoadingCommunity { get => _isLoadingCommunity; private set => SetProperty(ref _isLoadingCommunity, value); }
    public string CommunityText { get => _communityText; private set => SetProperty(ref _communityText, value); }
    public ICommand OpenCommunityItemCommand { get; private set; } = null!;

    // ===================== Carga =====================

    private IEnumerable<Task> LoadExtrasTasks(CancellationToken ct)
    {
        if (_extrasLoaded || !Card.IsSteam || !uint.TryParse(Card.Entry.AppId, out var appId)) yield break;
        _extrasLoaded = true;
        yield return LoadMediaAsync(ct);
        if (!_session.HasSavedSession)
        {
            FriendsText = "Inicia sesión en Steam (Ajustes) para ver qué amigos lo juegan.";
            CardsText = "Inicia sesión en Steam para ver tus cromos.";
        }
        else
        {
            yield return LoadFriendsAsync(appId, ct);
            yield return LoadCardsAsync(appId, ct);
        }
        yield return LoadCommunityAsync(appId, ct);
    }

    private async Task LoadFriendsAsync(uint appId, CancellationToken ct)
    {
        IsLoadingFriends = true;
        FriendsText = "Consultando a tus amigos…";
        try
        {
            var result = await Task.Run(() => _community.GetFriendsAsync(appId, ct), ct);
            if (ct.IsCancellationRequested) return;
            if (result is null) { FriendsText = "No se pudo consultar (¿sin conexión?)."; return; }
            if (result.Friends.Count == 0)
            {
                FriendsText = result.Owners > 0 ? $"{result.Owners} amigos lo tienen, pero ninguno lo ha jugado." : "Ninguno de tus amigos lo ha jugado.";
                return;
            }
            var parts = new List<string>();
            if (result.InGame > 0) parts.Add($"{result.InGame} jugando ahora");
            if (result.Recent > 0) parts.Add($"{result.Recent} hace poco");
            parts.Add($"{result.Ever + result.Recent + result.InGame} lo han jugado");
            FriendsText = string.Join(" · ", parts);
            foreach (var f in result.Friends) Friends.Add(new FriendItemViewModel(f));
            foreach (var f in Friends.ToList())
            {
                f.Avatar = await _art.LoadRemoteAsync(f.AvatarUrl, CacheName("avatar", f.AvatarUrl), AvatarSize, ct);
                if (ct.IsCancellationRequested) return;
            }
        }
        finally { IsLoadingFriends = false; }
    }

    private async Task LoadCardsAsync(uint appId, CancellationToken ct)
    {
        IsLoadingCards = true;
        CardsText = "Consultando cromos…";
        try
        {
            var set = await Task.Run(() => _community.GetTradingCardsAsync(appId, ct), ct);
            if (ct.IsCancellationRequested) return;
            if (set is null) { CardsText = "No se pudieron consultar los cromos (¿sin conexión?)."; return; }
            if (set.Cards.Count == 0) { CardsText = "Este juego no tiene cromos de Steam."; return; }

            var owned = set.Cards.Count(c => c.Owned > 0);
            CardsPercent = set.SetKnown ? owned * 100.0 / set.Cards.Count : 0;
            var badge = set.BadgeLevel > 0 ? $" · insignia nivel {set.BadgeLevel}" : string.Empty;
            CardsText = set.SetKnown
                ? $"Tienes {owned} de {set.Cards.Count} cromos{badge}"
                : $"Tienes {owned} cromos de este juego{badge}";
            foreach (var c in set.Cards.OrderByDescending(c => c.Owned > 0)) Cards.Add(new CardItemViewModel(c));
            foreach (var c in Cards.ToList())
            {
                if (c.IconUrl.Length == 0) continue;
                c.Icon = await _art.LoadRemoteAsync(c.IconUrl, CacheName("card", c.IconUrl), CardIconSize, ct);
                if (ct.IsCancellationRequested) return;
            }
        }
        finally { IsLoadingCards = false; }
    }

    private async Task LoadMediaAsync(CancellationToken ct)
    {
        var media = await Task.Run(() => SteamMediaLocator.Find(_steamPath, Card.Entry.AppId), ct);
        if (ct.IsCancellationRequested) return;
        _screenshotsDir = media.ScreenshotsDir;
        _recordingsDir = media.RecordingsDir;
        RecordingsCount = media.RecordingCount;
        MediaText = (media.Screenshots.Count, media.RecordingCount) switch
        {
            (0, 0) => "No hay capturas ni grabaciones de este juego en este equipo (F12 hace capturas en Steam).",
            var (s, r) => $"{s} {(s == 1 ? "captura" : "capturas")} · {r} {(r == 1 ? "grabación" : "grabaciones")}",
        };
        CommandManager.InvalidateRequerySuggested();
        foreach (var file in media.Screenshots.Take(MaxScreenshots))
        {
            var item = new ScreenshotItemViewModel(file);
            Screenshots.Add(item);
            var thumb = SteamMediaLocator.ThumbnailFor(file);
            item.Thumb = await Task.Run(() => ImageLoader.FromFile(thumb, ScreenshotThumbWidth), ct);
            if (ct.IsCancellationRequested) return;
        }
    }

    private async Task LoadCommunityAsync(uint appId, CancellationToken ct)
    {
        IsLoadingCommunity = true;
        CommunityText = "Cargando guías y novedades…";
        try
        {
            var items = await Task.Run(() => _community.GetCommunityFeedAsync(appId, ct), ct);
            if (ct.IsCancellationRequested) return;
            if (items is null) { CommunityText = "No se pudo cargar el contenido de la comunidad."; return; }
            CommunityText = string.Empty;
            foreach (var i in items) CommunityItems.Add(i);
        }
        finally { IsLoadingCommunity = false; }
    }

    /// <summary>Amigos, cromos y comunidad que se quedaron cargando pasan a «no se pudo». True si había alguno.</summary>
    private bool MarkExtrasInterrupted(string retry)
    {
        var any = false;
        if (FriendsText.StartsWith("Consultando", StringComparison.Ordinal)) { FriendsText = "No se pudo consultar (¿sin conexión?)." + retry; any = true; }
        if (CardsText.StartsWith("Consultando", StringComparison.Ordinal)) { CardsText = "No se pudieron consultar los cromos (¿sin conexión?)." + retry; any = true; }
        if (CommunityText.StartsWith("Cargando", StringComparison.Ordinal)) { CommunityText = "No se pudo cargar el contenido de la comunidad." + retry; any = true; }
        if (any) _extrasLoaded = false;
        return any;
    }

    /// <summary>Vacía lo que viene de la red para volver a consultarlo (las notas y capturas locales se quedan).</summary>
    private void ResetExtras()
    {
        _extrasLoaded = false;
        Friends.Clear();
        Cards.Clear();
        Screenshots.Clear();
        CommunityItems.Clear();
        if (!Card.IsSteam) return;
        if (_session.HasSavedSession)
        {
            IsLoadingFriends = true;
            FriendsText = "Consultando a tus amigos…";
            IsLoadingCards = true;
            CardsText = "Consultando cromos…";
            CardsPercent = 0;
        }
        IsLoadingCommunity = true;
        CommunityText = "Cargando guías y novedades…";
    }

    private void CloseExtras()
    {
        _notes.Flush();
        Friends.Clear();
        Cards.Clear();
        Screenshots.Clear();
        CommunityItems.Clear();
    }

    private static string CacheName(string prefix, string url) =>
        $"{prefix}_{Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20]}.jpg";

    /// <summary>Carpeta → Explorador (blindado); archivo (captura) → su visor predeterminado.</summary>
    private static void OpenPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (Directory.Exists(path)) { ShellOpen.Folder(path); return; }
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"No se pudo abrir {path}: {ex.Message}"); ShellOpen.Folder(path); }
    }
}
