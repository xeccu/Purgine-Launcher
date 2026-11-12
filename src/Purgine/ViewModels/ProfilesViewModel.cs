using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Purgine.Infrastructure;

namespace Purgine.ViewModels;

/// <summary>Un perfil en el menú: foto (o inicial), nombre y si es el activo.</summary>
public sealed class ProfileItemViewModel(UserProfile profile, bool isActive)
{
    public UserProfile Profile { get; } = profile;
    public Guid Id => Profile.Id;
    public string Name => Profile.Name;
    public bool IsActive { get; } = isActive;
    public bool CanDelete => !IsActive && !Profile.UsesRootFolder;
    public string Initial => Profile.Name.Length > 0 ? char.ToUpperInvariant(Profile.Name[0]).ToString() : "?";
    public ImageSource? Avatar { get; } = profile.AvatarPath is { } p && File.Exists(p) ? ImageLoader.FromFile(p, 96) : null;
    public bool HasAvatar => Avatar is not null;
}

/// <summary>Formulario de crear / editar perfil (superposición).</summary>
public sealed class ProfileEditorViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<ProfileEditorViewModel, string?> _save;
    private string _name;
    private string? _newAvatarPath;
    private ImageSource? _preview;
    private string? _error;

    public ProfileEditorViewModel(UserProfile? existing, Func<ProfileEditorViewModel, string?> save)
    {
        Existing = existing;
        _save = save;
        _name = existing?.Name ?? string.Empty;
        _preview = existing?.AvatarPath is { } p && File.Exists(p) ? ImageLoader.FromFile(p, 192) : null;
        ChoosePhotoCommand = new RelayCommand(_ => ChoosePhoto());
        SaveCommand = new RelayCommand(_ => Save(), _ => _name.Trim().Length > 0);
        CancelCommand = new RelayCommand(_ => _result.TrySetResult(false));
    }

    public UserProfile? Existing { get; }
    public string Title => Existing is null ? "Nuevo perfil" : "Editar perfil";
    public string SaveText => Existing is null ? "Crear perfil" : "Guardar";
    public Task<bool> Result => _result.Task;

    public string Name
    {
        get => _name;
        set { if (SetProperty(ref _name, value ?? string.Empty)) { OnPropertyChanged(nameof(Initial)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string Initial => _name.Trim() is { Length: > 0 } n ? char.ToUpperInvariant(n[0]).ToString() : "?";

    /// <summary>Foto nueva elegida (null = sin cambios).</summary>
    public string? NewAvatarPath => _newAvatarPath;

    public ImageSource? Preview
    {
        get => _preview;
        private set { if (SetProperty(ref _preview, value)) OnPropertyChanged(nameof(HasPreview)); }
    }

    public bool HasPreview => _preview is not null;

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public ICommand ChoosePhotoCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void ChoosePhoto()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Foto del perfil",
            Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
        };
        if (dialog.ShowDialog() != true) return;
        _newAvatarPath = dialog.FileName;
        Preview = ImageLoader.FromFile(dialog.FileName, 192);
    }

    private void Save()
    {
        Error = _save(this);
        if (Error is null) _result.TrySetResult(true);
    }
}

/// <summary>
/// Perfiles locales: el activo (foto y nombre en la cabecera o en la barra lateral) y el menú para cambiar de perfil,
/// crear (hasta 5), editar o borrar. Cambiar de perfil reinicia Purgine con los datos de ese perfil.
/// </summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly Func<IDialogService?> _dialogs;
    private ProfileEditorViewModel? _editor;
    private bool _isMenuOpen;

    public ProfilesViewModel(Func<IDialogService?> dialogs)
    {
        _dialogs = dialogs;
        SwitchCommand = new RelayCommand(p => { if (p is ProfileItemViewModel item) Switch(item); });
        AddCommand = new RelayCommand(_ => _ = AddAsync(), _ => CanAdd);
        EditCommand = new RelayCommand(p => _ = EditAsync((p as ProfileItemViewModel)?.Profile ?? Active.Profile));
        DeleteCommand = new RelayCommand(p => { if (p is ProfileItemViewModel item) Delete(item); }, p => p is ProfileItemViewModel { CanDelete: true });
        Reload();
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];
    public ProfileItemViewModel Active { get; private set; } = null!;
    public IEnumerable<ProfileItemViewModel> Others => Profiles.Where(p => !p.IsActive);
    public bool CanAdd => Profiles.Count < ProfileStore.MaxProfiles;
    public string CountText => $"{Profiles.Count} de {ProfileStore.MaxProfiles} perfiles";

    public bool IsMenuOpen
    {
        get => _isMenuOpen;
        set => SetProperty(ref _isMenuOpen, value);
    }

    /// <summary>Formulario abierto (null = cerrado).</summary>
    public ProfileEditorViewModel? Editor
    {
        get => _editor;
        private set => SetProperty(ref _editor, value);
    }

    /// <summary>Hay que reiniciar con el perfil nuevo (lo hace la ventana, que guarda todo antes).</summary>
    public event EventHandler? SwitchRequested;

    public ICommand SwitchCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }

    private void Reload()
    {
        var list = ProfileStore.Load(AppPaths.DataDir);
        Profiles.Clear();
        foreach (var p in list.Profiles) Profiles.Add(new ProfileItemViewModel(p, p.Id == list.ActiveId));
        Active = Profiles.First(p => p.IsActive);
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(Others));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CountText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Switch(ProfileItemViewModel item)
    {
        IsMenuOpen = false;
        if (item.IsActive) return;
        ProfileStore.SetActive(AppPaths.DataDir, item.Id);
        Log.Info($"Cambio de perfil: {Active.Name} → {item.Name}.");
        SwitchRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task AddAsync()
    {
        IsMenuOpen = false;
        UserProfile? created = null;
        var editor = new ProfileEditorViewModel(null, e => ProfileStore.Create(AppPaths.DataDir, e.Name, e.NewAvatarPath, out created));
        Editor = editor;
        var saved = await editor.Result;
        Editor = null;
        Reload();
        if (!saved || created is null) return;
        if (_dialogs()?.Confirm("Perfil creado", $"¿Cambiar ahora al perfil «{created.Name}»? Purgine se reiniciará con sus datos (empieza sin cuentas vinculadas).") == true)
            Switch(Profiles.First(p => p.Id == created.Id));
    }

    private async Task EditAsync(UserProfile profile)
    {
        IsMenuOpen = false;
        var editor = new ProfileEditorViewModel(profile, e => ProfileStore.Update(AppPaths.DataDir, profile.Id, e.Name, e.NewAvatarPath));
        Editor = editor;
        await editor.Result;
        Editor = null;
        Reload();
    }

    private void Delete(ProfileItemViewModel item)
    {
        IsMenuOpen = false;
        if (_dialogs()?.Confirm("Borrar perfil",
                $"¿Borrar el perfil «{item.Name}»?\n\nSe eliminan de este equipo sus cuentas vinculadas (Steam, Epic, itch.io), " +
                "su sesión del navegador, sus ajustes, etiquetas, tiempo jugado y notas. Los juegos instalados no se tocan.") != true)
            return;
        var error = ProfileStore.Delete(AppPaths.DataDir, item.Id);
        if (error is not null) _dialogs()?.Confirm("Borrar perfil", error);
        Reload();
    }
}
