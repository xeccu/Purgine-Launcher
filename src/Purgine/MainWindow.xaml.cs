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
    private bool _exitRequested;
    private bool _trayHintShown;

    private readonly SteamNet.SteamSession _steamSession;

    public MainWindow(MainViewModel viewModel, LauncherSettings settings, UserLibrary library, SteamNet.SteamSession steamSession)
    {
        InitializeComponent();
        WindowTheming.UseDarkTitleBar(this);
        _vm = viewModel;
        _settings = settings;
        _library = library;
        _steamSession = steamSession;
        DataContext = viewModel;
        viewModel.Dialogs = this;

        SourceInitialized += (_, _) =>
        {
            CreateTrayIcon();
            ApplyBackdrop(GraphicsTheme.Current);
        };
        GraphicsTheme.Changed += ApplyBackdrop;
        Loaded += async (_, _) => await _vm.RefreshAsync();
        StateChanged += OnStateChanged;

        _vm.GameLaunched += (_, _) =>
        {
            if (_settings.MinimizeToTray) HideToTray(showHint: false);
            else WindowState = WindowState.Minimized;
        };
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
        WindowTheming.SetAcrylic(this, preset != Settings.GraphicsPreset.Low, (Brush)FindResource("BgBrush"));

    // =====================================================================================
    // Bandeja del sistema
    // =====================================================================================

    private void CreateTrayIcon()
    {
        _tray = new TrayIcon("Purgine");
        _tray.RestoreRequested += (_, _) => RestoreFromTray();
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

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        if (_settings.MinimizeToTray) HideToTray(showHint: true);
        else ProcessUtils.TrimCurrentProcess();
    }

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
        _vm.Shutdown();
        _tray?.Dispose();
        _tray = null;
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

    public bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private Window? VisibleOwner() => IsVisible ? this : null;
}
