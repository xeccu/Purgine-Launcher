using System.IO.Compression;
using Purgine.EpicNet;
using Purgine.Infrastructure;
using Purgine.Library;

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
    /// <summary>Sin nube de Epic para este juego: las partidas solo están en este equipo (con copias en Purgine).</summary>
    LocalOnly,
    /// <summary>El juego guarda en la nube por su cuenta (EOS) o no se conoce su carpeta.</summary>
    NotManaged,
    Error,
}

public sealed record CloudSaveStatus(CloudSaveState State, string Text, string? SavePath = null, SavePathSource? Source = null);

/// <summary>
/// Partidas en la nube de los juegos de Epic sin Epic Games Launcher (su misma nube, la de «savesync»):
/// <list type="bullet">
///   <item>Antes de jugar: si la nube tiene una partida más nueva (más de 60 s, como Epic), se hace copia de lo
///         local en Purgine y se descarga encima (los archivos que la nube no tiene se conservan).</item>
///   <item>Al cerrar el juego: copia local y, si lo local es más nuevo, se empaqueta y se sube como una partida
///         nueva (las anteriores siguen en la nube).</item>
/// </list>
/// Los juegos sin nube de Epic (o con carpeta elegida a mano) se quedan en «Guardado local», con copias
/// en <c>save-backups</c>. Nada de esto bloquea la interfaz.
/// </summary>
public sealed class CloudSaveManager
{
    private const int MaxBackupsPerGame = 10;
    private const long MaxBackupBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan SameAgeTolerance = TimeSpan.FromSeconds(60);

    private readonly EpicAccountService _epic;
    private readonly SavePathResolver _paths;
    private readonly string _backupDir;
    private readonly Dictionary<string, CloudSaveStatus> _status = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Cambió el estado de un juego (id de biblioteca). Se dispara desde hilos de fondo.</summary>
    public event Action<string, CloudSaveStatus>? StatusChanged;

    public CloudSaveManager(EpicAccountService epic, SavePathResolver paths, string backupDir)
    {
        _epic = epic;
        _paths = paths;
        _backupDir = backupDir;
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

    /// <summary>Comprueba el estado (para la ficha) sin transferir nada.</summary>
    public async Task RefreshStatusAsync(GameEntry game, CancellationToken ct)
    {
        try
        {
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
            var remote = await LatestRemoteAsync(game, ct).ConfigureAwait(false);
            var local = LatestLocalWrite(path.Path);
            Set(game, Compare(local, remote?.Time) switch
            {
                Freshness.None => new CloudSaveStatus(CloudSaveState.Synced, "Sin partidas todavía (ni aquí ni en la nube).", path.Path, path.Source),
                Freshness.Same => new CloudSaveStatus(CloudSaveState.Synced, $"Nube sincronizada · {Ago(remote!.Value.Time)}", path.Path, path.Source),
                Freshness.LocalNewer => new CloudSaveStatus(CloudSaveState.LocalOnly, "Hay partidas locales sin subir (se suben al cerrar el juego).", path.Path, path.Source),
                _ => new CloudSaveStatus(CloudSaveState.Synced, "La nube tiene una partida más nueva: se descargará antes de jugar.", path.Path, path.Source),
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Nube de {game.Name}: {ex.Message}");
            Set(game, new CloudSaveStatus(CloudSaveState.Error, "No se pudo consultar la nube (¿sin conexión?)."));
        }
    }

    private static string LocalOnlyText(SavePathInfo path) => path.Source switch
    {
        SavePathSource.Detected => "Guardado local (carpeta deducida: compruébala) · copias en Purgine al cerrar.",
        _ => "Guardado local · copias de seguridad en Purgine al cerrar el juego.",
    };

    /// <summary>
    /// Antes de lanzar: baja la partida de la nube si es más nueva que la local (con copia previa de lo local).
    /// Nunca lanza excepciones: si algo falla, el juego arranca con lo que hay en disco.
    /// </summary>
    public async Task PreLaunchAsync(GameEntry game, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var (path, info, usesCloud) = await ResolveAsync(game, timeout.Token).ConfigureAwait(false);
            if (path is null || !usesCloud) return;

            Set(game, new CloudSaveStatus(CloudSaveState.Checking, "Comprobando la nube…", path.Path, path.Source));
            var files = await _epic.GetCloudSaveFilesAsync(game.AppId, timeout.Token).ConfigureAwait(false) ?? [];
            var latest = LatestManifest(files);
            var local = LatestLocalWrite(path.Path);
            if (Compare(local, latest?.Time) != Freshness.RemoteNewer)
            {
                Set(game, new CloudSaveStatus(CloudSaveState.Synced,
                    latest is null ? "Nube vacía: la primera partida se subirá al cerrar el juego." : "Nube sincronizada.", path.Path, path.Source));
                return;
            }

            Set(game, new CloudSaveStatus(CloudSaveState.Downloading, "Descargando partidas de la nube…", path.Path, path.Source));
            Backup(game, path.Path, "antes-de-descargar");
            var count = await DownloadAsync(files, latest!.Value, path.Path, timeout.Token).ConfigureAwait(false);
            Log.Info($"Nube de Epic: {game.Name} ← {latest.Value.Key} ({count} archivos) en {path.Path}.");
            Set(game, new CloudSaveStatus(CloudSaveState.Synced, $"Nube sincronizada · descargada {Ago(latest.Value.Time)}", path.Path, path.Source));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Nube de {game.Name} (antes de jugar): {ex.Message}");
            Set(game, new CloudSaveStatus(CloudSaveState.Error, "No se pudo comprobar la nube antes de jugar: se usaron las partidas locales."));
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Al cerrar el juego: copia local y, si lo local es más nuevo que la nube, subida. Devuelve true si se subió.
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

            var include = EpicSavePackager.ParseList(info!.CloudIncludeList);
            var exclude = EpicSavePackager.ParseList(info.CloudExcludeList);
            var localFiles = EpicSavePackager.EnumerateSaveFiles(path.Path, include, exclude);
            if (localFiles.Count == 0)
            {
                Set(game, new CloudSaveStatus(CloudSaveState.Synced, "Sin partidas locales que subir.", path.Path, path.Source));
                return false;
            }

            var files = await _epic.GetCloudSaveFilesAsync(game.AppId, ct).ConfigureAwait(false) ?? [];
            var latest = LatestManifest(files);
            var newest = localFiles.Max(f => File.GetLastWriteTimeUtc(f.Full));
            if (Compare(newest, latest?.Time) != Freshness.LocalNewer)
            {
                Set(game, new CloudSaveStatus(CloudSaveState.Synced, "Nube sincronizada.", path.Path, path.Source));
                return false;
            }

            Set(game, new CloudSaveStatus(CloudSaveState.Uploading, "Subiendo partidas a la nube…", path.Path, path.Source));
            var package = await Task.Run(() => EpicSavePackager.Package(path.Path, game.AppId, _epic.AccountId!,
                info.CloudSaveFolder!, include, exclude, newest), ct).ConfigureAwait(false);
            await UploadAsync(game.AppId, package, ct).ConfigureAwait(false);
            Log.Info($"Nube de Epic: {game.Name} → {package.ManifestKey} ({package.FileCount} archivos, {Formatting.Bytes(package.TotalBytes)}).");
            Set(game, new CloudSaveStatus(CloudSaveState.Synced, "Nube sincronizada · subida ahora mismo", path.Path, path.Source));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Nube de {game.Name} (al cerrar): {ex.Message}");
            Set(game, new CloudSaveStatus(CloudSaveState.Error, "No se pudieron subir las partidas (quedan en este equipo y en la copia de Purgine)."));
            return false;
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------------------------

    private readonly record struct RemoteSave(string Key, DateTime Time);

    private async Task<RemoteSave?> LatestRemoteAsync(GameEntry game, CancellationToken ct) =>
        LatestManifest(await _epic.GetCloudSaveFilesAsync(game.AppId, ct).ConfigureAwait(false) ?? []);

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

    /// <summary>Copia comprimida de la carpeta de partidas en <c>save-backups</c> (se guardan las 10 últimas).</summary>
    private void Backup(GameEntry game, string folder, string reason)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0) return;
            if (files.Sum(f => new FileInfo(f).Length) > MaxBackupBytes)
            {
                Log.Warn($"Partidas de {game.Name}: más de 2 GB, sin copia de seguridad.");
                return;
            }
            var dir = Path.Combine(_backupDir, SafeName(game.Id));
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.zip");
            ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
            foreach (var old in Directory.GetFiles(dir, "*.zip").OrderByDescending(f => f).Skip(MaxBackupsPerGame))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Partidas de {game.Name}: no se pudo hacer la copia de seguridad ({ex.Message}).");
        }
    }

    private static string Ago(DateTime utc) => Formatting.RelativeDate(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    public string BackupFolderFor(GameEntry game) => Path.Combine(_backupDir, SafeName(game.Id));

    private static string SafeName(string id) => string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
}
