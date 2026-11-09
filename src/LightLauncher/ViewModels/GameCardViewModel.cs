using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LightLauncher.Infrastructure;
using LightLauncher.SteamNet;
using LightLauncher.Library;
using LightLauncher.Stats;

namespace LightLauncher.ViewModels;

/// <summary>Una etiqueta marcable en el menú contextual de una tarjeta.</summary>
public sealed class TagToggleViewModel : ObservableObject
{
    private readonly string _gameId;
    private readonly UserLibrary _library;

    public TagToggleViewModel(string name, string gameId, UserLibrary library)
    {
        Name = name;
        _gameId = gameId;
        _library = library;
    }

    public string Name { get; }

    public bool IsChecked
    {
        get => _library.HasTag(_gameId, Name);
        set { if (value != IsChecked) _library.SetTag(_gameId, Name, value); }
    }

    public void NotifyChanged() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>Un juego de la biblioteca (tarjeta de la cuadrícula o fila de la lista).</summary>
public sealed class GameCardViewModel : ObservableObject
{
    /// <summary>
    /// Anchura de decodificación. Con la biblioteca completa (cientos de portadas) cada píxel cuenta:
    /// 180 px cubre el tamaño por defecto (160) y escala bien hasta el máximo con muy poca pérdida.
    /// </summary>
    private const int CoverDecodeWidth = 180;

    private readonly GameTimeTracker _tracker;
    private readonly UserLibrary _library;
    private readonly ICardHost _host;
    private ImageSource? _cover;
    private ImageSource? _appIcon;
    private bool _coverRequested;
    private bool _isRunning;
    private string _playtimeText = string.Empty;
    private string _tagsText = string.Empty;

    public GameCardViewModel(GameEntry entry, GameTimeTracker tracker, UserLibrary library, ICardHost host)
    {
        Entry = entry;
        _tracker = tracker;
        _library = library;
        _host = host;

        // Clic en la tarjeta: jugar si está instalado; instalar si no.
        PlayCommand = new RelayCommand(
            _ => { if (IsNotInstalled) _host.Install(this); else _host.Launch(this); },
            _ => IsNotInstalled || _host.CanLaunch);
        InstallCommand = new RelayCommand(_ => _host.Install(this), _ => IsNotInstalled && !HasActiveJob);
        CancelInstallCommand = new RelayCommand(_ => _host.CancelInstall(this), _ => HasActiveJob);
        UninstallCommand = new RelayCommand(_ => _host.Uninstall(this), _ => CanUninstall);
        UpdateCommand = new RelayCommand(_ => _host.Update(this), _ => ShowUpdateButton);
        NewTagCommand = new RelayCommand(_ => _host.CreateTagFor(this));
        OpenFolderCommand = new RelayCommand(_ => _host.OpenInstallFolder(this), _ => Entry.InstallDir is not null);
        ViewInStoreCommand = new RelayCommand(_ => _host.ViewInStore(this), _ => IsSteam);
        EditCommand = new RelayCommand(_ => _host.EditLocal(this), _ => IsLocal);
        RemoveCommand = new RelayCommand(_ => _host.RemoveLocal(this), _ => IsLocal && !IsRunning);

        RefreshStats();
        RefreshTags();
    }

    public GameEntry Entry { get; private set; }

    public string Name => Entry.Name;
    public bool IsLocal => Entry.Platform == GamePlatform.Local;
    public bool IsSteam => Entry.Platform == GamePlatform.Steam;
    public string PlatformLabel =>
        IsLocal ? "Local"
        : IsNotInstalled ? "No instalado"
        : HasUpdate ? "Actualización pendiente"
        : "Steam";

    public string Tooltip
    {
        get
        {
            var head = IsLocal
                ? $"{Entry.Name}\n{Entry.ExecutablePath}"
                : Entry.InstallDir is null
                    ? $"{Entry.Name}\nAppID {Entry.AppId}"
                    : $"{Entry.Name}\nAppID {Entry.AppId}\n{Entry.InstallDir}";
            return _playtimeDetail is null ? head : $"{head}\n{_playtimeDetail}";
        }
    }

    // ----- Imagen -----

    public ImageSource? Cover
    {
        get => _cover;
        private set { if (SetProperty(ref _cover, value)) OnPropertyChanged(nameof(HasCover)); }
    }

    public bool HasCover => _cover is not null;

    /// <summary>Icono del .exe (solo ejecutables locales sin portada).</summary>
    public ImageSource? AppIcon
    {
        get => _appIcon;
        private set => SetProperty(ref _appIcon, value);
    }

    // ----- Estado -----

    public bool IsRunning
    {
        get => _isRunning;
        set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(OverlayText)); }
    }

    public string OverlayText =>
        _isRunning ? "En curso"
        : HasActiveJob ? "Ver descarga"
        : IsNotInstalled ? "Instalar"
        : "Jugar";

    /// <summary>Juego de la cuenta que no está en disco.</summary>
    public bool IsNotInstalled => !Entry.IsInstalled;

    // ----- Descarga nativa en curso (SteamDownloadManager) -----

    private DownloadJob? _job;

    /// <summary>Descarga/actualización de LightLauncher para este juego (null si no hay).</summary>
    public DownloadJob? Job => _job;

    public bool HasActiveJob => _job is { IsFinished: false };

    /// <summary>Asigna o refresca la descarga en curso (se llama en cada cambio de progreso).</summary>
    public void SetJob(DownloadJob? job)
    {
        _job = job;
        OnPropertyChanged(nameof(Job));
        OnPropertyChanged(nameof(HasActiveJob));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(DownloadPercent));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(ShowInstallProgress));
        OnPropertyChanged(nameof(IsIndeterminateProgress));
        OnPropertyChanged(nameof(OverlayText));
        OnPropertyChanged(nameof(ShowUpdateButton));
        OnPropertyChanged(nameof(CanUninstall));
    }

    public bool IsQueued => _job?.State == DownloadJobState.Queued;
    public bool ShowInstallProgress => HasActiveJob;
    public bool IsDownloading => _job?.State == DownloadJobState.Downloading;
    public bool IsIndeterminateProgress => _job is { } j && (j.Progress is null || j.State is DownloadJobState.Queued or DownloadJobState.Preparing);
    public double DownloadPercent => (_job?.Progress ?? 0) * 100;

    /// <summary>«Descargando 45,2 % · 12,3 MB/s · 4 min» / «En cola» / «En pausa».</summary>
    public string DownloadText => _job is { } j ? JobText.Short(j) : string.Empty;

    public bool CanUninstall => IsSteam && !IsNotInstalled && !IsRunning && !HasActiveJob;

    // ----- Actualizaciones -----

    private bool _remoteUpdate;

    /// <summary>Steam (red) publica una build más nueva que la instalada.</summary>
    public void SetRemoteUpdate(bool available)
    {
        if (_remoteUpdate == available) return;
        _remoteUpdate = available;
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ShowUpdateButton));
        OnPropertyChanged(nameof(PlatformLabel));
    }

    /// <summary>Hay actualización: según el manifiesto local o según la build pública de Steam.</summary>
    public bool HasUpdate => IsSteam && !IsNotInstalled && (Entry.HasPendingUpdate || _remoteUpdate);

    /// <summary>Botón «Actualizar» visible: hay actualización y no hay ya una descarga de este juego.</summary>
    public bool ShowUpdateButton => HasUpdate && !IsRunning && !HasActiveJob;

    public string UpdateButtonText => "Actualizar";

    public string PlaytimeText
    {
        get => _playtimeText;
        private set => SetProperty(ref _playtimeText, value);
    }

    // ----- Etiquetas -----

    public ObservableCollection<TagToggleViewModel> TagToggles { get; } = [];

    public string TagsText
    {
        get => _tagsText;
        private set => SetProperty(ref _tagsText, value);
    }

    public bool IsFavorite => _library.HasTag(Entry.Id, "Favoritos");

    // ----- Comandos (menú contextual) -----

    public ICommand PlayCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand CancelInstallCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand NewTagCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ViewInStoreCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RemoveCommand { get; }

    // ----- Actualizaciones -----

    /// <summary>Reemplaza los datos del juego (manifiesto actualizado, ejecutable editado…).</summary>
    public void UpdateEntry(GameEntry entry)
    {
        var coverChanged = entry.CoverImagePath != Entry.CoverImagePath || entry.ExecutablePath != Entry.ExecutablePath;
        Entry = entry;
        if (coverChanged)
        {
            _coverRequested = false;
            Cover = null;
            AppIcon = null;
            _ = LoadCoverAsync();
        }
        OnPropertyChanged(string.Empty); // refresca todos los bindings
    }

    public void RefreshStats()
    {
        var b = _tracker.GetBreakdown(Entry.Id);
        var last = _tracker.GetLastPlayed(Entry.Id);
        PlaytimeText = b.Total == TimeSpan.Zero
            ? "Sin jugar"
            : last is null ? Formatting.Duration(b.Total) : $"{Formatting.Duration(b.Total)} · {Formatting.RelativeDate(last)}";

        _playtimeDetail = b.Total == TimeSpan.Zero ? null
            : IsSteam && b.LocalExtra > TimeSpan.Zero
                ? $"Jugado: {Formatting.Duration(b.Total)} (Steam {Formatting.Duration(b.Steam)} + {Formatting.Duration(b.LocalExtra)} recientes que Steam aún no ha guardado)"
                : IsSteam ? $"Jugado: {Formatting.Duration(b.Total)} (histórico de Steam)"
                : $"Jugado: {Formatting.Duration(b.Total)} (medido por LightLauncher)";
        OnPropertyChanged(nameof(Tooltip));
    }

    private string? _playtimeDetail;

    public void RefreshTags()
    {
        var tags = _library.Tags;
        if (tags.SequenceEqual(TagToggles.Select(t => t.Name)))
        {
            // Misma lista de etiquetas: solo refrescar marcas (no regenerar el menú si está abierto).
            foreach (var toggle in TagToggles) toggle.NotifyChanged();
        }
        else
        {
            TagToggles.Clear();
            foreach (var tag in tags)
                TagToggles.Add(new TagToggleViewModel(tag, Entry.Id, _library));
        }
        TagsText = string.Join(" · ", _library.GetTags(Entry.Id));
        OnPropertyChanged(nameof(IsFavorite));
    }

    /// <summary>Carga la portada (o el icono del .exe) en segundo plano, una sola vez.</summary>
    public async Task LoadCoverAsync()
    {
        if (_coverRequested) return;
        _coverRequested = true;

        var coverPath = Entry.CoverImagePath;
        if (coverPath is not null)
        {
            Cover = await Task.Run(() => ImageLoader.FromFile(coverPath, CoverDecodeWidth));
            if (Cover is not null) return;
        }

        if (IsLocal && Entry.ExecutablePath is { } exe)
            AppIcon = await Task.Run(() => ImageLoader.IconFromExecutable(exe));
    }
}
