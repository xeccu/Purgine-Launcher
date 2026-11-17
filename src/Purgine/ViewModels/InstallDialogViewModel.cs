using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;

namespace Purgine.ViewModels;

/// <summary>Una unidad del selector «INSTALAR EN:».</summary>
public sealed class DriveOptionViewModel : ObservableObject
{
    public DriveOptionViewModel(string root, string label, long freeBytes, string libraryPath, bool isDefault, bool isNewLibrary, ulong requiredBytes)
    {
        Root = root;
        Label = label;
        FreeBytes = freeBytes;
        LibraryPath = libraryPath;
        IsDefault = isDefault;
        IsNewLibrary = isNewLibrary;
        SetRequired(requiredBytes);
    }

    private bool _hasEnoughSpace = true;

    /// <summary>
    /// Recalcula si cabe con el peso de lo elegido. Peso desconocido (0) nunca bloquea: el motor de descarga vuelve a
    /// comprobar el espacio con la lista real de archivos antes de escribir nada.
    /// </summary>
    public void SetRequired(ulong requiredBytes)
    {
        var enough = requiredBytes == 0 || (ulong)Math.Max(0, FreeBytes) >= requiredBytes;
        if (_hasEnoughSpace == enough) return;
        _hasEnoughSpace = enough;
        OnPropertyChanged(nameof(HasEnoughSpace));
        OnPropertyChanged(nameof(Tooltip));
    }

    public string Root { get; }
    /// <summary>«Unidad local (C:)».</summary>
    public string Label { get; }
    public long FreeBytes { get; }
    /// <summary>«ESPACIO LIBRE: 208,99 GB».</summary>
    public string FreeText => $"ESPACIO LIBRE: {InstallDialogViewModel.PreciseSize(FreeBytes)}";
    /// <summary>Biblioteca de Steam donde se instalará (existente, o nueva <c>X:\SteamLibrary</c>).</summary>
    public string LibraryPath { get; }
    /// <summary>Unidad con más espacio entre las bibliotecas existentes (la que se usaría sin preguntar).</summary>
    public bool IsDefault { get; }
    public bool IsNewLibrary { get; }
    public bool HasEnoughSpace => _hasEnoughSpace;

    public string Tooltip =>
        !HasEnoughSpace ? "No hay espacio suficiente en esta unidad."
        : IsNewLibrary ? $"Se creará la biblioteca de Steam {LibraryPath} y se registrará en Steam."
        : $"Biblioteca de Steam: {LibraryPath}";
}

/// <summary>
/// Un componente del selector previo a instalar: un DLC con contenido (Steam) o una etiqueta de instalación (Epic).
/// <see cref="Key"/> es el id del DLC o la etiqueta.
/// </summary>
public sealed class InstallComponentViewModel : ObservableObject
{
    private bool _isSelected;
    private readonly Action _changed;

    public InstallComponentViewModel(string key, string name, long sizeBytes, bool selected, Action changed, string? note = null)
    {
        Key = key;
        Name = name;
        SizeBytes = sizeBytes;
        _isSelected = selected;
        _changed = changed;
        Note = note;
    }

    public string Key { get; }
    public string Name { get; }
    /// <summary>Lo que añade (bytes); -1 = no disponible.</summary>
    public long SizeBytes { get; }
    public string SizeText => SizeBytes < 0 ? "No disponible por el momento"
        : AddedSizeText(SizeBytes);

    /// <summary>
    /// «+27,39 GB»; «Sin descarga extra» si no añade nada y «+ &lt;1 MB» si son unos pocos bytes (p. ej. las opciones de
    /// Epic que solo cambian cómo se transmiten las texturas), en vez de un engañoso «+0 KB».
    /// </summary>
    public static string AddedSizeText(long bytes) =>
        bytes <= 0 ? "Sin descarga extra" : "+" + InstallDialogViewModel.PreciseSize(bytes);
    /// <summary>Aclaración bajo el nombre (p. ej. «Opcional»).</summary>
    public string? Note { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) _changed(); }
    }
}

/// <summary>Lo elegido en el diálogo de instalar (con los componentes marcados y desmarcados).</summary>
public sealed record InstallChoice(string LibraryPath, bool CreateDesktopShortcut, bool CreateStartMenuShortcut,
    IReadOnlyList<string>? SelectedComponents = null, IReadOnlyList<string>? UnselectedComponents = null);

/// <summary>
/// Diálogo previo a una instalación (superposición dentro de la ventana, sin ventanas externas): juego y
/// peso, componentes opcionales (DLC de Steam, etiquetas de Epic), accesos directos y unidad de destino con su
/// espacio libre. El peso y la comprobación de espacio siguen a los componentes marcados.
/// </summary>
public sealed class InstallDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<InstallChoice?> _result = new();
    private DriveOptionViewModel? _selectedDrive;
    private ImageSource? _banner;
    private bool _createDesktopShortcut = true;
    private bool _createStartMenuShortcut = true;

    public InstallDialogViewModel(string name, ulong sizeBytes, IEnumerable<DriveOptionViewModel> drives,
        Func<string?>? browse = null, bool showShortcuts = true, ulong downloadBytes = 0)
    {
        _browse = browse;
        _downloadBytes = downloadBytes;
        ShowShortcuts = showShortcuts;
        BrowseCommand = new RelayCommand(_ => Browse(), _ => _browse is not null);
        Name = name;
        _sizeBytes = sizeBytes;
        Drives = new ObservableCollection<DriveOptionViewModel>(drives);
        _selectedDrive = Drives.FirstOrDefault(d => d.IsDefault && d.HasEnoughSpace)
                         ?? Drives.FirstOrDefault(d => d.HasEnoughSpace)
                         ?? Drives.FirstOrDefault();
        InstallCommand = new RelayCommand(_ => Close(new InstallChoice(_selectedDrive!.LibraryPath, _createDesktopShortcut, _createStartMenuShortcut,
                HasComponents ? Components.Where(c => c.IsSelected).Select(c => c.Key).ToList() : null,
                HasComponents ? Components.Where(c => !c.IsSelected).Select(c => c.Key).ToList() : null)),
            _ => _selectedDrive is { HasEnoughSpace: true });
        CancelCommand = new RelayCommand(_ => Close(null));
    }

    private readonly Func<string?>? _browse;

    /// <summary>Hay botón «Examinar…» para elegir cualquier carpeta (itch.io).</summary>
    public bool CanBrowse => _browse is not null;

    /// <summary>Casillas de accesos directos (no para itch.io).</summary>
    public bool ShowShortcuts { get; }

    public ICommand BrowseCommand { get; }

    /// <summary>Carpeta elegida a mano: se añade a la lista (con el espacio libre de su unidad) y queda seleccionada.</summary>
    private void Browse()
    {
        if (_browse?.Invoke() is not { Length: > 0 } folder) return;
        var existing = Drives.FirstOrDefault(d => string.Equals(d.LibraryPath, folder, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var root = Path.GetPathRoot(folder) ?? folder;
            long free = 0;
            try { free = new DriveInfo(root).AvailableFreeSpace; } catch (Exception) { /* unidad de red o sin acceso */ }
            existing = new DriveOptionViewModel(root, folder, free, folder, isDefault: false, isNewLibrary: !Directory.Exists(folder), SizeBytes);
            Drives.Add(existing);
        }
        SelectedDrive = existing;
    }

    public string Name { get; }

    private ulong _sizeBytes;
    private ulong _downloadBytes;
    private Func<IReadOnlyList<InstallComponentViewModel>, (ulong Install, ulong Download)?>? _sizeOf;

    /// <summary>Peso instalado de lo elegido (0 = no disponible).</summary>
    public ulong SizeBytes
    {
        get => _sizeBytes;
        private set
        {
            _sizeBytes = value;
            foreach (var d in Drives) d.SetRequired(value);
            OnPropertyChanged(nameof(SizeBytes));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(DownloadText));
            OnPropertyChanged(nameof(WarningText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>«72,01 GB» o «No disponible por el momento» (nunca una cifra inventada).</summary>
    public string SizeText => _sizeBytes == 0 ? "No disponible por el momento" : PreciseSize((long)_sizeBytes);

    /// <summary>«Descarga necesaria: 58,3 GB» (comprimido), si se conoce.</summary>
    public string? DownloadText => _sizeBytes == 0 || _downloadBytes == 0 ? null : $"Descarga necesaria: {PreciseSize((long)_downloadBytes)}";

    /// <summary>«72,01 GB» del juego base obligatorio, sin ningún componente (null si no hay selector).</summary>
    public string? BaseSizeText { get; private set; }

    /// <summary>Componentes opcionales (DLC con contenido, etiquetas de Epic). Vacío = el juego no tiene.</summary>
    public ObservableCollection<InstallComponentViewModel> Components { get; } = [];
    public bool HasComponents => Components.Count > 0;

    /// <summary>
    /// Añade el selector de componentes. <paramref name="sizeOf"/> calcula el peso y la descarga exactos de lo
    /// marcado (null = no disponible).
    /// </summary>
    public void SetComponents(IEnumerable<InstallComponentViewModel> components,
        Func<IReadOnlyList<InstallComponentViewModel>, (ulong Install, ulong Download)?> sizeOf)
    {
        _sizeOf = sizeOf;
        Components.Clear();
        foreach (var c in components) Components.Add(c);
        var baseSize = sizeOf([]);
        BaseSizeText = baseSize is { Install: > 0 } b ? PreciseSize((long)b.Install) : "No disponible por el momento";
        OnPropertyChanged(nameof(BaseSizeText));
        OnPropertyChanged(nameof(HasComponents));
        RecalculateSize();
    }

    /// <summary>Los componentes llaman aquí al marcarse o desmarcarse.</summary>
    public void RecalculateSize()
    {
        if (_sizeOf is null) return;
        var size = _sizeOf(Components.Where(c => c.IsSelected).ToList());
        _downloadBytes = size?.Download ?? 0;
        SizeBytes = size?.Install ?? 0;
    }

    public ImageSource? Banner
    {
        get => _banner;
        set => SetProperty(ref _banner, value);
    }

    public bool CreateDesktopShortcut
    {
        get => _createDesktopShortcut;
        set => SetProperty(ref _createDesktopShortcut, value);
    }

    public bool CreateStartMenuShortcut
    {
        get => _createStartMenuShortcut;
        set => SetProperty(ref _createStartMenuShortcut, value);
    }

    public ObservableCollection<DriveOptionViewModel> Drives { get; }

    public DriveOptionViewModel? SelectedDrive
    {
        get => _selectedDrive;
        set { if (SetProperty(ref _selectedDrive, value)) OnPropertyChanged(nameof(WarningText)); }
    }

    /// <summary>Aviso bajo la lista (unidad sin espacio o sin unidades).</summary>
    public string? WarningText =>
        Drives.Count == 0 ? "No se encontró ninguna unidad de disco donde instalar."
        : _selectedDrive is { HasEnoughSpace: false } ? $"No hay espacio suficiente en {_selectedDrive.Label}: elige otra unidad."
        : null;

    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>Se completa al pulsar Instalar (con la elección) o Cancelar / Esc (null).</summary>
    public Task<InstallChoice?> Result => _result.Task;

    public void Close(InstallChoice? choice) => _result.TrySetResult(choice);

    /// <summary>Tamaño con dos decimales, como en el diálogo de Steam: «72,01 GB».</summary>
    public static string PreciseSize(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.00} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => Formatting.Bytes(bytes), // «4 bytes», «12 KB»: nunca «0 KB»
    };
}
