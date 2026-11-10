using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Purgine.Core;
using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.Settings;
using Purgine.Stats;
using Purgine.SteamNet;

namespace Purgine.ViewModels;

public enum AppView
{
    Library,
    Store,
    Downloads,
    Settings,
}

/// <summary>Chip de filtro por etiqueta («Todas» tiene <see cref="Tag"/> = null).</summary>
public sealed class TagFilterViewModel : ObservableObject
{
    private int _count;

    public TagFilterViewModel(string? tag, string label)
    {
        Tag = tag;
        Label = label;
    }

    public string? Tag { get; }
    public string Label { get; }

    public int Count
    {
        get => _count;
        set { if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(Display)); }
    }

    public string Display => $"{Label}  {Count}";
}

/// <summary>
/// Estado de la ventana principal: navegación, biblioteca (propiedad consultada en la red de Steam), descargas
/// nativas (SteamKit2), sesión de juego en curso y banner de sincronización en la nube.
/// </summary>
public sealed class MainViewModel : ObservableObject, ICardHost, IStoreActions, IDownloadActions
{
    private readonly SteamLibraryScanner _scanner;
    private readonly ProcessOrchestrator _orchestrator;
    private readonly GameTimeTracker _tracker;
    private readonly UserLibrary _library;
    private readonly LauncherSettings _settings;
    private readonly LibraryWatcher _watcher;
    private readonly DebouncedAction _saveSettings;
    private readonly SteamSession _session;
    private readonly SteamOwnershipService _ownership;
    private readonly SteamDownloadManager _downloads;
    private readonly SteamArtService _art;
    private readonly SteamCommunityService _community;
    private readonly GameNotesStore _notes;
    private readonly SteamLocator _steam;
    private readonly EpicNet.EpicAccountService _epic;
    private readonly EpicNet.EpicDownloadManager _epicDownloads;

    /// <summary>Juegos de la cuenta de Epic que no están instalados (última consulta a la red).</summary>
    private List<GameEntry> _epicOwned = [];
    /// <summary>Build pública y tamaño instalado de cada juego de Epic (caché en disco).</summary>
    private readonly Dictionary<string, EpicBuildInfo> _epicBuilds = JsonFileStore.Load<Dictionary<string, EpicBuildInfo>>(AppPaths.EpicBuildsFile);

    /// <summary>AppIDs de la cuenta (red de Steam si hay sesión; si no, caché local del cliente).</summary>
    private IReadOnlySet<string> _owned = new HashSet<string>();
    private IReadOnlyDictionary<string, SteamAppBasics> _appInfo = new Dictionary<string, SteamAppBasics>();

    private CancellationTokenSource? _sessionCts;
    private int _refreshGeneration;
    private AppView _currentView = AppView.Library;
    private string _searchText = string.Empty;
    private string _statusText = "Listo.";
    private bool _isScanning;
    private bool _isSessionActive;
    private bool _isBannerVisible;
    private string _bannerText = string.Empty;
    private string _bannerDetail = string.Empty;
    private bool _cloudNotified;
    private TagFilterViewModel? _selectedTagFilter;
    private GameCardViewModel? _selectedGame;
    private GameDetailViewModel? _detail;

    public MainViewModel(
        SteamLibraryScanner scanner,
        ProcessOrchestrator orchestrator,
        GameTimeTracker tracker,
        UserLibrary library,
        LauncherSettings settings,
        SteamArtService art,
        SteamCommunityService community,
        GameNotesStore notes,
        EpicNet.EpicAccountService epic,
        LibraryWatcher watcher,
        SteamLocator steam,
        SteamSession session,
        SteamOwnershipService ownership,
        SteamDownloadManager downloads,
        Action saveSettings)
    {
        _scanner = scanner;
        _orchestrator = orchestrator;
        _tracker = tracker;
        _library = library;
        _settings = settings;
        _watcher = watcher;
        _session = session;
        _ownership = ownership;
        _downloads = downloads;
        _art = art;
        _community = community;
        _notes = notes;
        _steam = steam;
        _epic = epic;
        _epicDownloads = new EpicNet.EpicDownloadManager(epic, AppPaths.EpicQueueFile);
        _saveSettings = new DebouncedAction(saveSettings, TimeSpan.FromMilliseconds(600));

        Store = new StoreViewModel(this);
        Downloads = new DownloadsViewModel(this);
        Settings = new SettingsViewModel(settings, library, steam, _saveSettings.Request, () => Dialogs,
            login: LoginAsync, logout: LogoutAsync, epicLogin: EpicLoginAsync, epicLogout: EpicLogoutAsync);

        GamesView = CollectionViewSource.GetDefaultView(Games);
        ApplySort();
        GamesView.Filter = FilterGame;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning);
        CancelCommand = new RelayCommand(_ => _sessionCts?.Cancel(), _ => IsSessionActive);
        ForceCloseCommand = new RelayCommand(_ => ForceCloseRunning(), _ => IsSessionActive);
        OpenDataFolderCommand = new RelayCommand(_ => OpenPath(AppPaths.DataDir));
        AddLocalGameCommand = new RelayCommand(_ => AddLocalGame());
        NewTagCommand = new RelayCommand(_ => PromptNewTag());
        ShowHomeCommand = new RelayCommand(_ => SelectedGame = null);
        ShowViewCommand = new RelayCommand(p => { if (p is AppView v) CurrentView = v; else if (p is string s && Enum.TryParse<AppView>(s, out var pv)) CurrentView = pv; });

        _orchestrator.StatusChanged += (_, s) => OnUi(() => OnOrchestratorStatus(s));
        _orchestrator.SteamClosed += (_, _) => _downloads.OnSteamClosed(); // re-registrar lo instalado con Steam abierto
        _library.Changed += (_, _) => OnUi(OnLibraryChanged);
        _watcher.ManifestChanged += path => _ = Task.Run(() => _scanner.ParseManifestFile(path))
            .ContinueWith(t => { if (t.Result is { } e) OnUi(() => UpsertGame(e)); }, TaskContinuationOptions.OnlyOnRanToCompletion);
        _watcher.ManifestDeleted += appId => OnUi(() => OnManifestDeleted(appId));
        _tracker.SessionRecorded += (_, _) => OnUi(() => OnPropertyChanged(nameof(SummaryText)));

        _downloads.QueueChanged += () => OnUi(SyncDownloads);
        _downloads.JobChanged += job => OnUi(() => OnJobChanged(job));
        _downloads.JobFinished += job => OnUi(() => OnJobFinished(job));
        _epicDownloads.QueueChanged += () => OnUi(SyncDownloads);
        _epicDownloads.JobChanged += job => OnUi(() => OnJobChanged(job));
        _epicDownloads.JobFinished += job => OnUi(() => OnJobFinished(job));
        _session.StateChanged += (state, message) => OnUi(() => OnSessionState(state, message));

        RebuildTagFilters();
        SyncDownloads();
        UpdateAccountText();
        UpdateEpicAccountText();
    }

    // =====================================================================================
    // Servicios expuestos a la vista
    // =====================================================================================

    public StoreViewModel Store { get; }
    public SettingsViewModel Settings { get; }
    public DownloadsViewModel Downloads { get; }

    /// <summary>Lo asigna la ventana tras construirse.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Se pide a la vista que oculte la ventana (al lanzar un juego).</summary>
    public event EventHandler? GameLaunched;

    /// <summary>La sesión terminó (con éxito o no) y la UI ya está actualizada.</summary>
    public event EventHandler? SessionFinished;

    /// <summary>Notificación para la bandeja (título, texto).</summary>
    public event Action<string, string>? NotificationRequested;

    // =====================================================================================
    // Navegación
    // =====================================================================================

    public AppView CurrentView
    {
        get => _currentView;
        set
        {
            if (!SetProperty(ref _currentView, value)) return;
            OnPropertyChanged(nameof(IsLibraryView));
            OnPropertyChanged(nameof(IsStoreView));
            OnPropertyChanged(nameof(IsDownloadsView));
            OnPropertyChanged(nameof(IsSettingsView));
            Store.IsActive = value == AppView.Store;
        }
    }

    public bool IsLibraryView { get => _currentView == AppView.Library; set { if (value) CurrentView = AppView.Library; } }
    public bool IsStoreView { get => _currentView == AppView.Store; set { if (value) CurrentView = AppView.Store; } }
    public bool IsSettingsView { get => _currentView == AppView.Settings; set { if (value) CurrentView = AppView.Settings; } }
    public bool IsDownloadsView { get => _currentView == AppView.Downloads; set { if (value) CurrentView = AppView.Downloads; } }

    // =====================================================================================
    // Biblioteca
    // =====================================================================================

    public ObservableCollection<GameCardViewModel> Games { get; } = [];
    public ICollectionView GamesView { get; }
    public ObservableCollection<TagFilterViewModel> TagFilters { get; } = [];

    // ----- Juego seleccionado (lista lateral / clic en una portada) → ficha -----

    /// <summary>Juego cuya ficha se muestra (null = inicio de la biblioteca con la cuadrícula).</summary>
    public GameCardViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (ReferenceEquals(_selectedGame, value)) return;
            _selectedGame = value;
            _detail?.Close();
            _detail = value is null ? null
                : new GameDetailViewModel(value, _art, _session, OpenInStoreTab, ShowHomeCommand, _downloads.InstallTarget,
                    _community, _notes, _steam.SteamPath, _epic);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(IsHomeVisible));
            if (_detail is not null) _ = _detail.LoadAsync();
        }
    }

    public GameDetailViewModel? Detail => _detail;

    public bool IsHomeVisible => _selectedGame is null;

    public void ShowDetails(GameCardViewModel card)
    {
        CurrentView = AppView.Library;
        SelectedGame = card;
    }

    /// <summary>Enlaces de la ficha (tienda, comunidad, soporte…): se abren en la pestaña Tienda.</summary>
    private void OpenInStoreTab(string url)
    {
        Store.Navigate(url);
        CurrentView = AppView.Store;
    }

    // ----- Orden -----

    public bool SortByName
    {
        get => _settings.SortMode == LibrarySortMode.Name;
        set { if (value) SetSortMode(LibrarySortMode.Name); }
    }

    public bool SortByRecent
    {
        get => _settings.SortMode == LibrarySortMode.Recent;
        set { if (value) SetSortMode(LibrarySortMode.Recent); }
    }

    /// <summary>Texto del botón de orden.</summary>
    public string SortLabel => _settings.SortMode switch
    {
        LibrarySortMode.Recent => "Actividad reciente",
        LibrarySortMode.Playtime => "Tiempo de juego",
        _ => "Alfabético (A-Z)",
    };

    public bool SortByPlaytime
    {
        get => _settings.SortMode == LibrarySortMode.Playtime;
        set { if (value) SetSortMode(LibrarySortMode.Playtime); }
    }

    private void SetSortMode(LibrarySortMode mode)
    {
        if (_settings.SortMode == mode) return;
        _settings.SortMode = mode;
        OnPropertyChanged(nameof(SortByName));
        OnPropertyChanged(nameof(SortByPlaytime));
        OnPropertyChanged(nameof(SortByRecent));
        OnPropertyChanged(nameof(SortLabel));
        ApplySort();
        _saveSettings.Request();
    }

    private void ApplySort()
    {
        using (GamesView.DeferRefresh())
        {
            GamesView.SortDescriptions.Clear();
            if (_settings.SortMode == LibrarySortMode.Playtime)
                GamesView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.TotalPlaytimeHours), ListSortDirection.Descending));
            else if (_settings.SortMode == LibrarySortMode.Recent)
                GamesView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.LastPlayedTicks), ListSortDirection.Descending));
            GamesView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.Name), ListSortDirection.Ascending));
        }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value ?? string.Empty)) GamesView.Refresh(); }
    }

    public TagFilterViewModel? SelectedTagFilter
    {
        get => _selectedTagFilter;
        set
        {
            if (!SetProperty(ref _selectedTagFilter, value)) return;
            GamesView.Refresh();
            OnPropertyChanged(nameof(EmptyStateText));
        }
    }

    public bool IsGridView
    {
        get => _settings.ViewMode == LibraryViewMode.Grid;
        set { if (value) SetViewMode(LibraryViewMode.Grid); }
    }

    public bool IsListView
    {
        get => _settings.ViewMode == LibraryViewMode.List;
        set { if (value) SetViewMode(LibraryViewMode.List); }
    }

    /// <summary>Filtro rápido «Todos».</summary>
    public bool ShowAllGames
    {
        get => !_settings.LibraryInstalledOnly;
        set { if (value) SetInstalledOnly(false); }
    }

    /// <summary>Filtro rápido «Instalados».</summary>
    public bool ShowInstalledOnly
    {
        get => _settings.LibraryInstalledOnly;
        set { if (value) SetInstalledOnly(true); }
    }

    /// <summary>Listos para jugar entre los tipos visibles.</summary>
    public int InstalledCount => Games.Count(g => IsTypeShown(g.ContentType) && g.IsReadyToPlay);
    /// <summary>Todo lo de los tipos visibles.</summary>
    public int AllCount => Games.Count(g => IsTypeShown(g.ContentType));

    // ----- Filtros por tipo (Juegos / Software / Herramientas) -----

    private bool IsTypeShown(LibraryContentType type) => type switch
    {
        LibraryContentType.Software => _settings.LibraryShowSoftware,
        LibraryContentType.Tool => _settings.LibraryShowTools,
        _ => _settings.LibraryShowGames,
    };

    public bool ShowGames { get => _settings.LibraryShowGames; set => SetTypeFilter(() => _settings.LibraryShowGames = value); }
    public bool ShowSoftware { get => _settings.LibraryShowSoftware; set => SetTypeFilter(() => _settings.LibraryShowSoftware = value); }
    public bool ShowTools { get => _settings.LibraryShowTools; set => SetTypeFilter(() => _settings.LibraryShowTools = value); }

    public string GamesFilterLabel => $"Juegos ({Games.Count(g => g.ContentType == LibraryContentType.Game)})";
    public string SoftwareFilterLabel => $"Software ({Games.Count(g => g.ContentType == LibraryContentType.Software)})";
    public string ToolsFilterLabel => $"Herramientas ({Games.Count(g => g.ContentType == LibraryContentType.Tool)})";

    /// <summary>Resumen en el botón de filtros: «Juegos y software», «Todos los tipos»…</summary>
    public string TypeFilterSummary
    {
        get
        {
            var on = new List<string>();
            if (_settings.LibraryShowGames) on.Add("Juegos");
            if (_settings.LibraryShowSoftware) on.Add("Software");
            if (_settings.LibraryShowTools) on.Add("Herramientas");
            return on.Count switch
            {
                0 => "Ningún tipo",
                3 => "Todos los tipos",
                1 => on[0],
                _ => string.Join(" y ", on).ToLowerInvariant() is var s ? char.ToUpper(s[0]) + s[1..] : string.Empty,
            };
        }
    }

    private void SetTypeFilter(Action apply)
    {
        apply();
        OnPropertyChanged(nameof(ShowGames));
        OnPropertyChanged(nameof(ShowSoftware));
        OnPropertyChanged(nameof(ShowTools));
        OnPropertyChanged(nameof(TypeFilterSummary));
        GamesView.Refresh();
        RebuildTagFilters();
        RaiseLibraryTexts();
        _saveSettings.Request();
    }

    private void SetInstalledOnly(bool installedOnly)
    {
        if (_settings.LibraryInstalledOnly == installedOnly) return;
        _settings.LibraryInstalledOnly = installedOnly;
        OnPropertyChanged(nameof(ShowAllGames));
        OnPropertyChanged(nameof(ShowInstalledOnly));
        GamesView.Refresh();
        OnPropertyChanged(nameof(EmptyStateText));
        _saveSettings.Request();
    }

    public int CoverWidth
    {
        get => _settings.CoverWidth;
        set
        {
            var v = Math.Clamp(value, LauncherSettings.MinCoverWidth, LauncherSettings.MaxCoverWidth);
            if (v == _settings.CoverWidth) return;
            _settings.CoverWidth = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CoverHeight));
            _saveSettings.Request();
        }
    }

    public int CoverHeight => (int)Math.Round(_settings.CoverWidth * 1.5);
    public int MinCoverWidth => LauncherSettings.MinCoverWidth;
    public int MaxCoverWidth => LauncherSettings.MaxCoverWidth;

    public bool IsScanning
    {
        get => _isScanning;
        private set { if (SetProperty(ref _isScanning, value)) OnPropertyChanged(nameof(EmptyStateText)); }
    }

    public string SummaryText
    {
        get
        {
            // Solo los tipos visibles (las herramientas ocultas no inflan «más en tu cuenta»).
            var installed = Games.Count(g => g.IsSteam && !g.IsNotInstalled && IsTypeShown(g.ContentType));
            var notInstalled = Games.Count(g => g.IsNotInstalled && IsTypeShown(g.ContentType));
            var local = Games.Count(g => g.IsLocal);
            var epic = Games.Count(g => g.IsEpic && IsTypeShown(g.ContentType));
            var total = _tracker.GetGrandTotal();
            var count = $"{installed} instalados";
            if (notInstalled > 0) count += $" · {notInstalled} más en tu cuenta";
            if (epic > 0) count += $" · {epic} de Epic";
            if (local > 0) count += $" · {local} {(local == 1 ? "local" : "locales")}";
            var played = total == TimeSpan.Zero ? "aún sin tiempo registrado" : $"{Formatting.Duration(total)} jugadas en total";
            return $"{count} · {played}";
        }
    }

    /// <summary>Texto del estado vacío (null = hay juegos visibles).</summary>
    public string? EmptyStateText
    {
        get
        {
            if (IsScanning) return "Escaneando bibliotecas…";
            if (Games.Count == 0)
                return _session.HasSavedSession
                    ? "Tu biblioteca de Steam está vacía.\nAñade un ejecutable con «Añadir ejecutable»."
                    : "No hay juegos instalados en este equipo.\nInicia sesión en Steam para ver tu biblioteca completa.";
            if (GamesView.IsEmpty && _settings.LibraryInstalledOnly && _searchText.Length == 0 && _selectedTagFilter?.Tag is null)
                return "No tienes juegos instalados.\nPulsa «Todos» para ver los de tu cuenta e instalarlos.";
            if (GamesView.IsEmpty)
                return _selectedTagFilter?.Tag is { } tag
                    ? $"Ningún juego con la etiqueta «{tag}».\nClic derecho en un juego → Etiquetas para asignarla."
                    : "Ningún juego coincide con la búsqueda.";
            return null;
        }
    }

    // =====================================================================================
    // Sesión y banner
    // =====================================================================================

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsSessionActive
    {
        get => _isSessionActive;
        private set => SetProperty(ref _isSessionActive, value);
    }

    /// <summary>Banner visible mientras se sincroniza con Steam Cloud y se cierra Steam.</summary>
    public bool IsBannerVisible
    {
        get => _isBannerVisible;
        private set => SetProperty(ref _isBannerVisible, value);
    }

    public string BannerText
    {
        get => _bannerText;
        private set => SetProperty(ref _bannerText, value);
    }

    public string BannerDetail
    {
        get => _bannerDetail;
        private set => SetProperty(ref _bannerDetail, value);
    }

    // =====================================================================================
    // Comandos
    // =====================================================================================

    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }
    /// <summary>Forzar cierre del juego en curso (botón de la barra de estado).</summary>
    public ICommand ForceCloseCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand AddLocalGameCommand { get; }
    public ICommand NewTagCommand { get; }
    public ICommand ShowViewCommand { get; }
    public ICommand ShowHomeCommand { get; }

    // =====================================================================================
    // Carga de biblioteca
    // =====================================================================================

    /// <summary>
    /// Escanea lo instalado (manifiestos locales), añade lo que posee la cuenta (red de Steam con caché; o la
    /// caché del cliente si no hay sesión), los ejecutables locales, y vigila cambios. Después consulta la red
    /// en segundo plano para actualizar propiedad y actualizaciones disponibles.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (IsScanning) return;
        var generation = ++_refreshGeneration;
        IsScanning = true;
        StatusText = "Escaneando bibliotecas…";
        try
        {
            var snapshot = await Task.Run(_scanner.ScanFull);
            _tracker.SetSteamPlaytime(snapshot.Usage); // antes de crear las tarjetas
            _appInfo = snapshot.AppInfo;

            var running = Games.Where(g => g.IsRunning).Select(g => g.Entry.Id).ToHashSet();
            var selectedId = _selectedGame?.Entry.Id;
            SelectedGame = null;
            Games.Clear();
            foreach (var e in snapshot.Installed) Games.Add(CreateCard(e));

            if (_session.HasSavedSession && _ownership.HasData)
            {
                // Con sesión: biblioteca completa según los servidores de Steam (última consulta; se refresca abajo).
                AddOwnedFromNetwork(snapshot.Installed);
            }
            else
            {
                // Sin sesión: solo lo instalado en los discos (manifiestos locales).
                _owned = snapshot.Installed.Select(e => e.AppId).ToHashSet();
            }

            // Epic Games: juegos instalados según sus manifiestos locales (sin red).
            foreach (var epic in await Task.Run(EpicLibraryScanner.Scan)) Games.Add(CreateCard(epic));
            AddEpicOwned();
            foreach (var local in _library.LocalGames) Games.Add(CreateCard(local.ToEntry()));
            foreach (var g in Games) g.IsRunning = running.Contains(g.Entry.Id);
            ApplyRemoteUpdates();
            SyncDownloads();
            if (selectedId is not null) SelectedGame = Games.FirstOrDefault(g => g.Entry.Id == selectedId);

            _watcher.Watch(snapshot.LibraryFolders);
            StatusText = Games.Count > 0 ? "Listo. Haz clic en un juego para jugar, o en uno no instalado para instalarlo." : "Sin juegos.";
            if (_pendingLaunch is { } pending) _ = Application.Current.Dispatcher.BeginInvoke(() => RequestLaunch(pending));
        }
        catch (Exception ex)
        {
            Log.Error("Fallo al escanear", ex);
            StatusText = $"Error al escanear: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            RebuildTagFilters();
            RaiseLibraryTexts();
        }

        if (_session.HasSavedSession) _ = RefreshOwnershipAsync();
        if (_epic.HasSession) _ = SyncEpicAccountAsync();

        // Iconos de la lista lateral (pequeños, casi siempre ya en la caché del cliente) y después las
        // portadas, uno a uno, sin bloquear la UI ni disparar picos de memoria.
        foreach (var card in Games.ToList())
        {
            if (generation != _refreshGeneration) return;
            await LoadIconAsync(card);
        }
        foreach (var card in Games.ToList())
        {
            if (generation != _refreshGeneration) break;
            await card.LoadCoverAsync();
        }
    }

    private Task LoadIconAsync(GameCardViewModel card) =>
        card.LoadIconAsync(_art, card.IsSteam && uint.TryParse(card.Entry.AppId, out var id) ? _ownership.Find(id)?.Icon : null);

    private GameCardViewModel CreateCard(GameEntry entry) => new(entry, _tracker, _library, this) { ContentType = TypeFor(entry) };

    /// <summary>Tipo según Steam (red de Steam o appinfo.vdf local); por defecto, juego.</summary>
    private LibraryContentType TypeFor(GameEntry entry)
    {
        if (entry.Platform == GamePlatform.Epic)
            return entry.EpicCategories.Contains("games") || !entry.EpicCategories.Contains("applications")
                ? LibraryContentType.Game : LibraryContentType.Software;
        if (entry.Platform != GamePlatform.Steam) return LibraryContentType.Game;
        var type = uint.TryParse(entry.AppId, out var id) && _ownership.Find(id) is { } app ? app.Type
                 : _appInfo.TryGetValue(entry.AppId, out var info) ? info.Type : "game";
        return type switch
        {
            "application" => LibraryContentType.Software,
            "tool" => LibraryContentType.Tool,
            _ => LibraryContentType.Game,
        };
    }

    /// <summary>Tipos de la cuenta que aparecen en la biblioteca (las demos cuentan como juegos, como en Steam).</summary>
    private static bool IsLibraryType(string type) => type is "game" or "demo" or "application" or "tool";

    /// <summary>Añade como «no instalados» los juegos de la cuenta (según la red) que no están en disco.</summary>
    private void AddOwnedFromNetwork(IEnumerable<GameEntry> installed)
    {
        var installedIds = installed.Select(e => e.AppId).ToHashSet();
        var owned = new HashSet<string>(installedIds);
        foreach (var app in _ownership.Cache.Apps)
        {
            var id = app.AppId.ToString();
            owned.Add(id);
            if (!IsLibraryType(app.Type) || installedIds.Contains(id) || Games.Any(g => g.Entry.Id == GameEntry.MakeSteamId(id))) continue;
            Games.Add(CreateCard(_scanner.CreateNotInstalledEntry(id, app.Name, app.InstallDir)));
        }
        _owned = owned;
    }

    /// <summary>
    /// Marca «Actualizar» en los instalados cuya build difiere de la pública en Steam, y asigna a cada juego el
    /// espacio que ocupa instalado (PICS) para mostrarlo en los no instalados.
    /// </summary>
    private void ApplyRemoteUpdates()
    {
        foreach (var card in Games.Where(g => g.IsSteam))
        {
            var owned = uint.TryParse(card.Entry.AppId, out var sid) ? _ownership.Find(sid) : null;
            card.SetInstallSize(owned?.InstallSize ?? 0);
            card.SetSteamFeatures(owned?.HasWorkshop, owned?.HasPointsShop);
        }
        foreach (var card in Games.Where(g => g.IsSteam && !g.IsNotInstalled))
        {
            var remote = uint.TryParse(card.Entry.AppId, out var id) ? _ownership.Find(id)?.PublicBuildId ?? 0 : 0;
            card.SetRemoteUpdate(remote != 0 && card.Entry.BuildId != 0 && remote != card.Entry.BuildId);
        }
        // Epic: tamaño del manifiesto actual y «Actualizar» si la versión instalada no es la build pública.
        foreach (var card in Games.Where(g => g.IsEpic))
        {
            if (!_epicBuilds.TryGetValue(card.Entry.AppId, out var build)) continue;
            card.SetInstallSize((ulong)Math.Max(0, build.InstallSize));
            if (!card.IsNotInstalled)
                card.SetRemoteUpdate(card.Entry.EpicVersion is { Length: > 0 } v && build.BuildVersion.Length > 0 && v != build.BuildVersion);
        }
    }

    /// <summary>Añade como «no instalados» los juegos de la cuenta de Epic que no están en disco.</summary>
    private void AddEpicOwned()
    {
        foreach (var entry in _epicOwned)
            if (Games.All(g => g.Entry.Id != entry.Id)) Games.Add(CreateCard(entry));
    }

    /// <summary>Consulta la red de Steam (licencias y builds) y refleja los cambios en la biblioteca.</summary>
    private async Task RefreshOwnershipAsync()
    {
        try
        {
            var cache = await Task.Run(() => _ownership.RefreshAsync(CancellationToken.None));
            if (cache is null) { UpdateAccountText(); return; }

            // Quitar las aproximaciones locales que la red no confirma (y no estén instaladas).
            var ownedIds = cache.Apps.Select(a => a.AppId.ToString()).ToHashSet();
            foreach (var card in Games.Where(g => g.IsNotInstalled && !ownedIds.Contains(g.Entry.AppId) && !g.HasActiveJob).ToList())
            {
                if (ReferenceEquals(card, _selectedGame)) SelectedGame = null;
                Games.Remove(card);
            }

            AddOwnedFromNetwork(Games.Where(g => g.IsSteam && !g.IsNotInstalled).Select(g => g.Entry).ToList());
            ApplyRemoteUpdates();
            RebuildTagFilters();
            RaiseLibraryTexts();
            UpdateAccountText();
            foreach (var card in Games.Where(g => g.SmallIcon is null).ToList()) await LoadIconAsync(card);
            foreach (var card in Games.Where(g => !g.HasCover).ToList()) await card.LoadCoverAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo consultar la biblioteca en Steam: {ex.Message}");
            UpdateAccountText();
        }
    }

    /// <summary>Vuelve a leer el histórico de Steam (tras una partida) y refresca los tiempos mostrados.</summary>
    private async Task ReloadSteamPlaytimeAsync()
    {
        try
        {
            var usage = await Task.Run(_scanner.ReadSteamPlaytime);
            _tracker.SetSteamPlaytime(usage);
            foreach (var g in Games) g.RefreshStats();
            if (_settings.SortMode != LibrarySortMode.Name) GamesView.Refresh();
            RaiseLibraryTexts();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo recargar el tiempo de Steam: {ex.Message}");
        }
    }

    /// <summary>Inserta o actualiza un juego a partir de su manifiesto (instalación registrada, actualización…).</summary>
    private void UpsertGame(GameEntry entry)
    {
        var existing = Games.FirstOrDefault(g => g.Entry.Id == entry.Id);
        if (existing is not null)
        {
            existing.UpdateEntry(entry);
        }
        else
        {
            var card = CreateCard(entry);
            Games.Add(card);
            _ = card.LoadCoverAsync();
            _ = LoadIconAsync(card);
        }
        ApplyRemoteUpdates();
        RebuildTagFilters();
        RaiseLibraryTexts();
    }

    /// <summary>Un manifiesto desapareció (desinstalación): si es de la cuenta vuelve a «no instalado».</summary>
    private void OnManifestDeleted(string appId)
    {
        var id = GameEntry.MakeSteamId(appId);
        var card = Games.FirstOrDefault(g => g.Entry.Id == id);
        if (card is null || card.IsRunning) return;

        var name = uint.TryParse(appId, out var uid) && _ownership.Find(uid) is { } app ? app.Name
                 : _appInfo.TryGetValue(appId, out var info) ? info.Name : null;
        if (_owned.Contains(appId) && name is not null)
        {
            var dir = _ownership.Find(uid)?.InstallDir ?? (_appInfo.TryGetValue(appId, out var i2) ? i2.InstallDir : null);
            card.UpdateEntry(_scanner.CreateNotInstalledEntry(appId, name, dir));
            card.SetRemoteUpdate(false);
            StatusText = $"{card.Name} ya no está instalado.";
        }
        else
        {
            if (ReferenceEquals(card, _selectedGame)) SelectedGame = null;
            Games.Remove(card);
            StatusText = $"{card.Name} se ha quitado de la biblioteca.";
        }
        RebuildTagFilters();
        RaiseLibraryTexts();
    }

    private bool FilterGame(object o)
    {
        if (o is not GameCardViewModel g) return false;
        if (!IsTypeShown(g.ContentType)) return false;
        if (_settings.LibraryInstalledOnly && !g.IsReadyToPlay) return false;
        if (_searchText.Length > 0 && !g.Name.Contains(_searchText.Trim(), StringComparison.CurrentCultureIgnoreCase))
            return false;
        return _selectedTagFilter?.Tag is not { } tag || _library.HasTag(g.Entry.Id, tag);
    }

    private void OnLibraryChanged()
    {
        SyncLocalCards();
        foreach (var g in Games) g.RefreshTags();
        RebuildTagFilters();
        GamesView.Refresh();
        RaiseLibraryTexts();
    }

    /// <summary>Alinea las tarjetas locales con <c>library.json</c> (altas, ediciones y bajas).</summary>
    private void SyncLocalCards()
    {
        var defs = _library.LocalGames.ToDictionary(d => GameEntry.MakeLocalId(d.Id));
        foreach (var card in Games.Where(g => g.IsLocal).ToList())
        {
            if (defs.Remove(card.Entry.Id, out var def)) card.UpdateEntry(def.ToEntry());
            else if (!card.IsRunning)
            {
                if (ReferenceEquals(card, _selectedGame)) SelectedGame = null;
                Games.Remove(card);
            }
        }
        foreach (var def in defs.Values)
        {
            var card = CreateCard(def.ToEntry());
            Games.Add(card);
            _ = LoadLocalArtAsync(card);
        }
    }

    private async Task LoadLocalArtAsync(GameCardViewModel card)
    {
        await card.LoadCoverAsync();
        await LoadIconAsync(card);
    }

    private void RebuildTagFilters()
    {
        var selected = _selectedTagFilter?.Tag;
        var wanted = new List<string?> { null };
        wanted.AddRange(_library.Tags);

        if (!wanted.SequenceEqual(TagFilters.Select(f => f.Tag)))
        {
            TagFilters.Clear();
            foreach (var tag in wanted) TagFilters.Add(new TagFilterViewModel(tag, tag ?? "Todas las etiquetas"));
        }

        foreach (var f in TagFilters)
            f.Count = f.Tag is null ? Games.Count(g => IsTypeShown(g.ContentType))
                : Games.Count(g => IsTypeShown(g.ContentType) && _library.HasTag(g.Entry.Id, f.Tag));

        var keep = TagFilters.FirstOrDefault(f => f.Tag == selected) ?? TagFilters[0];
        if (!ReferenceEquals(keep, _selectedTagFilter))
        {
            _selectedTagFilter = keep;
            OnPropertyChanged(nameof(SelectedTagFilter));
            GamesView.Refresh();
        }
    }

    private void RaiseLibraryTexts()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(AllCount));
        OnPropertyChanged(nameof(GamesFilterLabel));
        OnPropertyChanged(nameof(SoftwareFilterLabel));
        OnPropertyChanged(nameof(ToolsFilterLabel));
    }

    private void SetViewMode(LibraryViewMode mode)
    {
        if (_settings.ViewMode == mode) return;
        _settings.ViewMode = mode;
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsListView));
        _saveSettings.Request();
    }

    // =====================================================================================
    // Cuenta de Steam (SteamKit2)
    // =====================================================================================

    private async Task LoginAsync()
    {
        if (Dialogs is null) return;
        if (!await Dialogs.SteamLoginAsync()) return;
        StatusText = $"Conectado a Steam como {_session.AccountName}. Cargando tu biblioteca…";
        await RefreshOwnershipAsync();
        StatusText = $"Biblioteca de Steam actualizada ({_ownership.Cache.Apps.Count(a => a.Type == "game")} juegos en tu cuenta).";
    }

    private async Task LogoutAsync()
    {
        if (!(Dialogs?.Confirm("Cerrar sesión de Steam",
                "¿Cerrar la sesión de Steam en Purgine? Se borra el token guardado; para descargar tendrás que volver a iniciar sesión.") ?? false))
            return;
        _downloads.PauseAll();
        await _session.LogOutAsync();
        _ownership.Clear();
        UpdateAccountText();
        await RefreshAsync();
    }

    private void OnSessionState(SteamSessionState state, string message)
    {
        if (state is SteamSessionState.Failed) StatusText = message;
        UpdateAccountText();
    }

    /// <summary>Banner de la biblioteca: sin sesión solo se ven los juegos instalados.</summary>
    public bool ShowSignInBanner => !_session.HasSavedSession && _orchestrator.IsSteamInstalled;

    public ICommand SignInCommand => _signInCommand ??= new RelayCommand(async _ => await LoginAsync());
    private ICommand? _signInCommand;

    private void UpdateAccountText()
    {
        OnPropertyChanged(nameof(ShowSignInBanner));
        var name = _session.AccountName ?? _session.SavedAccountName;
        var games = _ownership.Cache.Apps.Count(a => a.Type == "game");
        var updated = _ownership.HasData ? $" · biblioteca consultada {Formatting.RelativeDate(_ownership.Cache.UpdatedUtc)} ({games} juegos)" : string.Empty;
        Settings.SetAccountState(
            name is null ? "Sin sesión. Inicia sesión para ver tu biblioteca completa y descargar sin el cliente de Steam." :
            _session.IsLoggedIn ? $"Conectado como {name}{updated}" : $"Sesión guardada de {name}{updated}",
            loggedIn: name is not null);
    }

    /// <summary>Asegura sesión para descargar; si no hay, ofrece iniciarla.</summary>
    private async Task<bool> EnsureSteamAccountAsync()
    {
        if (_session.HasSavedSession) return true;
        if (!(Dialogs?.Confirm("Inicia sesión en Steam",
                "Para descargar juegos sin el cliente de Steam, Purgine necesita iniciar sesión con tu cuenta.\n\n" +
                "Tu contraseña no se guarda: solo un token de sesión, cifrado en este equipo.\n\n¿Iniciar sesión ahora?") ?? false))
            return false;
        await LoginAsync();
        return _session.HasSavedSession;
    }

    // =====================================================================================
    // Cuenta de Epic Games (sin su launcher)
    // =====================================================================================

    private async Task EpicLoginAsync()
    {
        if (Dialogs is null || !Dialogs.EpicLogin()) return;
        StatusText = $"Cuenta de Epic Games vinculada: {_epic.DisplayName}.";
        UpdateEpicAccountText();
        await SyncEpicAccountAsync();
    }

    private async Task EpicLogoutAsync()
    {
        if (!(Dialogs?.Confirm("Desvincular Epic Games",
                "¿Desvincular tu cuenta de Epic Games de Purgine? Se borra el token guardado; para jugar a los juegos de Epic desde Purgine tendrás que volver a iniciar sesión.") ?? false))
            return;
        await _epic.LogOutAsync();
        UpdateEpicAccountText();
    }

    private void UpdateEpicAccountText() =>
        Settings.SetEpicAccountState(_epic.HasSession
            ? $"Vinculada como {_epic.DisplayName}. Los juegos de Epic se lanzan directamente, sin Epic Games Launcher."
            : "Sin vincular. Inicia sesión para jugar a los juegos de Epic desde Purgine sin abrir su launcher.", _epic.HasSession);

    /// <summary>Para jugar a un juego de Epic hace falta la cuenta vinculada (una sola vez).</summary>
    private async Task<bool> EnsureEpicAccountAsync()
    {
        if (_epic.HasSession) return true;
        if (!(Dialogs?.Confirm("Vincula tu cuenta de Epic Games",
                "Para jugar a los juegos de Epic sin abrir Epic Games Launcher, Purgine necesita tu cuenta de Epic (solo una vez).\n\n" +
                "Inicias sesión en la página oficial de Epic; tu contraseña no pasa por Purgine: solo se guarda un token, cifrado en este equipo.\n\n¿Vincular ahora?") ?? false))
            return false;
        await EpicLoginAsync();
        return _epic.HasSession;
    }

    /// <summary>
    /// Datos de la cuenta de Epic: tiempo jugado (se suma al de Purgine) y arte del catálogo (portada, imagen
    /// panorámica y logo) para los juegos de Epic instalados.
    /// </summary>
    private async Task SyncEpicAccountAsync()
    {
        try
        {
            var playtime = await Task.Run(() => _epic.GetPlaytimeAsync(CancellationToken.None));
            if (playtime is not null)
            {
                _tracker.SetPlatformPlaytime("epic", playtime.ToDictionary(
                    kv => GameEntry.MakeEpicId(kv.Key), kv => new SteamAppUsage(kv.Key, kv.Value, null)));
                foreach (var g in Games.Where(g => g.IsEpic)) g.RefreshStats();
                RaiseLibraryTexts();
            }
            await SyncEpicOwnedAsync();
            foreach (var card in Games.Where(g => g.IsEpic).ToList())
            {
                if (card.Entry.EpicNamespace is not { } ns || card.Entry.EpicCatalogItemId is not { } id) continue;
                var info = await Task.Run(() => _epic.GetCatalogInfoAsync(ns, id, CancellationToken.None));
                if (info is null) continue;
                card.RemoteHeroUrl = info.HeroUrl;
                card.RemoteLogoUrl = info.LogoUrl;
                if (info.CoverUrl is { } cover && card.Entry.CoverImagePath is null)
                    card.SetCover(await _art.LoadRemoteAsync(cover, $"epic_cover_{card.Entry.AppId}.jpg", 180));
            }
            await SyncEpicBuildsAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Epic: no se pudieron leer los datos de la cuenta: {ex.Message}");
        }
    }

    /// <summary>
    /// Biblioteca completa de Epic: lo comprado o reclamado que no está instalado aparece como «No instalado»
    /// (sin Unreal Engine/Fab, contenidos descargables ni lo que no es juego o aplicación).
    /// </summary>
    private async Task SyncEpicOwnedAsync()
    {
        var items = await Task.Run(() => _epic.GetLibraryItemsAsync(CancellationToken.None));
        if (items is null) return;
        var owned = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Namespace == "ue" || !seen.Add(item.AppName)) continue;
            var info = await Task.Run(() => _epic.GetCatalogInfoAsync(item.Namespace, item.CatalogItemId, CancellationToken.None));
            if (info is null || info.IsAddOn || !(info.IsGame || info.IsApplication)) continue;
            owned.Add(new GameEntry
            {
                Id = GameEntry.MakeEpicId(item.AppName),
                Platform = GamePlatform.Epic,
                AppId = item.AppName,
                Name = info.Title.Trim(),
                IsInstalled = false,
                EpicNamespace = item.Namespace,
                EpicCatalogItemId = item.CatalogItemId,
                EpicCategories = info.IsGame ? ["games"] : ["applications"],
            });
        }
        _epicOwned = owned;
        var before = Games.Count;
        AddEpicOwned();
        if (Games.Count != before)
        {
            ApplyRemoteUpdates();
            SyncDownloads();
            RebuildTagFilters();
            RaiseLibraryTexts();
        }
    }

    /// <summary>
    /// Build pública de cada juego de Epic (actualizaciones) y, si cambió, su tamaño instalado según el
    /// manifiesto. Uno a uno y con caché en disco: tras la primera vez solo se consulta la versión.
    /// </summary>
    private async Task SyncEpicBuildsAsync()
    {
        var changed = false;
        foreach (var card in Games.Where(g => g.IsEpic).ToList())
        {
            var e = card.Entry;
            if (e.EpicNamespace is not { } ns || e.EpicCatalogItemId is not { } item) continue;
            try
            {
                var asset = await Task.Run(() => _epic.GetAssetInfoAsync(ns, item, e.AppId, CancellationToken.None));
                if (asset is null) continue;
                _epicBuilds.TryGetValue(e.AppId, out var known);
                if (known is not null && known.BuildVersion == asset.BuildVersion) continue;
                // Instalados: el tamaño ya se conoce (no hace falta su manifiesto, que puede pesar decenas de MB).
                long size = card.IsNotInstalled ? 0 : e.SizeOnDisk;
                if (card.IsNotInstalled)
                {
                    try
                    {
                        var bytes = await Task.Run(() => _epic.DownloadManifestAsync(asset, CancellationToken.None));
                        size = await Task.Run(() => EpicNet.EpicManifest.Parse(bytes).InstallSize);
                    }
                    catch (NotSupportedException) { /* manifiesto antiguo: sin tamaño */ }
                }
                _epicBuilds[e.AppId] = new EpicBuildInfo(asset.BuildVersion, size);
                changed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Epic: versión de {e.Name} no disponible: {ex.Message}");
            }
        }
        if (!changed) return;
        JsonFileStore.Save(AppPaths.EpicBuildsFile, _epicBuilds);
        ApplyRemoteUpdates();
    }

    // =====================================================================================
    // Descargas nativas (IDownloadActions)
    // =====================================================================================

    private IReadOnlyList<DownloadJob> AllJobs => [.. _downloads.Jobs, .. _epicDownloads.Jobs];

    private void SyncDownloads()
    {
        var jobs = AllJobs;
        Downloads.SyncJobs(jobs);
        foreach (var g in Games.Where(g => g.IsSteam || g.IsEpic))
            g.SetJob(jobs.FirstOrDefault(j => j.GameKey == g.Entry.Id));
        GamesView.Refresh();
    }

    private void OnJobChanged(DownloadJob job)
    {
        Downloads.RefreshJob(job);
        Games.FirstOrDefault(g => g.Entry.Id == job.GameKey)?.SetJob(job);
    }

    private void OnJobFinished(DownloadJob job)
    {
        Downloads.AddFinished(job);
        var card = Games.FirstOrDefault(g => g.Entry.Id == job.GameKey);
        card?.SetJob(null);

        switch (job.State)
        {
            case DownloadJobState.Completed:
                StatusText = $"{job.Name}: {JobText.Phase(job).ToLowerInvariant()} y listo para jugar.";
                Notify(job.Kind == DownloadJobKind.Update ? "Actualización completada" : "Instalación completada",
                    $"{job.Name} está listo para jugar.");
                if (job.IsEpic)
                {
                    if (EpicLibraryScanner.Scan().FirstOrDefault(e => e.AppId == job.EpicAppName) is { } epicEntry)
                    {
                        if (_epicBuilds.TryGetValue(epicEntry.AppId, out var b) && epicEntry.EpicVersion is { } v)
                            _epicBuilds[epicEntry.AppId] = b with { BuildVersion = v };
                        UpsertGame(epicEntry);
                        _ = SyncEpicAccountAsync(); // arte del catálogo en la tarjeta ya instalada
                    }
                }
                else if (_scanner.FindGame(job.AppId.ToString()) is { } entry) UpsertGame(entry);
                if (job.Kind == DownloadJobKind.Install) _ = CreateShortcutsAsync(job);
                break;
            case DownloadJobState.Failed:
                StatusText = $"{job.Name}: {job.Message}";
                Notify("Error en la descarga", $"{job.Name}: {job.Message}");
                break;
            default:
                StatusText = $"{job.Name}: {job.Message}";
                break;
        }
        GamesView.Refresh();
    }

    public void PauseDownload(DownloadJob job)
    {
        if (job.IsEpic) _epicDownloads.Pause(job); else _downloads.Pause(job);
    }

    public void ResumeDownload(DownloadJob job)
    {
        if (job.IsEpic) _epicDownloads.Resume(job); else _downloads.Resume(job);
    }

    public void CancelDownload(DownloadJob job)
    {
        var message = job.Kind == DownloadJobKind.Install
            ? $"¿Cancelar la instalación de «{job.Name}»? Se borrará lo descargado."
            : $"¿Cancelar la actualización de «{job.Name}»? Los archivos quedarán a medio actualizar hasta que vuelvas a actualizar.";
        if (!(Dialogs?.Confirm("Cancelar descarga", message) ?? false)) return;
        if (job.IsEpic) _epicDownloads.Cancel(job); else _downloads.Cancel(job);
    }

    public void PauseAllDownloads() { _downloads.PauseAll(); _epicDownloads.PauseAll(); }

    public void ResumeAllDownloads() { _downloads.ResumeAll(); _epicDownloads.ResumeAll(); }

    public void PlayDownloaded(string gameKey)
    {
        var card = Games.FirstOrDefault(g => g.Entry.Id == gameKey && !g.IsNotInstalled);
        if (card is null) return;
        CurrentView = AppView.Library;
        Launch(card);
    }

    public ImageSource? CoverFor(string gameKey) =>
        Games.FirstOrDefault(g => g.Entry.Id == gameKey)?.Cover;

    // ----- Instalar / actualizar / desinstalar (ICardHost) -----

    public void Install(GameCardViewModel card) => _ = EnqueueAsync(card.Entry, DownloadJobKind.Install);

    public void Update(GameCardViewModel card)
    {
        if (card.IsRunning || !card.ShowUpdateButton) return;
        _ = EnqueueAsync(card.Entry, DownloadJobKind.Update);
    }

    private async Task EnqueueAsync(GameEntry entry, DownloadJobKind kind)
    {
        if (entry.Platform == GamePlatform.Epic) { await EnqueueEpicAsync(entry, kind); return; }
        if (entry.Platform != GamePlatform.Steam) return;
        if (!await EnsureSteamAccountAsync()) return;

        DownloadJob job;
        if (kind == DownloadJobKind.Install && _downloads.Find(uint.Parse(entry.AppId)) is null)
        {
            // Antes de una instalación nueva: accesos directos y unidad de destino (como en el cliente de Steam).
            var choice = await AskInstallOptionsAsync(entry);
            if (choice is null)
            {
                StatusText = $"Instalación de {entry.Name} cancelada.";
                return;
            }
            job = _downloads.Enqueue(entry, kind, choice.LibraryPath, choice.CreateDesktopShortcut, choice.CreateStartMenuShortcut);
        }
        else
        {
            job = _downloads.Enqueue(entry, kind);
        }
        StatusText = kind == DownloadJobKind.Install
            ? $"{entry.Name} en la cola de descargas (directo desde la CDN de Steam)."
            : $"Actualizando {entry.Name} (solo se descargará lo que ha cambiado).";
        SyncDownloads();
        CurrentView = AppView.Downloads;
        _ = job;
    }

    /// <summary>Instalar o actualizar un juego de Epic sin Epic Games Launcher (directo desde su CDN).</summary>
    private async Task EnqueueEpicAsync(GameEntry entry, DownloadJobKind kind)
    {
        if (!await EnsureEpicAccountAsync()) return;
        if (kind == DownloadJobKind.Install && _epicDownloads.Find(entry.AppId) is null)
        {
            StatusText = $"Consultando el tamaño de {entry.Name} en Epic…";
            long size = _epicBuilds.TryGetValue(entry.AppId, out var b) ? b.InstallSize : 0;
            if (size <= 0)
            {
                try
                {
                    size = await Task.Run(() => _epicDownloads.Engine.GetInstallSizeAsync(
                        entry.EpicNamespace!, entry.EpicCatalogItemId!, entry.AppId, CancellationToken.None)) ?? 0;
                }
                catch (NotSupportedException ex)
                {
                    StatusText = $"{entry.Name}: {ex.Message}";
                    Notify("No se puede instalar", $"{entry.Name}: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Epic: tamaño de {entry.Name}: {ex.Message}");
                }
            }
            var choice = await AskInstallOptionsAsync(entry, (ulong)Math.Max(0, size));
            if (choice is null)
            {
                StatusText = $"Instalación de {entry.Name} cancelada.";
                return;
            }
            _epicDownloads.Enqueue(entry, kind, choice.LibraryPath, choice.CreateDesktopShortcut, choice.CreateStartMenuShortcut);
        }
        else
        {
            _epicDownloads.Enqueue(entry, kind);
        }
        StatusText = kind == DownloadJobKind.Install
            ? $"{entry.Name} en la cola de descargas (directo desde la CDN de Epic, sin su launcher)."
            : $"Actualizando {entry.Name} (solo se descargará lo que ha cambiado).";
        SyncDownloads();
        CurrentView = AppView.Downloads;
    }

    // ----- Diálogo de instalar (superposición dentro de la ventana) -----

    private InstallDialogViewModel? _installDialog;

    /// <summary>Diálogo de instalar abierto (null = cerrado).</summary>
    public InstallDialogViewModel? InstallDialog
    {
        get => _installDialog;
        private set
        {
            if (!SetProperty(ref _installDialog, value)) return;
            Store.IsCovered = value is not null; // el navegador (HWND) se dibujaría encima del diálogo
        }
    }

    private async Task<InstallChoice?> AskInstallOptionsAsync(GameEntry entry, ulong? knownSize = null)
    {
        if (_installDialog is not null) return null; // ya hay uno abierto
        var isEpic = entry.Platform == GamePlatform.Epic;
        uint.TryParse(entry.AppId, out var appId);
        var size = knownSize ?? _ownership.Find(appId)?.InstallSize ?? 0;

        IReadOnlyList<string> libraries = isEpic ? [] : _downloads.Libraries;
        var defaultRoot = isEpic ? DefaultEpicDrive()
            : _downloads.InstallTarget() is { } target ? Path.GetPathRoot(target.Library) : null;
        var drives = new List<DriveOptionViewModel>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                var library = isEpic ? EpicNet.EpicDownloadManager.LibraryForDrive(root) : LibraryFolders.ForDrive(libraries, root);
                var isNew = isEpic ? !Directory.Exists(library) : !libraries.Any(l => string.Equals(l, library, StringComparison.OrdinalIgnoreCase));
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Unidad local" : drive.VolumeLabel;
                drives.Add(new DriveOptionViewModel(root, $"{label} ({root.TrimEnd('\\')})", drive.AvailableFreeSpace, library,
                    string.Equals(root, defaultRoot, StringComparison.OrdinalIgnoreCase), isNew, size));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unidad que deja de responder: se omite
            }
        }

        var dialog = new InstallDialogViewModel(entry.Name, size, drives);
        InstallDialog = dialog;
        if (isEpic) dialog.Banner = Games.FirstOrDefault(g => g.Entry.Id == entry.Id)?.Cover;
        else _ = LoadBannerAsync(dialog, entry.AppId);
        try
        {
            return await dialog.Result;
        }
        finally
        {
            InstallDialog = null;
        }
    }

    /// <summary>Unidad por defecto para Epic: la que ya tiene «Epic Games»; si no, la de más espacio libre.</summary>
    private static string? DefaultEpicDrive()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .OrderByDescending(d => Directory.Exists(EpicNet.EpicDownloadManager.LibraryForDrive(d.RootDirectory.FullName)))
                .ThenByDescending(d => d.AvailableFreeSpace)
                .Select(d => d.RootDirectory.FullName).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task LoadBannerAsync(InstallDialogViewModel dialog, string appId)
    {
        dialog.Banner = await _art.LoadHeaderAsync(appId, 240)
                        ?? Games.FirstOrDefault(g => g.IsSteam && g.Entry.AppId == appId)?.Cover;
    }

    /// <summary>Instalación terminada: accesos directos elegidos en el diálogo (a Purgine, no a steam://).</summary>
    private async Task CreateShortcutsAsync(DownloadJob job)
    {
        if (!job.CreateDesktopShortcut && !job.CreateStartMenuShortcut) return;
        if (job.IsEpic)
        {
            // El icono es el del propio ejecutable del juego; el acceso directo abre Purgine (--run epic:App).
            var exe = Games.FirstOrDefault(g => g.Entry.Id == job.GameKey)?.Entry.ExecutablePath;
            var key = job.GameKey;
            await Task.Run(() =>
            {
                if (job.CreateDesktopShortcut) ShortcutWriter.CreateGameShortcut(ShortcutWriter.DesktopFolder, job.Name, key, exe);
                if (job.CreateStartMenuShortcut) ShortcutWriter.CreateGameShortcut(ShortcutWriter.StartMenuFolder, job.Name, key, exe);
            });
            return;
        }
        var icon = await _art.GetClientIconFileAsync(job.AppId.ToString(), _ownership.Find(job.AppId)?.ClientIcon);
        var appId = job.AppId.ToString();
        await Task.Run(() =>
        {
            if (job.CreateDesktopShortcut) ShortcutWriter.CreateGameShortcut(ShortcutWriter.DesktopFolder, job.Name, appId, icon);
            if (job.CreateStartMenuShortcut) ShortcutWriter.CreateGameShortcut(ShortcutWriter.StartMenuFolder, job.Name, appId, icon);
        });
    }

    // ----- Lanzamiento desde un acceso directo (Purgine.exe --run {appid}) -----

    private string? _pendingLaunch;

    /// <summary>Jugar a un juego pedido desde fuera (acceso directo). Si la biblioteca aún carga, se aplaza.</summary>
    public void RequestLaunch(string appId)
    {
        if (IsScanning || Games.Count == 0) { _pendingLaunch = appId; return; }
        _pendingLaunch = null;
        var card = appId.StartsWith("epic:", StringComparison.Ordinal)
            ? Games.FirstOrDefault(g => g.Entry.Id == appId)
            : Games.FirstOrDefault(g => g.IsSteam && g.Entry.AppId == appId);
        if (card is null) { StatusText = $"El juego {appId} no está en tu biblioteca."; return; }
        if (card.IsNotInstalled) { ShowDetails(card); return; }
        Launch(card);
    }

    public void CancelInstall(GameCardViewModel card)
    {
        if (card.Job is { } job) CancelDownload(job);
    }

    public void Uninstall(GameCardViewModel card)
    {
        if (!card.CanUninstall) return;
        if (!(Dialogs?.Confirm("Desinstalar",
                $"¿Desinstalar «{card.Name}»?\n\nSe borrarán de forma permanente los archivos del juego de este equipo " +
                "(no van a la papelera). Las partidas en la nube y el tiempo jugado se conservan.") ?? false))
            return;

        var error = card.IsEpic ? _epicDownloads.Uninstall(card.Entry) : _downloads.Uninstall(card.Entry);
        if (error is null && card.IsEpic) _ = RefreshAsync(); // vuelve a «No instalado» con los de la cuenta
        StatusText = error ?? $"{card.Name} desinstalado.";
        if (error is not null) Notify("No se pudo desinstalar", error);
    }

    // =====================================================================================
    // Lanzamiento (Steam solo se usa para esto)
    // =====================================================================================

    public bool CanLaunch => !IsSessionActive;

    public void Launch(GameCardViewModel card)
    {
        if (card.HasActiveJob) { CurrentView = AppView.Downloads; return; }
        if (card.IsNotInstalled) { Install(card); return; }
        _ = LaunchAsync(card);
    }

    private async Task LaunchAsync(GameCardViewModel card)
    {
        if (IsSessionActive || _orchestrator.IsBusy) return;
        if (card.IsEpic && !await EnsureEpicAccountAsync()) return;

        _sessionCts = new CancellationTokenSource();
        _cloudNotified = false;
        IsSessionActive = true;
        card.IsRunning = true;
        CommandManager.InvalidateRequerySuggested();
        if (_settings.MinimizeOnLaunch) GameLaunched?.Invoke(this, EventArgs.Empty);

        try
        {
            // Juego instalado por Purgine con Steam ya abierto: ese Steam no lo ve hasta reiniciarse.
            if (card.IsSteam && uint.TryParse(card.Entry.AppId, out var appId) && _downloads.NeedsSteamRestartFor(appId))
                await Task.Run(_orchestrator.CloseSteamAsync);

            var token = _sessionCts.Token;
            var result = await Task.Run(() => _orchestrator.LaunchAsync(card.Entry, token));
            if (!result.Success)
            {
                StatusText = result.Message;
                if (result.Message != "Vigilancia cancelada.") Notify("No se pudo jugar", result.Message);
            }
            else if (card.IsLocal)
            {
                Notify("Sesión terminada", result.Message);
            }
        }
        finally
        {
            card.IsRunning = false;
            card.RefreshStats();
            foreach (var g in Games.Where(g => g.Entry.Id == card.Entry.Id))
            {
                g.IsRunning = false;
                g.RefreshStats();
            }
            IsSessionActive = false;
            IsBannerVisible = false;
            _sessionCts.Dispose();
            _sessionCts = null;
            RaiseLibraryTexts();
            CommandManager.InvalidateRequerySuggested();
        }

        if (card.IsSteam) await ReloadSteamPlaytimeAsync();
        SessionFinished?.Invoke(this, EventArgs.Empty);
    }

    private void OnOrchestratorStatus(OrchestratorStatus s)
    {
        StatusText = s.SecondsRemaining is int n ? $"{s.Message} ({n} s)" : s.Message;

        switch (s.Phase)
        {
            case LaunchPhase.SyncingCloud:
            {
                var launcher = s.Game?.Platform == GamePlatform.Epic ? "Epic Games Launcher" : "Steam";
                IsBannerVisible = true;
                BannerText = s.Game?.Platform == GamePlatform.Epic
                    ? "Sincronizando partidas guardadas con la nube de Epic…"
                    : "Sincronizando partidas guardadas con Steam Cloud…";
                BannerDetail = s.SecondsRemaining is int left
                    ? $"{launcher} se cerrará en {left} s. No apagues el equipo."
                    : $"Esperando a {launcher}…";
                if (!_cloudNotified)
                {
                    _cloudNotified = true;
                    Notify(s.Game?.Platform == GamePlatform.Epic ? "Nube de Epic" : "Steam Cloud", "Sincronizando partidas guardadas…");
                }
                break;
            }

            case LaunchPhase.CleaningUp:
                IsBannerVisible = true;
                BannerText = s.Game?.Platform == GamePlatform.Epic ? "Cerrando Epic Games Launcher…" : "Cerrando Steam de forma segura…";
                BannerDetail = "El launcher termina de subir tus partidas y se cierra. Después se liberará la memoria.";
                break;

            case LaunchPhase.Finished when s.Game?.Platform is GamePlatform.Steam or GamePlatform.Epic:
                IsBannerVisible = false;
                Notify("Sesión terminada", s.Message);
                break;

            default:
                IsBannerVisible = false;
                break;
        }
    }

    // =====================================================================================
    // ICardHost (menú contextual de las tarjetas)
    // =====================================================================================

    public void ForceClose(GameCardViewModel card) => ForceCloseRunning();

    public void ShowProperties(GameCardViewModel card)
    {
        if (!card.IsSteam || card.IsNotInstalled) return;
        Dialogs?.ShowGameProperties(new GamePropertiesViewModel(card, _session, _ownership, _community));
    }

    /// <summary>
    /// Forzar cierre: confirma (se pierde lo no guardado), termina el juego y sus procesos hijos y cancela la
    /// sesión; la interfaz vuelve a «Listo para jugar» al momento y Steam se cierra en segundo plano si toca.
    /// </summary>
    private void ForceCloseRunning()
    {
        if (!IsSessionActive) return;
        var name = Games.FirstOrDefault(g => g.IsRunning)?.Name ?? "el juego";
        if (!(Dialogs?.Confirm("Forzar cierre",
                $"¿Cerrar {name} a la fuerza?\n\nSe terminan el juego y todos sus procesos al instante: se perderá lo que no esté guardado. " +
                "El tiempo jugado se registra igualmente.") ?? false))
            return;
        var killed = _orchestrator.ForceStopGame();
        _sessionCts?.Cancel();
        StatusText = killed > 0 ? $"{name} cerrado a la fuerza. Listo para jugar." : $"No quedaba ningún proceso de {name}. Listo para jugar.";
    }

    public void CreateTagFor(GameCardViewModel card)
    {
        var name = Dialogs?.PromptText("Nueva etiqueta", $"Nombre de la etiqueta para «{card.Name}»:");
        if (string.IsNullOrWhiteSpace(name)) return;
        _library.AddTag(name);
        _library.SetTag(card.Entry.Id, name.Trim(), true);
    }

    public void OpenInstallFolder(GameCardViewModel card)
    {
        if (card.Entry.InstallDir is { } dir && Directory.Exists(dir)) OpenPath(dir);
        else StatusText = "La carpeta de instalación no existe.";
    }

    public void ViewInStore(GameCardViewModel card)
    {
        if (card.IsEpic) Store.Navigate(StoreViewModel.EpicSearchUrl(card.Name));
        else Store.ShowApp(card.Entry.AppId);
        CurrentView = AppView.Store;
    }

    public void EditLocal(GameCardViewModel card)
    {
        var def = _library.FindLocal(card.Entry.AppId);
        if (def is null) return;
        var edited = Dialogs?.EditLocalGame(def);
        if (edited is not null) _library.AddOrUpdateLocalGame(edited);
    }

    public void RemoveLocal(GameCardViewModel card)
    {
        if (!(Dialogs?.Confirm("Quitar de la biblioteca",
                $"¿Quitar «{card.Name}» de Purgine?\n\nNo se borra ningún archivo del juego; solo deja de aparecer aquí. El tiempo jugado se conserva.") ?? false))
            return;
        _library.RemoveLocalGame(card.Entry.AppId);
        StatusText = $"«{card.Name}» quitado de la biblioteca.";
    }

    private void AddLocalGame()
    {
        var def = Dialogs?.EditLocalGame(null);
        if (def is null) return;
        _library.AddOrUpdateLocalGame(def);
        CurrentView = AppView.Library;
        StatusText = $"«{def.Name}» añadido. Haz clic en su portada para jugar.";
    }

    private void PromptNewTag()
    {
        var name = Dialogs?.PromptText("Nueva etiqueta", "Nombre de la etiqueta:");
        if (!string.IsNullOrWhiteSpace(name) && !_library.AddTag(name))
            StatusText = $"La etiqueta «{name.Trim()}» ya existe.";
    }

    // =====================================================================================
    // IStoreActions (enlaces steam:// de la tienda web)
    // =====================================================================================

    private bool IsKnownOwned(string appId) =>
        Games.Any(g => g.IsSteam && !g.IsNotInstalled && g.Entry.AppId == appId) || _owned.Contains(appId);

    public void ReportStatus(string text) => StatusText = text;

    public void PlayOrInstallFromStore(string appId, string? name, bool installOnly)
    {
        var card = Games.FirstOrDefault(g => g.IsSteam && g.Entry.AppId == appId);
        if (card is { IsNotInstalled: false })
        {
            if (installOnly) { ShowDetails(card); StatusText = $"{card.Name} ya está instalado."; }
            else Launch(card);
            return;
        }
        _ = InstallFromStoreAsync(appId, card?.Name ?? name ?? $"App {appId}");
    }

    /// <summary>
    /// Instalar desde la Tienda, sin el cliente: de la cuenta → a la cola; si aún no consta en la cuenta se pide
    /// su licencia gratuita por la red (SteamKit2): Steam solo la concede si el juego es gratuito.
    /// </summary>
    private async Task InstallFromStoreAsync(string appId, string name)
    {
        if (!uint.TryParse(appId, out var id)) return;
        if (!await EnsureSteamAccountAsync()) return;

        if (!IsKnownOwned(appId))
        {
            StatusText = $"Añadiendo {name} a tu cuenta…";
            var granted = await Task.Run(async () =>
                await _session.LoginWithSavedSessionAsync(CancellationToken.None) &&
                await _session.RequestFreeLicenseAsync(id));
            if (!granted)
            {
                StatusText = $"{name} no está en tu cuenta. Si es de pago, cómpralo en la tienda y vuelve a pulsar «Instalar».";
                return;
            }
            await RefreshOwnershipAsync();
        }

        var card = Games.FirstOrDefault(g => g.IsSteam && g.Entry.AppId == appId);
        var owned = _ownership.Find(id);
        var entry = card?.Entry ?? _scanner.CreateNotInstalledEntry(appId, owned?.Name ?? name, owned?.InstallDir);
        if (entry.IsInstalled) { CurrentView = AppView.Library; return; }
        await EnqueueAsync(entry, DownloadJobKind.Install);
    }

    // =====================================================================================
    // Ciclo de vida de la ventana
    // =====================================================================================

    /// <summary>
    /// La ventana se ocultó en la bandeja: cerrar el navegador de la tienda (sus procesos) y soltar las
    /// imágenes grandes de la ficha abierta.
    /// </summary>
    public void OnHiddenToTray()
    {
        Store.Release();
        _detail?.ReleaseImages();
    }

    /// <summary>La ventana vuelve de la bandeja: recargar lo que se soltó.</summary>
    public void OnRestoredFromTray()
    {
        if (_detail is not null) _ = _detail.LoadAsync();
    }

    /// <summary>Guardar lo pendiente al salir (las descargas quedan en pausa y se recuperan al volver).</summary>
    public void Shutdown()
    {
        _notes.Flush();
        Store.Release();
        _detail?.Close();
        _downloads.Dispose();
        _epicDownloads.Dispose();
        _saveSettings.Flush();
        _saveSettings.Dispose();
        _watcher.Dispose();
    }

    private void Notify(string title, string text) => NotificationRequested?.Invoke(title, text);

    private void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo abrir {path}: {ex.Message}");
            StatusText = $"No se pudo abrir: {ex.Message}";
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>Build pública de un juego de Epic y su tamaño instalado (caché <c>epic-builds.json</c>).</summary>
public sealed record EpicBuildInfo(string BuildVersion, long InstallSize);
