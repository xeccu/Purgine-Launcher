using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using LightLauncher.Core;
using LightLauncher.Infrastructure;
using LightLauncher.Library;
using LightLauncher.Settings;
using LightLauncher.Stats;

namespace LightLauncher.ViewModels;

/// <summary>Estado de la ventana principal: biblioteca, búsqueda y sesión en curso.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly SteamLibraryScanner _scanner;
    private readonly ProcessOrchestrator _orchestrator;
    private readonly GameTimeTracker _tracker;
    private readonly LauncherSettings _settings;

    private CancellationTokenSource? _sessionCts;
    private string _searchText = string.Empty;
    private string _statusText = "Listo.";
    private bool _isScanning;
    private bool _isSessionActive;

    public MainViewModel(SteamLibraryScanner scanner, ProcessOrchestrator orchestrator, GameTimeTracker tracker, LauncherSettings settings)
    {
        _scanner = scanner;
        _orchestrator = orchestrator;
        _tracker = tracker;
        _settings = settings;

        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = o => o is GameCardViewModel g &&
            (_searchText.Length == 0 || g.Name.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase));

        RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsScanning && !IsSessionActive);
        LaunchCommand = new RelayCommand(p => { if (p is GameCardViewModel g) _ = LaunchAsync(g); }, _ => !IsSessionActive);
        CancelCommand = new RelayCommand(_ => _sessionCts?.Cancel(), _ => IsSessionActive);
        OpenDataFolderCommand = new RelayCommand(_ => OpenDataFolder());

        _orchestrator.StatusChanged += (_, s) => OnUi(() => StatusText = s.Message);
    }

    public ObservableCollection<GameCardViewModel> Games { get; } = [];
    public ICollectionView GamesView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    /// <summary>La vista lo usa para minimizar la ventana al lanzar un juego.</summary>
    public event EventHandler? GameLaunched;

    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value?.Trim() ?? string.Empty)) GamesView.Refresh(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set { if (SetProperty(ref _isScanning, value)) OnPropertyChanged(nameof(EmptyStateText)); }
    }

    public bool IsSessionActive
    {
        get => _isSessionActive;
        private set => SetProperty(ref _isSessionActive, value);
    }

    public string SummaryText
    {
        get
        {
            var total = _tracker.GetGrandTotal();
            var played = total == TimeSpan.Zero
                ? "aún sin sesiones registradas"
                : $"{Formatting.Duration(total)} jugadas con LightLauncher";
            return $"{Games.Count} juegos instalados · {played}";
        }
    }

    /// <summary>Texto del estado vacío (null = hay juegos, no se muestra).</summary>
    public string? EmptyStateText =>
        IsScanning ? "Escaneando bibliotecas de Steam…"
        : Games.Count > 0 ? null
        : "No se han encontrado juegos de Steam instalados.\nComprueba que Steam está instalado y pulsa «Actualizar».";

    /// <summary>Escanea las bibliotecas (en segundo plano) y repuebla el grid.</summary>
    public async Task RefreshAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        StatusText = "Escaneando bibliotecas…";
        try
        {
            var entries = await Task.Run(() => _scanner.Scan());
            Games.Clear();
            foreach (var e in entries) Games.Add(new GameCardViewModel(e, _tracker));
            StatusText = entries.Count > 0 ? "Listo. Haz clic en un juego para lanzarlo." : "Sin juegos.";
        }
        catch (Exception ex)
        {
            Log.Error("Fallo al escanear", ex);
            StatusText = $"Error al escanear: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(EmptyStateText));
        }

        // Portadas una a una, sin bloquear la UI ni disparar picos de memoria.
        foreach (var card in Games.ToList())
            await card.LoadCoverAsync();
    }

    private async Task LaunchAsync(GameCardViewModel card)
    {
        if (IsSessionActive || _orchestrator.IsBusy) return;

        _sessionCts = new CancellationTokenSource();
        IsSessionActive = true;
        card.IsRunning = true;
        CommandManager.InvalidateRequerySuggested();
        if (_settings.MinimizeOnLaunch) GameLaunched?.Invoke(this, EventArgs.Empty);

        try
        {
            // Todo el sondeo de procesos corre fuera del hilo de UI.
            var token = _sessionCts.Token;
            var result = await Task.Run(() => _orchestrator.LaunchSteamGameAsync(card.Entry, token));
            if (!result.Success) StatusText = result.Message;
            else StatusText = $"{result.Message} {StatusText}";
        }
        finally
        {
            card.IsRunning = false;
            card.RefreshStats();
            IsSessionActive = false;
            _sessionCts.Dispose();
            _sessionCts = null;
            OnPropertyChanged(nameof(SummaryText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo(AppPaths.DataDir) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo abrir la carpeta de datos: {ex.Message}");
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
