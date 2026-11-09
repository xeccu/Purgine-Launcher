using System.Windows;
using System.Windows.Media;
using LightLauncher.Core;
using LightLauncher.Infrastructure;
using LightLauncher.Library;
using LightLauncher.Settings;
using LightLauncher.Stats;
using LightLauncher.Store;
using LightLauncher.ViewModels;

namespace LightLauncher;

/// <summary>Punto de entrada: instancia única, composición de servicios y manejo global de errores.</summary>
public partial class App : Application
{
    private const string MutexName = @"Local\LightLauncher.SingleInstance";
    private const string ShowEventName = @"Local\LightLauncher.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private SteamStoreService? _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Ya hay una instancia (quizá oculta en la bandeja): le pedimos que se muestre y salimos.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                MessageBox.Show("LightLauncher ya está abierto.", "LightLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        Log.Init(AppPaths.LogFile);
        Log.Info($"LightLauncher {typeof(App).Assembly.GetName().Version} iniciado. Datos: {AppPaths.DataDir}");
        HookGlobalErrorHandlers();

        var settings = JsonFileStore.LoadOrCreate<LauncherSettings>(AppPaths.SettingsFile);
        if (settings.Migrate())
        {
            JsonFileStore.Save(AppPaths.SettingsFile, settings);
            Log.Info($"Ajustes migrados a la versión {LauncherSettings.CurrentVersion}.");
        }
        if (settings.UseSoftwareRendering)
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Composición manual: pocos servicios no justifican un contenedor de DI.
        var locator = new SteamLocator();
        var scanner = new SteamLibraryScanner(locator);
        var tracker = new GameTimeTracker(AppPaths.PlaytimeFile);
        var library = new UserLibrary(AppPaths.LibraryFile, AppPaths.CoversDir);
        var orchestrator = new ProcessOrchestrator(locator, scanner, tracker, settings);
        _store = new SteamStoreService(settings);
        var installer = new SteamInstaller(locator, scanner, orchestrator);
        // Descargas: monitor en vivo + pausa global; el orquestador los consulta antes de cerrar Steam.
        var suspender = new SteamSuspender(AppPaths.SuspendedStateFile);
        var monitor = new DownloadMonitor(locator, scanner) { IsSteamSuspended = () => suspender.IsSuspended };
        orchestrator.Downloads = monitor;
        orchestrator.Suspender = suspender;
        var viewModel = new MainViewModel(
            scanner, orchestrator, tracker, library, settings, _store, new LibraryWatcher(), locator,
            installer, new AppNameCache(AppPaths.AppNamesFile), monitor, suspender,
            saveSettings: () => JsonFileStore.Save(AppPaths.SettingsFile, settings));
        monitor.Start();

        var window = new MainWindow(viewModel, settings, library);
        MainWindow = window;
        window.Show();

        // Segunda instancia → mostrar esta ventana.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(window.RestoreFromTray), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("LightLauncher cerrado.");
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _store?.Dispose();
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { /* no era nuestro */ }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    private void HookGlobalErrorHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Excepción no controlada en UI", args.Exception);
            MessageBox.Show(args.Exception.Message, "LightLauncher — error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Excepción no controlada", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Excepción de tarea no observada", args.Exception);
            args.SetObserved();
        };
    }
}
