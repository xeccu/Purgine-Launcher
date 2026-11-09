using Purgine.Core;
using Purgine.Infrastructure;
using Purgine.Library;

namespace Purgine.SteamNet;

/// <summary>Entrada persistida de la cola (para recuperarla tras reiniciar Purgine).</summary>
public sealed class QueuedDownload
{
    public uint AppId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DownloadJobKind Kind { get; set; }
    public string LibraryPath { get; set; } = string.Empty;
    public string? InstallDirName { get; set; }
    /// <summary>Descarga terminada pendiente de registrar en Steam (ruta del resultado serializable no hace falta: se rehace la verificación).</summary>
    public bool AwaitingSteamRegistration { get; set; }
}

/// <summary>
/// Cola de descargas e instalaciones de Purgine, sin el cliente de Steam:
/// una descarga a la vez (como Steam), pausar / reanudar / cancelar, cola persistida en disco y registro del
/// juego en Steam (appmanifest + depotcache) en cuanto Steam está cerrado. Mientras hay descargas, mantiene
/// abierta la conexión de red con Steam; al terminar, la suelta.
/// </summary>
public sealed class SteamDownloadManager : IDisposable
{
    private readonly SteamSession _session;
    private readonly SteamOwnershipService _ownership;
    private readonly DepotDownloadEngine _engine;
    private readonly SteamLocator _steam;
    private readonly SteamLibraryScanner _scanner;
    private readonly string _queuePath;
    private readonly List<DownloadJob> _jobs = [];
    private readonly Dictionary<DownloadJob, DepotDownloadResult> _awaitingSteam = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer _registrationTimer;

    /// <summary>Cambió algo en la cola (se dispara desde hilos de fondo).</summary>
    public event Action? QueueChanged;
    /// <summary>Progreso o estado de un trabajo (frecuente durante la descarga).</summary>
    public event Action<DownloadJob>? JobChanged;
    /// <summary>Un trabajo terminó: completado, fallido o cancelado.</summary>
    public event Action<DownloadJob>? JobFinished;

    public SteamDownloadManager(SteamSession session, SteamOwnershipService ownership, SteamLocator steam,
        SteamLibraryScanner scanner, string queuePath)
    {
        _session = session;
        _ownership = ownership;
        _steam = steam;
        _scanner = scanner;
        _queuePath = queuePath;
        _engine = new DepotDownloadEngine(session, ownership);
        _registrationTimer = new Timer(_ => OnRegistrationTimer(), null, Timeout.Infinite, Timeout.Infinite);

        // La cola de la sesión anterior vuelve en pausa: el usuario decide cuándo seguir.
        foreach (var q in JsonFileStore.Load<List<QueuedDownload>>(queuePath))
        {
            var job = Track(new DownloadJob(q.AppId, q.Name, q.Kind, q.LibraryPath, q.InstallDirName));
            job.SetState(DownloadJobState.Paused, "En pausa (de la sesión anterior)");
        }

        _ = Task.Run(RunQueueAsync);
    }

    public IReadOnlyList<DownloadJob> Jobs { get { lock (_gate) return _jobs.ToList(); } }

    public bool HasActiveJobs { get { lock (_gate) return _jobs.Any(j => j.IsActive || j.State == DownloadJobState.Queued); } }

    public DownloadJob? Find(uint appId)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.AppId == appId && !j.IsFinished);
    }

    // =====================================================================================
    // Acciones
    // =====================================================================================

    /// <summary>Añade una instalación o actualización a la cola (o devuelve la que ya existe).</summary>
    public DownloadJob Enqueue(GameEntry game, DownloadJobKind kind)
    {
        var appId = uint.Parse(game.AppId);
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(j => j.AppId == appId && !j.IsFinished) is { } existing)
            {
                if (existing.State == DownloadJobState.Paused) Resume(existing);
                return existing;
            }
        }

        var library = kind == DownloadJobKind.Update && game.LibraryPath is { } lib ? lib : ChooseLibrary();
        var installDir = game.InstallDirName ?? (game.InstallDir is { } dir ? Path.GetFileName(dir) : _ownership.Find(appId)?.InstallDir);
        var job = Track(new DownloadJob(appId, game.Name, kind, library, installDir));
        Persist();
        _wake.Release();
        return job;
    }

    public void Pause(DownloadJob job)
    {
        if (job.IsFinished || job.State == DownloadJobState.Paused) return;
        job.PauseRequested = true;
        if (job.Cts is { } cts) cts.Cancel();
        else job.SetState(DownloadJobState.Paused, "En pausa");
        Persist();
    }

    public void Resume(DownloadJob job)
    {
        if (job.State != DownloadJobState.Paused) return;
        job.PauseRequested = false;
        job.SetState(DownloadJobState.Queued, "En cola");
        Persist();
        _wake.Release();
    }

    public void PauseAll() { foreach (var j in Jobs) Pause(j); }
    public void ResumeAll() { foreach (var j in Jobs) Resume(j); }

    /// <summary>
    /// Cancela. En una instalación nueva se borra lo descargado; en una actualización los archivos quedan
    /// a medio actualizar (Steam o una nueva actualización lo arreglan) y se avisa de ello.
    /// </summary>
    public void Cancel(DownloadJob job)
    {
        job.PauseRequested = false;
        if (job.Cts is { } cts) { cts.Cancel(); return; } // el bucle termina de cancelarlo
        FinishCanceled(job);
    }

    private void FinishCanceled(DownloadJob job)
    {
        if (job.Kind == DownloadJobKind.Install && job.InstallDirName is { } dir &&
            !File.Exists(Path.Combine(job.LibraryPath, "steamapps", $"appmanifest_{job.AppId}.acf")))
        {
            var path = Path.Combine(job.LibraryPath, "steamapps", "common", dir);
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception ex) { Log.Warn($"No se pudo borrar la descarga cancelada {path}: {ex.Message}"); }
        }
        job.SetState(DownloadJobState.Canceled, job.Kind == DownloadJobKind.Install
            ? "Cancelado: se borró lo descargado."
            : "Cancelado: la actualización quedó a medias. Vuelve a actualizar para completarla.");
        Untrack(job);
        JobFinished?.Invoke(job);
    }

    /// <summary>
    /// Desinstala sin el cliente de Steam: borra la carpeta del juego y su appmanifest. Requiere Steam cerrado
    /// (abierto, mantiene su propio registro del juego).
    /// </summary>
    public string? Uninstall(GameEntry game)
    {
        if (SteamLocator.IsClientRunning()) return "Cierra Steam antes de desinstalar (o espera a que Purgine lo cierre tras la partida).";
        if (Find(uint.Parse(game.AppId)) is not null) return "Hay una descarga de este juego en curso: cancélala primero.";
        try
        {
            if (game.InstallDir is { } dir && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (game.LibraryPath is { } lib)
            {
                var acf = Path.Combine(lib, "steamapps", $"appmanifest_{game.AppId}.acf");
                if (File.Exists(acf)) File.Delete(acf);
            }
            Log.Info($"Desinstalado sin Steam: {game.Name} ({game.AppId}).");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Error al desinstalar {game.Name}", ex);
            return $"No se pudo desinstalar por completo: {ex.Message}";
        }
    }

    // =====================================================================================
    // Bucle de la cola
    // =====================================================================================

    private async Task RunQueueAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            DownloadJob? next;
            lock (_gate) next = _jobs.FirstOrDefault(j => j.State == DownloadJobState.Queued);
            if (next is null)
            {
                try { await _wake.WaitAsync(_shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            await ExecuteAsync(next).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(DownloadJob job)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        job.Cts = cts;
        _session.Hold();
        try
        {
            job.SetState(DownloadJobState.Preparing, "Conectando con Steam…");
            if (!await _session.LoginWithSavedSessionAsync(cts.Token).ConfigureAwait(false))
                throw new InvalidOperationException("Inicia sesión en Steam (Ajustes → Cuenta de Steam) para descargar.");

            var steamPath = _steam.SteamPath ?? throw new InvalidOperationException("No se encontró la carpeta de Steam.");
            var result = await _engine.RunAsync(job, steamPath, cts.Token).ConfigureAwait(false);

            lock (_gate) _awaitingSteam[job] = result;
            job.SetState(DownloadJobState.WaitingForSteam, "Registrando el juego en Steam…");
            TryRegisterPending(); // inmediato: el juego pasa a «Instalado» ya
        }
        catch (OperationCanceledException) when (job.PauseRequested)
        {
            job.SetState(DownloadJobState.Paused, "En pausa");
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            FinishCanceled(job);
        }
        catch (OperationCanceledException)
        {
            job.SetState(DownloadJobState.Paused, "En pausa");
        }
        catch (Exception ex)
        {
            Log.Error($"Descarga de {job.Name} ({job.AppId})", ex);
            job.SetState(DownloadJobState.Failed, ex.Message);
            Untrack(job);
            JobFinished?.Invoke(job);
        }
        finally
        {
            job.Cts = null;
            _session.Release();
            Persist();
        }
    }

    /// <summary>
    /// Registra en Steam las descargas terminadas, en el momento: el juego pasa a «Instalado» sin esperar a nada.
    /// Si Steam estaba abierto (mantiene su propio registro en memoria y podría reescribir el manifiesto), se
    /// vuelve a registrar en cuanto Steam se cierre, y antes de jugar ese juego se reinicia Steam.
    /// </summary>
    public void TryRegisterPending()
    {
        List<(DownloadJob Job, DepotDownloadResult Result)> pending;
        lock (_gate) pending = _awaitingSteam.Select(kv => (kv.Key, kv.Value)).ToList();
        if (pending.Count == 0) return;

        var steamRunning = SteamLocator.IsClientRunning();
        foreach (var (job, result) in pending)
        {
            try
            {
                AppManifestWriter.Write(_steam.SteamPath!, job.AppId, job.Name, result, OwnerSteamId());
                if (steamRunning)
                    lock (_gate) _rewriteWhenSteamCloses[job.AppId] = (job.Name, result);
                job.SetState(DownloadJobState.Completed, job.Kind == DownloadJobKind.Install ? "Instalado." : "Actualizado.");
            }
            catch (Exception ex)
            {
                Log.Error($"No se pudo registrar {job.Name} en Steam", ex);
                job.SetState(DownloadJobState.Failed, $"Descargado, pero no se pudo registrar en Steam: {ex.Message}");
            }
            lock (_gate) _awaitingSteam.Remove(job);
            Untrack(job);
            JobFinished?.Invoke(job);
        }
        if (steamRunning) _registrationTimer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        Persist();
    }

    /// <summary>Juegos registrados con Steam abierto: hay que volver a registrarlos cuando Steam se cierre.</summary>
    private readonly Dictionary<uint, (string Name, DepotDownloadResult Result)> _rewriteWhenSteamCloses = new();

    /// <summary>
    /// ¿Este juego se registró mientras el Steam actual ya estaba abierto? Ese Steam no lo ve como instalado
    /// hasta reiniciarse: antes de jugarlo hay que cerrarlo (el orquestador lo vuelve a abrir para jugar).
    /// </summary>
    public bool NeedsSteamRestartFor(uint appId)
    {
        lock (_gate) return _rewriteWhenSteamCloses.ContainsKey(appId) && SteamLocator.IsClientRunning();
    }

    /// <summary>Steam se ha cerrado: se vuelven a registrar los juegos instalados mientras estaba abierto.</summary>
    public void OnSteamClosed()
    {
        if (SteamLocator.IsClientRunning()) return;
        List<KeyValuePair<uint, (string Name, DepotDownloadResult Result)>> items;
        lock (_gate)
        {
            items = _rewriteWhenSteamCloses.ToList();
            _rewriteWhenSteamCloses.Clear();
        }
        foreach (var (appId, (name, result)) in items)
        {
            try { AppManifestWriter.Write(_steam.SteamPath!, appId, name, result, OwnerSteamId()); }
            catch (Exception ex) { Log.Warn($"No se pudo volver a registrar {name}: {ex.Message}"); }
        }
        TryRegisterPending();
    }

    private void OnRegistrationTimer()
    {
        bool waiting;
        lock (_gate) waiting = _rewriteWhenSteamCloses.Count > 0;
        if (!waiting) return;
        if (SteamLocator.IsClientRunning()) _registrationTimer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        else OnSteamClosed();
    }

    private ulong? OwnerSteamId() =>
        _session.SteamId?.ConvertToUInt64() ?? SteamPlaytimeReader.CurrentSteamId64(_steam.SteamPath);

    // =====================================================================================

    private DownloadJob Track(DownloadJob job)
    {
        job.Changed += j => JobChanged?.Invoke(j);
        lock (_gate) _jobs.Add(job);
        QueueChanged?.Invoke();
        return job;
    }

    private void Untrack(DownloadJob job)
    {
        lock (_gate) _jobs.Remove(job);
        QueueChanged?.Invoke();
        Persist();
    }

    private void Persist()
    {
        List<QueuedDownload> items;
        lock (_gate)
        {
            items = _jobs.Where(j => !j.IsFinished).Select(j => new QueuedDownload
            {
                AppId = j.AppId,
                Name = j.Name,
                Kind = j.Kind,
                LibraryPath = j.LibraryPath,
                InstallDirName = j.InstallDirName,
                AwaitingSteamRegistration = _awaitingSteam.ContainsKey(j),
            }).ToList();
        }
        JsonFileStore.Save(_queuePath, items);
    }

    /// <summary>La biblioteca de Steam con más espacio libre.</summary>
    private string ChooseLibrary() =>
        _scanner.GetLibraryFolders()
            .OrderByDescending(FreeSpace)
            .FirstOrDefault() ?? throw new InvalidOperationException("No se encontró ninguna biblioteca de Steam.");

    private static long FreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; }
        catch (Exception) { return 0; }
    }

    public void Dispose()
    {
        PauseAll();
        _shutdown.Cancel();
        _registrationTimer.Dispose();
        Persist();
    }
}
