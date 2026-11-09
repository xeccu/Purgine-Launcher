using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Purgine.Core;
using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.Settings;

namespace Purgine.ViewModels;

/// <summary>
/// Pestaña Ajustes. Edita en vivo la instancia compartida de <see cref="LauncherSettings"/>
/// (el orquestador lee los valores en cada sesión) y guarda automáticamente.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly LauncherSettings _s;
    private readonly UserLibrary _library;
    private readonly SteamLocator _steam;
    private readonly Action _save;
    private readonly Func<IDialogService?> _dialogs;
    private string _newTagName = string.Empty;
    private string _savedText = string.Empty;

    public SettingsViewModel(LauncherSettings settings, UserLibrary library, SteamLocator steam, Action save, Func<IDialogService?> dialogs,
        Func<Task> login, Func<Task> logout)
    {
        _s = settings;
        _library = library;
        _steam = steam;
        _save = save;
        _dialogs = dialogs;
        LoginCommand = new RelayCommand(async _ => await login(), _ => !IsSteamLoggedIn);
        LogoutCommand = new RelayCommand(async _ => await logout(), _ => IsSteamLoggedIn);

        AddTagCommand = new RelayCommand(_ => AddTag(), _ => !string.IsNullOrWhiteSpace(NewTagName));
        RemoveTagCommand = new RelayCommand(p => { if (p is string t) RemoveTag(t); });
        OpenDataFolderCommand = new RelayCommand(_ => OpenFolder(AppPaths.DataDir));
        ResetCommand = new RelayCommand(_ => ResetDefaults());

        RefreshTags();
        _library.Changed += (_, _) => RefreshTags();
    }

    // ===================== Cuenta de Steam (SteamKit2) =====================

    private string _accountText = string.Empty;
    private bool _isSteamLoggedIn;

    public string AccountText
    {
        get => _accountText;
        private set => SetProperty(ref _accountText, value);
    }

    public bool IsSteamLoggedIn
    {
        get => _isSteamLoggedIn;
        private set => SetProperty(ref _isSteamLoggedIn, value);
    }

    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }

    /// <summary>Lo llama la ventana principal cuando cambia la sesión.</summary>
    public void SetAccountState(string text, bool loggedIn)
    {
        AccountText = text;
        IsSteamLoggedIn = loggedIn;
        CommandManager.InvalidateRequerySuggested();
    }

    // ===================== Al terminar de jugar =====================

    public bool CleanupAlways { get => _s.SteamCleanupPolicy == CleanupPolicy.Always; set { if (value) SetPolicy(CleanupPolicy.Always); } }
    public bool CleanupOnlyOurs { get => _s.SteamCleanupPolicy == CleanupPolicy.OnlyIfStartedByLauncher; set { if (value) SetPolicy(CleanupPolicy.OnlyIfStartedByLauncher); } }
    public bool CleanupNever { get => _s.SteamCleanupPolicy == CleanupPolicy.Never; set { if (value) SetPolicy(CleanupPolicy.Never); } }

    public int CloudSyncGraceSeconds { get => _s.CloudSyncGraceSeconds; set => Set(v => _s.CloudSyncGraceSeconds = v, Math.Clamp(value, 0, 120)); }
    public int SteamShutdownTimeoutSeconds { get => _s.SteamShutdownTimeoutSeconds; set => Set(v => _s.SteamShutdownTimeoutSeconds = v, Math.Clamp(value, 5, 180)); }
    public int GameStartTimeoutSeconds { get => _s.GameStartTimeoutSeconds; set => Set(v => _s.GameStartTimeoutSeconds = v, Math.Clamp(value, 30, 1800)); }

    // ===================== Steam mientras juegas =====================

    public bool HelperPriorityNormal { get => _s.SteamHelperPriority == HelperPriority.Normal; set { if (value) SetHelperPriority(HelperPriority.Normal); } }
    public bool HelperPriorityBelowNormal { get => _s.SteamHelperPriority == HelperPriority.BelowNormal; set { if (value) SetHelperPriority(HelperPriority.BelowNormal); } }
    public bool HelperPriorityIdle { get => _s.SteamHelperPriority == HelperPriority.Idle; set { if (value) SetHelperPriority(HelperPriority.Idle); } }

    public bool TrimSteamMemoryWhilePlaying { get => _s.TrimSteamMemoryWhilePlaying; set => Set(v => _s.TrimSteamMemoryWhilePlaying = v, value); }
    public bool KillSteamWebHelperWhilePlaying { get => _s.KillSteamWebHelperWhilePlaying; set => Set(v => _s.KillSteamWebHelperWhilePlaying = v, value); }

    private void SetHelperPriority(HelperPriority priority)
    {
        if (_s.SteamHelperPriority == priority) return;
        _s.SteamHelperPriority = priority;
        OnPropertyChanged(nameof(HelperPriorityNormal));
        OnPropertyChanged(nameof(HelperPriorityBelowNormal));
        OnPropertyChanged(nameof(HelperPriorityIdle));
        Saved();
    }

    // ===================== Ventana y bandeja =====================

    public bool MinimizeOnLaunch { get => _s.MinimizeOnLaunch; set => Set(v => _s.MinimizeOnLaunch = v, value); }
    public bool CloseToTray { get => _s.CloseToTray; set => Set(v => _s.CloseToTray = v, value); }
    public bool MinimizeToTray { get => _s.MinimizeToTray; set => Set(v => _s.MinimizeToTray = v, value); }
    public bool ShowNotifications { get => _s.ShowNotifications; set => Set(v => _s.ShowNotifications = v, value); }
    public bool UseSoftwareRendering { get => _s.UseSoftwareRendering; set => Set(v => _s.UseSoftwareRendering = v, value); }

    // ===================== Avanzado =====================

    public string SteamClientArguments { get => _s.SteamClientArguments; set => Set(v => _s.SteamClientArguments = v, value?.Trim() ?? string.Empty); }

    // ===================== Etiquetas =====================

    public ObservableCollection<string> Tags { get; } = [];

    public string NewTagName
    {
        get => _newTagName;
        set => SetProperty(ref _newTagName, value ?? string.Empty);
    }

    // ===================== Información =====================

    public string DataDir => AppPaths.DataDir;
    public string SteamPath => _steam.SteamPath ?? "No encontrado";
    public string Version => $"Purgine {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)} · MIT";

    public string SavedText
    {
        get => _savedText;
        private set => SetProperty(ref _savedText, value);
    }

    public ICommand AddTagCommand { get; }
    public ICommand RemoveTagCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ResetCommand { get; }

    // ------------------------------------------------------------------------------------

    private void SetPolicy(CleanupPolicy policy)
    {
        if (_s.SteamCleanupPolicy == policy) return;
        _s.SteamCleanupPolicy = policy;
        OnPropertyChanged(nameof(CleanupAlways));
        OnPropertyChanged(nameof(CleanupOnlyOurs));
        OnPropertyChanged(nameof(CleanupNever));
        Saved();
    }

    private void Set<T>(Action<T> apply, T value, [CallerMemberName] string? name = null)
    {
        apply(value);
        OnPropertyChanged(name);
        Saved();
    }

    private void Saved()
    {
        _save();
        SavedText = "Cambios guardados automáticamente";
    }

    private void AddTag()
    {
        if (_library.AddTag(NewTagName)) NewTagName = string.Empty;
    }

    private void RemoveTag(string tag)
    {
        var count = _library.CountTagged(tag);
        var msg = count == 0
            ? $"¿Eliminar la etiqueta «{tag}»?"
            : $"¿Eliminar la etiqueta «{tag}»? Se quitará de {count} juego(s). Los juegos no se borran.";
        if (_dialogs()?.Confirm("Eliminar etiqueta", msg) ?? true)
            _library.RemoveTag(tag);
    }

    private void RefreshTags()
    {
        Tags.Clear();
        foreach (var t in _library.Tags) Tags.Add(t);
    }

    private void ResetDefaults()
    {
        if (!(_dialogs()?.Confirm("Restablecer ajustes", "¿Volver a los valores por defecto? Las etiquetas y juegos no se tocan.") ?? true))
            return;

        var d = new LauncherSettings();
        _s.SteamClientArguments = d.SteamClientArguments;
        _s.SteamCleanupPolicy = d.SteamCleanupPolicy;
        _s.CloudSyncGraceSeconds = d.CloudSyncGraceSeconds;
        _s.SteamShutdownTimeoutSeconds = d.SteamShutdownTimeoutSeconds;
        _s.GameStartTimeoutSeconds = d.GameStartTimeoutSeconds;
        _s.MinimizeOnLaunch = d.MinimizeOnLaunch;
        _s.CloseToTray = d.CloseToTray;
        _s.MinimizeToTray = d.MinimizeToTray;
        _s.ShowNotifications = d.ShowNotifications;
        _s.UseSoftwareRendering = d.UseSoftwareRendering;
        _s.SteamHelperPriority = d.SteamHelperPriority;
        _s.TrimSteamMemoryWhilePlaying = d.TrimSteamMemoryWhilePlaying;
        _s.KillSteamWebHelperWhilePlaying = d.KillSteamWebHelperWhilePlaying;
        OnPropertyChanged(string.Empty);
        Saved();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo abrir {path}: {ex.Message}");
        }
    }
}
