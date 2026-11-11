using Purgine.Library;

namespace Purgine.ViewModels;

/// <summary>
/// Casilla del filtro por launcher («Steam (120)», «Epic Games (41)»…). Se genera una por cada valor de
/// <see cref="GamePlatform"/> (vía <see cref="PlatformRegistry"/>): un proveedor nuevo aparece solo.
/// </summary>
public sealed class PlatformFilterViewModel : ObservableObject
{
    private readonly Action<PlatformFilterViewModel> _changed;
    private bool _isShown;
    private int _count;

    public PlatformFilterViewModel(PlatformInfo info, bool isShown, Action<PlatformFilterViewModel> changed)
    {
        Info = info;
        _isShown = isShown;
        _changed = changed;
    }

    public PlatformInfo Info { get; }
    public GamePlatform Platform => Info.Platform;
    public string DisplayName => Info.DisplayName;

    public bool IsShown
    {
        get => _isShown;
        set { if (SetProperty(ref _isShown, value)) _changed(this); }
    }

    public int Count
    {
        get => _count;
        set { if (SetProperty(ref _count, value)) OnPropertyChanged(nameof(Label)); }
    }

    public string Label => $"{DisplayName} ({Count})";
}
