using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LightLauncher.Core;
using LightLauncher.Infrastructure;
using LightLauncher.Library;
using LightLauncher.Settings;
using LightLauncher.Stats;
using LightLauncher.Store;

namespace LightLauncher.ViewModels;

public enum AppView
{
    Library,
    Store,
    Downloads,
    Settings,
}

/// <summary>Chip de filtro por etiqueta («Todos» tiene <see cref="Tag"/> = null).</summary>
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
/// Estado de la ventana principal: navegación, biblioteca (búsqueda, etiquetas, vistas),
/// sesión en curso y banner de sincronización en la nube.
/// </summary>
public sealed class MainViewModel : ObservableObject, ICardHost, IStoreActions, IDownloadActions
{
    private readonly DownloadMonitor _monitor;
    private readonly SteamSuspender _suspender;
    private readonly SteamLibraryScanner _scanner;
    private readonly ProcessOrchestrator _orchestrator;
    private readonly GameTimeTracker _tracker;
    private readonly UserLibrary _library;
    private readonly LauncherSettings _settings;
    private readonly LibraryWatcher _watcher;
    private readonly DebouncedAction _saveSettings;
    private readonly SteamInstaller _installer;
    private readonly AppNameCache _names;
    private readonly SteamStoreService _storeService;

    /// <summary>AppIDs de la cuenta según la caché local (instalados o no).</summary>
    private IReadOnlySet<string> _owned = new HashSet<string>();
    private IReadOnlyDictionary<string, SteamAppBasics> _appInfo = new Dictionary<string, SteamAppBasics>();
    /// <summary>Instalaciones en segundo plano lanzadas desde aquí que aún no han terminado.</summary>
    private readonly HashSet<string> _pendingInstalls = [];
    /// <summary>Steam lo abrimos nosotros para descargar: se cierra al acabar (según la política).</summary>
    private bool _steamOpenedForInstall;

    private CancellationTokenSource? _sessionCts;
    private IReadOnlyDictionary<string, SteamAppUsage> _steamUsage = new Dictionary<string, SteamAppUsage>();
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

    public MainViewModel(
        SteamLibraryScanner scanner,
        ProcessOrchestrator orchestrator,
        GameTimeTracker tracker,
        UserLibrary library,
        LauncherSettings settings,
        SteamStoreService store,
        LibraryWatcher watcher,
        SteamLocator steam,
        SteamInstaller installer,
        AppNameCache names,
        DownloadMonitor monitor,
        SteamSuspender suspender,
        Action saveSettings)
    {
        _monitor = monitor;
        _suspender = suspender;
        _installer = installer;
        _names = names;
        _storeService = store;
        _scanner = scanner;
        _orchestrator = orchestrator;
        _tracker = tracker;
        _library = library;
        _settings = settings;
        _watcher = watcher;
        _saveSettings = new DebouncedAction(saveSettings, TimeSpan.FromMilliseconds(600));

        Store = new StoreViewModel(store, this);
        Downloads = new DownloadsViewModel(this);
        _monitor.Changed += list => OnUi(() => OnDownloadsChanged(list));
        _monitor.Finished += info => OnUi(() => Downloads.AddFinished(info));
        Settings = new SettingsViewModel(settings, library, steam, _saveSettings.Request, () => Dialogs);

        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.Name), ListSortDirection.Ascending));
        GamesView.Filter = FilterGame;

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning);
        CancelCommand = new RelayCommand(_ => _sessionCts?.Cancel(), _ => IsSessionActive);
        OpenDataFolderCommand = new RelayCommand(_ => OpenPath(AppPaths.DataDir));
        AddLocalGameCommand = new RelayCommand(_ => AddLocalGame());
        NewTagCommand = new RelayCommand(_ => PromptNewTag());
        ShowViewCommand = new RelayCommand(p => { if (p is AppView v) CurrentView = v; else if (p is string s && Enum.TryParse<AppView>(s, out var pv)) CurrentView = pv; });

        _orchestrator.StatusChanged += (_, s) => OnUi(() => OnOrchestratorStatus(s));
        _library.Changed += (_, _) => OnUi(OnLibraryChanged);
        _watcher.ManifestChanged += path => _ = Task.Run(() => _scanner.ParseManifestFile(path))
            .ContinueWith(t => { if (t.Result is { } e) OnUi(() => UpsertGame(e)); }, TaskContinuationOptions.OnlyOnRanToCompletion);
        _watcher.ManifestDeleted += appId => OnUi(() => OnManifestDeleted(appId));
        _tracker.SessionRecorded += (_, _) => OnUi(() => OnPropertyChanged(nameof(SummaryText)));

        RebuildTagFilters();
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
            if (value == AppView.Store) _ = Store.EnsureInitializedAsync();
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

    public int InstalledCount => Games.Count(g => !g.IsNotInstalled);
    public int AllCount => Games.Count;

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
            var installed = Games.Count(g => g.IsSteam && !g.IsNotInstalled);
            var notInstalled = Games.Count(g => g.IsNotInstalled);
            var local = Games.Count(g => g.IsLocal);
            var total = _tracker.GetGrandTotal();
            var count = $"{installed} instalados";
            if (notInstalled > 0) count += $" · {notInstalled} más en tu cuenta";
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
                return "No hay juegos todavía.\nInstala alguno desde la pestaña Tienda o añade un ejecutable con «Añadir ejecutable».";
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
    public ICommand OpenDataFolderCommand { get; }
    public ICommand AddLocalGameCommand { get; }
    public ICommand NewTagCommand { get; }
    public ICommand ShowViewCommand { get; }

    // =====================================================================================
    // Carga de biblioteca
    // =====================================================================================

    /// <summary>Escanea Steam (en segundo plano), añade los ejecutables locales y vigila cambios.</summary>
    public async Task RefreshAsync()
    {
        if (IsScanning) return;
        var generation = ++_refreshGeneration;
        IsScanning = true;
        StatusText = "Escaneando bibliotecas…";
        try
        {
            var snapshot = await Task.Run(_scanner.ScanFull);
            ApplySteamUsage(snapshot.Usage); // antes de crear las tarjetas: calculan su tiempo al construirse
            _owned = snapshot.Owned;
            _appInfo = snapshot.AppInfo;

            var running = Games.Where(g => g.IsRunning).Select(g => g.Entry.Id).ToHashSet();
            Games.Clear();
            foreach (var e in snapshot.Installed) Games.Add(CreateCard(e));
            foreach (var e in snapshot.NotInstalled) Games.Add(CreateCard(e));
            foreach (var e in CachedUnresolvedGames(snapshot.UnresolvedAppIds)) Games.Add(CreateCard(e));
            foreach (var local in _library.LocalGames) Games.Add(CreateCard(local.ToEntry()));
            foreach (var g in Games) g.IsRunning = running.Contains(g.Entry.Id);

            _watcher.Watch(snapshot.LibraryFolders);
            StatusText = Games.Count > 0 ? "Listo. Haz clic en un juego para jugar, o en uno no instalado para instalarlo." : "Sin juegos.";
            _ = ResolveUnknownNamesAsync(snapshot.UnresolvedAppIds, generation);
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
            Store.RefreshInstalledState();
        }

        // Portadas una a una, sin bloquear la UI ni disparar picos de memoria.
        foreach (var card in Games.ToList())
        {
            if (generation != _refreshGeneration) break; // otro refresco en marcha
            await card.LoadCoverAsync();
        }
    }

    private GameCardViewModel CreateCard(GameEntry entry) => new(entry, _tracker, _library, this);

    private void ApplySteamUsage(IReadOnlyDictionary<string, SteamAppUsage> usage)
    {
        _steamUsage = usage;
        _tracker.SetSteamPlaytime(usage);
    }

    /// <summary>
    /// Vuelve a leer el histórico de Steam (tras una partida Steam ya ha cerrado y volcado
    /// <c>localconfig.vdf</c>) y refresca los tiempos mostrados.
    /// </summary>
    private async Task ReloadSteamPlaytimeAsync()
    {
        try
        {
            var usage = await Task.Run(_scanner.ReadSteamPlaytime);
            ApplySteamUsage(usage);
            foreach (var g in Games) g.RefreshStats();
            RaiseLibraryTexts();
            Store.RefreshInstalledState();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo recargar el tiempo de Steam: {ex.Message}");
        }
    }

    /// <summary>
    /// Inserta o actualiza un juego a partir de su manifiesto (instalación nueva, progreso de descarga,
    /// actualización). Cuando una instalación termina, avisa y, si abrimos Steam para descargar, lo cierra.
    /// </summary>
    private void UpsertGame(GameEntry entry)
    {
        var existing = Games.FirstOrDefault(g => g.Entry.Id == entry.Id);
        if (existing is not null)
        {
            var wasPending = !existing.Entry.IsFullyInstalled; // no instalado, en cola o descargando
            existing.UpdateEntry(entry);
            if (wasPending && entry.IsFullyInstalled) OnInstallCompleted(entry);
            else if (wasPending && existing.IsDownloading) StatusText = $"{entry.Name}: {existing.DownloadText}";
        }
        else
        {
            var card = CreateCard(entry);
            Games.Add(card);
            _ = card.LoadCoverAsync();
            StatusText = entry.IsFullyInstalled ? $"{entry.Name} añadido a la biblioteca." : $"{entry.Name}: en cola para descargar…";
        }
        RebuildTagFilters();
        RaiseLibraryTexts();
        Store.RefreshInstalledState();
    }

    private void OnInstallCompleted(GameEntry entry)
    {
        StatusText = $"{entry.Name} está instalado y listo para jugar.";
        Notify("Instalación completada", $"{entry.Name} está listo para jugar.");

        _pendingInstalls.Remove(entry.AppId);
        // Si abrimos Steam para descargar, se cerrará cuando TODAS las colas terminen (nunca antes).
        if (_steamOpenedForInstall && !IsSessionActive)
            _orchestrator.ScheduleShutdownWhenIdle(TimeSpan.Zero);
        if (_pendingInstalls.Count == 0) _steamOpenedForInstall = false;
    }

    // =====================================================================================
    // Descargas (IDownloadActions)
    // =====================================================================================

    private void OnDownloadsChanged(IReadOnlyList<DownloadInfo> list)
    {
        Downloads.Apply(list);
        var byId = list.ToDictionary(d => GameEntry.MakeSteamId(d.AppId));
        foreach (var g in Games)
            if (g.IsSteam) g.SetLive(byId.GetValueOrDefault(g.Entry.Id));
    }

    public bool IsSteamPaused => _suspender.IsSuspended;

    public void PauseDownloads()
    {
        if (IsSessionActive) { StatusText = "Hay una partida en curso: no se puede pausar Steam ahora."; return; }
        var error = _suspender.Suspend();
        StatusText = error ?? "Descargas en pausa. Steam está congelado hasta que pulses «Reanudar».";
        _monitor.PollSoon();
    }

    public void ResumeDownloads()
    {
        _suspender.Resume();
        StatusText = "Descargas reanudadas.";
        _monitor.PollSoon();
    }

    public void CancelDownload(DownloadItemViewModel item)
    {
        var card = Games.FirstOrDefault(g => g.IsSteam && g.Entry.AppId == item.AppId);

        // En cola y sin empezar: basta con quitar el manifiesto (sin Steam).
        if (_installer.CancelPending(item.AppId))
        {
            _monitor.Forget(item.AppId);
            _pendingInstalls.Remove(item.AppId);
            StatusText = $"Instalación de {item.Name} cancelada.";
            return;
        }

        var fresh = card?.IsFreshInstallInProgress ?? item.Info.IsRequested;
        var message = fresh
            ? $"¿Cancelar la instalación de «{item.Name}»? Steam borrará lo descargado."
            : $"Steam no permite cancelar solo la actualización de «{item.Name}» sin abrir su ventana de descargas. " +
              "La única forma silenciosa es desinstalar el juego. ¿Desinstalarlo?";
        if (!(Dialogs?.Confirm("Cancelar descarga", message) ?? false)) return;

        _pendingInstalls.Remove(item.AppId);
        var entry = card?.Entry ?? _scanner.FindGame(item.AppId);
        if (entry is not null) _ = UninstallEntryAsync(entry);
    }

    public void PlayDownloaded(string appId)
    {
        var card = Games.FirstOrDefault(g => g.IsSteam && g.Entry.AppId == appId && !g.IsNotInstalled);
        if (card is null) return;
        CurrentView = AppView.Library;
        Launch(card);
    }

    public ImageSource? CoverFor(string appId) =>
        Games.FirstOrDefault(g => g.IsSteam && g.Entry.AppId == appId)?.Cover;

    // =====================================================================================
    // Desinstalación
    // =====================================================================================

    public void Uninstall(GameCardViewModel card)
    {
        if (!card.CanUninstall) return;
        if (!(Dialogs?.Confirm("Desinstalar",
                $"¿Desinstalar «{card.Name}»?\n\nSteam borrará los archivos del juego de este equipo. Las partidas en la nube y el tiempo jugado se conservan.\n\nSteam mostrará su propia confirmación.") ?? false))
            return;
        _ = UninstallEntryAsync(card.Entry);
    }

    private async Task UninstallEntryAsync(GameEntry entry)
    {
        StatusText = $"Desinstalando {entry.Name}… confirma en el diálogo de Steam.";
        if (!await _orchestrator.UninstallAsync(entry))
            StatusText = $"No se pudo pedir a Steam que desinstale {entry.Name}.";
        _monitor.PollSoon();
    }

    /// <summary>
    /// Un manifiesto desapareció (desinstalación). Si el juego es de la cuenta, vuelve a «no instalado»;
    /// si no, se quita de la biblioteca.
    /// </summary>
    private void OnManifestDeleted(string appId)
    {
        var id = GameEntry.MakeSteamId(appId);
        var card = Games.FirstOrDefault(g => g.Entry.Id == id);
        if (card is null || card.IsRunning) return;
        _pendingInstalls.Remove(appId);

        if (_owned.Contains(appId) && _appInfo.TryGetValue(appId, out var info))
        {
            card.UpdateEntry(_scanner.CreateNotInstalledEntry(appId, info.Name, info.InstallDir));
            StatusText = $"{card.Name} ya no está instalado.";
        }
        else
        {
            Games.Remove(card);
            StatusText = $"{card.Name} se ha quitado de la biblioteca.";
        }
        RebuildTagFilters();
        RaiseLibraryTexts();
        Store.RefreshInstalledState();
    }

    /// <summary>Juegos propios sin nombre en appinfo.vdf que ya resolvimos en otra sesión.</summary>
    private IEnumerable<GameEntry> CachedUnresolvedGames(IEnumerable<string> appIds)
    {
        foreach (var id in appIds)
        {
            if (_names.TryGet(id, out var n) && n.Type == "game")
                yield return _scanner.CreateNotInstalledEntry(id, n.Name, null);
        }
    }

    /// <summary>
    /// Resuelve por la tienda (con caché en disco) los pocos juegos propios cuyo nombre no está en la
    /// caché local de Steam. Una petición cada ~1,5 s para respetar los límites de la tienda.
    /// </summary>
    private async Task ResolveUnknownNamesAsync(IReadOnlyList<string> appIds, int generation)
    {
        var pending = appIds.Where(id => !_names.TryGet(id, out _)).ToList();
        foreach (var id in pending)
        {
            if (generation != _refreshGeneration) return;
            try
            {
                var details = await _storeService.GetDetailsAsync(id, CancellationToken.None);
                if (details is null)
                {
                    _names.Set(id, string.Empty, "unavailable");
                }
                else
                {
                    _names.Set(id, details.Name, details.Type.ToLowerInvariant());
                    if (details.Type.Equals("game", StringComparison.OrdinalIgnoreCase) &&
                        Games.All(g => g.Entry.Id != GameEntry.MakeSteamId(id)))
                    {
                        var card = CreateCard(_scanner.CreateNotInstalledEntry(id, details.Name, null));
                        Games.Add(card);
                        _ = card.LoadCoverAsync();
                        RebuildTagFilters();
                        RaiseLibraryTexts();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"No se pudo resolver el nombre de la app {id}: {ex.Message}");
                return; // sin red o límite de peticiones: se reintenta en el próximo escaneo
            }
            await Task.Delay(1500);
        }
    }

    private bool FilterGame(object o)
    {
        if (o is not GameCardViewModel g) return false;
        if (_settings.LibraryInstalledOnly && g.IsNotInstalled) return false;
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
            else if (!card.IsRunning) Games.Remove(card);
        }
        foreach (var def in defs.Values)
        {
            var card = CreateCard(def.ToEntry());
            Games.Add(card);
            _ = card.LoadCoverAsync();
        }
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
            f.Count = f.Tag is null ? Games.Count : Games.Count(g => _library.HasTag(g.Entry.Id, f.Tag));

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
    // Lanzamiento
    // =====================================================================================

    public bool CanLaunch => !IsSessionActive;

    public void Launch(GameCardViewModel card)
    {
        if (card.IsNotInstalled) { Install(card); return; }
        // Instalación nueva aún descargándose (o en cola): se muestra la descarga, no se intenta jugar.
        if (card.IsFreshInstallInProgress || card.Live is { IsActive: true } or { Phase: DownloadPhase.Paused })
        {
            CurrentView = AppView.Downloads;
            return;
        }
        _ = LaunchAsync(card);
    }

    private async Task LaunchAsync(GameCardViewModel card)
    {
        if (IsSessionActive || _orchestrator.IsBusy) return;

        _sessionCts = new CancellationTokenSource();
        _cloudNotified = false;
        IsSessionActive = true;
        card.IsRunning = true;
        CommandManager.InvalidateRequerySuggested();
        if (_settings.MinimizeOnLaunch) GameLaunched?.Invoke(this, EventArgs.Empty);

        try
        {
            // Todo el sondeo de procesos corre fuera del hilo de UI.
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
            // Si la biblioteca se refrescó durante la partida, la tarjeta visible es otra instancia.
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

        // Steam vuelca su tiempo jugado al cerrarse: releerlo para que el total no se quede atrás.
        if (card.IsSteam) await ReloadSteamPlaytimeAsync();
        SessionFinished?.Invoke(this, EventArgs.Empty);
    }

    private void OnOrchestratorStatus(OrchestratorStatus s)
    {
        StatusText = s.SecondsRemaining is int n ? $"{s.Message} ({n} s)" : s.Message;

        switch (s.Phase)
        {
            case LaunchPhase.SyncingCloud:
                IsBannerVisible = true;
                BannerText = "Sincronizando partidas guardadas con Steam Cloud…";
                BannerDetail = s.SecondsRemaining is int left
                    ? $"Steam se cerrará de forma segura en {left} s. No apagues el equipo."
                    : "Esperando a Steam…";
                if (!_cloudNotified)
                {
                    _cloudNotified = true;
                    Notify("Steam Cloud", "Sincronizando partidas guardadas…");
                }
                break;

            case LaunchPhase.CleaningUp:
                IsBannerVisible = true;
                BannerText = "Cerrando Steam de forma segura…";
                BannerDetail = "Steam termina de subir tus partidas y se cierra. Después se liberará la memoria.";
                break;

            case LaunchPhase.Finished when s.Game?.Platform == GamePlatform.Steam:
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
        CurrentView = AppView.Store;
        _ = Store.ShowDetailsAsync(card.Entry.AppId);
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
                $"¿Quitar «{card.Name}» de LightLauncher?\n\nNo se borra ningún archivo del juego; solo deja de aparecer aquí. El tiempo jugado se conserva.") ?? false))
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
    // IStoreActions (puente con Steam)
    // =====================================================================================

    /// <summary>Tarjeta de un juego de Steam con manifiesto (instalado, en cola o descargando).</summary>
    public GameCardViewModel? FindInstalled(string appId) =>
        Games.FirstOrDefault(g => g.IsSteam && !g.IsNotInstalled && g.Entry.AppId == appId);

    public bool IsKnownOwned(string appId) =>
        FindInstalled(appId) is not null || _owned.Contains(appId);

    /// <summary>
    /// Instalar desde la Tienda. Juegos de la cuenta → instalación en segundo plano. Juegos gratuitos
    /// que aún no están en la cuenta → diálogo de Steam (solo Steam puede añadir la licencia gratuita).
    /// De pago sin licencia → compra en la web (nunca steam://install, que daría «no hay licencias»).
    /// </summary>
    public async Task InstallAsync(string appId, string name, bool isFree)
    {
        if (IsKnownOwned(appId))
        {
            var card = Games.FirstOrDefault(g => g.IsSteam && g.Entry.AppId == appId);
            var entry = card?.Entry ?? _scanner.CreateNotInstalledEntry(
                appId, name, _appInfo.TryGetValue(appId, out var info) ? info.InstallDir : null);
            await InstallInBackgroundAsync(entry);
            return;
        }

        if (isFree)
        {
            StatusText = $"Añadiendo {name} (gratis) a tu cuenta con Steam…";
            await _orchestrator.OpenSteamUriAsync($"steam://install/{appId}",
                $"Steam: confirma la instalación de {name}. Después aparecerá en tu biblioteca con su progreso.");
            return;
        }

        StatusText = $"{name} no consta en tu cuenta: abriendo la compra en la web…";
        OpenInBrowser(appId);
    }

    // ----- Instalación en segundo plano -----

    public void Install(GameCardViewModel card) => _ = InstallInBackgroundAsync(card.Entry);

    private async Task InstallInBackgroundAsync(GameEntry entry)
    {
        if (entry.Platform != GamePlatform.Steam || entry.IsInstalled) return;

        var restart = false;
        if (SteamInstaller.RequiresSteamRestart)
        {
            if (IsSessionActive)
            {
                StatusText = "Hay una partida en curso: instala el juego cuando termines.";
                return;
            }
            restart = Dialogs?.Confirm("Instalar en segundo plano",
                $"Steam está abierto. Para que «{entry.Name}» empiece a descargarse sin abrir la ventana de Steam, " +
                "hay que reiniciar Steam en segundo plano (se cierra de forma ordenada y vuelve a abrirse minimizado).\n\n" +
                "¿Reiniciar Steam ahora?\n\nSi eliges No, la descarga empezará la próxima vez que se abra Steam.") ?? false;
        }

        StatusText = $"Preparando la instalación de {entry.Name}…";
        var outcome = await Task.Run(() => _installer.InstallAsync(entry, restart));
        StatusText = outcome.Message;

        switch (outcome.Result)
        {
            case InstallStart.Started:
            case InstallStart.StartedAfterSteamRestart:
            case InstallStart.QueuedUntilSteamRestart:
                _pendingInstalls.Add(entry.AppId);
                _monitor.MarkRequested(entry.AppId, entry.Name); // progreso en vivo + Steam no se cierra
                if (outcome.SteamStartedByUs) _steamOpenedForInstall = true;
                // Se aceptó reiniciar pero Steam estaba ocupado descargando: reiniciar en cuanto quede libre.
                if (outcome.Result == InstallStart.QueuedUntilSteamRestart && restart) _orchestrator.ScheduleRestartWhenIdle();
                if (outcome.Result != InstallStart.QueuedUntilSteamRestart) CurrentView = AppView.Downloads;
                // El vigilante de manifiestos refleja la cola y el progreso; por si el evento se retrasa:
                if (await Task.Run(() => _scanner.FindGame(entry.AppId)) is { } created) UpsertGame(created);
                if (outcome.Result != InstallStart.QueuedUntilSteamRestart)
                {
                    Notify("Instalación", outcome.Message);
                    _ = WarnIfDownloadDoesNotStartAsync(entry);
                }
                break;
            case InstallStart.Failed:
                Notify("No se pudo instalar", outcome.Message);
                break;
        }
    }

    /// <summary>
    /// Si a los 3 minutos Steam no ha empezado a descargar, se sugiere la alternativa oficial (el método
    /// en segundo plano no está documentado por Valve y podría no funcionar en todas las versiones).
    /// </summary>
    private async Task WarnIfDownloadDoesNotStartAsync(GameEntry entry)
    {
        await Task.Delay(TimeSpan.FromMinutes(3));
        var card = Games.FirstOrDefault(g => g.Entry.Id == entry.Id);
        if (card is { IsQueued: true })
        {
            StatusText = $"Steam aún no ha empezado a descargar {entry.Name}. Clic derecho → «Instalar con el diálogo de Steam».";
            Notify("La descarga no ha empezado", $"Prueba con clic derecho sobre {entry.Name} → «Instalar con el diálogo de Steam».");
        }
    }

    public void InstallWithSteamDialog(GameCardViewModel card)
    {
        _installer.CancelPending(card.Entry.AppId); // por si había un manifiesto en cola sin empezar
        _pendingInstalls.Add(card.Entry.AppId);
        StatusText = $"Abriendo el diálogo de instalación de Steam para {card.Name}…";
        _ = _orchestrator.OpenSteamUriAsync($"steam://install/{card.Entry.AppId}",
            $"Steam: confirma la instalación de {card.Name}. El progreso se verá aquí.");
    }

    public void CancelInstall(GameCardViewModel card)
    {
        if (_installer.CancelPending(card.Entry.AppId))
        {
            _pendingInstalls.Remove(card.Entry.AppId);
            StatusText = $"Instalación de {card.Name} cancelada.";
        }
        else
        {
            StatusText = $"La descarga de {card.Name} ya empezó: cancélala desde las descargas de Steam.";
        }
    }

    public void OpenInBrowser(string appId)
    {
        StatusText = "Abriendo la tienda de Steam en tu navegador…";
        OpenPath($"https://store.steampowered.com/app/{Uri.EscapeDataString(appId)}/");
    }

    public void OpenCart()
    {
        StatusText = "Abriendo tu carrito de Steam en el navegador…";
        OpenPath("https://store.steampowered.com/cart/");
    }

    public void Play(GameCardViewModel card) => Launch(card);

    // =====================================================================================
    // Ciclo de vida de la ventana
    // =====================================================================================

    /// <summary>La ventana se ocultó en la bandeja: soltar recursos de red de la tienda.</summary>
    public void OnHiddenToTray() => Store.OnHidden();

    /// <summary>Guardar lo pendiente al salir.</summary>
    public void Shutdown()
    {
        _suspender.Resume(); // nunca dejar Steam congelado al salir
        _monitor.Dispose();
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
