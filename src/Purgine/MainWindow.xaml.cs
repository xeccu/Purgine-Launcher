using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Purgine.Core;
using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.Settings;
using Purgine.ViewModels;
using Purgine.Views;

namespace Purgine;

/// <summary>
/// Ventana principal. Además de la vista, gestiona la bandeja del sistema: la X y el minimizado
/// la ocultan (según ajustes) y el proceso recorta su memoria mientras está oculto.
/// </summary>
public partial class MainWindow : Window, IDialogService
{
    private readonly MainViewModel _vm;
    private readonly LauncherSettings _settings;
    private readonly UserLibrary _library;
    private TrayIcon? _tray;
    private readonly DispatcherTimer _idleTrim;
    private bool _exitRequested;
    private bool _trayHintShown;

    private readonly SteamNet.SteamSession _steamSession;

    private readonly EpicNet.EpicAccountService _epic;

    public MainWindow(MainViewModel viewModel, LauncherSettings settings, UserLibrary library, SteamNet.SteamSession steamSession,
        EpicNet.EpicAccountService epic)
    {
        InitializeComponent();
        WindowTheming.UseDarkTitleBar(this);
        _vm = viewModel;
        _settings = settings;
        _library = library;
        _steamSession = steamSession;
        _epic = epic;
        DataContext = viewModel;
        viewModel.Dialogs = this;

        SourceInitialized += (_, _) =>
        {
            CreateTrayIcon();
            ApplyBackdrop(GraphicsTheme.Current);
        };
        GraphicsTheme.Changed += ApplyBackdrop;

        // Reposo: si la ventana se queda en segundo plano (otra app delante) un rato, se compacta la memoria.
        _idleTrim = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(45) };
        _idleTrim.Tick += (_, _) =>
        {
            _idleTrim.Stop();
            if (!IsActive) ProcessUtils.TrimCurrentProcess();
        };
        Deactivated += (_, _) => _idleTrim.Start();
        Activated += (_, _) => _idleTrim.Stop();
        Loaded += async (_, _) => await _vm.RefreshAsync();
        Loaded += (_, _) => _ = _vm.Updates.CheckOnStartupAsync();
        // Auto-actualización: se cierra Purgine guardando todo y el instalador descargado hace el resto.
        _vm.Updates.InstallRequested += (_, setupPath) =>
        {
            _pendingUpdate = setupPath;
            ExitApplication();
            if (!_closed)
            {
                _pendingUpdate = null;
                _vm.Updates.InstallCancelled();
            }
        };
        StateChanged += OnStateChanged;

        // Al lanzar un juego se minimiza a la barra de tareas (como el botón de minimizar).
        _vm.GameLaunched += (_, _) => WindowState = WindowState.Minimized;
        _vm.SessionFinished += (_, _) => { if (!IsVisible) ScheduleTrimWhileHidden(); };
        _vm.NotificationRequested += (title, text) =>
        {
            if (_settings.ShowNotifications) _tray?.ShowNotification(title, text);
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.StatusText))
                _tray?.SetTooltip($"Purgine — {_vm.StatusText}");
        };
    }

    /// <summary>Ultra-moderno: material acrílico detrás de los paneles translúcidos.</summary>
    private void ApplyBackdrop(Settings.GraphicsPreset preset) =>
        WindowTheming.SetAcrylic(this, GraphicsTheme.UseAcrylic, (Brush)FindResource("BgBrush"));

    // =====================================================================================
    // Bandeja del sistema
    // =====================================================================================

    private void CreateTrayIcon()
    {
        _tray = new TrayIcon("Purgine");
        _tray.RestoreRequested += (_, _) => RestoreFromTray();
        _vm.AttentionRequested += (_, _) => RestoreFromTray();
        // Cambio de perfil: se guarda todo, se cierra y arranca una instancia nueva con los datos de ese perfil.
        _vm.Profiles.SwitchRequested += (_, _) =>
        {
            App.StartNewInstanceForProfile();
            ExitApplication();
        };
        _tray.RefreshRequested += async (_, _) => await _vm.RefreshAsync();
        _tray.ExitRequested += (_, _) => ExitApplication();
    }

    /// <summary>Muestra la ventana (desde la bandeja o desde una segunda instancia del .exe).</summary>
    public void RestoreFromTray()
    {
        var wasHidden = !IsVisible;
        Show();
        if (wasHidden) _vm.OnRestoredFromTray();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;   // truco habitual para traerla al frente de verdad
        Topmost = false;
        Focus();
    }

    private void HideToTray(bool showHint)
    {
        Hide();
        _vm.OnHiddenToTray();
        if (showHint && !_trayHintShown && _settings.ShowNotifications)
        {
            _trayHintShown = true;
            _tray?.ShowNotification("Purgine sigue activo",
                "Está en la bandeja del sistema usando el mínimo de memoria. Clic derecho en el icono para salir.");
        }
        ProcessUtils.TrimCurrentProcess();

        // El hilo de render de WPF sigue tocando memoria justo después de ocultar la ventana:
        // un segundo recorte unos segundos después deja el proceso realmente en mínimos.
        ScheduleTrimWhileHidden();
    }

    /// <summary>Recorta la memoria dentro de unos segundos si la ventana sigue oculta.</summary>
    private void ScheduleTrimWhileHidden()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsVisible) ProcessUtils.TrimCurrentProcess();
        };
        timer.Start();
    }

    /// <summary>
    /// Minimizar es el comportamiento normal de Windows (la ventana queda en la barra de tareas); solo se
    /// recorta la memoria. La bandeja se reserva para la X (ver <see cref="OnClosing"/>).
    /// </summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) ScheduleTrimWhileMinimized();
    }

    /// <summary>Recorta la memoria al minimizar y otra vez unos segundos después (el render aún toca memoria).</summary>
    private void ScheduleTrimWhileMinimized()
    {
        ProcessUtils.TrimCurrentProcess();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (WindowState == WindowState.Minimized) ProcessUtils.TrimCurrentProcess();
        };
        timer.Start();
    }

    private string? _pendingUpdate;
    private bool _closed;

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested && _settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray(showHint: true);
            return;
        }

        if (_vm.IsSessionActive)
        {
            if (!IsVisible) RestoreFromTray();
            var answer = MessageBox.Show(this,
                "Hay una sesión de juego en curso.\n\nSi sales ahora, el tiempo se registrará solo hasta este momento y Steam no se cerrará automáticamente.\n\n¿Salir de todos modos?",
                "Purgine", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                _exitRequested = false;
                e.Cancel = true;
                return;
            }
            _vm.CancelCommand.Execute(null);
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _vm.Shutdown();
        _tray?.Dispose();
        _tray = null;
        if (_pendingUpdate is not null)
        {
            try { App.StartUpdater(_pendingUpdate); }
            catch (Exception ex) { Log.Error("No se pudo abrir el instalador de la actualización", ex); }
        }
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    // =====================================================================================
    // IDialogService
    // =====================================================================================

    public string? PromptText(string title, string message, string initial = "") =>
        TextPromptDialog.Ask(VisibleOwner(), title, message, initial);

    public LocalGameDefinition? EditLocalGame(LocalGameDefinition? existing) =>
        LocalGameDialog.Show(VisibleOwner(), _library, existing);

    public Task<bool> SteamLoginAsync()
    {
        if (!IsVisible) RestoreFromTray();
        return Task.FromResult(SteamLoginDialog.Show(this, _steamSession));
    }

    public bool EpicLogin()
    {
        if (!IsVisible) RestoreFromTray();
        return EpicLoginDialog.Show(this, _epic);
    }

    public void ShowGameProperties(GamePropertiesViewModel properties)
    {
        if (!IsVisible) RestoreFromTray();
        GamePropertiesDialog.Show(this, properties);
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private Window? VisibleOwner() => IsVisible ? this : null;
}
