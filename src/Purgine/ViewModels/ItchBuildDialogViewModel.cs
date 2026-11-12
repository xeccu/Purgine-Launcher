using System.Windows.Input;
using System.Windows.Media;
using Purgine.Infrastructure;
using Purgine.ItchNet;

namespace Purgine.ViewModels;

/// <summary>Una variante descargable en el selector.</summary>
public sealed class ItchBuildOptionViewModel(ItchUpload upload)
{
    public ItchUpload Upload { get; } = upload;
    public string Label => Upload.Label;
    public string FileName => Upload.FileName;
    public string SizeText => Upload.Size > 0 ? Formatting.Bytes(Upload.Size) : string.Empty;

    public string PlatformsText
    {
        get
        {
            var p = new List<string>();
            if (Upload.Windows) p.Add("Windows");
            if (Upload.Linux) p.Add("Linux");
            if (Upload.Mac) p.Add("macOS");
            if (p.Count == 0) p.Add(Upload.Type is { Length: > 0 } t ? t : "Otro");
            return string.Join(" · ", p);
        }
    }

    public bool IsWindows => Upload.Windows;
}

/// <summary>
/// Selector de versiones de un juego de itch.io (Windows 64 / 32 bits, portable, Linux…): se elige qué archivo
/// descargar. Las de Windows van primero y la primera queda elegida.
/// </summary>
public sealed class ItchBuildDialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ItchUpload?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ItchBuildOptionViewModel? _selected;
    private ImageSource? _banner;

    public ItchBuildDialogViewModel(string title, IEnumerable<ItchUpload> uploads)
    {
        Name = title;
        Options = uploads
            .OrderByDescending(u => u.Windows)
            .ThenByDescending(u => u.Label.Contains("64", StringComparison.Ordinal))
            .ThenByDescending(u => u.Updated)
            .Select(u => new ItchBuildOptionViewModel(u)).ToList();
        _selected = Options.FirstOrDefault();
        InstallCommand = new RelayCommand(_ => _result.TrySetResult(_selected?.Upload), _ => _selected is not null);
        CancelCommand = new RelayCommand(_ => _result.TrySetResult(null));
    }

    public string Name { get; }
    public IReadOnlyList<ItchBuildOptionViewModel> Options { get; }
    public Task<ItchUpload?> Result => _result.Task;

    public ItchBuildOptionViewModel? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public ImageSource? Banner
    {
        get => _banner;
        set => SetProperty(ref _banner, value);
    }

    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }
}
