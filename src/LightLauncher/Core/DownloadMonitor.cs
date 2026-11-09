using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LightLauncher.Infrastructure;
using LightLauncher.Library;

namespace LightLauncher.Core;

/// <summary>Fase de una descarga/actualización de Steam.</summary>
public enum DownloadPhase
{
    /// <summary>En cola o con actualización pendiente (Steam aún no trabaja en ella).</summary>
    Queued,
    Preparing,
    Preallocating,
    Downloading,
    Verifying,
    Committing,
    Stopping,
    /// <summary>En pausa (en Steam, por LightLauncher, o porque Steam está cerrado).</summary>
    Paused,
    Uninstalling,
    Completed,
    Canceled,
    Removed,
}

/// <summary>Estado en vivo de una descarga.</summary>
/// <param name="IsEstimated">El progreso combina el último valor de Steam con la transferencia medida desde entonces.</param>
/// <param name="IsRequested">Instalación pedida desde LightLauncher.</param>
public sealed record DownloadInfo(
    string AppId,
    string Name,
    DownloadPhase Phase,
    long BytesDownloaded,
    long BytesTotal,
    double BytesPerSecond,
    TimeSpan? Eta,
    bool IsEstimated,
    bool IsRequested,
    string? Detail = null)
{
    public double? Progress => BytesTotal > 0 ? Math.Clamp((double)BytesDownloaded / BytesTotal, 0, 1) : null;

    /// <summary>Steam está trabajando en ella ahora mismo.</summary>
    public bool IsActive => Phase is DownloadPhase.Preparing or DownloadPhase.Preallocating or DownloadPhase.Downloading
        or DownloadPhase.Verifying or DownloadPhase.Committing or DownloadPhase.Stopping or DownloadPhase.Uninstalling;

    public bool IsTerminal => Phase is DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Removed;

    public string PhaseText => Phase switch
    {
        DownloadPhase.Queued => Detail ?? (IsRequested ? "En cola" : "Actualización pendiente"),
        DownloadPhase.Preparing => "Preparando…",
        DownloadPhase.Preallocating => "Reservando espacio en disco…",
        DownloadPhase.Downloading => "Descargando",
        DownloadPhase.Verifying => "Verificando archivos…",
        DownloadPhase.Committing => "Instalando…",
        DownloadPhase.Stopping => "Deteniendo…",
        DownloadPhase.Paused => Detail ?? "En pausa",
        DownloadPhase.Uninstalling => "Desinstalando…",
        DownloadPhase.Completed => "Completado",
        DownloadPhase.Canceled => Detail ?? "Cancelado",
        _ => "Quitado",
    };
}

/// <summary>
/// Monitor de descargas de Steam en tiempo real, sin abrir Steam ni usar APIs privadas:
/// <list type="bullet">
///   <item><b>Estado</b>: <c>logs/content_log.txt</c>, que Steam escribe al instante en cada cambio
///         (en cola, reservando, descargando, verificando, instalando, en pausa, cancelado, terminado)
///         junto con el tamaño total (<c>update started : download 0/N</c>).</item>
///   <item><b>Nombre y bytes confirmados</b>: el manifiesto <c>appmanifest_{id}.acf</c>.</item>
///   <item><b>Velocidad y progreso en vivo</b>: los bytes que transfiere <c>steam.exe</c>
///         (<c>GetProcessIoCounters</c>) mientras una app está en fase «Descargando». Steam descarga de
///         una en una, así que la transferencia se atribuye a esa app. Steam no escribe el progreso en
///         disco mientras descarga, por eso esta medición es la única fuente en vivo disponible.</item>
/// </list>
/// También decide si Steam debe seguir abierto (<see cref="HasActiveDownloads"/>).
/// </summary>
public sealed partial class DownloadMonitor : IDisposable
{
    private static readonly TimeSpan ActivePoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(10);
    /// <summary>Al arrancar se lee el final del log para reconstruir el estado actual.</summary>
    private const int InitialTailBytes = 512 * 1024;
    private const double SpeedSmoothing = 0.35;

    private readonly SteamLocator _steam;
    private readonly SteamLibraryScanner _scanner;
    private readonly Dictionary<string, AppState> _apps = new();
    private readonly Dictionary<string, string> _requested = new(); // appId → nombre
    private readonly object _gate = new();
    private readonly Timer _timer;
    private long _logOffset = -1;
    private ulong _lastIoBytes;
    private long _lastIoTimestamp;
    private int _ioPid;
    private IReadOnlyList<DownloadInfo> _current = [];
    private bool _disposed;

    /// <summary>Lista actualizada (en un hilo del pool; la UI debe despachar).</summary>
    public event Action<IReadOnlyList<DownloadInfo>>? Changed;

    /// <summary>Una descarga pasó a estado terminal (completada, cancelada o quitada).</summary>
    public event Action<DownloadInfo>? Finished;

    /// <summary>Si es true, las descargas se muestran como pausadas por LightLauncher.</summary>
    public Func<bool>? IsSteamSuspended { get; set; }

    public DownloadMonitor(SteamLocator steam, SteamLibraryScanner scanner)
    {
        _steam = steam;
        _scanner = scanner;
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<DownloadInfo> Current { get { lock (_gate) return _current; } }

    /// <summary>
    /// ¿Hay que mantener Steam abierto? Sí si Steam trabaja en alguna descarga/instalación/desinstalación,
    /// si hay instalaciones pedidas desde aquí aún en cola, o si LightLauncher pausó Steam.
    /// </summary>
    public bool HasActiveDownloads
    {
        get
        {
            if (IsSteamSuspended?.Invoke() == true) return true;
            lock (_gate)
            {
                return _downloadFolderActive ||
                       _current.Any(d => d.IsActive || (d.Phase == DownloadPhase.Queued && d.IsRequested));
            }
        }
    }

    /// <summary>Hubo escrituras recientes en alguna carpeta <c>steamapps/downloading</c>.</summary>
    private bool _downloadFolderActive;

    /// <summary>Plazo para que Steam empiece una tarea pedida antes de darla por no iniciada.</summary>
    private static readonly TimeSpan RequestStartTimeout = TimeSpan.FromMinutes(6);

    /// <summary>¿Hay instalaciones pedidas desde aquí que Steam todavía no ha empezado?</summary>
    public bool HasQueuedRequests
    {
        get { lock (_gate) return _current.Any(d => d.IsRequested && d.Phase == DownloadPhase.Queued); }
    }

    /// <summary>¿Steam está trabajando ahora (descargando, verificando, instalando, desinstalando) o pausado por nosotros?</summary>
    public bool IsSteamWorking
    {
        get
        {
            if (IsSteamSuspended?.Invoke() == true) return true;
            lock (_gate) return _current.Any(d => d.IsActive);
        }
    }

    /// <summary>
    /// ¿Hay peticiones en cola que el Steam actual no puede ver? Steam solo lee manifiestos nuevos al
    /// arrancar: si se abrió antes de crear el manifiesto, hace falta reiniciarlo.
    /// </summary>
    public bool NeedsSteamRestartForQueued()
    {
        DateTime? steamStart = null;
        var procs = Process.GetProcessesByName("steam");
        try
        {
            if (procs.Length == 0) return false;
            try { steamStart = procs[0].StartTime.ToUniversalTime(); } catch (Exception) { return false; }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }

        lock (_gate)
        {
            return _current.Any(d => d.IsRequested && d.Phase == DownloadPhase.Queued &&
                                     _requestedAt.TryGetValue(d.AppId, out var at) && at > steamStart);
        }
    }

    private readonly Dictionary<string, DateTime> _requestedAt = new();
    private readonly Dictionary<string, DateTime> _recentlyFinished = new();

    public void Start() => _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

    /// <summary>Marca una instalación pedida desde LightLauncher (se vigila desde ya y mantiene Steam abierto).</summary>
    public void MarkRequested(string appId, string name)
    {
        lock (_gate)
        {
            _requested[appId] = name;
            _requestedAt[appId] = DateTime.UtcNow;
            if (!_apps.TryGetValue(appId, out var s) || s.IsTerminal)
                _apps[appId] = new AppState { Phase = DownloadPhase.Queued, Name = name, LastEventUtc = DateTimeOffset.UtcNow };
        }
        PollSoon();
    }

    public void Forget(string appId)
    {
        lock (_gate)
        {
            _requested.Remove(appId);
            _requestedAt.Remove(appId);
            _apps.Remove(appId);
        }
        PollSoon();
    }

    public void PollSoon() => _timer.Change(TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);

    /// <summary>Fase interna actual de una app según el log (diagnóstico y pruebas).</summary>
    public DownloadPhase? GetLoggedPhase(string appId)
    {
        lock (_gate) return _apps.TryGetValue(appId, out var s) ? s.Phase : null;
    }

    /// <summary>Total a descargar anunciado por Steam en el log (diagnóstico y pruebas).</summary>
    public long GetLoggedTotal(string appId)
    {
        lock (_gate) return _apps.TryGetValue(appId, out var s) ? s.TotalDownload : 0;
    }

    // =====================================================================================

    private void Poll()
    {
        if (_disposed) return;
        var finished = new List<DownloadInfo>();
        IReadOnlyList<DownloadInfo> snapshot;
        bool busy;
        try
        {
            ReadNewLogLines();
            var ioRate = MeasureSteamIoRate(out var dt);
            var folderActive = IsDownloadFolderActive();

            lock (_gate)
            {
                _downloadFolderActive = folderActive;
                var steamRunning = SteamLocator.IsClientRunning();
                var suspended = IsSteamSuspended?.Invoke() == true;
                var list = new List<DownloadInfo>();

                foreach (var (appId, s) in _apps.ToList())
                {
                    RefreshFromManifest(appId, s);
                    UpdateEstimate(s, ioRate, dt);
                    CheckRequestTimeout(appId, s, steamRunning);

                    var phase = s.Phase;
                    string? detail = null;
                    if (!s.IsTerminal && phase != DownloadPhase.Queued)
                    {
                        if (suspended) { phase = DownloadPhase.Paused; detail = "En pausa (LightLauncher)"; }
                        else if (!steamRunning) { phase = DownloadPhase.Paused; detail = "En pausa: Steam está cerrado"; }
                    }
                    else if (phase == DownloadPhase.Queued && !steamRunning && _requested.ContainsKey(appId))
                    {
                        detail = "Empezará al abrirse Steam";
                    }
                    if (phase == DownloadPhase.Queued && !_requested.ContainsKey(appId) &&
                        _delayedUntil.TryGetValue(appId, out var until) && until > DateTimeOffset.Now)
                    {
                        detail = $"Aplazada por Steam hasta el {until.LocalDateTime:d MMM}";
                    }

                    var total = s.TotalDownload > 0 ? s.TotalDownload : s.ManifestTotal;
                    var done = (long)Math.Min(total > 0 ? total : long.MaxValue, Math.Max(s.ManifestBytes, s.EstimatedBytes));
                    // En espera: mostrar el tamaño real de lo pendiente (no restos de descargas anteriores).
                    if (phase == DownloadPhase.Queued && _pendingBytes.TryGetValue(appId, out var pending))
                    {
                        total = pending;
                        done = 0;
                    }
                    if (s.Phase == DownloadPhase.Completed) done = total;
                    var speed = phase == DownloadPhase.Downloading ? s.SpeedEma : 0;
                    TimeSpan? eta = speed > 50_000 && total > done ? TimeSpan.FromSeconds((total - done) / speed) : null;

                    if (s.NotStarted) detail = "Steam no la empezó";
                    var info = new DownloadInfo(appId, s.Name ?? _requested.GetValueOrDefault(appId) ?? $"App {appId}",
                        phase, done, total, speed, eta, s.EstimatedBytes > s.ManifestBytes, _requested.ContainsKey(appId), detail);

                    if (s.IsTerminal)
                    {
                        finished.Add(info);
                        _recentlyFinished[appId] = DateTime.UtcNow;
                        _apps.Remove(appId);
                        _requested.Remove(appId);
                        _requestedAt.Remove(appId);
                    }
                    else
                    {
                        list.Add(info);
                    }
                }

                _current = list
                    .OrderByDescending(d => d.IsActive)
                    .ThenBy(d => d.Phase == DownloadPhase.Queued && !d.IsRequested) // pendientes ajenas al final
                    .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                snapshot = _current;
                busy = _apps.Count > 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Monitor de descargas: {ex.Message}");
            snapshot = Current;
            busy = false;
        }
        finally
        {
            if (!_disposed)
                _timer.Change(Current.Count > 0 ? ActivePoll : IdlePoll, Timeout.InfiniteTimeSpan);
        }

        // En la primera pasada el log contiene historial (descargas de otros días): no se notifica.
        if (_initialized)
            foreach (var f in finished) Finished?.Invoke(f);
        _initialized = true;
        Changed?.Invoke(snapshot);
        _ = busy;
    }

    private bool _initialized;

    // ----- content_log.txt -----

    private void ReadNewLogLines()
    {
        var path = _steam.SteamPath is { } root ? Path.Combine(root, "logs", "content_log.txt") : null;
        if (path is null || !File.Exists(path)) return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        if (_logOffset < 0)
        {
            // Primer arranque: los aplazamientos de Steam pueden estar en cualquier punto del log.
            ScanUpdateDelays(fs);
            _logOffset = Math.Max(0, length - InitialTailBytes);
        }
        if (length < _logOffset) _logOffset = 0;                                  // Steam rotó el log
        if (length == _logOffset) return;

        fs.Position = _logOffset;
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var text = reader.ReadToEnd();
        // Solo líneas completas: la última puede estar a medio escribir.
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0) return;
        _logOffset += Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNewline + 1));

        lock (_gate)
        {
            foreach (var line in text.AsSpan(0, lastNewline).ToString().Split('\n'))
                ApplyLogLine(line.TrimEnd('\r'));
        }
    }

    // ----- Aplazamientos de Steam -----

    /// <summary>
    /// Steam retrasa las actualizaciones automáticas de juegos que no se juegan desde hace tiempo y lo anota:
    /// <c>state changed : Update Required,Fully Installed, (Update delayed for N secs)</c>. Mientras dure,
    /// despertar Steam no la aplica (comprobado): solo la aplica al abrir el juego o desde su ventana.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _delayedUntil = new();

    /// <summary>Fecha hasta la que Steam aplaza la actualización de esta app, si se conoce y no ha pasado.</summary>
    public DateTimeOffset? GetUpdateDelayedUntil(string appId)
    {
        lock (_gate)
            return _delayedUntil.TryGetValue(appId, out var until) && until > DateTimeOffset.Now ? until : null;
    }

    private void ScanUpdateDelays(FileStream fs)
    {
        fs.Position = 0;
        using var reader = new StreamReader(fs, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        lock (_gate)
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                // Solo interesan los aplazamientos y lo que los anula (actualización hecha o empezada).
                if (line.Contains("delayed for", StringComparison.Ordinal) ||
                    line.Contains("update started", StringComparison.Ordinal) ||
                    line.Contains("finished update", StringComparison.Ordinal) ||
                    line.Contains("prefetch finished", StringComparison.Ordinal))
                    TrackDelay(line);
            }
        }
    }

    private void TrackDelay(string line)
    {
        var m = LogLine().Match(line);
        if (!m.Success) return;
        var appId = m.Groups["id"].Value;
        var rest = m.Groups["rest"].Value;
        if (rest.StartsWith("Workshop", StringComparison.Ordinal)) return;

        // «update prefetch finished : N bytes to download»: tamaño real de la actualización pendiente.
        var prefetch = PrefetchBytes().Match(rest);
        if (prefetch.Success && long.TryParse(prefetch.Groups["bytes"].Value, out var pendingBytes))
        {
            _pendingBytes[appId] = pendingBytes;
            return;
        }

        var delay = UpdateDelay().Match(rest);
        if (delay.Success &&
            DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var ts) &&
            long.TryParse(delay.Groups["secs"].Value, out var secs))
        {
            _delayedUntil[appId] = ts + TimeSpan.FromSeconds(secs);
        }
        else if (rest.Contains("update started", StringComparison.Ordinal) || rest.StartsWith("finished update", StringComparison.Ordinal))
        {
            _delayedUntil.Remove(appId);
            _pendingBytes.Remove(appId);
        }
    }

    /// <summary>Tamaño de la actualización pendiente según el último «prefetch» de Steam.</summary>
    private readonly Dictionary<string, long> _pendingBytes = new();

    /// <summary>Aplica una línea del log a la máquina de estados (expuesto para pruebas).</summary>
    public void ApplyLogLine(string line)
    {
        var m = LogLine().Match(line);
        if (!m.Success) return;
        var rest = m.Groups["rest"].Value;
        if (rest.StartsWith("Workshop", StringComparison.Ordinal)) return; // contenido del taller: no es una descarga de juego
        TrackDelay(line);

        var appId = m.Groups["id"].Value;
        _ = DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var ts);
        var sep = rest.IndexOf(" : ", StringComparison.Ordinal);
        var evt = sep >= 0 ? rest[..sep] : rest;
        var detail = sep >= 0 ? rest[(sep + 3)..] : string.Empty;

        if (!_apps.TryGetValue(appId, out var s))
        {
            // Solo empezamos a seguir apps por eventos que indican trabajo de descarga.
            if (evt is not ("state changed" or "App update changed" or "update started")) return;

            // Tras terminar/cancelar, Steam escribe unas líneas de cierre: no deben resucitar la entrada
            // (ni duplicar el aviso). Solo se acepta trabajo nuevo de verdad.
            var newWork = evt == "update started" ||
                          (evt == "App update changed" && !detail.Contains("Stopping") &&
                           (detail.Contains("Downloading") || detail.Contains("Preallocating")));
            if (!newWork && _recentlyFinished.TryGetValue(appId, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(30))
                return;
            s = new AppState { Phase = DownloadPhase.Queued };
            _apps[appId] = s;
        }
        s.LastEventUtc = ts == default ? DateTimeOffset.UtcNow : ts.ToUniversalTime();

        switch (evt)
        {
            case "state changed":
                ApplyStateFlags(s, detail);
                break;

            case "App update changed":
                // «Stopping» primero: «Running Update,Downloading,Staging,Stopping,» es una parada, no una descarga.
                if (detail.Contains("Stopping")) s.Phase = DownloadPhase.Stopping;
                else if (detail.Contains("Downloading")) s.Phase = DownloadPhase.Downloading;
                else if (detail.Contains("Preallocating")) s.Phase = DownloadPhase.Preallocating;
                else if (detail.Contains("Verifying")) s.Phase = DownloadPhase.Verifying;
                else if (detail.Contains("Committing")) s.Phase = DownloadPhase.Committing;
                else if (detail.Contains("Running Update")) s.Phase = DownloadPhase.Preparing;
                // "None": lo decide la siguiente línea de estado.
                break;

            case "update started":
                var dl = DownloadPair().Match(detail);
                if (dl.Success)
                {
                    s.TotalDownload = long.Parse(dl.Groups["total"].Value, CultureInfo.InvariantCulture);
                    s.EstimatedBytes = long.Parse(dl.Groups["done"].Value, CultureInfo.InvariantCulture);
                }
                s.SpeedEma = 0;
                break;

            case "update canceled":
                s.Phase = DownloadPhase.Canceled;
                break;

            default:
                if (evt.StartsWith("finished update", StringComparison.Ordinal)) s.Phase = DownloadPhase.Completed;
                else if (rest.StartsWith("finished uninstall", StringComparison.Ordinal)) s.Phase = DownloadPhase.Removed;
                break;
        }
    }

    private static void ApplyStateFlags(AppState s, string flags)
    {
        if (flags.StartsWith("Uninstalled", StringComparison.Ordinal)) s.Phase = DownloadPhase.Removed;
        else if (flags.Contains("Uninstalling")) s.Phase = DownloadPhase.Uninstalling;
        else if (flags.Contains("(Canceled)")) s.Phase = DownloadPhase.Canceled;
        else if (flags.Contains("Update Paused")) s.Phase = DownloadPhase.Paused;
        else if (flags.Contains("Update Running")) { if (!IsWorking(s.Phase)) s.Phase = DownloadPhase.Preparing; }
        else if (flags.Contains("Update Queued") || flags.Contains("Update Required")) s.Phase = DownloadPhase.Queued;
        else if (flags.TrimEnd(',', ' ') == "Fully Installed") s.Phase = DownloadPhase.Completed;
    }

    private static bool IsWorking(DownloadPhase p) => p is DownloadPhase.Downloading or DownloadPhase.Preallocating
        or DownloadPhase.Verifying or DownloadPhase.Committing or DownloadPhase.Preparing or DownloadPhase.Stopping;

    // ----- Manifiesto -----

    private void RefreshFromManifest(string appId, AppState s)
    {
        var manifest = _scanner.FindGame(appId);
        if (manifest is null)
        {
            // Sin manifiesto: o se desinstaló, o es una petición que aún no se ha escrito.
            if (s.Phase is not (DownloadPhase.Uninstalling or DownloadPhase.Queued) && !s.IsTerminal) s.Phase = DownloadPhase.Removed;
            else if (s.Phase == DownloadPhase.Queued && !_requested.ContainsKey(appId)) s.Phase = DownloadPhase.Removed;
            return;
        }

        s.Name = manifest.Name;
        s.ManifestBytes = manifest.BytesDownloaded;
        s.ManifestTotal = manifest.BytesToDownload;
        // Manifiesto «instalado y sin nada pendiente» (StateFlags = 4): terminada aunque nos
        // perdiéramos la línea del log. Con el bit 2 (actualización requerida) sigue pendiente.
        if (manifest.StateFlags == 4 && !IsWorking(s.Phase) && s.Phase != DownloadPhase.Uninstalling)
            s.Phase = DownloadPhase.Completed;
    }

    /// <summary>
    /// Una tarea pedida que sigue «en cola» tras <see cref="RequestStartTimeout"/> con Steam abierto se da
    /// por no iniciada (p. ej. la app está en uso o Steam tiene restricciones de descarga), para no
    /// mantener Steam abierto indefinidamente.
    /// </summary>
    private void CheckRequestTimeout(string appId, AppState s, bool steamRunning)
    {
        if (!_requested.ContainsKey(appId) || s.Phase != DownloadPhase.Queued || !steamRunning)
        {
            s.WaitingSince = null;
            return;
        }
        s.WaitingSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - s.WaitingSince > RequestStartTimeout)
        {
            s.Phase = DownloadPhase.Canceled;
            s.NotStarted = true;
            Log.Warn($"Steam no empezó la tarea pedida para {s.Name ?? appId} en {RequestStartTimeout.TotalMinutes:0} min.");
        }
    }

    /// <summary>¿Algún archivo de <c>steamapps/downloading</c> se escribió en los últimos 20 s?</summary>
    private bool IsDownloadFolderActive()
    {
        var threshold = DateTime.UtcNow - TimeSpan.FromSeconds(20);
        try
        {
            foreach (var library in _scanner.GetLibraryFolders())
            {
                var dir = Path.Combine(library, "steamapps", "downloading");
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (File.GetLastWriteTimeUtc(file) > threshold) return true;
                }
            }
        }
        catch (Exception) { /* carpeta en uso o desaparecida a mitad: se reintenta en la próxima pasada */ }
        return false;
    }

    // ----- Velocidad real (transferencia de steam.exe) -----

    private void UpdateEstimate(AppState s, double ioRate, double dt)
    {
        if (s.Phase != DownloadPhase.Downloading || dt <= 0) return;
        s.SpeedEma = s.SpeedEma <= 0 ? ioRate : SpeedSmoothing * ioRate + (1 - SpeedSmoothing) * s.SpeedEma;
        var total = s.TotalDownload > 0 ? s.TotalDownload : s.ManifestTotal;
        var next = s.EstimatedBytes + (long)(ioRate * dt);
        // Nunca 100 % por estimación: el 100 % lo confirma Steam (fase «Completado»).
        s.EstimatedBytes = total > 0 ? Math.Min(next, (long)(total * 0.995)) : next;
    }

    /// <summary>Bytes por segundo leídos por steam.exe desde la última medición (red + disco).</summary>
    private double MeasureSteamIoRate(out double seconds)
    {
        seconds = 0;
        var procs = Process.GetProcessesByName("steam");
        try
        {
            if (procs.Length == 0) { _ioPid = 0; return 0; }
            var pid = procs[0].Id;
            var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return 0;
            try
            {
                if (!NativeMethods.GetProcessIoCounters(handle, out var io)) return 0;
                var now = Stopwatch.GetTimestamp();
                var bytes = io.ReadTransferCount;
                double rate = 0;
                if (_ioPid == pid && _lastIoTimestamp != 0 && bytes >= _lastIoBytes)
                {
                    seconds = Stopwatch.GetElapsedTime(_lastIoTimestamp, now).TotalSeconds;
                    if (seconds > 0) rate = (bytes - _lastIoBytes) / seconds;
                }
                _ioPid = pid;
                _lastIoBytes = bytes;
                _lastIoTimestamp = now;
                return rate;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private sealed class AppState
    {
        public DownloadPhase Phase;
        public string? Name;
        public long TotalDownload;
        public long ManifestBytes;
        public long ManifestTotal;
        public long EstimatedBytes;
        public double SpeedEma;
        public DateTimeOffset LastEventUtc;
        public DateTime? WaitingSince;
        public bool NotStarted;
        public bool IsTerminal => Phase is DownloadPhase.Completed or DownloadPhase.Canceled or DownloadPhase.Removed;
    }

    [GeneratedRegex(@"^\[(?<ts>[^\]]+)\] AppID (?<id>\d+) (?<rest>.+)$")]
    private static partial Regex LogLine();

    [GeneratedRegex(@"download (?<done>\d+)/(?<total>\d+)")]
    private static partial Regex DownloadPair();

    [GeneratedRegex(@"Update delayed for (?<secs>\d+) secs")]
    private static partial Regex UpdateDelay();

    [GeneratedRegex(@"prefetch finished : (?<bytes>\d+) bytes to download")]
    private static partial Regex PrefetchBytes();
}
