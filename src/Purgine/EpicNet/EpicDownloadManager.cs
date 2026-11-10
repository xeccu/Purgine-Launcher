using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.SteamNet;

namespace Purgine.EpicNet;

/// <summary>Juego de Epic instalado o actualizado por Purgine (sin Epic Games Launcher).</summary>
public sealed class EpicInstallRecord
{
    public string AppName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string InstallDir { get; set; } = string.Empty;
    public string LaunchExe { get; set; } = string.Empty;
    public string LaunchCommand { get; set; } = string.Empty;
    public string BuildVersion { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string CatalogItemId { get; set; } = string.Empty;
    public long InstallSize { get; set; }
    public string[] Categories { get; set; } = [];
}

/// <summary>Registro (<c>epic-installs.json</c>) de lo que Purgine ha instalado o actualizado de Epic.</summary>
public static class EpicInstallRegistry
{
    private static readonly object Gate = new();

    public static List<EpicInstallRecord> Load()
    {
        lock (Gate) return JsonFileStore.Load<List<EpicInstallRecord>>(AppPaths.EpicInstallsFile);
    }

    public static void Upsert(EpicInstallRecord record)
    {
        lock (Gate)
        {
            var all = JsonFileStore.Load<List<EpicInstallRecord>>(AppPaths.EpicInstallsFile);
            all.RemoveAll(r => r.AppName == record.AppName);
            all.Add(record);
            JsonFileStore.Save(AppPaths.EpicInstallsFile, all);
        }
    }

    public static void Remove(string appName)
    {
        lock (Gate)
        {
            var all = JsonFileStore.Load<List<EpicInstallRecord>>(AppPaths.EpicInstallsFile);
            if (all.RemoveAll(r => r.AppName == appName) > 0) JsonFileStore.Save(AppPaths.EpicInstallsFile, all);
        }
    }
}

/// <summary>Entrada persistida de la cola de Epic.</summary>
public sealed class QueuedEpicDownload
{
    public string AppName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string CatalogItemId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DownloadJobKind Kind { get; set; }
    public string LibraryPath { get; set; } = string.Empty;
    public string? InstallDirName { get; set; }
    public string[] Categories { get; set; } = [];
    public bool CreateDesktopShortcut { get; set; }
    public bool CreateStartMenuShortcut { get; set; }
}

/// <summary>
/// Cola de descargas de Epic de Purgine, gemela de la de Steam: una a la vez, pausar / reanudar / cancelar y
/// cola persistida. Al terminar, el juego queda en <see cref="EpicInstallRegistry"/> (y la biblioteca lo ve
/// como instalado, con su versión, sin Epic Games Launcher).
/// </summary>
public sealed class EpicDownloadManager : IDisposable
{
    private readonly EpicDownloadEngine _engine;
    private readonly string _queuePath;
    private readonly List<DownloadJob> _jobs = [];
    private readonly Dictionary<DownloadJob, string[]> _categories = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    public event Action? QueueChanged;
    public event Action<DownloadJob>? JobChanged;
    public event Action<DownloadJob>? JobFinished;

    public EpicDownloadManager(EpicAccountService epic, string queuePath)
    {
        _engine = new EpicDownloadEngine(epic);
        _queuePath = queuePath;
        foreach (var q in JsonFileStore.Load<List<QueuedEpicDownload>>(queuePath))
        {
            var job = Track(new DownloadJob(0, q.Name, q.Kind, q.LibraryPath, q.InstallDirName)
            {
                EpicAppName = q.AppName,
                EpicNamespace = q.Namespace,
                EpicCatalogItemId = q.CatalogItemId,
                CreateDesktopShortcut = q.CreateDesktopShortcut,
                CreateStartMenuShortcut = q.CreateStartMenuShortcut,
            }, q.Categories);
            job.SetState(DownloadJobState.Paused, "En pausa (de la sesión anterior)");
        }
        _ = Task.Run(RunQueueAsync);
    }

    public EpicDownloadEngine Engine => _engine;

    public IReadOnlyList<DownloadJob> Jobs { get { lock (_gate) return _jobs.ToList(); } }

    public DownloadJob? Find(string appName)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.EpicAppName == appName && !j.IsFinished);
    }

    /// <summary>Carpeta de juegos de Epic en una unidad (la misma que usa Epic Games Launcher).</summary>
    public static string LibraryForDrive(string root) => Path.Combine(root, "Epic Games");

    /// <summary>Nombre de carpeta seguro a partir del título.</summary>
    public static string FolderName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(title.Select(c => invalid.Contains(c) || c is '™' or '®' ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return clean.Length == 0 ? "Juego" : clean;
    }

    public DownloadJob Enqueue(GameEntry game, DownloadJobKind kind, string? libraryPath = null,
        bool desktopShortcut = false, bool startMenuShortcut = false)
    {
        if (game.EpicNamespace is null || game.EpicCatalogItemId is null)
            throw new InvalidOperationException("Faltan los datos de catálogo de Epic de este juego.");
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(j => j.EpicAppName == game.AppId && !j.IsFinished) is { } existing)
            {
                if (existing.State == DownloadJobState.Paused) Resume(existing);
                return existing;
            }
        }

        string library, folder;
        if (kind == DownloadJobKind.Update && game.InstallDir is { } dir)
        {
            library = Path.GetDirectoryName(dir.TrimEnd('\\'))!;
            folder = Path.GetFileName(dir.TrimEnd('\\'));
        }
        else
        {
            library = libraryPath ?? LibraryForDrive(Path.GetPathRoot(Environment.SystemDirectory)!);
            folder = FolderName(game.Name);
        }

        var job = Track(new DownloadJob(0, game.Name, kind, library, folder)
        {
            EpicAppName = game.AppId,
            EpicNamespace = game.EpicNamespace,
            EpicCatalogItemId = game.EpicCatalogItemId,
            CreateDesktopShortcut = desktopShortcut,
            CreateStartMenuShortcut = startMenuShortcut,
        }, game.EpicCategories.ToArray());
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

    public void Cancel(DownloadJob job)
    {
        job.PauseRequested = false;
        if (job.Cts is { } cts) { cts.Cancel(); return; }
        FinishCanceled(job);
    }

    private void FinishCanceled(DownloadJob job)
    {
        // Solo se borra una instalación nueva que nadie más tiene registrada.
        if (job.Kind == DownloadJobKind.Install && job.InstallDirName is { } name)
        {
            var path = Path.Combine(job.LibraryPath, name);
            var known = EpicInstallRegistry.Load().Any(r => string.Equals(r.InstallDir, path, StringComparison.OrdinalIgnoreCase))
                        || EpicLibraryScanner.Scan().Any(g => string.Equals(g.InstallDir?.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase));
            if (!known)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
                catch (Exception ex) { Log.Warn($"No se pudo borrar la descarga cancelada {path}: {ex.Message}"); }
            }
        }
        job.SetState(DownloadJobState.Canceled, job.Kind == DownloadJobKind.Install
            ? "Cancelado: se borró lo descargado."
            : "Cancelado: la actualización quedó a medias. Vuelve a actualizar para completarla.");
        Untrack(job);
        JobFinished?.Invoke(job);
    }

    /// <summary>Desinstala un juego de Epic instalado por Purgine (borra su carpeta y su registro).</summary>
    public string? Uninstall(GameEntry game)
    {
        if (Find(game.AppId) is not null) return "Hay una descarga de este juego en curso: cancélala primero.";
        try
        {
            if (game.InstallDir is { } dir && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            EpicInstallRegistry.Remove(game.AppId);
            Log.Info($"Desinstalado (Epic): {game.Name} ({game.AppId}).");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Error al desinstalar {game.Name}", ex);
            return $"No se pudo desinstalar por completo: {ex.Message}";
        }
    }

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
        try
        {
            var dir = Path.Combine(job.LibraryPath, job.InstallDirName!);
            var result = await _engine.RunAsync(job, dir, cts.Token).ConfigureAwait(false);
            string[] categories;
            lock (_gate) categories = _categories.GetValueOrDefault(job) ?? [];
            EpicInstallRegistry.Upsert(new EpicInstallRecord
            {
                AppName = job.EpicAppName!,
                Title = job.Name,
                InstallDir = result.InstallDir,
                LaunchExe = result.LaunchExe,
                LaunchCommand = result.LaunchCommand,
                BuildVersion = result.BuildVersion,
                Namespace = job.EpicNamespace!,
                CatalogItemId = job.EpicCatalogItemId!,
                InstallSize = result.InstallSize,
                Categories = categories,
            });
            job.SetState(DownloadJobState.Completed, job.Kind == DownloadJobKind.Install ? "Instalado." : "Actualizado.");
            Untrack(job);
            JobFinished?.Invoke(job);
        }
        catch (OperationCanceledException) when (job.PauseRequested || _shutdown.IsCancellationRequested)
        {
            job.SetState(DownloadJobState.Paused, "En pausa");
        }
        catch (OperationCanceledException)
        {
            FinishCanceled(job);
        }
        catch (Exception ex)
        {
            Log.Error($"Descarga de Epic {job.Name} ({job.EpicAppName})", ex);
            job.SetState(DownloadJobState.Failed, ex.Message);
            Untrack(job);
            JobFinished?.Invoke(job);
        }
        finally
        {
            job.Cts = null;
            Persist();
        }
    }

    private DownloadJob Track(DownloadJob job, string[] categories)
    {
        job.Changed += j => JobChanged?.Invoke(j);
        lock (_gate)
        {
            _jobs.Add(job);
            _categories[job] = categories;
        }
        QueueChanged?.Invoke();
        return job;
    }

    private void Untrack(DownloadJob job)
    {
        lock (_gate)
        {
            _jobs.Remove(job);
            _categories.Remove(job);
        }
        QueueChanged?.Invoke();
        Persist();
    }

    private void Persist()
    {
        List<QueuedEpicDownload> items;
        lock (_gate)
        {
            items = _jobs.Where(j => !j.IsFinished).Select(j => new QueuedEpicDownload
            {
                AppName = j.EpicAppName!,
                Namespace = j.EpicNamespace!,
                CatalogItemId = j.EpicCatalogItemId!,
                Name = j.Name,
                Kind = j.Kind,
                LibraryPath = j.LibraryPath,
                InstallDirName = j.InstallDirName,
                Categories = _categories.GetValueOrDefault(j) ?? [],
                CreateDesktopShortcut = j.CreateDesktopShortcut,
                CreateStartMenuShortcut = j.CreateStartMenuShortcut,
            }).ToList();
        }
        JsonFileStore.Save(_queuePath, items);
    }

    public void Dispose()
    {
        PauseAll();
        _shutdown.Cancel();
        Persist();
    }
}
