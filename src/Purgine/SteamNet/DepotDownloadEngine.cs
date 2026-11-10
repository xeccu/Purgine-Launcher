using System.Buffers;
using Purgine.Infrastructure;
using Microsoft.Win32.SafeHandles;
using SteamKit2;
using SteamKit2.CDN;

namespace Purgine.SteamNet;

/// <summary>Un depot elegido para descargar.</summary>

/// <summary>Resultado de una descarga terminada (lo necesario para escribir el appmanifest de Steam).</summary>
public sealed record DepotDownloadResult(
    string LibraryPath,
    string InstallDirName,
    uint BuildId,
    IReadOnlyList<(uint DepotId, ulong ManifestId, ulong Size, DepotManifest Manifest)> Depots);

/// <summary>
/// Motor de descarga de depots desde la CDN de Steam (patrón DepotDownloader), 100 % en C#:
/// <list type="number">
///   <item>Lee la info PICS de la app y elige los depots (Windows, 64 bits si hay, idioma, DLC propios).</item>
///   <item>Por depot: clave de descifrado, código de manifiesto y manifiesto desde la CDN.</item>
///   <item>Plan incremental: ficheros iguales al manifiesto instalado se saltan (hash); de los demás solo se
///         descargan los fragmentos (chunks) cuyo Adler32 no coincide con lo que ya hay en disco. Por eso
///         reanudar tras una pausa y actualizar solo bajan lo que falta o cambió.</item>
///   <item>Descarga en paralelo con reintentos y rotación de servidores, escribiendo cada fragmento en su
///         posición del fichero.</item>
/// </list>
/// El progreso se mide en bytes comprimidos transferidos (lo que realmente viaja por la red).
/// </summary>
public sealed class DepotDownloadEngine
{
    private const int MaxParallelChunks = 8;
    private const int MaxChunkAttempts = 6;

    private readonly SteamSession _session;
    private readonly SteamOwnershipService _ownership;

    public DepotDownloadEngine(SteamSession session, SteamOwnershipService ownership)
    {
        _session = session;
        _ownership = ownership;
    }

    /// <summary>Descarga o actualiza una app. <paramref name="job"/> recibe el progreso en vivo.</summary>
    public async Task<DepotDownloadResult> RunAsync(DownloadJob job, string steamPath, CancellationToken ct)
    {
        // ---------- 1. Información de la app ----------
        job.SetState(DownloadJobState.Preparing, "Consultando la información del juego en Steam…");
        var appInfo = (await _session.GetAppInfoAsync([job.AppId], ct).ConfigureAwait(false)).GetValueOrDefault(job.AppId)
                      ?? throw new InvalidOperationException("Steam no devolvió información de este juego.");
        var installDirName = appInfo["config"]["installdir"].Value ?? job.InstallDirName
                             ?? throw new InvalidOperationException("El juego no indica su carpeta de instalación.");
        var buildId = appInfo["depots"]["branches"]["public"]["buildid"].AsUnsignedInteger();
        var installPath = Path.Combine(job.LibraryPath, "steamapps", "common", installDirName);

        // DLC desactivados en Propiedades: sus depots no se descargan ni se actualizan.
        var disabledDlc = AppManifestWriter.ReadDisabledDlc(Path.Combine(job.LibraryPath, "steamapps", $"appmanifest_{job.AppId}.acf"));
        var selections = DepotSelector.Select(appInfo["depots"], DepotSelector.SteamLanguage(),
            dlc => _ownership.IsOwned(dlc) && !disabledDlc.Contains(dlc));
        if (selections.Count == 0) throw new InvalidOperationException("No hay depots para Windows que esta cuenta pueda descargar.");

        // Manifiestos instalados (para actualizar solo lo que cambia).
        var installed = ReadInstalledDepots(job.LibraryPath, job.AppId);

        // ---------- 2. Claves y manifiestos ----------
        var servers = await _session.GetCdnServersAsync(job.AppId).ConfigureAwait(false);
        if (servers.Count == 0) throw new InvalidOperationException("Steam no ofreció servidores de descarga.");
        using var cdn = _session.CreateCdnClient();

        var depots = new List<DepotWork>();
        foreach (var sel in selections)
        {
            ct.ThrowIfCancellationRequested();
            var key = await _session.GetDepotKeyAsync(sel.DepotId, job.AppId).ConfigureAwait(false);
            if (key is null)
            {
                Log.Warn($"Depot {sel.DepotId} de {job.AppId}: sin clave (no incluido en tu licencia); se omite.");
                continue;
            }

            job.SetState(DownloadJobState.Preparing, $"Descargando la lista de archivos (depot {sel.DepotId})…");
            var code = await _session.GetManifestRequestCodeAsync(sel.DepotId, job.AppId, sel.ManifestId, "public").ConfigureAwait(false);
            var manifest = await WithServerRetry(servers, ct, server =>
                cdn.DownloadManifestAsync(sel.DepotId, sel.ManifestId, code, server, key)).ConfigureAwait(false);
            if (manifest.FilenamesEncrypted) manifest.DecryptFilenames(key);

            DepotManifest? old = null;
            if (installed.TryGetValue(sel.DepotId, out var oldId) && oldId != sel.ManifestId)
                old = TryLoadSteamManifest(steamPath, sel.DepotId, oldId, key);
            else if (oldId == sel.ManifestId)
                old = manifest; // mismo manifiesto: basta verificar que los archivos están completos

            depots.Add(new DepotWork(sel.DepotId, sel.ManifestId, key, manifest, old));
        }
        if (depots.Count == 0) throw new InvalidOperationException("Tu cuenta no tiene acceso a los depots de este juego.");

        // ---------- 3. Plan incremental (verificación de lo que ya hay en disco) ----------
        job.SetState(DownloadJobState.Verifying, "Comprobando los archivos que ya están en disco…");
        var plan = await Task.Run(() => BuildPlan(installPath, depots, job, ct), ct).ConfigureAwait(false);

        var needed = plan.Files.Sum(f => (long)f.NewBytes);
        EnsureDiskSpace(job.LibraryPath, needed);

        // ---------- 4. Descarga ----------
        job.BeginTransfer(plan.Chunks.Sum(c => (long)c.Chunk.CompressedLength));
        job.SetState(DownloadJobState.Downloading, null);
        try
        {
            await Parallel.ForEachAsync(plan.Chunks, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelChunks, CancellationToken = ct },
                async (work, token) =>
                {
                    var buffer = ArrayPool<byte>.Shared.Rent((int)work.Chunk.UncompressedLength);
                    try
                    {
                        var written = await WithServerRetry(servers, token, server =>
                            cdn.DownloadDepotChunkAsync(work.Depot.DepotId, work.Chunk, server, buffer, work.Depot.Key)).ConfigureAwait(false);
                        work.File.Write(buffer.AsSpan(0, written), (long)work.Chunk.Offset);
                        job.AddTransferred(work.Chunk.CompressedLength);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }).ConfigureAwait(false);
        }
        finally
        {
            foreach (var f in plan.Files) f.Close();
        }

        // ---------- 5. Limpieza de archivos que ya no existen en la nueva versión ----------
        foreach (var removed in plan.RemovedFiles)
        {
            try { File.Delete(removed); } catch (Exception ex) { Log.Warn($"No se pudo borrar {removed}: {ex.Message}"); }
        }

        return new DepotDownloadResult(job.LibraryPath, installDirName, buildId,
            depots.Select(d => (d.DepotId, d.ManifestId, d.Manifest.TotalUncompressedSize, d.Manifest)).ToList());
    }

    // =====================================================================================
    // Plan incremental
    // =====================================================================================

    private sealed record DepotWork(uint DepotId, ulong ManifestId, byte[] Key, DepotManifest Manifest, DepotManifest? Old);

    private sealed record ChunkWork(DepotWork Depot, FileTarget File, DepotManifest.ChunkData Chunk);

    private sealed record Plan(List<FileTarget> Files, List<ChunkWork> Chunks, List<string> RemovedFiles);

    private static Plan BuildPlan(string installPath, List<DepotWork> depots, DownloadJob job, CancellationToken ct)
    {
        var files = new List<FileTarget>();
        var chunks = new List<ChunkWork>();
        var removed = new List<string>();
        Directory.CreateDirectory(installPath);

        long totalToCheck = depots.Sum(d => (long)d.Manifest.TotalUncompressedSize);
        long checkedBytes = 0;

        foreach (var depot in depots)
        {
            var oldFiles = depot.Old?.Files?.ToDictionary(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                           ?? new Dictionary<string, DepotManifest.FileData>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in depot.Manifest.Files ?? [])
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(installPath, file.FileName.Replace('/', Path.DirectorySeparatorChar));

                if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }
                if (file.Flags.HasFlag(EDepotFileFlag.Symlink)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var exists = File.Exists(path);
                var length = exists ? new FileInfo(path).Length : -1;

                // Igual que en el manifiesto instalado y con su tamaño: no se toca.
                if (exists && length == (long)file.TotalSize && oldFiles.TryGetValue(file.FileName, out var oldFile) &&
                    oldFile.FileHash.AsSpan().SequenceEqual(file.FileHash) && !ReferenceEquals(depot.Old, depot.Manifest))
                {
                    checkedBytes += (long)file.TotalSize;
                    continue;
                }

                var target = new FileTarget(path, file.TotalSize);
                var missing = exists
                    ? MissingChunks(path, length, file.Chunks, ct)
                    : file.Chunks;

                checkedBytes += (long)file.TotalSize;
                job.ReportVerification(checkedBytes, totalToCheck);

                if (missing.Count == 0)
                {
                    if (length != (long)file.TotalSize) target.Resize();
                    continue;
                }

                target.Pending = missing.Count;
                target.NewBytes = (ulong)missing.Sum(c => (long)c.UncompressedLength);
                files.Add(target);
                chunks.AddRange(missing.Select(c => new ChunkWork(depot, target, c)));
            }

            // Ficheros que estaban en la versión anterior y ya no existen.
            if (depot.Old is not null && !ReferenceEquals(depot.Old, depot.Manifest))
            {
                var current = new HashSet<string>((depot.Manifest.Files ?? []).Select(f => f.FileName), StringComparer.OrdinalIgnoreCase);
                removed.AddRange(oldFiles.Keys.Where(k => !current.Contains(k))
                    .Select(k => Path.Combine(installPath, k.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(File.Exists));
            }
        }

        // Fragmentos ordenados por fichero y posición: escrituras más secuenciales y pocos ficheros abiertos a la vez.
        chunks.Sort((a, b) =>
        {
            var byFile = string.CompareOrdinal(a.File.Path, b.File.Path);
            return byFile != 0 ? byFile : a.Chunk.Offset.CompareTo(b.Chunk.Offset);
        });
        return new Plan(files, chunks, removed);
    }

    /// <summary>Fragmentos de un fichero existente cuyo contenido no coincide (Adler32) con el manifiesto.</summary>
    private static List<DepotManifest.ChunkData> MissingChunks(string path, long length, List<DepotManifest.ChunkData> chunks, CancellationToken ct)
    {
        var missing = new List<DepotManifest.ChunkData>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            foreach (var chunk in chunks.OrderBy(c => c.Offset))
            {
                ct.ThrowIfCancellationRequested();
                var end = (long)chunk.Offset + chunk.UncompressedLength;
                if (end > length) { missing.Add(chunk); continue; }

                if (buffer.Length < chunk.UncompressedLength)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
                }
                fs.Position = (long)chunk.Offset;
                fs.ReadExactly(buffer, 0, (int)chunk.UncompressedLength);
                if (DepotChunk.AdlerHash(buffer.AsSpan(0, (int)chunk.UncompressedLength)) != chunk.Checksum)
                    missing.Add(chunk);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return missing;
    }

    /// <summary>Fichero de destino: un handle compartido por todos sus fragmentos, cerrado al terminar.</summary>
    private sealed class FileTarget(string path, ulong size)
    {
        private readonly object _gate = new();
        private SafeFileHandle? _handle;
        public string Path { get; } = path;
        public int Pending;
        public ulong NewBytes;

        public void Write(ReadOnlySpan<byte> data, long offset)
        {
            SafeFileHandle handle;
            lock (_gate)
            {
                if (_handle is null)
                {
                    _handle = File.OpenHandle(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    RandomAccess.SetLength(_handle, (long)size);
                }
                handle = _handle;
            }
            RandomAccess.Write(handle, data, offset);
            if (Interlocked.Decrement(ref Pending) == 0) Close();
        }

        public void Resize()
        {
            using var handle = File.OpenHandle(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            RandomAccess.SetLength(handle, (long)size);
        }

        public void Close()
        {
            lock (_gate)
            {
                _handle?.Dispose();
                _handle = null;
            }
        }
    }

    // =====================================================================================
    // Utilidades
    // =====================================================================================

    /// <summary>Reintenta una petición CDN rotando de servidor (los fallos de red sueltos son normales).</summary>
    private static async Task<T> WithServerRetry<T>(IReadOnlyList<Server> servers, CancellationToken ct, Func<Server, Task<T>> action)
    {
        var start = Random.Shared.Next(servers.Count);
        Exception? last = null;
        for (int attempt = 0; attempt < MaxChunkAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var server = servers[(start + attempt) % servers.Count];
            try
            {
                return await action(server).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
        throw new IOException($"La CDN de Steam no respondió tras {MaxChunkAttempts} intentos: {last?.Message}", last);
    }

    /// <summary>Depots y manifiestos instalados según el appmanifest de Steam (vacío si no está instalado).</summary>
    private static Dictionary<uint, ulong> ReadInstalledDepots(string libraryPath, uint appId)
    {
        var result = new Dictionary<uint, ulong>();
        var acf = Path.Combine(libraryPath, "steamapps", $"appmanifest_{appId}.acf");
        if (!File.Exists(acf)) return result;
        try
        {
            var depots = Library.VdfParser.ParseFile(acf)["AppState"]?["InstalledDepots"];
            if (depots is null) return result;
            foreach (var (id, node) in depots.Children)
                if (uint.TryParse(id, out var depotId) && ulong.TryParse(node.GetValue("manifest"), out var manifestId))
                    result[depotId] = manifestId;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer {acf}: {ex.Message}");
        }
        return result;
    }

    /// <summary>Manifiesto instalado guardado por Steam en <c>depotcache</c> (o por nosotros), si existe y se puede leer.</summary>
    private static DepotManifest? TryLoadSteamManifest(string steamPath, uint depotId, ulong manifestId, byte[] key)
    {
        var path = Path.Combine(steamPath, "depotcache", $"{depotId}_{manifestId}.manifest");
        if (!File.Exists(path)) return null;
        try
        {
            // LoadFromFile devuelve null si el archivo no tiene el formato esperado.
            var manifest = DepotManifest.LoadFromFile(path);
            if (manifest is null) return null;
            if (manifest.FilenamesEncrypted) manifest.DecryptFilenames(key);
            return manifest;
        }
        catch (Exception ex)
        {
            Log.Warn($"Manifiesto instalado ilegible ({path}): {ex.Message}. Se verificarán los archivos uno a uno.");
            return null;
        }
    }

    private static void EnsureDiskSpace(string libraryPath, long neededBytes)
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(libraryPath)!).AvailableFreeSpace;
            if (free < neededBytes + 512L * 1024 * 1024)
                throw new IOException($"No hay espacio suficiente en {Path.GetPathRoot(libraryPath)}: se necesitan {Formatting.Bytes(neededBytes)} y quedan {Formatting.Bytes(free)}.");
        }
        catch (IOException) { throw; }
        catch (Exception) { /* unidad de red u otra: no se puede comprobar */ }
    }
}
