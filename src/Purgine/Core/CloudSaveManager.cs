using System.IO.Compression;
using Purgine.EpicNet;
using Purgine.Infrastructure;
using Purgine.Library;
using Purgine.SteamNet;

namespace Purgine.Core;

/// <summary>Estado de las partidas de un juego respecto a la nube.</summary>
public enum CloudSaveState
{
    /// <summary>Aún no se ha comprobado.</summary>
    Unknown,
    Checking,
    Downloading,
    Uploading,
    /// <summary>Lo local y la nube coinciden (o se acaba de subir/bajar).</summary>
    Synced,
    /// <summary>Hay cambios locales que aún no están en la nube (o el juego no tiene nube).</summary>
    LocalOnly,
    /// <summary>La nube tiene una partida más nueva: se descargará al jugar.</summary>
    RemoteNewer,
    /// <summary>Cambiaron la partida local y la de la nube desde la última sincronización.</summary>
    Conflict,
    /// <summary>El juego guarda en la nube por su cuenta (EOS / servidores propios) o no se conoce su carpeta.</summary>
    NotManaged,
    Error,
}

public sealed record CloudSaveStatus(CloudSaveState State, string Text, string? SavePath = null, SavePathSource? Source = null);

/// <summary>Elección ante un conflicto (como en el diálogo de Steam Cloud).</summary>
public enum CloudConflictChoice
{
    /// <summary>Usar la partida de la nube (se descarga encima de la local, con copia de seguridad).</summary>
    UseCloud,
    /// <summary>Mantener la partida de este equipo (se sube y pasa a ser la de la nube).</summary>
    KeepLocal,
    /// <summary>Steam: seguir y elegir en la ventana de Steam.</summary>
    Continue,
    /// <summary>No jugar ahora / no hacer nada.</summary>
    Cancel,
}

/// <summary>Datos para el diálogo de conflicto.</summary>
public sealed record CloudConflictInfo(
    GameEntry Game,
    bool AtExit,
    DateTime? LocalTimeUtc,
    int LocalFiles,
    DateTime? CloudTimeUtc,
    string? CloudMachine,
    bool SteamDecides);

/// <summary>
/// Partidas en la nube con la misma lógica que Steam Cloud:
/// <list type="bullet">
///   <item>Tras cada sincronización se anota el estado (qué partida de la nube y cómo estaban los archivos
///         locales). Así se distingue «cambió lo local», «cambió la nube» y «cambiaron los dos» (conflicto),
///         en vez de comparar solo fechas.</item>
///   <item><b>Epic</b> (Purgine sincroniza con la nube de Epic Games Launcher): solo la nube cambió → se descarga
///         antes de jugar; solo lo local → se sube al cerrar; los dos → diálogo para elegir, igual que Steam.</item>
///   <item><b>Steam</b> (sincroniza el propio cliente de Steam al lanzar): Purgine lee su registro
///         (<c>remotecache.vdf</c>) y la nube (<see cref="SteamCloudInspector"/>) para el indicador; ante un
///         conflicto avisa con los detalles y hace copia de lo local; la elección se hace en la ventana de Steam,
///         que es quien mantiene su registro coherente.</item>
/// </list>
/// Antes de bajar algo encima, y al cerrar cada juego, se guarda una copia comprimida en <c>save-backups</c>.
/// </summary>
public sealed class CloudSaveManager
{
    private const int MaxBackupsPerGame = 10;
    private const long MaxBackupBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan SameAgeTolerance = TimeSpan.FromSeconds(60);

    private readonly EpicAccountService _epic;
    private readonly SavePathResolver _paths;
    private readonly string _backupDir;
    private readonly string _syncStateFile;
    private readonly SteamSession? _steamSession;
    private readonly Func<string?>? _steamPath;
    private readonly Dictionary<string, CloudSaveStatus> _status = new();
    private readonly Dictionary<string, EpicSyncRecord> _sync;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Cambió el estado de un juego (id de biblioteca). Se dispara desde hilos de fondo.</summary>
    public event Action<string, CloudSaveStatus>? StatusChanged;

    /// <summary>Pregunta al usuario ante un conflicto (la pone la ventana principal). Sin ella: no se toca nada.</summary>
    public Func<CloudConflictInfo, Task<CloudConflictChoice>>? ResolveConflict { get; set; }

    public CloudSaveManager(EpicAccountService epic, SavePathResolver paths, string backupDir, string syncStateFile,
        SteamSession? steamSession = null, Func<string?>? steamPath = null)
    {
        _epic = epic;
        _paths = paths;
        _backupDir = backupDir;
        _syncStateFile = syncStateFile;
        _steamSession = steamSession;
        _steamPath = steamPath;
        _sync = JsonFileStore.Load<Dictionary<string, EpicSyncRecord>>(syncStateFile);
    }

    public SavePathResolver Paths => _paths;

    public CloudSaveStatus GetStatus(string gameId)
    {
        lock (_status) return _status.GetValueOrDefault(gameId) ?? new CloudSaveStatus(CloudSaveState.Unknown, "Sin comprobar");
    }

    private void Set(GameEntry game, CloudSaveStatus status)
    {
        lock (_status) _status[game.Id] = status;
        StatusChanged?.Invoke(game.Id, status);
    }

    // =====================================================================================
    // Estado (indicador de la ficha)
    // =====================================================================================

    /// <summary>Comprueba el estado (para la ficha) sin transferir nada.</summary>
    public async Task RefreshStatusAsync(GameEntry game, CancellationToken ct)
    {
        try
        {
            if (game.Platform == GamePlatform.Steam) { await RefreshSteamAsync(game, ct).ConfigureAwait(false); return; }

            var (path, info, usesCloud) = await ResolveAsync(game, ct).ConfigureAwait(false);
            if (path is null)
            {
                Set(game, new CloudSaveStatus(CloudSaveState.NotManaged,
                    game.Platform == GamePlatform.Epic && _epic.HasSession
                        ? "Este juego guarda en la nube por su cuenta (o no se conoce su carpeta de partidas)."
                        : "Carpeta de partidas desconocida."));
                return;
            }
            if (!usesCloud)
            {
                Set(game, new CloudSaveStatus(CloudSaveState.LocalOnly, LocalOnlyText(path), path.Path, path.Source));
                return;
            }
            Set(game, new CloudSaveStatus(CloudSaveState.Checking, "Comprobando la nube…", path.Path, path.Source));
            var state = await EpicStateAsync(game, path, info!, ct).ConfigureAwait(false);
            Set(game, state.Kind switch
            {
                CloudSyncKind.NoCloud => new CloudSaveStatus(CloudSaveState.Synced, "Sin partidas todavía (ni aquí ni en la nube).", path.Path, path.Source),
                CloudSyncKind.Synced => new CloudSaveStatus(CloudSaveState.Synced, $"Nube sincronizada · {Ago(state.RemoteTime)}", path.Path, path.Source),
                CloudSyncKind.LocalNewer => new CloudSaveStatus(CloudSaveState.LocalOnly, "Hay cambios locales sin subir: se suben al cerrar el juego.", path.Path, path.Source),
                CloudSyncKind.RemoteNewer => new CloudSaveStatus(CloudSaveState.RemoteNewer, $"La nube tiene una partida más nueva ({Ago(state.RemoteTime)}): se descargará antes de jugar.", path.Path, path.Source),
                _ => new CloudSaveStatus(CloudSaveState.Conflict, ConflictText(state.LocalTime, state.RemoteTime, null), path.Path, path.Source),
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Nube de {game.Name}: {ex.Message}");
            Set(game, new CloudSaveStatus(CloudSaveState.Error, "No se pudo consultar la nube (¿sin conexión?)."));
        }
    }

    private async Task RefreshSteamAsync(GameEntry game, CancellationToken ct)
    {
        var check = await CheckSteamAsync(game, ct).ConfigureAwait(false);
        if (check is null)
        {
            Set(game, new CloudSaveStatus(CloudSaveState.NotManaged, "Steam Cloud no disponible (sin carpeta de Steam o AppID no válido)."));
            return;
        }
        var from = check.RemoteMachine is { } m ? $" desde «{m}»" : string.Empty;
        Set(game, check.Kind switch
        {
            CloudSyncKind.NoCloud => new CloudSaveStatus(CloudSaveState.NotManaged, "Este juego no usa Steam Cloud (o aún no tiene partidas).", check.SaveFolder),
            CloudSyncKind.Synced => new CloudSaveStatus(CloudSaveState.Synced, $"Steam Cloud sincronizado · {Ago(check.RemoteTimeUtc)}", check.SaveFolder),
            CloudSyncKind.LocalNewer => new CloudSaveStatus(CloudSaveState.LocalOnly, $"{check.LocalChanged} archivos cambiados en este equipo: Steam los subirá.", check.SaveFolder),
            CloudSyncKind.RemoteNewer => new CloudSaveStatus(CloudSaveState.RemoteNewer, $"La nube tiene una partida más nueva{from} ({Ago(check.RemoteTimeUtc)}): Steam la descargará al jugar.", check.SaveFolder),
            CloudSyncKind.Conflict => new CloudSaveStatus(CloudSaveState.Conflict, ConflictText(check.LocalTimeUtc, check.RemoteTimeUtc, check.RemoteMachine), check.SaveFolder),
            _ => new CloudSaveStatus(CloudSaveState.Error, "No se pudo consultar Steam Cloud (¿sin conexión o sin sesión de Steam?).", check.SaveFolder),
        });
    }

    private static string ConflictText(DateTime? local, DateTime? remote, string? machine) =>
        $"Conflicto: la partida de este equipo ({Ago(local)}) y la de la nube ({Ago(remote)}{(machine is null ? string.Empty : $", «{machine}»")}) " +
        "han cambiado las dos. Al jugar podrás elegir cuál conservar.";

    // =====================================================================================
    // Steam: comprobación antes de jugar (sin tocar la nube; la sincroniza el cliente de Steam)
    // =====================================================================================

    public async Task<CloudSyncCheck?> CheckSteamAsync(GameEntry game, CancellationToken ct)
    {
        if (_steamSession is null || !uint.TryParse(game.AppId, out var appId)) return null;
        var userDir = SteamPlaytimeReader.FindUserDir(_steamPath?.Invoke());
        if (userDir is null) return null;
        return await SteamCloudInspector.CheckAsync(_steamSession, userDir, appId, game.InstallDir, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Antes de lanzar un juego de Steam: si la nube cambió (va a bajar) o hay conflicto, copia de lo local; ante un
    /// conflicto, aviso con los detalles. Devuelve false si el usuario prefiere no jugar ahora.
    /// </summary>
    public async Task<bool> SteamPreLaunchAsync(GameEntry game, CancellationToken ct)
    {
        CloudSyncCheck? check;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            check = await CheckSteamAsync(game, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Steam Cloud de {game.Name}: {ex.Message}");
            return true;
        }
        if (check is null) return true;
        await RefreshSteamAsyncFrom(game, check).ConfigureAwait(false);

        if (check.Kind is CloudSyncKind.RemoteNewer or CloudSyncKind.Conflict && check.LocalFiles.Count > 0)
            BackupFiles(game, check.LocalFiles, check.Kind == CloudSyncKind.Conflict ? "conflicto" : "antes-de-descargar");

        if (check.Kind != CloudSyncKind.Conflict || ResolveConflict is null) return true;
        var choice = await ResolveConflict(new CloudConflictInfo(game, false, check.LocalTimeUtc, check.LocalFiles.Count,
            check.RemoteTimeUtc, check.RemoteMachine, SteamDecides: true)).ConfigureAwait(false);
        return choice != CloudConflictChoice.Cancel;
    }

    private Task RefreshSteamAsyncFrom(GameEntry game, CloudSyncCheck check)
    {
        var from = check.RemoteMachine is { } m ? $" desde «{m}»" : string.Empty;
        if (check.Kind == CloudSyncKind.Conflict)
            Set(game, new CloudSaveStatus(CloudSaveState.Conflict, ConflictText(check.LocalTimeUtc, check.RemoteTimeUtc, check.RemoteMachine), check.SaveFolder));
        else if (check.Kind == CloudSyncKind.RemoteNewer)
            Set(game, new CloudSaveStatus(CloudSaveState.RemoteNewer, $"La nube tiene una partida más nueva{from}: Steam la descargará al jugar.", check.SaveFolder));
        return Task.CompletedTask;
    }

    // =====================================================================================
    // Epic: sincronización completa
    // =====================================================================================

    /// <summary>Carpeta de partidas, configuración de nube del catálogo y si la nube de Epic se usa para este juego.</summary>
    private async Task<(SavePathInfo? Path, EpicCatalogInfo? Info, bool UsesCloud)> ResolveAsync(GameEntry game, CancellationToken ct)
    {
        EpicCatalogInfo? info = null;
        if (game.Platform == GamePlatform.Epic && game.EpicNamespace is { } ns && game.EpicCatalogItemId is { } item && _epic.HasSession)
            info = await _epic.GetCatalogInfoAsync(ns, item, ct).ConfigureAwait(false);
        var path = _paths.Resolve(game, info?.CloudSaveFolder, _epic.AccountId);
        // Con carpeta manual también se sincroniza si el juego tiene nube de Epic: el usuario solo corrige la ruta.
        var usesCloud = info?.SupportsCloudSaves == true && path is not null && path.Source != SavePathSource.Detected;
        return (path, info, usesCloud);
    }

    private sealed record EpicState(CloudSyncKind Kind, List<EpicCloudFile> Files, RemoteSave? Latest, DateTime? LocalTime, DateTime? RemoteTime,
        Dictionary<string, string> Snapshot, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude);

    /// <summary>
    /// Estado al estilo Steam: ¿cambió lo local desde la última sincronización (archivos distintos de los anotados)?
    /// ¿cambió la nube (otra partida distinta de la anotada)? Sin registro previo se decide por fechas y, si
    /// hay partidas en los dos lados que no coinciden, se considera conflicto (Steam hace lo mismo).
    /// </summary>
    private async Task<EpicState> EpicStateAsync(GameEntry game, SavePathInfo path, EpicCatalogInfo info, CancellationToken ct)
    {
        var include = EpicSavePackager.ParseList(info.CloudIncludeList);
        var exclude = EpicSavePackager.ParseList(info.CloudExcludeList);
        var files = await _epic.GetCloudSaveFilesAsync(game.AppId, ct).ConfigureAwait(false) ?? [];
        var latest = LatestManifest(files);
        var snapshot = Snapshot(path.Path, include, exclude);
        var localTime = snapshot.Count == 0 ? (DateTime?)null : LatestLocalWrite(path.Path);
        DateTime? remoteTime = latest?.Time;

        CloudSyncKind kind;
        EpicSyncRecord? record;
        lock (_sync) record = _sync.GetValueOrDefault(game.Id);
        if (snapshot.Count == 0 && latest is null) kind = CloudSyncKind.NoCloud;
        else if (record is null)
        {
            if (latest is null) kind = CloudSyncKind.LocalNewer;
            else if (snapshot.Count == 0) kind = CloudSyncKind.RemoteNewer;
            else kind = Compare(localTime, remoteTime) == Freshness.Same ? CloudSyncKind.Synced : CloudSyncKind.Conflict;
        }
        else
        {
            var localChanged = !SameSnapshot(snapshot, record.Files);
            var remoteChanged = latest is not null && latest.Value.Key != record.ManifestKey;
            kind = (localChanged, remoteChanged) switch
            {
                (true, true) => CloudSyncKind.Conflict,
                (false, true) => CloudSyncKind.RemoteNewer,
                (true, false) => CloudSyncKind.LocalNewer,
                _ => CloudSyncKind.Synced,
            };
        }
        return new EpicState(kind, files, latest, localTime, remoteTime, snapshot, include, exclude);
    }

    /// <summary>
    /// Antes de lanzar (Epic): baja la partida si solo cambió la nube; ante un conflicto pregunta. Devuelve false si
    /// el usuario cancela el lanzamiento. Si algo falla, el juego arranca con lo que hay en disco.
    /// </summary>
    public async Task<bool> PreLaunchAsync(GameEntry game, CancellationToken ct)
    {
        if (game.Platform == GamePlatform.Steam) return await SteamPreLaunchAsync(game, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var (path, info, usesCloud) = await ResolveAsync(game, timeout.Token).ConfigureAwait(false);
            if (path is null || !usesCloud) return true;

            Set(game, new CloudSaveStatus(CloudSaveState.Checking, "Comprobando la nube…", path.Path, path.Source));
            var state = await EpicStateAsync(game, path, info!, timeout.Token).ConfigureAwait(false);
            switch (state.Kind)
            {
                case CloudSyncKind.RemoteNewer:
                    await DownloadLatestAsync(game, path, state, "antes-de-descargar", timeout.Token).ConfigureAwait(false);
                    return true;
                case CloudSyncKind.Conflict:
                {
                    var choice = await AskAsync(game, state, atExit: false).ConfigureAwait(false);
                    if (choice == CloudConflictChoice.Cancel)
                    {
                        Set(game, new CloudSaveStatus(CloudSaveState.Conflict, ConflictText(state.LocalTime, state.RemoteTime, null), path.Path, path.Source));
                        return false;
                    }
                    if (choice == CloudConflictChoice.UseCloud)
                        await DownloadLatestAsync(game, path, state, "conflicto-antes-de-usar-la-nube", ct).ConfigureAwait(false);
                    else
                        await UploadLocalAsync(game, path, info!, state, ct).ConfigureAwait(false);
                    return true;
                }
                case CloudSyncKind.Synced:
                    Remember(game, state.Latest?.Key, state.Snapshot);
                    Set(game, new CloudSaveStatus(CloudSaveState.Synced, "Nube sincronizada.", path.Path, path.Source));
                    return true;
                default:
                    Set(game, new CloudSaveStatus(state.Kind == CloudSyncKind.LocalNewer ? CloudSaveState.LocalOnly : CloudSaveState.Synced,
                        state.Kind == CloudSyncKind.LocalNewer ? "Cambios locales sin subir: se suben al cerrar el juego." : "Sin partidas todavía.",
                        path.Path, path.Source));
                    return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Nube de {game.Name} (antes de jugar): {ex.Message}");
            Set(game, new CloudSaveStatus(CloudSaveState.Error, "No se pudo comprobar la nube antes de jugar: se usaron las partidas locales."));
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Al cerrar el juego (Epic): copia local y, si cambió lo local, subida. Si mientras tanto otro equipo subió una
    /// partida (cambiaron los dos), se pregunta. Devuelve true si se subió.
    /// </summary>
    public async Task<bool> PostExitAsync(GameEntry game, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (path, info, usesCloud) = await ResolveAsync(game, ct).ConfigureAwait(false);
            if (path is null) return false;
            if (Directory.Exists(path.Path)) Backup(game, path.Path, "al-cerrar");
            if (!usesCloud)
            {
                Set(game, new CloudSaveStatus(CloudSaveState.LocalOnly, LocalOnlyText(path), path.Path, path.Source));
                return false;
            }

            var state = await EpicStateAsync(game, path, info!, ct).ConfigureAwait(false);
            switch (state.Kind)
            {
                case CloudSyncKind.NoCloud:
                    Set(game, new CloudSaveStatus(CloudSaveState.Synced,
                        "El juego aún no ha guardado ninguna partida en su carpeta (nada que subir).", path.Path, path.Source));
                    return false;
                case CloudSyncKind.LocalNewer:
                    await UploadLocalAsync(game, path, info!, state, ct).ConfigureAwait(false);
                    return true;
                case CloudSyncKind.Conflict:
                {
                    var choice = await AskAsync(game, state, atExit: true).ConfigureAwait(false);
                    if (choice == CloudConflictChoice.KeepLocal)
                    {
                        await UploadLocalAsync(game, path, info!, state, ct).ConfigureAwait(false);
                        return true;
                    }
                    if (choice == CloudConflictChoice.UseCloud)
                        await DownloadLatestAsync(game, path, state, "conflicto-antes-de-usar-la-nube", ct).ConfigureAwait(false);
                    else
                        Set(game, new CloudSaveStatus(CloudSaveState.Conflict, ConflictText(state.LocalTime, state.RemoteTime, null), path.Path, path.Source));
                    return false;
                }
                case CloudSyncKind.RemoteNewer:
                    // No jugaste nada nuevo y otro equipo subió: se bajará la próxima vez antes de jugar.
                    Set(game, new CloudSaveStatus(CloudSaveState.RemoteNewer, "La nube tiene una partida más nueva: se descargará antes de jugar.", path.Path, path.Source));
                    return false;
                default:
                    Remember(game, state.Latest?.Key, state.Snapshot);
                    Set(game, new CloudSaveStatus(CloudSaveState.Synced, "Nube sincronizada.", path.Path, path.Source));
                    return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Nube de {game.Name} (al cerrar): {ex.Message}");
            Set(game, new CloudSaveStatus(CloudSaveState.Error, "No se pudieron subir las partidas (quedan en este equipo y en la copia de Purgine)."));
            return false;
        }
        finally { _gate.Release(); }
    }

    private async Task<CloudConflictChoice> AskAsync(GameEntry game, EpicState state, bool atExit)
    {
        if (ResolveConflict is null) return CloudConflictChoice.Cancel;
        return await ResolveConflict(new CloudConflictInfo(game, atExit, state.LocalTime, state.Snapshot.Count,
            state.RemoteTime, null, SteamDecides: false)).ConfigureAwait(false);
    }

    private async Task DownloadLatestAsync(GameEntry game, SavePathInfo path, EpicState state, string reason, CancellationToken ct)
    {
        Set(game, new CloudSaveStatus(CloudSaveState.Downloading, "Descargando partidas de la nube…", path.Path, path.Source));
        Backup(game, path.Path, reason);
        var count = await DownloadAsync(state.Files, state.Latest!.Value, path.Path, ct).ConfigureAwait(false);
        Remember(game, state.Latest.Value.Key, Snapshot(path.Path, state.Include, state.Exclude));
        Log.Info($"Nube de Epic: {game.Name} ← {state.Latest.Value.Key} ({count} archivos) en {path.Path}.");
        Set(game, new CloudSaveStatus(CloudSaveState.Synced, $"Nube sincronizada · descargada la partida de {Ago(state.Latest.Value.Time)}", path.Path, path.Source));
    }

    private async Task UploadLocalAsync(GameEntry game, SavePathInfo path, EpicCatalogInfo info, EpicState state, CancellationToken ct)
    {
        var localFiles = EpicSavePackager.EnumerateSaveFiles(path.Path, state.Include, state.Exclude);
        if (localFiles.Count == 0) return;
        Set(game, new CloudSaveStatus(CloudSaveState.Uploading, "Subiendo partidas a la nube…", path.Path, path.Source));
        var newest = localFiles.Max(f => File.GetLastWriteTimeUtc(f.Full));
        // El nombre de la partida no puede repetir uno que ya existe (pasaría si el reloj del otro equipo va por delante).
        if (state.Latest is { } latest && newest <= latest.Time) newest = latest.Time.AddSeconds(1);
        var package = await Task.Run(() => EpicSavePackager.Package(path.Path, game.AppId, _epic.AccountId!,
            info.CloudSaveFolder!, state.Include, state.Exclude, newest), ct).ConfigureAwait(false);
        await UploadAsync(game.AppId, package, ct).ConfigureAwait(false);
        Remember(game, package.ManifestKey, Snapshot(path.Path, state.Include, state.Exclude));
        Log.Info($"Nube de Epic: {game.Name} → {package.ManifestKey} ({package.FileCount} archivos, {Formatting.Bytes(package.TotalBytes)}).");
        Set(game, new CloudSaveStatus(CloudSaveState.Synced, "Nube sincronizada · subida ahora mismo", path.Path, path.Source));
    }

    private static string LocalOnlyText(SavePathInfo path) => path.Source switch
    {
        SavePathSource.Detected => "Guardado local (carpeta deducida: compruébala) · copias en Purgine al cerrar.",
        _ => "Guardado local · copias de seguridad en Purgine al cerrar el juego.",
    };

    // ---------------------------------------------------------------------------------
    // Registro de sincronización (Epic)
    // ---------------------------------------------------------------------------------

    /// <summary>Lo que quedó sincronizado: la partida de la nube y los archivos locales (tamaño + fecha).</summary>
    public sealed class EpicSyncRecord
    {
        public string? ManifestKey { get; set; }
        public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private void Remember(GameEntry game, string? manifestKey, Dictionary<string, string> snapshot)
    {
        lock (_sync)
        {
            _sync[game.Id] = new EpicSyncRecord { ManifestKey = manifestKey, Files = snapshot };
            JsonFileStore.Save(_syncStateFile, _sync);
        }
    }

    private const long HashLimit = 64L * 1024 * 1024;

    /// <summary>
    /// Huella de la carpeta, como la de Steam: ruta relativa → «tamaño:SHA-1» (contenido exacto). Los archivos de
    /// más de 64 MB, raros en partidas, usan «tamaño:fecha» para no leerlos enteros cada vez.
    /// </summary>
    private static Dictionary<string, string> Snapshot(string folder, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (full, relative) in EpicSavePackager.EnumerateSaveFiles(folder, include, exclude))
            {
                var info = new FileInfo(full);
                if (info.Length > HashLimit)
                {
                    map[relative] = $"{info.Length}:t{new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()}";
                    continue;
                }
                using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                map[relative] = $"{info.Length}:{Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(fs))}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* carpeta inaccesible: vacía */ }
        return map;
    }

    private static bool SameSnapshot(Dictionary<string, string> now, Dictionary<string, string> then)
    {
        if (now.Count != then.Count) return false;
        foreach (var (k, v) in now)
            if (!then.TryGetValue(k, out var old) || !string.Equals(v, old, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // ---------------------------------------------------------------------------------

    private readonly record struct RemoteSave(string Key, DateTime Time);

    /// <summary>La partida más reciente: el manifiesto con la fecha (UTC) más alta en su nombre.</summary>
    private static RemoteSave? LatestManifest(IEnumerable<EpicCloudFile> files)
    {
        RemoteSave? best = null;
        foreach (var f in files)
        {
            if (!f.Key.StartsWith("manifests/", StringComparison.Ordinal) || !f.Key.EndsWith(".manifest", StringComparison.Ordinal)) continue;
            var name = Path.GetFileNameWithoutExtension(f.Key);
            if (!DateTime.TryParseExact(name, "yyyy.MM.dd-HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var time))
                continue;
            if (best is null || time > best.Value.Time) best = new RemoteSave(f.Key, time);
        }
        return best;
    }

    private enum Freshness { None, Same, LocalNewer, RemoteNewer }

    private static Freshness Compare(DateTime? localUtc, DateTime? remoteUtc)
    {
        if (localUtc is null && remoteUtc is null) return Freshness.None;
        if (remoteUtc is null) return Freshness.LocalNewer;
        if (localUtc is null) return Freshness.RemoteNewer;
        var diff = localUtc.Value - remoteUtc.Value;
        if (diff.Duration() < SameAgeTolerance) return Freshness.Same;
        return diff > TimeSpan.Zero ? Freshness.LocalNewer : Freshness.RemoteNewer;
    }

    private static DateTime? LatestLocalWrite(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            DateTime? latest = null;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (latest is null || t > latest) latest = t;
            }
            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Descarga una partida: manifiesto, sus fragmentos, y los archivos escritos en la carpeta con la fecha de la partida.</summary>
    private async Task<int> DownloadAsync(List<EpicCloudFile> files, RemoteSave save, string folder, CancellationToken ct)
    {
        var byKey = files.Where(f => f.ReadLink is not null).ToDictionary(f => f.Key, f => f.ReadLink!, StringComparer.Ordinal);
        if (!byKey.TryGetValue(save.Key, out var manifestLink)) throw new InvalidDataException("La nube no da acceso al manifiesto de la partida.");
        var manifest = EpicManifest.Parse(await _epic.DownloadSignedAsync(manifestLink, ct).ConfigureAwait(false));

        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var chunk in manifest.Chunks.Values)
        {
            var key = manifest.ChunkPath(chunk);
            if (!byKey.TryGetValue(key, out var link)) throw new InvalidDataException($"Falta un fragmento de la partida en la nube ({key}).");
            data[chunk.GuidHex] = EpicManifest.DecodeChunk(await _epic.DownloadSignedAsync(link, ct).ConfigureAwait(false));
        }

        // Todo en memoria y verificado antes de tocar el disco: una descarga a medias no deja partidas rotas.
        var output = new List<(string Path, byte[] Bytes)>();
        var root = Path.GetFullPath(folder);
        foreach (var file in manifest.Files)
        {
            var target = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!target.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Ruta no válida en la partida de la nube: {file.Path}");
            var bytes = new byte[file.Size];
            long offset = 0;
            foreach (var part in file.Parts)
            {
                Buffer.BlockCopy(data[part.ChunkGuidHex], (int)part.Offset, bytes, (int)offset, (int)part.Size);
                offset += part.Size;
            }
            if (!System.Security.Cryptography.SHA1.HashData(bytes).AsSpan().SequenceEqual(file.Sha1))
                throw new InvalidDataException($"La partida de la nube está dañada ({file.Path}).");
            output.Add((target, bytes));
        }
        var stamp = DateTime.SpecifyKind(save.Time, DateTimeKind.Utc);
        foreach (var (path, bytes) in output)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(path, stamp);
        }
        return output.Count;
    }

    /// <summary>Sube una partida: enlaces de escritura para todo y después fragmentos y, por último, el manifiesto.</summary>
    private async Task UploadAsync(string appName, EpicSavePackage package, CancellationToken ct)
    {
        var keys = package.Chunks.Keys.Append(package.ManifestKey).ToList();
        var links = await _epic.CreateCloudSaveFilesAsync(appName, keys, ct).ConfigureAwait(false);
        foreach (var (key, bytes) in package.Chunks)
        {
            if (!links.TryGetValue(key, out var link)) throw new InvalidDataException($"La nube no dio enlace para {key}.");
            await _epic.UploadSignedAsync(link, bytes, ct).ConfigureAwait(false);
        }
        if (!links.TryGetValue(package.ManifestKey, out var manifestLink)) throw new InvalidDataException("La nube no dio enlace para el manifiesto.");
        await _epic.UploadSignedAsync(manifestLink, package.Manifest, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------
    // Copias de seguridad
    // ---------------------------------------------------------------------------------

    /// <summary>Copia comprimida de la carpeta de partidas en <c>save-backups</c> (se guardan las 10 últimas).</summary>
    private void Backup(GameEntry game, string folder, string reason)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0) return;
            WriteBackup(game, reason, files, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Partidas de {game.Name}: no se pudo hacer la copia de seguridad ({ex.Message}).");
        }
    }

    /// <summary>Copia de una lista de archivos sueltos (Steam Cloud: pueden estar en varias raíces).</summary>
    private void BackupFiles(GameEntry game, IReadOnlyList<string> files, string reason)
    {
        try { WriteBackup(game, reason, files.Where(File.Exists).ToList(), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Partidas de {game.Name}: no se pudo hacer la copia de seguridad ({ex.Message}).");
        }
    }

    private void WriteBackup(GameEntry game, string reason, IReadOnlyList<string> files, string? baseFolder)
    {
        if (files.Count == 0) return;
        if (files.Sum(f => new FileInfo(f).Length) > MaxBackupBytes)
        {
            Log.Warn($"Partidas de {game.Name}: más de 2 GB, sin copia de seguridad.");
            return;
        }
        var dir = BackupFolderFor(game);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                var entry = baseFolder is not null
                    ? Path.GetRelativePath(baseFolder, file)
                    : file.Replace(':', '_').TrimStart('\\'); // ruta completa (varias raíces)
                zip.CreateEntryFromFile(file, entry.Replace('\\', '/'), CompressionLevel.Fastest);
            }
        }
        foreach (var old in Directory.GetFiles(dir, "*.zip").OrderByDescending(f => f).Skip(MaxBackupsPerGame))
            File.Delete(old);
    }

    private static string Ago(DateTime? utc) =>
        utc is null ? "sin fecha" : Formatting.RelativeDate(new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)));

    public string BackupFolderFor(GameEntry game) => Path.Combine(_backupDir, SafeName(game.Id));

    private static string SafeName(string id) => string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
}
