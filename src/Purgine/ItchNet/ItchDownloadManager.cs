using System.Net.Http;
using System.Net.Http.Headers;
using Purgine.Infrastructure;
using Purgine.SteamNet;

namespace Purgine.ItchNet;

/// <summary>Qué descargar de itch.io: un archivo de la API (juego de la cuenta) o un enlace capturado en la tienda.</summary>
public sealed class ItchDownloadRequest
{
    public string Key { get; set; } = string.Empty;   // «autor/juego»
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;   // página del juego
    public string? CoverUrl { get; set; }
    public long? GameId { get; set; }
    public long? DownloadKeyId { get; set; }
    public long? UploadId { get; set; }
    public string? DirectUrl { get; set; }            // descarga capturada en la web (enlace firmado de la CDN)
    public string? FileName { get; set; }
    public long Size { get; set; }
}

/// <summary>
/// Cola de descargas de itch.io (una a la vez, como las de Steam y Epic): progreso real, pausar / reanudar
/// (continúa donde iba con <c>Range</c>), cancelar y, al terminar, descompresión, búsqueda del ejecutable y alta en
/// la biblioteca como instalado. Todo va a la carpeta de itch.io de Purgine.
/// </summary>
public sealed class ItchDownloadManager : IDisposable
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "Purgine/1.0" } },
    };

    private readonly ItchApiClient _api;
    private readonly string _libraryRoot;
    private readonly string _queuePath;
    private readonly List<DownloadJob> _jobs = [];
    private readonly Dictionary<DownloadJob, ItchDownloadRequest> _requests = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    public event Action? QueueChanged;
    public event Action<DownloadJob>? JobChanged;
    public event Action<DownloadJob>? JobFinished;

    public ItchDownloadManager(ItchApiClient api, string libraryRoot, string queuePath)
    {
        _api = api;
        _libraryRoot = libraryRoot;
        _queuePath = queuePath;
        foreach (var r in JsonFileStore.Load<List<ItchDownloadRequest>>(queuePath))
        {
            var job = Track(r);
            job.SetState(DownloadJobState.Paused, "En pausa (de la sesión anterior)");
        }
        _ = Task.Run(RunQueueAsync);
    }

    public string LibraryRoot => _libraryRoot;
    public IReadOnlyList<DownloadJob> Jobs { get { lock (_gate) return _jobs.ToList(); } }

    public DownloadJob? Find(string key)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.ItchKey == key && !j.IsFinished);
    }

    public DownloadJob Enqueue(ItchDownloadRequest request)
    {
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(j => j.ItchKey == request.Key && !j.IsFinished) is { } existing)
            {
                if (existing.State == DownloadJobState.Paused) Resume(existing);
                return existing;
            }
        }
        var job = Track(request);
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
        ItchDownloadRequest? req;
        lock (_gate) _requests.TryGetValue(job, out req);
        if (req is not null) TryDelete(PartialPath(req));
        job.SetState(DownloadJobState.Canceled, "Cancelado: se borró lo descargado.");
        Untrack(job);
        JobFinished?.Invoke(job);
    }

    /// <summary>Desinstala un juego de itch.io (borra su carpeta y su registro).</summary>
    public string? Uninstall(string key, string? installDir)
    {
        if (Find(key) is not null) return "Hay una descarga de este juego en curso: cancélala primero.";
        try
        {
            if (installDir is not null && Directory.Exists(installDir) &&
                Path.GetFullPath(installDir).StartsWith(Path.GetFullPath(_libraryRoot), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(installDir, recursive: true);
            ItchRegistry.Remove(key);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
        ItchDownloadRequest req;
        lock (_gate) req = _requests[job];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        job.Cts = cts;
        try
        {
            job.SetState(DownloadJobState.Preparing, "Conectando con itch.io…");
            var file = await DownloadAsync(job, req, cts.Token).ConfigureAwait(false);

            job.SetState(DownloadJobState.Preparing, ItchInstaller.IsArchive(file) ? "Descomprimiendo…" : "Instalando…");
            var dir = ItchInstaller.FolderFor(_libraryRoot, req.Title);
            var exe = await Task.Run(() => ItchInstaller.Install(file, dir, req.Title), cts.Token).ConfigureAwait(false);
            ItchRegistry.Upsert(new ItchInstallRecord
            {
                Key = req.Key,
                Title = req.Title,
                Url = req.Url,
                GameId = req.GameId,
                DownloadKeyId = req.DownloadKeyId,
                UploadId = req.UploadId,
                InstallDir = dir,
                Executable = exe,
                CoverUrl = req.CoverUrl,
                SizeOnDisk = ItchInstaller.DirectorySize(dir),
                InstalledAt = DateTimeOffset.UtcNow,
            });
            Log.Info($"itch.io: {req.Title} instalado en {dir} (ejecutable: {exe ?? "no encontrado"}).");
            job.SetState(DownloadJobState.Completed, exe is null
                ? "Descargado, pero no se encontró el ejecutable (si es un instalador, ábrelo desde la carpeta del juego)."
                : "Instalado.");
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
            Log.Error($"itch.io: descarga de {req.Title}", ex);
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

    /// <summary>Descarga a un archivo parcial (se continúa si existe) y lo renombra al terminar.</summary>
    private async Task<string> DownloadAsync(DownloadJob job, ItchDownloadRequest req, CancellationToken ct)
    {
        var part = PartialPath(req);
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        HttpRequestMessage NewRequest()
        {
            var r = req.UploadId is { } upload
                ? _api.CreateDownloadRequest(upload, req.DownloadKeyId)
                : new HttpRequestMessage(HttpMethod.Get, req.DirectUrl ?? throw new InvalidOperationException("Sin enlace de descarga."));
            if (existing > 0) r.Headers.Range = new RangeHeaderValue(existing, null);
            return r;
        }

        using var request = NewRequest();
        using var resp = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"itch.io respondió {(int)resp.StatusCode} al descargar (el enlace puede haber caducado: vuelve a pulsar Descargar).");
        var resumed = existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;

        var name = req.FileName ?? resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? Path.GetFileName(resp.RequestMessage?.RequestUri?.LocalPath) ?? "descarga.bin";
        req.FileName = SafeFileName(name);
        var total = (resp.Content.Headers.ContentLength ?? Math.Max(0, req.Size - existing)) + existing;

        job.BeginTransfer(total);
        if (existing > 0) job.AddTransferred((uint)Math.Min(existing, uint.MaxValue));
        job.SetState(DownloadJobState.Downloading, null);

        await using (var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                job.AddTransferred((uint)read);
            }
        }

        var final = Path.Combine(Path.GetDirectoryName(part)!, req.FileName);
        File.Move(part, final, overwrite: true);
        return final;
    }

    private string PartialPath(ItchDownloadRequest req) =>
        Path.Combine(_libraryRoot, ".descargas", SafeFileName(req.Key.Replace('/', '_')) + ".part");

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "descarga.bin" : clean;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* se reintenta en la próxima limpieza */ }
    }

    private DownloadJob Track(ItchDownloadRequest req)
    {
        var job = new DownloadJob(0, req.Title, DownloadJobKind.Install, _libraryRoot, null) { ItchKey = req.Key };
        job.Changed += j => JobChanged?.Invoke(j);
        lock (_gate)
        {
            _jobs.Add(job);
            _requests[job] = req;
        }
        QueueChanged?.Invoke();
        return job;
    }

    private void Untrack(DownloadJob job)
    {
        lock (_gate)
        {
            _jobs.Remove(job);
            _requests.Remove(job);
        }
        QueueChanged?.Invoke();
        Persist();
    }

    private void Persist()
    {
        List<ItchDownloadRequest> items;
        lock (_gate) items = _jobs.Where(j => !j.IsFinished).Select(j => _requests[j]).ToList();
        JsonFileStore.Save(_queuePath, items);
    }

    public void Dispose()
    {
        PauseAll();
        _shutdown.Cancel();
        Persist();
    }
}
