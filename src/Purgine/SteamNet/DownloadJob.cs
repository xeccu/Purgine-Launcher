using System.Diagnostics;

namespace Purgine.SteamNet;

public enum DownloadJobKind
{
    Install,
    Update,
}

public enum DownloadJobState
{
    Queued,
    Preparing,
    Verifying,
    Downloading,
    Paused,
    /// <summary>Descarga terminada; falta registrar el juego en Steam (se hace con Steam cerrado).</summary>
    WaitingForSteam,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// Una descarga o actualización gestionada por Purgine. Seguro entre hilos: el motor lo actualiza
/// desde el pool y la UI lo lee cuando recibe <see cref="Changed"/>.
/// </summary>
public sealed class DownloadJob
{
    private const double SpeedSmoothing = 0.3;
    private readonly object _gate = new();
    private long _transferred;
    private long _total;
    private long _lastSampleBytes;
    private long _lastSampleTicks;
    private double _speed;
    private long _verifyDone;
    private long _verifyTotal;

    public DownloadJob(uint appId, string name, DownloadJobKind kind, string libraryPath, string? installDirName)
    {
        AppId = appId;
        Name = name;
        Kind = kind;
        LibraryPath = libraryPath;
        InstallDirName = installDirName;
    }

    /// <summary>Al terminar una instalación: acceso directo en el escritorio (elegido en el diálogo de instalar).</summary>
    public bool CreateDesktopShortcut { get; set; }

    /// <summary>Al terminar una instalación: acceso directo en el menú Inicio.</summary>
    public bool CreateStartMenuShortcut { get; set; }

    // ----- Epic Games (null en los trabajos de Steam) -----
    public string? EpicAppName { get; init; }
    public string? EpicNamespace { get; init; }
    public string? EpicCatalogItemId { get; init; }
    public bool IsEpic => EpicAppName is not null;

    // ----- itch.io (null en los demás) -----
    public string? ItchKey { get; init; }
    public bool IsItch => ItchKey is not null;

    /// <summary>Id del juego en la biblioteca («steam:730», «epic:Sugar», «itch:autor/juego»).</summary>
    public string GameKey => ItchKey is { } itch ? Library.GameEntry.MakeItchId(itch)
        : EpicAppName is { } app ? Library.GameEntry.MakeEpicId(app) : Library.GameEntry.MakeSteamId(AppId.ToString());

    public uint AppId { get; }
    public string Name { get; }
    public DownloadJobKind Kind { get; }
    public string LibraryPath { get; }
    public string? InstallDirName { get; }

    public DownloadJobState State { get; private set; } = DownloadJobState.Queued;
    public string? Message { get; private set; }

    /// <summary>Se dispara (como mucho cada ~0,4 s durante la descarga) al cambiar estado o progreso.</summary>
    public event Action<DownloadJob>? Changed;

    internal CancellationTokenSource? Cts { get; set; }
    internal bool PauseRequested { get; set; }

    /// <summary>Bytes comprimidos transferidos desde la CDN en esta ejecución.</summary>
    public long TransferredBytes => Interlocked.Read(ref _transferred);
    /// <summary>Bytes comprimidos que había que transferir en esta ejecución (lo pendiente).</summary>
    public long TotalBytes => Interlocked.Read(ref _total);

    public double? Progress => State switch
    {
        DownloadJobState.Completed or DownloadJobState.WaitingForSteam => 1,
        DownloadJobState.Verifying when _verifyTotal > 0 => (double)_verifyDone / _verifyTotal,
        _ when TotalBytes > 0 => Math.Clamp((double)TransferredBytes / TotalBytes, 0, 1),
        _ => null,
    };

    /// <summary>Velocidad real de red (bytes/s, suavizada).</summary>
    public double BytesPerSecond { get { lock (_gate) return State == DownloadJobState.Downloading ? _speed : 0; } }

    public TimeSpan? Eta
    {
        get
        {
            var speed = BytesPerSecond;
            var left = TotalBytes - TransferredBytes;
            return speed > 20_000 && left > 0 ? TimeSpan.FromSeconds(left / speed) : null;
        }
    }

    public bool IsActive => State is DownloadJobState.Preparing or DownloadJobState.Verifying or DownloadJobState.Downloading;
    public bool IsFinished => State is DownloadJobState.Completed or DownloadJobState.Failed or DownloadJobState.Canceled;

    internal void SetState(DownloadJobState state, string? message)
    {
        State = state;
        Message = message;
        Changed?.Invoke(this);
    }

    internal void BeginTransfer(long totalBytes)
    {
        Interlocked.Exchange(ref _total, totalBytes);
        Interlocked.Exchange(ref _transferred, 0);
        lock (_gate)
        {
            _speed = 0;
            _lastSampleBytes = 0;
            _lastSampleTicks = Stopwatch.GetTimestamp();
        }
    }

    internal void AddTransferred(uint bytes)
    {
        var now = Interlocked.Add(ref _transferred, bytes);
        var ticks = Stopwatch.GetTimestamp();
        bool notify = false;
        lock (_gate)
        {
            var elapsed = Stopwatch.GetElapsedTime(_lastSampleTicks, ticks).TotalSeconds;
            if (elapsed >= 0.4)
            {
                var rate = (now - _lastSampleBytes) / elapsed;
                _speed = _speed <= 0 ? rate : SpeedSmoothing * rate + (1 - SpeedSmoothing) * _speed;
                _lastSampleBytes = now;
                _lastSampleTicks = ticks;
                notify = true;
            }
        }
        if (notify) Changed?.Invoke(this);
    }

    internal void ReportVerification(long done, long total)
    {
        var notify = done - Interlocked.Read(ref _verifyDone) > total / 200 || done == total;
        Interlocked.Exchange(ref _verifyDone, done);
        Interlocked.Exchange(ref _verifyTotal, total);
        if (notify) Changed?.Invoke(this);
    }
}
