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
        HasEnoughSpace = requiredBytes == 0 || (ulong)Math.Max(0, freeBytes) >= requiredBytes;
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
    public bool HasEnoughSpace { get; }

    public string Tooltip =>
        !HasEnoughSpace ? "No hay espacio suficiente en esta unidad."
        : IsNewLibrary ? $"Se creará la biblioteca de Steam {LibraryPath} y se registrará en Steam."
        : $"Biblioteca de Steam: {LibraryPath}";
}

/// <summary>Lo elegido en el diálogo de instalar.</summary>
public sealed record InstallChoice(string LibraryPath, bool CreateDesktopShortcut, bool CreateStartMenuShortcut);

/// <summary>
/// Diálogo previo a una instalación (superposición dentro de la ventana, sin ventanas externas): juego y
/// tamaño, accesos directos y unidad de destino con su espacio libre.
/// </summary>
public sealed class InstallDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<InstallChoice?> _result = new();
    private DriveOptionViewModel? _selectedDrive;
    private ImageSource? _banner;
    private bool _createDesktopShortcut = true;
    private bool _createStartMenuShortcut = true;

    public InstallDialogViewModel(string name, ulong sizeBytes, IEnumerable<DriveOptionViewModel> drives)
    {
        Name = name;
        SizeBytes = sizeBytes;
        Drives = new ObservableCollection<DriveOptionViewModel>(drives);
        _selectedDrive = Drives.FirstOrDefault(d => d.IsDefault && d.HasEnoughSpace)
                         ?? Drives.FirstOrDefault(d => d.HasEnoughSpace)
                         ?? Drives.FirstOrDefault();
        InstallCommand = new RelayCommand(_ => Close(new InstallChoice(_selectedDrive!.LibraryPath, _createDesktopShortcut, _createStartMenuShortcut)),
            _ => _selectedDrive is { HasEnoughSpace: true });
        CancelCommand = new RelayCommand(_ => Close(null));
    }

    public string Name { get; }
    public ulong SizeBytes { get; }
    /// <summary>«72,01 GB» (o «Tamaño desconocido» si Steam no lo publica).</summary>
    public string SizeText => SizeBytes == 0 ? "Tamaño desconocido" : PreciseSize((long)SizeBytes);

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
        _ => $"{Math.Max(0, bytes) / 1024.0:0} KB",
    };
}
