using System.Diagnostics;
using System.Text;
using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.Settings;
using Purgine.Stats;

namespace Purgine.Core;

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

/// <param name="SecondsRemaining">Cuenta atrás de la fase actual (sincronización en la nube), si aplica.</param>
public sealed record OrchestratorStatus(LaunchPhase Phase, string Message, GameEntry? Game = null, int? SecondsRemaining = null);

public sealed record LaunchResult(bool Success, string Message, PlaySession? Session = null);

/// <summary>
/// Orquestador de partidas. En esta arquitectura el cliente oficial de Steam tiene <b>una sola función</b>:
/// ejecutar el juego (licencia/DRM, overlay, nube) mediante <c>steam://run/{appId}</c>. Descargas,
/// actualizaciones, licencias y desinstalaciones las hace Purgine directamente con la red de Steam
/// (SteamKit2), sin abrir el cliente.
/// <para><b>Steam</b>: se despierta en modo ligero solo para jugar, se cronometra la sesión, se limita a Steam
/// durante la partida y, al salir, cuenta atrás para la nube y cierre seguro (<c>-shutdown</c> y limpieza
/// de residuos).</para>
/// <para><b>Ejecutables locales</b>: se lanzan y vigilan directamente.</para>
/// Solo admite una sesión de juego simultánea.
/// </summary>
public sealed class ProcessOrchestrator
{
    private readonly SteamLocator _steam;
    private readonly SteamLibraryScanner _scanner;
    private readonly GameTimeTracker _tracker;
    private readonly LauncherSettings _settings;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public event EventHandler<OrchestratorStatus>? StatusChanged;

    /// <summary>Steam se acaba de cerrar (momento de registrar en Steam las descargas terminadas).</summary>
    public event EventHandler? SteamClosed;

    public bool IsBusy => _sessionGate.CurrentCount == 0;
    public bool IsSteamInstalled => _steam.SteamExe is not null;
    public LaunchPhase CurrentPhase { get; private set; } = LaunchPhase.Idle;

    // ----- Forzar cierre -----
    /// <summary>Carpeta y PID raíz del juego en curso (para encontrar sus procesos al forzar el cierre).</summary>
    private volatile string? _currentWatchDir;
    private int _currentRootPid;
    /// <summary>El usuario pidió forzar el cierre: la cancelación posterior no es un «dejar de vigilar».</summary>
    private volatile bool _forceStopRequested;

    /// <summary>
    /// Forzar cierre: termina el juego en curso y todos sus procesos hijos (árbol completo). Devuelve cuántos
    /// procesos raíz se terminaron. Después hay que cancelar la sesión (token): el orquestador la cierra al
    /// momento, registra el tiempo jugado y, en segundo plano, cierra Steam si los ajustes lo piden.
    /// </summary>
    public int ForceStopGame()
    {
        _forceStopRequested = true;
        var pids = new HashSet<int>();
        if (_currentWatchDir is { } dir && GameProcessWatcher.IsSafeWatchDirectory(dir))
            foreach (var pid in ProcessUtils.FindProcessesUnder(dir)) pids.Add(pid);
        if (Volatile.Read(ref _currentRootPid) is > 0 and var root && ProcessUtils.IsAlive(root)) pids.Add(root);

        var killed = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                killed++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // ya había terminado, o sin permiso (juego elevado): se registra y se sigue
                Log.Warn($"Forzar cierre: no se pudo terminar el PID {pid}: {ex.Message}");
            }
        }
        Log.Info($"Forzar cierre: {killed} proceso(s) terminados (con sus hijos).");
        return killed;
    }

    /// <summary>
    /// Tras un cierre forzado: espera a que Steam suelte el juego y lo cierra (si los ajustes lo piden) sin
    /// bloquear la interfaz. Si entretanto empieza otra partida, no hace nada.
    /// </summary>
    private async Task CleanupAfterForceStopAsync(bool steamStartedByUs)
    {
        var policy = _settings.SteamCleanupPolicy;
        if (policy == CleanupPolicy.Never || (policy == CleanupPolicy.OnlyIfStartedByLauncher && !steamStartedByUs)) return;
        await ProcessUtils.WaitUntilAsync(() => SteamLocator.GetRunningAppId() == 0, TimeSpan.FromSeconds(20), CancellationToken.None)
            .ConfigureAwait(false);
        if (IsBusy) return; // ya hay otra partida en marcha
        try { await CloseSteamAsync("Cerrando Steam tras el cierre forzado…").ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn($"Cierre de Steam tras forzar cierre: {ex.Message}"); }
    }

    public ProcessOrchestrator(SteamLocator steam, SteamLibraryScanner scanner, GameTimeTracker tracker, LauncherSettings settings)
    {
        _steam = steam;
        _scanner = scanner;
        _tracker = tracker;
        _settings = settings;
    }

    /// <summary>Lanza cualquier juego de la biblioteca según su plataforma.</summary>
    public Task<LaunchResult> LaunchAsync(GameEntry game, CancellationToken ct = default) => game.Platform switch
    {
        GamePlatform.Steam => LaunchSteamGameAsync(game, ct),
        GamePlatform.Local => LaunchLocalGameAsync(game, ct),
        GamePlatform.Epic => LaunchEpicGameAsync(game, ct),
        _ => Task.FromResult(new LaunchResult(false, $"Plataforma no soportada: {game.Platform}")),
    };

    // =====================================================================================
    // Steam: única función del cliente oficial → ejecutar el juego
    // =====================================================================================

    /// <summary>Lanza un juego de Steam por AppID (resuelve su carpeta desde los manifiestos locales).</summary>
    public Task<LaunchResult> LaunchSteamGame(string appId, CancellationToken ct = default)
    {
        var game = _scanner.FindGame(appId) ?? new GameEntry
        {
            Id = GameEntry.MakeSteamId(appId),
            Platform = GamePlatform.Steam,
            AppId = appId,
            Name = $"App {appId}",
        };
        return LaunchSteamGameAsync(game, ct);
    }

    /// <summary>Sesión completa: cliente ligero → <c>steam://run</c> → vigilancia → nube → cierre seguro.</summary>
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

            steamStartedByUs = await EnsureSteamRunningAsync(steamExe, game, ct).ConfigureAwait(false);
            runCleanup = true;

            Report(LaunchPhase.LaunchingGame, $"Lanzando {game.Name}…", game);
            // Diálogos previos al juego (contrato de licencia, opciones de inicio, requisitos, conflicto de la nube):
            // se muestran flotando y al frente, con el cliente de Steam oculto.
            SteamWindowGuard.WatchLaunch(TimeSpan.FromSeconds(Math.Max(120, _settings.GameStartTimeoutSeconds)));
            ProcessUtils.Start(steamExe, $"-- \"steam://run/{game.AppId}\"");
            Log.Info($"steam://run/{game.AppId} enviado ({game.Name}).");

            _currentWatchDir = game.InstallDir;
            var watcher = new GameProcessWatcher(game.AppId, game.InstallDir, rootPid: null,
                TimeSpan.FromSeconds(_settings.RelaunchGraceSeconds));
            Report(LaunchPhase.WaitingForGame, $"Esperando a que arranque {game.Name}…", game);

            var started = await WaitForSteamGameStartAsync(watcher, game, ct).ConfigureAwait(false);
            SteamWindowGuard.StopSurfacing();
            if (!started)
                return Fail(game, $"{game.Name} no arrancó en {_settings.GameStartTimeoutSeconds} s.");

            var session = await TrackSessionAsync(game, watcher, ct).ConfigureAwait(false);
            var played = session is null ? TimeSpan.Zero : TimeSpan.FromSeconds(session.DurationSeconds);
            return new LaunchResult(true, $"{game.Name}: sesión de {Formatting.Duration(played)}.", session);
        }
        catch (OperationCanceledException) when (_forceStopRequested)
        {
            // Forzar cierre: la sesión termina ya (tiempo registrado) y Steam se cierra en segundo plano.
            runCleanup = false;
            var session = _tracker.EndSession();
            Report(LaunchPhase.Finished, $"{game.Name} se cerró a la fuerza. Listo para jugar.", game);
            _ = Task.Run(() => CleanupAfterForceStopAsync(steamStartedByUs));
            return new LaunchResult(true, $"{game.Name} se cerró a la fuerza.", session);
        }
        catch (OperationCanceledException)
        {
            // Dejar de vigilar: el juego puede seguir abierto, así que NO se cierra Steam.
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
                if (runCleanup)
                    await CleanupSteamAsync(steamStartedByUs, game).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Error durante la limpieza de Steam", ex);
            }
            finally
            {
                ResetForceStopState();
                ProcessUtils.TrimCurrentProcess();
                _sessionGate.Release();
            }
        }
    }

    private void ResetForceStopState()
    {
        _forceStopRequested = false;
        _currentWatchDir = null;
        Volatile.Write(ref _currentRootPid, 0);
    }

    // =====================================================================================
    // Ejecutables locales
    // =====================================================================================

    /// <summary>Lanza un ejecutable local, vigila su proceso y registra el tiempo jugado.</summary>
    public async Task<LaunchResult> LaunchLocalGameAsync(GameEntry game, CancellationToken ct = default)
    {
        if (!await _sessionGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return new LaunchResult(false, "Ya hay una sesión de juego en curso.");

        try
        {
            var exe = game.ExecutablePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                return Fail(game, $"No se encuentra el ejecutable de {game.Name}: {exe}");

            Report(LaunchPhase.LaunchingGame, $"Lanzando {game.Name}…", game);
            var workDir = !string.IsNullOrWhiteSpace(game.WorkingDirectory) && Directory.Exists(game.WorkingDirectory)
                ? game.WorkingDirectory
                : Path.GetDirectoryName(exe)!;

            int? rootPid;
            // ShellExecute: admite .exe, .bat, .lnk y ejecutables que piden elevación (UAC).
            using (var process = Process.Start(new ProcessStartInfo(exe, game.Arguments ?? string.Empty)
                   {
                       UseShellExecute = true,
                       WorkingDirectory = workDir,
                   }))
            {
                rootPid = SafePid(process);
            }
            Log.Info($"Ejecutable local lanzado: {exe} (PID {rootPid?.ToString() ?? "?"}).");

            var exeDir = Path.GetDirectoryName(exe);
            var watchDir = GameProcessWatcher.IsSafeWatchDirectory(exeDir) ? exeDir : null;
            _currentWatchDir = watchDir;
            Volatile.Write(ref _currentRootPid, rootPid ?? 0);
            var watcher = new GameProcessWatcher(null, watchDir, rootPid, TimeSpan.FromSeconds(_settings.RelaunchGraceSeconds));

            var started = await watcher.WaitForStartAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (!started)
                return Fail(game, $"{game.Name} se cerró nada más abrirse (o no se pudo vigilar su proceso).");

            var session = await TrackSessionAsync(game, watcher, ct).ConfigureAwait(false);
            var played = session is null ? TimeSpan.Zero : TimeSpan.FromSeconds(session.DurationSeconds);
            var msg = $"{game.Name}: sesión de {Formatting.Duration(played)}.";
            Report(LaunchPhase.Finished, msg, game);
            return new LaunchResult(true, msg, session);
        }
        catch (OperationCanceledException) when (_forceStopRequested)
        {
            var session = _tracker.EndSession();
            Report(LaunchPhase.Finished, $"{game.Name} se cerró a la fuerza. Listo para jugar.", game);
            return new LaunchResult(true, $"{game.Name} se cerró a la fuerza.", session);
        }
        catch (OperationCanceledException)
        {
            var session = _tracker.EndSession();
            Report(LaunchPhase.Cancelled, "Vigilancia cancelada.", game);
            return new LaunchResult(false, "Vigilancia cancelada.", session);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Fail(game, $"Se canceló el permiso de administrador para {game.Name}.");
        }
        catch (Exception ex)
        {
            Log.Error($"Error en la sesión local de {game.Name}", ex);
            _tracker.EndSession();
            return Fail(game, $"Error: {ex.Message}");
        }
        finally
        {
            ResetForceStopState();
            ProcessUtils.TrimCurrentProcess();
            _sessionGate.Release();
        }
    }

    // =====================================================================================
    // Epic Games
    // =====================================================================================

    /// <summary>
    /// Credenciales de lanzamiento de Epic (código de intercambio de la cuenta vinculada). Lo asigna la
    /// aplicación; null o resultado null = no hay cuenta de Epic vinculada.
    /// </summary>
    public Func<GameEntry, CancellationToken, Task<EpicNet.EpicLaunchAuth?>>? EpicAuthProvider { get; set; }

    /// <summary>
    /// Juego de Epic, lanzado directamente (sin Epic Games Launcher, como Legendary): se ejecuta el binario del
    /// juego con el código de intercambio de un solo uso de la cuenta vinculada (y el token de propiedad si el
    /// juego lo exige), de modo que arranca con la sesión de Epic iniciada y sin ningún proceso del launcher.
    /// Se vigila su proceso y se registra el tiempo jugado.
    /// </summary>
    /// <summary>Antes de lanzar un juego de Epic (partidas en la nube). No debe lanzar excepciones.</summary>
    public Func<GameEntry, CancellationToken, Task<bool>>? EpicPreLaunch { get; set; }

    /// <summary>Mensaje cuando el usuario cancela el lanzamiento (p. ej. en el diálogo de conflicto de la nube).</summary>
    public const string LaunchCanceledMessage = "Lanzamiento cancelado.";

    public async Task<LaunchResult> LaunchEpicGameAsync(GameEntry game, CancellationToken ct = default)
    {
        if (!await _sessionGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            return new LaunchResult(false, "Ya hay una sesión de juego en curso.");
        try
        {
            if (game.InstallDir is null || !Directory.Exists(game.InstallDir))
                return Fail(game, $"No se encuentra la carpeta de {game.Name}.");
            if (game.ExecutablePath is not { } exe || !File.Exists(exe))
                return Fail(game, $"No se encuentra el ejecutable de {game.Name}: {game.ExecutablePath}");

            Report(LaunchPhase.LaunchingGame, $"Iniciando sesión de Epic para {game.Name}…", game);
            var auth = EpicAuthProvider is null ? null : await EpicAuthProvider(game, ct).ConfigureAwait(false);
            if (auth is null)
                return Fail(game, "Vincula tu cuenta de Epic Games en Ajustes para jugar a los juegos de Epic sin su launcher.");

            var args = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(game.Arguments)) args.Append(game.Arguments.Trim()).Append(' ');
            args.Append("-AUTH_LOGIN=unused ")
                .Append("-AUTH_PASSWORD=").Append(auth.ExchangeCode).Append(' ')
                .Append("-AUTH_TYPE=exchangecode ")
                .Append("-epicapp=").Append(game.AppId).Append(' ')
                .Append("-epicenv=Prod -EpicPortal ")
                .Append("-epicusername=\"").Append(auth.DisplayName.Replace("\"", string.Empty)).Append("\" ")
                .Append("-epicuserid=").Append(auth.AccountId).Append(' ')
                .Append("-epiclocale=es-ES");
            if (game.EpicNamespace is { } ns) args.Append(" -epicsandboxid=").Append(ns);
            if (auth.OwnershipTokenPath is { } ovt) args.Append(" -epicovt=\"").Append(ovt).Append('"');

            if (EpicPreLaunch is { } preLaunch)
            {
                // Partidas en la nube: si la nube tiene una más nueva, se baja antes de arrancar el juego.
                Report(LaunchPhase.LaunchingGame, $"Comprobando tus partidas en la nube de Epic…", game);
                if (!await preLaunch(game, ct).ConfigureAwait(false))
                {
                    Report(LaunchPhase.Cancelled, LaunchCanceledMessage, game);
                    return new LaunchResult(false, LaunchCanceledMessage);
                }
            }

            Report(LaunchPhase.LaunchingGame, $"Lanzando {game.Name}…", game);
            int? rootPid;
            using (var process = Process.Start(new ProcessStartInfo(exe, args.ToString())
                   {
                       UseShellExecute = false,
                       WorkingDirectory = Path.GetDirectoryName(exe)!,
                   }))
            {
                rootPid = SafePid(process);
            }
            Log.Info($"Epic (directo): {exe} (PID {rootPid?.ToString() ?? "?"}), {game.Name}.");

            var watchDir = GameProcessWatcher.IsSafeWatchDirectory(game.InstallDir) ? game.InstallDir : null;
            _currentWatchDir = watchDir;
            Volatile.Write(ref _currentRootPid, rootPid ?? 0);
            var watcher = new GameProcessWatcher(null, watchDir, rootPid, TimeSpan.FromSeconds(_settings.RelaunchGraceSeconds));
            Report(LaunchPhase.WaitingForGame, $"Esperando a que arranque {game.Name}…", game);
            if (!await watcher.WaitForStartAsync(TimeSpan.FromSeconds(Math.Max(60, _settings.GameStartTimeoutSeconds)), ct).ConfigureAwait(false))
                return Fail(game, $"{game.Name} se cerró nada más abrirse (o no se pudo vigilar su proceso).");

            var session = await TrackSessionAsync(game, watcher, ct).ConfigureAwait(false);
            var played = session is null ? TimeSpan.Zero : TimeSpan.FromSeconds(session.DurationSeconds);
            var msg = $"{game.Name}: sesión de {Formatting.Duration(played)}.";
            Report(LaunchPhase.Finished, msg, game);
            return new LaunchResult(true, msg, session);
        }
        catch (OperationCanceledException) when (_forceStopRequested)
        {
            var session = _tracker.EndSession();
            Report(LaunchPhase.Finished, $"{game.Name} se cerró a la fuerza. Listo para jugar.", game);
            return new LaunchResult(true, $"{game.Name} se cerró a la fuerza.", session);
        }
        catch (OperationCanceledException)
        {
            var session = _tracker.EndSession();
            Report(LaunchPhase.Cancelled, "Vigilancia cancelada.", game);
            return new LaunchResult(false, "Vigilancia cancelada.", session);
        }
        catch (EpicNet.EpicApiException ex)
        {
            return Fail(game, $"Epic Games: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"Error en la sesión de Epic de {game.Name}", ex);
            _tracker.EndSession();
            return Fail(game, $"Error: {ex.Message}");
        }
        finally
        {
            ResetForceStopState();
            ProcessUtils.TrimCurrentProcess();
            _sessionGate.Release();
        }
    }


    private static int? SafePid(Process? process)
    {
        try { return process?.Id; }
        catch (InvalidOperationException) { return null; }
    }

    // =====================================================================================
    // Comunes
    // =====================================================================================

    /// <summary>Cada cuánto se repite la limitación de Steam durante la partida.</summary>
    private static readonly TimeSpan SteamLimitInterval = TimeSpan.FromMinutes(5);

    /// <summary>Cronometra la partida (latido cada minuto) y, en juegos de Steam, limita a Steam mientras dura.</summary>
    private async Task<PlaySession?> TrackSessionAsync(GameEntry game, GameProcessWatcher watcher, CancellationToken ct)
    {
        _tracker.StartSession(game.Id, game.Name);
        Report(LaunchPhase.Playing, $"Jugando a {game.Name}", game);

        var limitSteam = game.Platform == GamePlatform.Steam;
        var limiterDelay = TimeSpan.FromSeconds(Math.Max(5, _settings.SteamLimitDelaySeconds));

        using (new Timer(_ => _tracker.Heartbeat(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)))
        using (limitSteam ? new Timer(_ => LimitSteam(game), null, limiterDelay, SteamLimitInterval) : null)
        {
            await watcher.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        if (limitSteam) SteamResourceLimiter.RestorePriority();
        return _tracker.EndSession(excludeTail: watcher.TimeSinceLastAlive);
    }

    private void LimitSteam(GameEntry game)
    {
        try
        {
            var result = SteamResourceLimiter.Apply(_settings);
            if (result.Processes > 0)
                Report(LaunchPhase.Playing, $"Jugando a {game.Name} · {result.Describe()}", game);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo limitar Steam: {ex.Message}");
        }
    }

    /// <summary>
    /// Espera a que arranque el juego. Mientras haya un diálogo de Steam esperando al usuario (contrato de licencia,
    /// opciones de inicio…), el plazo se alarga: leer y aceptar no debe dar «no arrancó».
    /// </summary>
    private async Task<bool> WaitForSteamGameStartAsync(GameProcessWatcher watcher, GameEntry game, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(_settings.GameStartTimeoutSeconds);
        var waitingNotified = false;
        while (true)
        {
            var slice = deadline - DateTime.UtcNow;
            if (slice > TimeSpan.FromSeconds(3)) slice = TimeSpan.FromSeconds(3);
            if (slice > TimeSpan.Zero && await watcher.WaitForStartAsync(slice, ct).ConfigureAwait(false)) return true;
            if (SteamWindowGuard.IsUserDialogOpen)
            {
                if (!waitingNotified)
                {
                    waitingNotified = true;
                    Report(LaunchPhase.WaitingForGame, $"Steam necesita que confirmes algo para {game.Name} (contrato o opciones de inicio)…", game);
                }
                var extended = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                if (extended > deadline) deadline = extended;
                SteamWindowGuard.WatchLaunch(TimeSpan.FromSeconds(90));
                continue;
            }
            if (DateTime.UtcNow >= deadline) return false;
        }
    }

    /// <summary>Arranca Steam en modo ligero si no está abierto. Devuelve true si lo arrancamos nosotros.</summary>
    private async Task<bool> EnsureSteamRunningAsync(string steamExe, GameEntry? game, CancellationToken ct)
    {
        if (SteamLocator.IsClientRunning())
        {
            Log.Info("Steam ya estaba en ejecución.");
            return false;
        }

        Report(LaunchPhase.StartingClient, "Iniciando Steam en modo ligero…", game);
        ProcessUtils.Start(steamExe, _settings.SteamClientArguments);
        Log.Info($"steam.exe {_settings.SteamClientArguments}");
        SteamWindowGuard.Watch(TimeSpan.FromSeconds(90));

        var ready = await ProcessUtils
            .WaitUntilAsync(SteamLocator.IsClientReady, TimeSpan.FromSeconds(90), ct)
            .ConfigureAwait(false);
        if (ready)
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        else
            Log.Warn("Steam no confirmó estar listo en 90 s; se envía el comando igualmente.");

        return true;
    }

    /// <summary>
    /// Cierre tras la partida: cuenta atrás de sincronización en la nube → <c>steam.exe -shutdown</c>
    /// (Steam termina de subir partidas antes de salir) → cierre forzado de lo que sobreviva.
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
            SteamClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var running = SteamLocator.GetRunningAppId();
        if (running != 0 && running.ToString() != game.AppId)
        {
            Log.Info($"Otro juego de Steam en ejecución ({running}); se omite la limpieza.");
            Report(LaunchPhase.Finished, "Sesión terminada. Hay otro juego de Steam abierto; no se cierra Steam.", game);
            return;
        }

        for (int left = Math.Max(0, _settings.CloudSyncGraceSeconds); left > 0; left--)
        {
            Report(LaunchPhase.SyncingCloud, "Sincronizando partidas guardadas con Steam Cloud…", game, left);
            await Task.Delay(1000).ConfigureAwait(false);
        }

        Report(LaunchPhase.CleaningUp, "Cerrando Steam de forma segura (termina de sincronizar y sale)…", game);
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
            ? $"Partidas sincronizadas. Steam cerrado ({killed} procesos residuales forzados)."
            : "Partidas sincronizadas. Steam cerrado limpiamente.";
        Log.Info(msg);
        Report(LaunchPhase.Finished, msg, game);
        SteamClosed?.Invoke(this, EventArgs.Empty);
    }

    private bool AnyResidualProcess() => _settings.SteamProcessesToClean.Any(ProcessUtils.IsRunning);

    /// <summary>
    /// Cierra Steam de forma ordenada fuera de una partida (p. ej. para que reconozca un juego instalado por
    /// Purgine mientras estaba abierto). No hace nada si hay un juego de Steam en marcha.
    /// </summary>
    public Task<bool> CloseSteamAsync() => CloseSteamAsync("Reiniciando Steam para que reconozca el juego recién instalado…");

    private async Task<bool> CloseSteamAsync(string reason)
    {
        var steamExe = _steam.SteamExe;
        if (steamExe is null || !SteamLocator.IsClientRunning()) return true;
        if (SteamLocator.GetRunningAppId() != 0) return false;

        Report(LaunchPhase.CleaningUp, reason, null);
        ProcessUtils.Start(steamExe, "-shutdown");
        await ProcessUtils
            .WaitUntilAsync(() => !SteamLocator.IsClientRunning(),
                TimeSpan.FromSeconds(_settings.SteamShutdownTimeoutSeconds), CancellationToken.None)
            .ConfigureAwait(false);
        ProcessUtils.KillByNames(_settings.SteamProcessesToClean);
        SteamClosed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private LaunchResult Fail(GameEntry game, string message)
    {
        Log.Warn(message);
        Report(LaunchPhase.Failed, message, game);
        return new LaunchResult(false, message);
    }

    private void Report(LaunchPhase phase, string message, GameEntry? game, int? secondsRemaining = null)
    {
        CurrentPhase = phase;
        StatusChanged?.Invoke(this, new OrchestratorStatus(phase, message, game, secondsRemaining));
    }
}
