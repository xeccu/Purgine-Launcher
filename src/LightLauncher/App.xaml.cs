using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LightLauncher.Core;
using LightLauncher.Infrastructure;
using LightLauncher.Library;
using LightLauncher.Settings;
using LightLauncher.Stats;
using LightLauncher.ViewModels;

namespace LightLauncher;

/// <summary>Punto de entrada: instancia única, composición de servicios y manejo global de errores.</summary>
public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\LightLauncher.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("LightLauncher ya está abierto.", "LightLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        Log.Init(AppPaths.LogFile);
        Log.Info($"LightLauncher {typeof(App).Assembly.GetName().Version} iniciado. Datos: {AppPaths.DataDir}");
        HookGlobalErrorHandlers();

        var settings = JsonFileStore.LoadOrCreate<LauncherSettings>(AppPaths.SettingsFile);
        if (settings.UseSoftwareRendering)
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // Composición manual: cuatro servicios no justifican un contenedor de DI.
        var locator = new SteamLocator();
        var scanner = new SteamLibraryScanner(locator);
        var tracker = new GameTimeTracker(AppPaths.PlaytimeFile);
        var orchestrator = new ProcessOrchestrator(locator, scanner, tracker, settings);
        var viewModel = new MainViewModel(scanner, orchestrator, tracker, settings);

        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("LightLauncher cerrado.");
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
