using System.Windows;
using System.Windows.Media;
using Purgine.Core;
using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.Settings;
using Purgine.Stats;
using Purgine.SteamNet;
using Purgine.ViewModels;

namespace Purgine;

/// <summary>Punto de entrada: instancia única, composición de servicios y manejo global de errores.</summary>
public partial class App : Application
{
    private const string MutexName = @"Local\Purgine.SingleInstance";
    private const string ShowEventName = @"Local\Purgine.Show";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private SteamArtService? _art;
    private SteamCommunityService? _community;
    private EpicNet.EpicAccountService? _epic;
    private GameNotesStore? _notes;
    private SteamSession? _steamSession;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // El mismo .exe es el instalador (Purgine-Setup-*.exe), el actualizador (--update) y el desinstalador.
        if (Updates.SetupRequest.FromArgs(e.Args) is { } setup)
        {
            RunSetup(setup);
            return;
        }

        var runAppId = ParseRunArgument(e.Args);
        WaitForPreviousInstance(e.Args);
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Acceso directo de un juego con Purgine ya abierto: se le pasa la petición.
            if (runAppId is not null)
            {
                try { File.WriteAllText(AppPaths.LaunchRequestFile, runAppId); }
                catch (Exception) { /* sin petición, al menos se muestra la ventana */ }
            }
            // Ya hay una instancia (quizá oculta en la bandeja): le pedimos que se muestre y salimos.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                MessageBox.Show("Purgine ya está abierto.", "Purgine", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        Log.Init(AppPaths.LogFile);
        Log.Info($"Purgine {AppVersion.Display} iniciado. Datos: {AppPaths.DataDir}");
        HookGlobalErrorHandlers();

        var settings = JsonFileStore.LoadOrCreate<LauncherSettings>(AppPaths.SettingsFile);
        if (settings.Migrate())
        {
            JsonFileStore.Save(AppPaths.SettingsFile, settings);
            Log.Info($"Ajustes migrados a la versión {LauncherSettings.CurrentVersion}.");
        }
        if (settings.UseSoftwareRendering)
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        GraphicsTheme.Apply(settings.GraphicsPreset);
        GraphicsTheme.ApplySkin(settings.Skin);

        // Composición manual: pocos servicios no justifican un contenedor de DI.
        var locator = new SteamLocator();
        var scanner = new SteamLibraryScanner(locator);
        var tracker = new GameTimeTracker(AppPaths.PlaytimeFile);
        var library = new UserLibrary(AppPaths.LibraryFile, AppPaths.CoversDir);
        var orchestrator = new ProcessOrchestrator(locator, scanner, tracker, settings);
        _art = new SteamArtService(locator, AppPaths.ArtCacheDir);
        // Red de Steam (SteamKit2): sesión, propiedad y descargas sin el cliente de escritorio.
        _steamSession = new SteamSession(new SteamCredentialStore(AppPaths.SteamSessionFile));
        var ownership = new SteamOwnershipService(_steamSession, AppPaths.OwnedGamesFile);
        _community = new SteamCommunityService(_steamSession);
        // Epic Games sin su launcher: cuenta vinculada (OAuth) y lanzamiento directo con código de intercambio.
        _epic = new EpicNet.EpicAccountService(AppPaths.EpicSessionFile, AppPaths.EpicOwnershipDir);
        var epic = _epic;
        orchestrator.EpicAuthProvider = (game, ct) => epic.GetLaunchAuthAsync(game.EpicNamespace, game.EpicCatalogItemId, ct);
        // Partidas en la nube de Epic (la misma que usa su launcher) y copias locales de las partidas.
        var cloud = new CloudSaveManager(epic, new SavePathResolver(AppPaths.SavePathsFile), AppPaths.SaveBackupsDir,
            AppPaths.CloudSyncFile, _steamSession, () => locator.SteamPath);
        orchestrator.EpicPreLaunch = cloud.PreLaunchAsync;
        _notes = new GameNotesStore(AppPaths.GameNotesFile);
        var downloads = new SteamDownloadManager(_steamSession, ownership, locator, scanner, AppPaths.DownloadQueueFile);

        var viewModel = new MainViewModel(
            scanner, orchestrator, tracker, library, settings, _art, _community, _notes, _epic, new LibraryWatcher(), locator,
            _steamSession, ownership, downloads,
            saveSettings: () => JsonFileStore.Save(AppPaths.SettingsFile, settings), cloud: cloud);
        downloads.TryRegisterPending();

        var window = new MainWindow(viewModel, settings, library, _steamSession, _epic);
        MainWindow = window;
        window.Show();

        // Segunda instancia → mostrar esta ventana.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (TakeLaunchRequest() is { } appId) viewModel.RequestLaunch(appId);
                else window.RestoreFromTray();
            }), null, Timeout.Infinite, executeOnlyOnce: false);
        if (runAppId is not null) viewModel.RequestLaunch(runAppId);
    }

    /// <summary>
    /// Cambio de perfil: la instancia nueva llega con <c>--wait-pid &lt;pid&gt;</c> y espera a que la anterior termine de
    /// cerrarse (y suelte la instancia única y sus archivos) antes de arrancar con el perfil nuevo.
    /// </summary>
    private static void WaitForPreviousInstance(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) || !int.TryParse(args[i + 1], out var pid)) continue;
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(pid);
                previous.WaitForExit(20_000);
            }
            catch (ArgumentException) { /* ya terminó */ }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>Modo instalador / actualizador / desinstalador: solo su ventana, sin instancia única ni servicios.</summary>
    private async void RunSetup(Updates.SetupRequest setup)
    {
        Log.Init(Path.Combine(Path.GetTempPath(), "purgine-setup.log"));
        Log.Info($"Instalador de Purgine {AppVersion.Display}: {setup}");
        GraphicsTheme.Apply(GraphicsPreset.High);
        GraphicsTheme.ApplySkin(UiSkin.Steam);
        if (setup is { Mode: Updates.SetupMode.Install, Silent: true })
        {
            Shutdown(await Views.SetupWindow.RunSilentInstallAsync());
            return;
        }
        var window = new Views.SetupWindow(setup);
        window.Closed += (_, _) => Shutdown();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Auto-actualización: abre el instalador descargado en modo <c>--update</c>. Espera a que este proceso termine,
    /// reemplaza el ejecutable y vuelve a abrir Purgine.
    /// </summary>
    public static void StartUpdater(string setupPath)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setupPath,
            $"--update --wait-pid {Environment.ProcessId} --target \"{Environment.ProcessPath}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(setupPath)!,
        })?.Dispose();
    }

    /// <summary>Reinicia Purgine con el perfil activo nuevo (lo llama la ventana tras guardar todo).</summary>
    public static void StartNewInstanceForProfile()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!,
            $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false })?.Dispose();
    }

    /// <summary><c>--run 440</c> → "440" (accesos directos creados al instalar).</summary>
    private static string? ParseRunArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--run", StringComparison.OrdinalIgnoreCase) && NormalizeRunId(args[i + 1]) is { } id)
                return id;
        return null;
    }

    /// <summary>«440» (Steam) o «epic:AppName» (Epic); cualquier otra cosa se ignora.</summary>
    private static string? NormalizeRunId(string text)
    {
        if (uint.TryParse(text, out var id)) return id.ToString();
        return text.StartsWith("epic:", StringComparison.Ordinal) && text.Length > 5 && text.Length < 200 &&
               text[5..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? text : null;
    }

    private static string? TakeLaunchRequest()
    {
        try
        {
            if (!File.Exists(AppPaths.LaunchRequestFile)) return null;
            var text = File.ReadAllText(AppPaths.LaunchRequestFile).Trim();
            File.Delete(AppPaths.LaunchRequestFile);
            return NormalizeRunId(text);
        }
        catch (Exception) { return null; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Purgine cerrado.");
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _art?.Dispose();
        _community?.Dispose();
        _epic?.Dispose();
        _notes?.Dispose();
        try { _steamSession?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { Log.Warn($"Cierre de la sesión de Steam: {ex.Message}"); }
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
            MessageBox.Show(args.Exception.Message, "Purgine — error", MessageBoxButton.OK, MessageBoxImage.Error);
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
