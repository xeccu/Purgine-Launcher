using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.SteamNet;

namespace Purgine.ViewModels;

/// <summary>Un logro destacado de la ficha (icono + nombre y descripción en el tooltip).</summary>
public sealed class AchievementItemViewModel : ObservableObject
{
    private ImageSource? _icon;

    public AchievementItemViewModel(AchievementInfo info)
    {
        Name = info.Name;
        Tooltip = string.IsNullOrEmpty(info.GlobalPercent) || !double.TryParse(info.GlobalPercent,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct)
            ? $"{info.Name}\n{info.Description}"
            : $"{info.Name}\n{info.Description}\nLo tiene el {pct:0.#} % de los jugadores";
    }

    public string Name { get; }
    public string Tooltip { get; }

    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }
}

/// <summary>
/// Ficha de un juego al estilo del cliente de Steam: imagen panorámica con logo, barra de acción (botón
/// principal, última sesión, tiempo de juego), progreso de logros y enlaces rápidos. Las imágenes grandes se
/// cargan al abrir la ficha y se sueltan al cambiar de juego o al ocultar la ventana.
/// </summary>
public sealed partial class GameDetailViewModel : ObservableObject
{
    private const int HeroDecodeWidth = 1600;
    private const int LogoDecodeWidth = 640;
    private const int AchievementIconSize = 64;
    private const int MaxTopAchievements = 8;

    private readonly SteamArtService _art;
    private readonly SteamSession _session;
    private readonly Action<string> _openWeb;
    private CancellationTokenSource _cts = new();
    private ImageSource? _hero;
    private ImageSource? _logo;
    private bool _imagesLoaded;
    private bool _achievementsLoaded;
    private string _achievementsText = string.Empty;
    private double _achievementsPercent;
    private bool _hasAchievementProgress;
    private bool _isLoadingAchievements;
    private Color? _accent;
    private Effect? _glow;

    public GameDetailViewModel(GameCardViewModel card, SteamArtService art, SteamSession session,
        Action<string> openWeb, ICommand backCommand, Func<(string Library, long FreeBytes)?> installTarget,
        SteamCommunityService community, GameNotesStore notes, string? steamPath)
    {
        Card = card;
        _glow = GraphicsTheme.GlowFor(null);
        GraphicsTheme.Changed += OnGraphicsChanged;
        if (installTarget() is { } target)
        {
            var drive = Path.GetPathRoot(target.Library)?.TrimEnd('\\') ?? target.Library;
            FreeSpaceText = $"{Formatting.Bytes(target.FreeBytes)} libres en {drive}";
            IsSpaceInsufficient = card.InstallSize > 0 && (ulong)Math.Max(0, target.FreeBytes) < card.InstallSize;
        }
        _art = art;
        _session = session;
        _openWeb = openWeb;
        BackCommand = backCommand;

        var id = Uri.EscapeDataString(card.Entry.AppId);
        OpenStoreCommand = new RelayCommand(_ => _openWeb($"https://store.steampowered.com/app/{id}/?l=spanish"), _ => card.IsSteam);
        OpenCommunityCommand = new RelayCommand(_ => _openWeb($"https://steamcommunity.com/app/{id}/?l=spanish"), _ => card.IsSteam);
        OpenDiscussionsCommand = new RelayCommand(_ => _openWeb($"https://steamcommunity.com/app/{id}/discussions/?l=spanish"), _ => card.IsSteam);
        OpenGuidesCommand = new RelayCommand(_ => _openWeb($"https://steamcommunity.com/app/{id}/guides/?l=spanish"), _ => card.IsSteam);
        // Siempre con el AppID numérico del juego; sin AppID válido (ejecutables locales) se va a la portada general.
        var appIdNumber = uint.TryParse(card.Entry.AppId, out var n) && n > 0 ? n : 0u;
        OpenWorkshopCommand = new RelayCommand(_ => _openWeb(appIdNumber > 0
            ? $"https://steamcommunity.com/app/{appIdNumber}/workshop/"
            : "https://steamcommunity.com/workshop/"));
        OpenPointsShopCommand = new RelayCommand(_ => _openWeb(appIdNumber > 0
            ? $"https://store.steampowered.com/points/shop/app/{appIdNumber}"
            : "https://store.steampowered.com/points/shop"));
        OpenSupportCommand = new RelayCommand(_ => _openWeb($"https://help.steampowered.com/es/wizard/HelpWithGame/?appid={id}"), _ => card.IsSteam);
        OpenAchievementsCommand = new RelayCommand(
            _ => _openWeb(_session.SteamId is { } sid
                ? $"https://steamcommunity.com/profiles/{sid.ConvertToUInt64()}/stats/{id}/achievements/?l=spanish"
                : $"https://steamcommunity.com/stats/{id}/achievements/?l=spanish"),
            _ => card.IsSteam);
        InitExtras(community, notes, steamPath);
    }

    public GameCardViewModel Card { get; }

    /// <summary>«120 GB libres en D:» (disco donde se instalaría: la biblioteca de Steam con más espacio).</summary>
    public string FreeSpaceText { get; } = string.Empty;

    /// <summary>No cabe en ninguna biblioteca de Steam: el texto se muestra en rojo.</summary>
    public bool IsSpaceInsufficient { get; }

    public ICommand BackCommand { get; }
    public ICommand OpenStoreCommand { get; }
    public ICommand OpenCommunityCommand { get; }
    public ICommand OpenDiscussionsCommand { get; }
    public ICommand OpenGuidesCommand { get; }
    public ICommand OpenSupportCommand { get; }
    public ICommand OpenWorkshopCommand { get; }
    public ICommand OpenPointsShopCommand { get; }
    public ICommand OpenAchievementsCommand { get; }

    // ----- Imágenes -----

    public ImageSource? Hero
    {
        get => _hero;
        private set => SetProperty(ref _hero, value);
    }

    public ImageSource? Logo
    {
        get => _logo;
        private set { if (SetProperty(ref _logo, value)) OnPropertyChanged(nameof(ShowTitleText)); }
    }

    /// <summary>
    /// Brillo de la barra de acción con el color dominante de la imagen del juego (Ultra-moderno); sombra neutra
    /// mientras se calcula; null en Rendimiento.
    /// </summary>
    public Effect? GlowEffect
    {
        get => _glow;
        private set => SetProperty(ref _glow, value);
    }

    private void OnGraphicsChanged(Settings.GraphicsPreset _) => GlowEffect = GraphicsTheme.GlowFor(_accent);

    /// <summary>Sin logo (juegos locales o sin arte): el nombre se escribe sobre el héroe.</summary>
    public bool ShowTitleText => _logo is null;

    // ----- Logros -----

    public bool ShowAchievements => Card.IsSteam;

    public string AchievementsText
    {
        get => _achievementsText;
        private set => SetProperty(ref _achievementsText, value);
    }

    public double AchievementsPercent
    {
        get => _achievementsPercent;
        private set => SetProperty(ref _achievementsPercent, value);
    }

    public bool HasAchievementProgress
    {
        get => _hasAchievementProgress;
        private set => SetProperty(ref _hasAchievementProgress, value);
    }

    public bool IsLoadingAchievements
    {
        get => _isLoadingAchievements;
        private set => SetProperty(ref _isLoadingAchievements, value);
    }

    public ObservableCollection<AchievementItemViewModel> TopAchievements { get; } = [];

    // ----- Carga y liberación -----

    /// <summary>Carga héroe, logo y logros (lo que falte). Seguro de llamar varias veces.</summary>
    public async Task LoadAsync()
    {
        var ct = _cts.Token;
        var tasks = new List<Task>();
        if (!_imagesLoaded) tasks.Add(LoadImagesAsync(ct));
        if (!_achievementsLoaded && Card.IsSteam) tasks.Add(LoadAchievementsAsync(ct));
        tasks.AddRange(LoadExtrasTasks(ct));
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* se cambió de juego */ }
        catch (Exception ex)
        {
            // Se llama sin await (fire-and-forget): cualquier fallo se registra aquí y no queda sin observar.
            Infrastructure.Log.Warn($"Ficha de {Card.Name}: {ex.Message}");
        }
    }

    private async Task LoadImagesAsync(CancellationToken ct)
    {
        _imagesLoaded = true;
        if (!Card.IsSteam)
        {
            Hero = Card.Cover; // los locales solo tienen portada (si la hay)
            if (Hero is BitmapSource cover) GlowEffect = GraphicsTheme.GlowFor(_accent = ColorExtractor.Dominant(cover));
            return;
        }
        var heroTask = _art.LoadHeroAsync(Card.Entry.AppId, HeroDecodeWidth, ct);
        var logoTask = _art.LoadLogoAsync(Card.Entry.AppId, LogoDecodeWidth, ct);
        var hero = await heroTask;
        if (ct.IsCancellationRequested) return;
        Hero = hero ?? Card.Cover;
        if (_accent is null && Hero is BitmapSource bitmap)
        {
            _accent = await Task.Run(() => ColorExtractor.Dominant(bitmap), ct);
            if (!ct.IsCancellationRequested) GlowEffect = GraphicsTheme.GlowFor(_accent);
        }
        var logo = await logoTask;
        if (!ct.IsCancellationRequested) Logo = logo;
    }

    private async Task LoadAchievementsAsync(CancellationToken ct)
    {
        _achievementsLoaded = true;
        if (!_session.HasSavedSession)
        {
            AchievementsText = "Inicia sesión en Steam (Ajustes) para ver tus logros.";
            return;
        }
        if (!uint.TryParse(Card.Entry.AppId, out var appId)) return;

        IsLoadingAchievements = true;
        AchievementsText = "Consultando tus logros en Steam…";
        try
        {
            var summary = await Task.Run(() => _session.GetAchievementsAsync(appId, MaxTopAchievements, ct), ct);
            if (ct.IsCancellationRequested) return;
            if (summary is null)
            {
                AchievementsText = "No se pudieron consultar los logros (¿sin conexión?).";
                _achievementsLoaded = false; // reintentar la próxima vez que se abra la ficha
                return;
            }
            if (summary.Total == 0)
            {
                AchievementsText = "Este juego no tiene logros de Steam.";
                return;
            }

            var percent = summary.Unlocked * 100.0 / summary.Total;
            AchievementsPercent = percent;
            HasAchievementProgress = true;
            AchievementsText = $"Has desbloqueado {summary.Unlocked} de {summary.Total} ({percent:0} %)";

            TopAchievements.Clear();
            var items = summary.Top.Select(a => (Info: a, Vm: new AchievementItemViewModel(a))).ToList();
            foreach (var (_, vm) in items) TopAchievements.Add(vm);
            foreach (var (info, vm) in items)
            {
                if (info.IconUrl is null) continue;
                var cacheName = $"ach_{appId}_{Path.GetFileName(new Uri(info.IconUrl).AbsolutePath)}";
                vm.Icon = await _art.LoadRemoteAsync(info.IconUrl, cacheName, AchievementIconSize, ct);
                if (ct.IsCancellationRequested) return;
            }
        }
        finally
        {
            IsLoadingAchievements = false;
        }
    }

    /// <summary>Suelta las imágenes grandes (ventana en la bandeja). <see cref="LoadAsync"/> las recupera.</summary>
    public void ReleaseImages()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        Hero = null;
        Logo = null;
        _imagesLoaded = false;
        // Los iconos de logros (8 de 64 px) apenas ocupan: se quedan, y así no se vuelve a consultar Steam.
        if (IsLoadingAchievements) _achievementsLoaded = false; // se canceló a medias: reintentar
    }

    /// <summary>Ficha cerrada: cancelar cargas pendientes y soltar todo.</summary>
    /// <summary>La ficha se está cerrando (se abre otra o se vuelve al inicio).</summary>
    public bool IsClosing { get; private set; }

    public void Close()
    {
        IsClosing = true;
        GraphicsTheme.Changed -= OnGraphicsChanged;
        CloseExtras();
        _cts.Cancel();
        Hero = null;
        Logo = null;
        TopAchievements.Clear();
    }
}
