using System.Net.Http;
using System.Security.Cryptography;
using Purgine.Infrastructure;
using Purgine.SteamNet;

namespace Purgine.EpicNet;

/// <summary>Resultado de una instalación o actualización de Epic.</summary>
public sealed record EpicInstallResult(string BuildVersion, string LaunchExe, string LaunchCommand, string InstallDir, long InstallSize);

/// <summary>
/// Descarga e instala juegos de Epic sin Epic Games Launcher (patrón Legendary), 100 % en C#:
/// <list type="number">
///   <item>Build pública y manifiesto binario desde la API de la cuenta.</item>
///   <item>Plan incremental: los archivos que ya están en disco con el SHA-1 correcto se saltan; así reanudar y
///         actualizar solo bajan lo que falta o ha cambiado.</item>
///   <item>Cada fragmento necesario se descarga una sola vez (8 en paralelo, rotando entre las CDN con
///         reintentos), se descomprime y sus trozos se escriben directamente en su posición de cada archivo.</item>
/// </list>
/// El progreso son los bytes reales recibidos de la CDN.
/// </summary>
public sealed class EpicDownloadEngine(EpicAccountService epic)
{
    private const int Parallelism = 8;
    private static readonly HttpClient Cdn = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Tamaño instalado según el manifiesto actual (para el diálogo de instalar). Null si no se puede.</summary>
    public async Task<long?> GetInstallSizeAsync(string ns, string itemId, string appName, CancellationToken ct)
    {
        var asset = await epic.GetAssetInfoAsync(ns, itemId, appName, ct).ConfigureAwait(false);
        if (asset is null) return null;
        return EpicManifest.Parse(await epic.DownloadManifestAsync(asset, ct).ConfigureAwait(false)).InstallSize;
    }

    public async Task<EpicInstallResult> RunAsync(DownloadJob job, string installDir, CancellationToken ct)
    {
        job.SetState(DownloadJobState.Preparing, "Consultando la versión del juego en Epic…");
        var asset = await epic.GetAssetInfoAsync(job.EpicNamespace!, job.EpicCatalogItemId!, job.EpicAppName!, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Vincula tu cuenta de Epic Games para descargar.");
        job.SetState(DownloadJobState.Preparing, "Descargando la lista de archivos…");
        var manifest = EpicManifest.Parse(await epic.DownloadManifestAsync(asset, ct).ConfigureAwait(false));
        // Cada CDN: carpeta del manifiesto + su firma (algunas CDN la exigen también para los fragmentos).
        var bases = asset.ManifestUrls
            .Select(u =>
            {
                var parts = u.Split('?', 2);
                return (Base: parts[0][..parts[0].LastIndexOf('/')], Query: parts.Length > 1 ? parts[1] : string.Empty);
            })
            .Distinct().ToList();

        Directory.CreateDirectory(installDir);

        // ---------- Plan incremental ----------
        job.SetState(DownloadJobState.Verifying, "Comprobando los archivos que ya están en disco…");
        var needed = new List<EpicFile>();
        long checkedBytes = 0, toCheck = manifest.Files.Sum(f => f.Size);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(installDir, file.Path);
            if (!IsUpToDate(path, file)) needed.Add(file);
            checkedBytes += file.Size;
            job.ReportVerification(checkedBytes, toCheck);
        }

        var targets = new Dictionary<string, List<(string Path, long FileOffset, uint ChunkOffset, uint Size)>>(StringComparer.Ordinal);
        long neededInstallBytes = 0;
        foreach (var file in needed)
        {
            var path = Path.Combine(installDir, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) fs.SetLength(file.Size);
            long offset = 0;
            foreach (var part in file.Parts)
            {
                if (!targets.TryGetValue(part.ChunkGuidHex, out var list)) targets[part.ChunkGuidHex] = list = [];
                list.Add((path, offset, part.Offset, part.Size));
                offset += part.Size;
            }
            neededInstallBytes += file.Size;
        }
        EnsureDiskSpace(installDir, neededInstallBytes);

        // ---------- Descarga ----------
        var total = targets.Keys.Sum(g => manifest.Chunks[g].FileSize);
        job.BeginTransfer(total);
        job.SetState(DownloadJobState.Downloading, null);
        Log.Info($"Epic: {manifest.AppName} {manifest.BuildVersion} → {needed.Count}/{manifest.Files.Count} archivos, {targets.Count} fragmentos ({Formatting.Bytes(total)}).");

        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (kv, token) =>
        {
            var chunk = manifest.Chunks[kv.Key];
            var raw = await DownloadChunkAsync(manifest, chunk, bases, token).ConfigureAwait(false);
            var data = EpicManifest.DecodeChunk(raw);
            foreach (var (path, fileOffset, chunkOffset, size) in kv.Value)
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                RandomAccess.Write(handle, data.AsSpan((int)chunkOffset, (int)size), fileOffset);
            }
            job.AddTransferred((uint)chunk.FileSize);
        }).ConfigureAwait(false);

        return new EpicInstallResult(manifest.BuildVersion, manifest.LaunchExe, manifest.LaunchCommand, installDir, manifest.InstallSize);
    }

    private static bool IsUpToDate(string path, EpicFile file)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            return SHA1.HashData(fs).AsSpan().SequenceEqual(file.Sha1);
        }
        catch (IOException) { return false; }
    }

    private static async Task<byte[]> DownloadChunkAsync(EpicManifest manifest, EpicChunk chunk,
        IReadOnlyList<(string Base, string Query)> bases, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < bases.Count * 3; attempt++)
        {
            var (baseUrl, query) = bases[attempt % bases.Count];
            var url = $"{baseUrl}/{manifest.ChunkPath(chunk)}" + (query.Length > 0 ? "?" + query : string.Empty);
            try { return await Cdn.GetByteArrayAsync(url, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { last = ex; await Task.Delay(500 * (attempt + 1), ct).ConfigureAwait(false); }
        }
        throw new IOException($"No se pudo descargar un fragmento de Epic: {last?.Message}");
    }

    private static void EnsureDiskSpace(string path, long neededBytes)
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
            if (free < neededBytes)
                throw new IOException($"No hay espacio suficiente: hacen falta {Formatting.Bytes(neededBytes)} y quedan {Formatting.Bytes(free)}.");
        }
        catch (ArgumentException) { /* unidad desconocida: se intenta igualmente */ }
    }
}
