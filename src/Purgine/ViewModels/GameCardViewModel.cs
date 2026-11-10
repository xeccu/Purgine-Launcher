using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Purgine.Infrastructure;
using Purgine.SteamNet;
using Purgine.Library;
using Purgine.Stats;

namespace Purgine.ViewModels;

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

    /// <summary>Icono de la lista lateral: se muestra a 24 px; 48 cubre pantallas con escalado 200 %.</summary>
    private const int IconDecodeWidth = 48;

    private readonly GameTimeTracker _tracker;
    private readonly UserLibrary _library;
    private readonly ICardHost _host;
    private ImageSource? _cover;
    private ImageSource? _appIcon;
    private ImageSource? _smallIcon;
    private bool _iconRequested;
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

        ShowDetailsCommand = new RelayCommand(_ => _host.ShowDetails(this));
        ForceCloseCommand = new RelayCommand(_ => _host.ForceClose(this), _ => IsRunning);
        PropertiesCommand = new RelayCommand(_ => _host.ShowProperties(this), _ => IsSteam && !IsNotInstalled);
        PrimaryActionCommand = new RelayCommand(
            _ => { if (ShowUpdateButton) _host.Update(this); else if (IsNotInstalled && !HasActiveJob) _host.Install(this); else _host.Launch(this); },
            _ => !IsRunning && (IsNotInstalled || HasActiveJob || ShowUpdateButton || _host.CanLaunch));
        // Botón de jugar de la tarjeta: jugar si está instalado; instalar si no.
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

        CheckInstallPresent();
        RefreshStats();
        RefreshTags();
    }

    public GameEntry Entry { get; private set; }

    /// <summary>Juego, software o herramienta (según Steam; los ejecutables locales cuentan como juego).</summary>
    public Settings.LibraryContentType ContentType { get; set; } = Settings.LibraryContentType.Game;

    private bool _installPresent;

    /// <summary>
    /// «Listo para jugar»: instalado, con su carpeta (o su .exe, si es local) presente en el disco y sin descarga
    /// en curso. Se comprueba al crear o actualizar el juego, no en cada filtrado.
    /// </summary>
    public bool IsReadyToPlay => !IsNotInstalled && _installPresent && !HasActiveJob;

    private void CheckInstallPresent()
    {
        try
        {
            _installPresent = IsLocal
                ? Entry.ExecutablePath is { } exe && File.Exists(exe)
                : Entry.InstallDir is { } dir && Directory.Exists(dir);
        }
        catch (Exception) { _installPresent = false; }
    }

    /// <summary>Última sesión (ticks UTC; 0 = nunca) para ordenar por actividad reciente.</summary>
    public long LastPlayedTicks { get; private set; }

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

    /// <summary>Icono cuadrado de la lista lateral (icono de Steam, o el del .exe en los locales).</summary>
    public ImageSource? SmallIcon
    {
        get => _smallIcon;
        private set => SetProperty(ref _smallIcon, value);
    }

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
        set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(OverlayText));
            RaisePrimaryAction();
        }
    }

    // ----- Acción principal (botón grande de la ficha) -----

    public string PrimaryActionText =>
        _isRunning ? "EN CURSO"
        : HasActiveJob ? "VER DESCARGA"
        : ShowUpdateButton ? "ACTUALIZAR"
        : IsNotInstalled ? "INSTALAR"
        : "JUGAR";

    /// <summary>Glifo de Segoe Fluent Icons del botón principal.</summary>
    public string PrimaryActionGlyph =>
        _isRunning ? "\uE916"
        : HasActiveJob ? "\uE896"
        : ShowUpdateButton ? "\uE777"
        : IsNotInstalled ? "\uE896"
        : "\uE768";

    private void RaisePrimaryAction()
    {
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(PrimaryActionGlyph));
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

    /// <summary>Descarga/actualización de Purgine para este juego (null si no hay).</summary>
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
        RaisePrimaryAction();
    }

    public bool IsQueued => _job?.State == DownloadJobState.Queued;
    public bool ShowInstallProgress => HasActiveJob;
    public bool IsDownloading => _job?.State == DownloadJobState.Downloading;
    public bool IsIndeterminateProgress => _job is { } j && (j.Progress is null || j.State is DownloadJobState.Queued or DownloadJobState.Preparing);
    public double DownloadPercent => (_job?.Progress ?? 0) * 100;

    /// <summary>«Descargando 45,2 % · 12,3 MB/s · 4 min» / «En cola» / «En pausa».</summary>
    public string DownloadText => _job is { } j ? JobText.Short(j) : string.Empty;

    public bool CanUninstall => IsSteam && !IsNotInstalled && !IsRunning && !HasActiveJob;

    // ----- Espacio necesario (juegos no instalados) -----

    private ulong _installSize;

    /// <summary>Bytes que ocupará instalado según PICS (0 = desconocido).</summary>
    public ulong InstallSize => _installSize;

    public void SetInstallSize(ulong bytes)
    {
        if (_installSize == bytes) return;
        _installSize = bytes;
        OnPropertyChanged(nameof(InstallSize));
        OnPropertyChanged(nameof(RequiredSizeText));
        OnPropertyChanged(nameof(ShowRequiredSize));
    }

    /// <summary>«2,4 GB».</summary>
    public string RequiredSizeText => _installSize == 0 ? string.Empty : Formatting.Bytes((long)_installSize);

    public bool ShowRequiredSize => IsNotInstalled && _installSize > 0;

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
        RaisePrimaryAction();
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

    /// <summary>Horas jugadas (para ordenar por tiempo de juego).</summary>
    public double TotalPlaytimeHours { get; private set; }

    /// <summary>«12 h 30 min» / «Sin jugar» (ficha del juego).</summary>
    public string TotalPlaytimeText { get; private set; } = string.Empty;

    /// <summary>«hoy» / «hace 3 días» / «4 mar 2026» / «Nunca» (ficha del juego).</summary>
    public string LastPlayedText { get; private set; } = string.Empty;

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
    public ICommand ShowDetailsCommand { get; }
    public ICommand ForceCloseCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand PrimaryActionCommand { get; }
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
        CheckInstallPresent();
        if (coverChanged)
        {
            _coverRequested = false;
            Cover = null;
            AppIcon = null;
            _ = LoadCoverAsync();
            if (IsLocal) { _iconRequested = false; SmallIcon = null; }
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
        TotalPlaytimeHours = b.Total.TotalHours;
        LastPlayedTicks = last?.UtcTicks ?? 0;
        OnPropertyChanged(nameof(LastPlayedTicks));
        TotalPlaytimeText = b.Total == TimeSpan.Zero ? "Sin jugar" : Formatting.Duration(b.Total);
        LastPlayedText = last is null ? "Nunca" : Formatting.RelativeDate(last);
        LastPlayedText = LastPlayedText.Length > 0 ? char.ToUpper(LastPlayedText[0]) + LastPlayedText[1..] : LastPlayedText;
        OnPropertyChanged(nameof(TotalPlaytimeHours));
        OnPropertyChanged(nameof(TotalPlaytimeText));
        OnPropertyChanged(nameof(LastPlayedText));

        _playtimeDetail = b.Total == TimeSpan.Zero ? null
            : IsSteam && b.LocalExtra > TimeSpan.Zero
                ? $"Jugado: {Formatting.Duration(b.Total)} (Steam {Formatting.Duration(b.Steam)} + {Formatting.Duration(b.LocalExtra)} recientes que Steam aún no ha guardado)"
                : IsSteam ? $"Jugado: {Formatting.Duration(b.Total)} (histórico de Steam)"
                : $"Jugado: {Formatting.Duration(b.Total)} (medido por Purgine)";
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

    /// <summary>
    /// Icono de la lista lateral, una sola vez: el de Steam (caché del cliente o CDN con el hash de PICS) o,
    /// en los ejecutables locales, el del propio .exe.
    /// </summary>
    public async Task LoadIconAsync(SteamArtService art, string? iconHash)
    {
        if (_iconRequested) return;
        _iconRequested = true;

        if (IsLocal)
        {
            if (AppIcon is null && Entry.ExecutablePath is { } exe)
                AppIcon = await Task.Run(() => ImageLoader.IconFromExecutable(exe));
            SmallIcon = AppIcon ?? Cover;
            return;
        }
        SmallIcon = await art.LoadIconAsync(Entry.AppId, iconHash, IconDecodeWidth);
    }
}
