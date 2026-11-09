using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>Barra/insignia de instalación visible (en cola o descargando).</summary>
    public bool ShowInstallProgress => IsDownloading || IsQueued;

    public bool IsDownloading => Entry.DownloadProgress is not null;
    public double DownloadPercent => (Entry.DownloadProgress ?? 0) * 100;
    public string DownloadText =>
        Entry.DownloadProgress is double p
            ? $"Descargando {p:P0} · {Formatting.Bytes(Entry.BytesDownloaded)} de {Formatting.Bytes(Entry.BytesToDownload)}"
            : IsQueued ? "En cola · esperando a Steam…" : string.Empty;

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
