using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LightLauncher.Core;
using LightLauncher.Infrastructure;
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
        InstallCommand = new RelayCommand(_ => _host.Install(this), _ => IsNotInstalled);
        InstallWithSteamCommand = new RelayCommand(_ => _host.InstallWithSteamDialog(this), _ => IsSteam && (IsNotInstalled || IsQueued));
        CancelInstallCommand = new RelayCommand(_ => _host.CancelInstall(this), _ => IsQueued);
        UninstallCommand = new RelayCommand(_ => _host.Uninstall(this), _ => CanUninstall);
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
    public string PlatformLabel => IsLocal ? "Local" : IsNotInstalled ? "No instalado" : "Steam";

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
        : IsNotInstalled ? "Instalar"
        : _live is { IsActive: true } or { Phase: DownloadPhase.Paused } ? "Ver descarga"
        : IsDownloading ? "Descargando…"
        : IsQueued ? "En cola…"
        : Entry.IsFullyInstalled ? "Jugar"
        : "Actualizar y jugar";

    /// <summary>Juego de la cuenta que no está en disco.</summary>
    public bool IsNotInstalled => !Entry.IsInstalled;

    /// <summary>Manifiesto creado pero sin bytes descargados aún (esperando a que Steam empiece).</summary>
    public bool IsQueued => Entry.IsInstalling && Entry.SizeOnDisk == 0 && Entry.BytesDownloaded == 0 && !IsDownloading;

    /// <summary>Opción de menú «Instalar con el diálogo de Steam» (alternativa a la instalación en segundo plano).</summary>
    public bool ShowSteamInstallOption => IsSteam && (IsNotInstalled || IsQueued);

    // ----- Progreso en vivo (monitor de descargas) -----

    private DownloadInfo? _live;

    /// <summary>Estado en vivo de la descarga de este juego (null si no hay ninguna).</summary>
    public DownloadInfo? Live => _live;

    public void SetLive(DownloadInfo? live)
    {
        if (live == _live) return;
        _live = live;
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(DownloadPercent));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(ShowInstallProgress));
        OnPropertyChanged(nameof(IsIndeterminateProgress));
        OnPropertyChanged(nameof(OverlayText));
    }

    /// <summary>Barra/insignia de instalación visible (en cola, descargando, en pausa…).</summary>
    public bool ShowInstallProgress =>
        _live is { } l ? l.IsActive || l.Phase == DownloadPhase.Paused || (l.Phase == DownloadPhase.Queued && l.IsRequested)
        : IsQueued || Entry.DownloadProgress is not null;

    public bool IsDownloading => _live is { Phase: DownloadPhase.Downloading } || (_live is null && Entry.DownloadProgress is not null);

    public bool IsIndeterminateProgress => _live is { } l
        ? l.Progress is null || l.Phase is not (DownloadPhase.Downloading or DownloadPhase.Paused)
        : IsQueued;

    public double DownloadPercent => ((_live?.Progress) ?? Entry.DownloadProgress ?? 0) * 100;

    /// <summary>«Descargando 45,2 % · 12,3 MB/s · 4 min» / «En cola…» / «En pausa».</summary>
    public string DownloadText
    {
        get
        {
            if (_live is { } l)
            {
                var text = l.PhaseText;
                if (l.Progress is double p && l.Phase is DownloadPhase.Downloading or DownloadPhase.Paused) text += $" {p * 100:0.0} %";
                if (l.Phase == DownloadPhase.Downloading && l.BytesPerSecond > 0) text += $" · {Formatting.Speed(l.BytesPerSecond)}";
                if (l.Phase == DownloadPhase.Downloading && l.Eta is { } eta) text += $" · {Formatting.Eta(eta)}";
                return text;
            }
            return Entry.DownloadProgress is double dp
                ? $"Descargando {dp * 100:0.0} %"
                : IsQueued ? "En cola · esperando a Steam…" : string.Empty;
        }
    }

    /// <summary>Instalación nueva aún sin terminar (clic → panel de descargas en vez de jugar).</summary>
    public bool IsFreshInstallInProgress => IsSteam && Entry.IsInstalling && Entry.SizeOnDisk == 0;

    public bool CanUninstall => IsSteam && !IsNotInstalled && !IsRunning;

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
    public ICommand InstallWithSteamCommand { get; }
    public ICommand CancelInstallCommand { get; }
    public ICommand UninstallCommand { get; }
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
