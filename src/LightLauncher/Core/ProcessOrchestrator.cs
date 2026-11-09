using System.Diagnostics;
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

/// <param name="SecondsRemaining">Cuenta atrás de la fase actual (sincronización en la nube), si aplica.</param>
public sealed record OrchestratorStatus(LaunchPhase Phase, string Message, GameEntry? Game = null, int? SecondsRemaining = null);

public sealed record LaunchResult(bool Success, string Message, PlaySession? Session = null);

/// <summary>
/// Capa orquestadora "fantasma" (Process Lifecycle Manager).
/// <para><b>Steam</b>: arranca el cliente en modo ligero solo si hace falta (las licencias las valida él),
/// lanza el juego con <c>steam://run/{appId}</c>, cronometra la sesión y, al salir, deja margen a la
/// sincronización en la nube, cierra Steam de forma ordenada (<c>-shutdown</c>) y fuerza lo que sobreviva.</para>
/// <para><b>Ejecutables locales</b>: lanza el .exe, vigila su proceso (y su carpeta si es segura) y cronometra.</para>
/// <para><b>Puente de tienda</b>: abre <c>steam://store</c> / <c>steam://install</c> sin tocar pagos ni credenciales.</para>
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

    public bool IsBusy => _sessionGate.CurrentCount == 0;
    public bool IsSteamInstalled => _steam.SteamExe is not null;
    public LaunchPhase CurrentPhase { get; private set; } = LaunchPhase.Idle;

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
        _ => Task.FromResult(new LaunchResult(false, $"Plataforma no soportada: {game.Platform}")),
    };

    // =====================================================================================
    // Steam
    // =====================================================================================

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

    /// <summary>Ejecuta la sesión completa: cliente → juego → vigilancia → nube → limpieza.</summary>
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

            // 3) Protocolo oficial, pasado al propio steam.exe (igual que el manejador "steam.exe -- %1").
            Report(LaunchPhase.LaunchingGame, $"Lanzando {game.Name}…", game);
            ProcessUtils.Start(steamExe, $"-- \"steam://run/{game.AppId}\"");
            Log.Info($"steam://run/{game.AppId} enviado ({game.Name}).");

            // 4) Esperar a que el juego exista y engancharnos a él.
            var watcher = new GameProcessWatcher(game.AppId, game.InstallDir, rootPid: null,
                TimeSpan.FromSeconds(_settings.RelaunchGraceSeconds));
            Report(LaunchPhase.WaitingForGame, $"Esperando a que arranque {game.Name}… (actualizaciones o shaders pueden tardar)", game);

            var started = await watcher
                .WaitForStartAsync(TimeSpan.FromSeconds(_settings.GameStartTimeoutSeconds), ct)
                .ConfigureAwait(false);
            if (!started)
                return Fail(game, $"{game.Name} no arrancó en {_settings.GameStartTimeoutSeconds} s.");

            var session = await TrackSessionAsync(game, watcher, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Puente de tienda: abre una URI <c>steam://</c> (p. ej. <c>steam://install/570</c> o
    /// <c>steam://store/570</c>). Si Steam está cerrado lo arranca antes en modo ligero.
    /// LightLauncher nunca ve pagos ni credenciales: todo ocurre en la ventana oficial de Steam.
    /// </summary>
    public async Task<bool> OpenSteamUriAsync(string uri, string description, CancellationToken ct = default)
    {
        var steamExe = _steam.SteamExe;
        if (steamExe is null)
        {
            Report(LaunchPhase.Failed, "No se encontró Steam en este equipo.", null);
            return false;
        }

        try
        {
            if (!SteamLocator.IsClientRunning())
                await EnsureSteamRunningAsync(steamExe, null, ct).ConfigureAwait(false);

            ProcessUtils.Start(steamExe, $"-- \"{uri}\"");
            Log.Info($"URI enviada a Steam: {uri}");
            Report(LaunchPhase.Idle, description, null);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo abrir {uri}", ex);
            Report(LaunchPhase.Failed, $"No se pudo abrir Steam: {ex.Message}", null);
            return false;
        }
    }

    /// <summary>Arranca Steam en modo ligero si no está abierto. Devuelve true si lo arrancamos nosotros.</summary>
    public async Task<bool> StartSteamInBackgroundAsync(CancellationToken ct = default)
    {
        var steamExe = _steam.SteamExe;
        if (steamExe is null) return false;
        return await EnsureSteamRunningAsync(steamExe, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reinicia Steam en segundo plano (cierre ordenado + arranque ligero), p. ej. para que lea un
    /// manifiesto nuevo. Nunca durante una partida: devuelve false si hay una sesión en curso.
    /// </summary>
    public async Task<bool> RestartSteamInBackgroundAsync(CancellationToken ct = default)
    {
        var steamExe = _steam.SteamExe;
        if (steamExe is null || IsBusy) return false;
        if (SteamLocator.GetRunningAppId() != 0) return false; // hay un juego de Steam abierto fuera del launcher

        Report(LaunchPhase.Idle, "Reiniciando Steam en segundo plano…", null);
        await ShutdownSteamAsync(steamExe).ConfigureAwait(false);
        await EnsureSteamRunningAsync(steamExe, null, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Cierra Steam de forma ordenada si no hay partidas en curso (tras terminar una descarga que
    /// abrimos nosotros). Respeta la política de limpieza del usuario.
    /// </summary>
    public async Task<bool> CloseSteamIfIdleAsync()
    {
        var steamExe = _steam.SteamExe;
        if (steamExe is null || IsBusy || _settings.SteamCleanupPolicy == CleanupPolicy.Never) return false;
        if (!SteamLocator.IsClientRunning() || SteamLocator.GetRunningAppId() != 0) return false;

        Report(LaunchPhase.CleaningUp, "Descarga terminada. Cerrando Steam de forma segura…", null);
        await ShutdownSteamAsync(steamExe).ConfigureAwait(false);
        Report(LaunchPhase.Idle, "Steam cerrado tras la descarga.", null);
        return true;
    }

    /// <summary>Cierre ordenado (<c>-shutdown</c>) y, si vence el plazo, cierre forzado de residuos.</summary>
    private async Task ShutdownSteamAsync(string steamExe)
    {
        if (SteamLocator.IsClientRunning())
        {
            ProcessUtils.Start(steamExe, "-shutdown");
            await ProcessUtils
                .WaitUntilAsync(() => !SteamLocator.IsClientRunning(),
                    TimeSpan.FromSeconds(_settings.SteamShutdownTimeoutSeconds), CancellationToken.None)
                .ConfigureAwait(false);
        }
        ProcessUtils.KillByNames(_settings.SteamProcessesToClean);
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

            // Solo vigilamos la carpeta si es "propia" del juego (no el Escritorio, Descargas, etc.).
            var exeDir = Path.GetDirectoryName(exe);
            var watchDir = GameProcessWatcher.IsSafeWatchDirectory(exeDir) ? exeDir : null;
            var watcher = new GameProcessWatcher(null, watchDir, rootPid, TimeSpan.FromSeconds(_settings.RelaunchGraceSeconds));

            // El proceso puede tardar un instante en aparecer (o ser un acceso directo que lanza otro).
            var started = await watcher.WaitForStartAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (!started)
                return Fail(game, $"{game.Name} se cerró nada más abrirse (o no se pudo vigilar su proceso).");

            var session = await TrackSessionAsync(game, watcher, ct).ConfigureAwait(false);
            var played = session is null ? TimeSpan.Zero : TimeSpan.FromSeconds(session.DurationSeconds);
            var msg = $"{game.Name}: sesión de {Formatting.Duration(played)}.";
            Report(LaunchPhase.Finished, msg, game);
            return new LaunchResult(true, msg, session);
        }
        catch (OperationCanceledException)
        {
            var session = _tracker.EndSession();
            Report(LaunchPhase.Cancelled, "Vigilancia cancelada.", game);
            return new LaunchResult(false, "Vigilancia cancelada.", session);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: el usuario rechazó el aviso de UAC.
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
            ProcessUtils.TrimCurrentProcess();
            _sessionGate.Release();
        }
    }

    private static int? SafePid(Process? process)
    {
        try { return process?.Id; }
        catch (InvalidOperationException) { return null; } // ShellExecute sin proceso asociado
    }

    // =====================================================================================
    // Comunes
    // =====================================================================================

    /// <summary>Cada cuánto se repite la limitación de Steam durante la partida (steamwebhelper vuelve a crecer).</summary>
    private static readonly TimeSpan SteamLimitInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cronometra desde que el juego está vivo hasta que termina, con latido cada minuto.
    /// En juegos de Steam, además, limita a Steam mientras dura la partida.
    /// </summary>
    private async Task<PlaySession?> TrackSessionAsync(GameEntry game, GameProcessWatcher watcher, CancellationToken ct)
    {
        _tracker.StartSession(game.Id, game.Name);
        Report(LaunchPhase.Playing, $"Jugando a {game.Name}", game);

        var limitSteam = game.Platform == GamePlatform.Steam;
        // La primera pasada espera a que el juego haya validado su licencia con Steam (DRM, overlay).
        var limiterDelay = TimeSpan.FromSeconds(Math.Max(5, _settings.SteamLimitDelaySeconds));

        using (new Timer(_ => _tracker.Heartbeat(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)))
        using (limitSteam ? new Timer(_ => LimitSteam(game), null, limiterDelay, SteamLimitInterval) : null)
        {
            await watcher.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        // Si Steam va a quedarse abierto (según ajustes), que recupere su prioridad normal.
        if (limitSteam) SteamResourceLimiter.RestorePriority();

        // No contar el margen de gracia (el juego ya estaba cerrado durante esos segundos).
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

    /// <summary>Arranca Steam con los argumentos ligeros si no está ya abierto. Devuelve true si lo arrancamos nosotros.</summary>
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
    /// Cierre del cliente: cuenta atrás de sincronización en la nube → <c>steam.exe -shutdown</c>
    /// (Steam termina de subir partidas antes de salir) → kill de lo que sobreviva al plazo.
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

        // Temporizador de seguridad: nunca cerramos Steam mientras puede estar subiendo partidas.
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
    }

    private bool AnyResidualProcess() => _settings.SteamProcessesToClean.Any(ProcessUtils.IsRunning);

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
