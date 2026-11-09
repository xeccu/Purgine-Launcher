using LightLauncher.Infrastructure;
using LightLauncher.Library;
using LightLauncher.Settings;
using LightLauncher.Stats;

namespace LightLauncher.Core;

/// <summary>Fases del ciclo de vida de una sesión de juego.</summary>
public enum LaunchPhase
{
    Idle,
    StartingClient,
    LaunchingGame,
    WaitingForGame,
    Playing,
    SyncingCloud,
    CleaningUp,
    Finished,
    Cancelled,
    Failed,
}

public sealed record OrchestratorStatus(LaunchPhase Phase, string Message, GameEntry? Game = null);

public sealed record LaunchResult(bool Success, string Message, PlaySession? Session = null);

/// <summary>
/// Capa orquestadora "fantasma" (Process Lifecycle Manager):
/// <list type="number">
///   <item>Arranca el cliente oficial en modo ligero solo si hace falta (las licencias/DRM las valida él).</item>
///   <item>Lanza el juego con el protocolo oficial <c>steam://run/{appId}</c>.</item>
///   <item>Vigila el proceso del juego y cronometra la sesión.</item>
///   <item>Al salir: deja margen a la sincronización en la nube, cierra Steam de forma ordenada
///         (<c>-shutdown</c>) y fuerza el cierre de cualquier proceso residual.</item>
/// </list>
/// Solo admite una sesión simultánea.
/// </summary>
public sealed class ProcessOrchestrator
{
    private readonly SteamLocator _steam;
    private readonly SteamLibraryScanner _scanner;
    private readonly GameTimeTracker _tracker;
    private readonly LauncherSettings _settings;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public event EventHandler<OrchestratorStatus>? StatusChanged;

    public bool IsBusy => _sessionGate.CurrentCount == 0;

    public ProcessOrchestrator(SteamLocator steam, SteamLibraryScanner scanner, GameTimeTracker tracker, LauncherSettings settings)
    {
        _steam = steam;
        _scanner = scanner;
        _tracker = tracker;
        _settings = settings;
    }

    /// <summary>Lanza un juego de Steam por AppID (resuelve su carpeta desde los manifiestos locales).</summary>
    public Task<LaunchResult> LaunchSteamGame(string appId, CancellationToken ct = default)
    {
        var game = _scanner.FindGame(appId) ?? new GameEntry
        {
            // Juego no instalado en una biblioteca conocida: se vigila solo por Registro.
            Id = GameEntry.MakeSteamId(appId),
            Platform = GamePlatform.Steam,
            AppId = appId,
            Name = $"App {appId}",
        };
        return LaunchSteamGameAsync(game, ct);
    }

    /// <summary>Ejecuta la sesión completa: cliente → juego → vigilancia → limpieza.</summary>
    public async Task<LaunchResult> LaunchSteamGameAsync(GameEntry game, CancellationToken ct = default)
    {
        if (!await _sessionGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return new LaunchResult(false, "Ya hay una sesión de juego en curso.");

        bool steamStartedByUs = false;
        bool runCleanup = false;
        try
        {
            var steamExe = _steam.SteamExe;
            if (steamExe is null)
                return Fail(game, "No se encontró Steam en este equipo (HKCU\\Software\\Valve\\Steam).");

            // 1-2) Cliente oficial en modo ligero.
            steamStartedByUs = await EnsureSteamRunningAsync(steamExe, game, ct).ConfigureAwait(false);
            runCleanup = true; // a partir de aquí, siempre limpiamos según la política

            // 3) Protocolo oficial. Se pasa al propio steam.exe (igual que hace el manejador
            //    de protocolo registrado: "steam.exe -- %1"), así no dependemos del shell.
            Report(LaunchPhase.LaunchingGame, $"Lanzando {game.Name}…", game);
            ProcessUtils.Start(steamExe, $"-- \"steam://run/{game.AppId}\"");
            Log.Info($"steam://run/{game.AppId} enviado ({game.Name}).");

            // 4) Esperar a que el juego exista y engancharnos a él.
            var watcher = new GameProcessWatcher(game.AppId, game.InstallDir, TimeSpan.FromSeconds(_settings.RelaunchGraceSeconds));
            Report(LaunchPhase.WaitingForGame, $"Esperando a que arranque {game.Name}… (actualizaciones o shaders pueden tardar)", game);

            var started = await watcher
                .WaitForStartAsync(TimeSpan.FromSeconds(_settings.GameStartTimeoutSeconds), ct)
                .ConfigureAwait(false);
            if (!started)
                return Fail(game, $"{game.Name} no arrancó en {_settings.GameStartTimeoutSeconds} s.");

            _tracker.StartSession(game.Id, game.Name);
            Report(LaunchPhase.Playing, $"Jugando a {game.Name}", game);

            PlaySession? session;
            using (new Timer(_ => _tracker.Heartbeat(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)))
            {
                await watcher.WaitForExitAsync(ct).ConfigureAwait(false);
                session = _tracker.EndSession();
            }

            var played = session is null ? TimeSpan.Zero : TimeSpan.FromSeconds(session.DurationSeconds);
            return new LaunchResult(true, $"{game.Name}: sesión de {Formatting.Duration(played)}.", session);
        }
        catch (OperationCanceledException)
        {
            // El usuario ha dejado de vigilar: el juego puede seguir abierto, así que NO cerramos Steam.
            runCleanup = false;
            var session = _tracker.EndSession();
            Report(LaunchPhase.Cancelled, "Vigilancia cancelada. Steam se ha dejado abierto.", game);
            return new LaunchResult(false, "Vigilancia cancelada.", session);
        }
        catch (Exception ex)
        {
            Log.Error($"Error en la sesión de {game.Name}", ex);
            _tracker.EndSession();
            return Fail(game, $"Error: {ex.Message}");
        }
        finally
        {
            try
            {
                // 5) Cleanup del cliente.
                if (runCleanup)
                    await CleanupSteamAsync(steamStartedByUs, game).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Error durante la limpieza de Steam", ex);
            }
            finally
            {
                ProcessUtils.TrimCurrentProcess();
                _sessionGate.Release();
            }
        }
    }

    /// <summary>Arranca Steam con los argumentos ligeros si no está ya abierto. Devuelve true si lo arrancamos nosotros.</summary>
    private async Task<bool> EnsureSteamRunningAsync(string steamExe, GameEntry game, CancellationToken ct)
    {
        if (SteamLocator.IsClientRunning())
        {
            Log.Info("Steam ya estaba en ejecución.");
            return false;
        }

        Report(LaunchPhase.StartingClient, "Iniciando Steam en modo ligero…", game);
        ProcessUtils.Start(steamExe, _settings.SteamClientArguments);
        Log.Info($"steam.exe {_settings.SteamClientArguments}");

        // Esperar a que publique PID + usuario; si no llega (p. ej. pide login), seguimos igualmente:
        // el comando steam:// se encola y se ejecutará cuando el cliente esté listo.
        var ready = await ProcessUtils
            .WaitUntilAsync(SteamLocator.IsClientReady, TimeSpan.FromSeconds(90), ct)
            .ConfigureAwait(false);
        if (ready)
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); // margen para su IPC
        else
            Log.Warn("Steam no confirmó estar listo en 90 s; se envía el comando igualmente.");

        return true;
    }

    /// <summary>
    /// Cierre del cliente: margen para la nube → <c>steam.exe -shutdown</c> (Steam termina de sincronizar
    /// antes de salir) → kill de lo que sobreviva al plazo.
    /// </summary>
    private async Task CleanupSteamAsync(bool steamStartedByUs, GameEntry game)
    {
        var policy = _settings.SteamCleanupPolicy;
        if (policy == CleanupPolicy.Never ||
            (policy == CleanupPolicy.OnlyIfStartedByLauncher && !steamStartedByUs))
        {
            Report(LaunchPhase.Finished, "Sesión terminada. Steam se deja abierto (según ajustes).", game);
            return;
        }

        if (!SteamLocator.IsClientRunning() && !AnyResidualProcess())
        {
            Report(LaunchPhase.Finished, "Sesión terminada. Steam ya estaba cerrado.", game);
            return;
        }

        // Si hay OTRO juego de Steam abierto (lanzado fuera del launcher), no tocamos nada.
        var running = SteamLocator.GetRunningAppId();
        if (running != 0 && running.ToString() != game.AppId)
        {
            Log.Info($"Otro juego de Steam en ejecución ({running}); se omite la limpieza.");
            Report(LaunchPhase.Finished, "Sesión terminada. Hay otro juego de Steam abierto; no se cierra Steam.", game);
            return;
        }

        Report(LaunchPhase.SyncingCloud, $"Dejando que Steam sincronice la nube ({_settings.CloudSyncGraceSeconds} s)…", game);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _settings.CloudSyncGraceSeconds))).ConfigureAwait(false);

        Report(LaunchPhase.CleaningUp, "Cerrando Steam y sus procesos en segundo plano…", game);
        var steamExe = _steam.SteamExe;
        if (steamExe is not null && SteamLocator.IsClientRunning())
        {
            ProcessUtils.Start(steamExe, "-shutdown");
            await ProcessUtils
                .WaitUntilAsync(() => !SteamLocator.IsClientRunning(),
                    TimeSpan.FromSeconds(_settings.SteamShutdownTimeoutSeconds), CancellationToken.None)
                .ConfigureAwait(false);
        }

        var killed = ProcessUtils.KillByNames(_settings.SteamProcessesToClean);
        var msg = killed > 0
            ? $"Sesión terminada. Steam cerrado ({killed} procesos residuales forzados)."
            : "Sesión terminada. Steam cerrado limpiamente.";
        Log.Info(msg);
        Report(LaunchPhase.Finished, msg, game);
    }

    private bool AnyResidualProcess() => _settings.SteamProcessesToClean.Any(ProcessUtils.IsRunning);

    private LaunchResult Fail(GameEntry game, string message)
    {
        Log.Warn(message);
        Report(LaunchPhase.Failed, message, game);
        return new LaunchResult(false, message);
    }

    private void Report(LaunchPhase phase, string message, GameEntry? game) =>
        StatusChanged?.Invoke(this, new OrchestratorStatus(phase, message, game));
}
