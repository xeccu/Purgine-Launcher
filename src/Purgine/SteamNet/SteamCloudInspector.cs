using System.Security.Cryptography;
using Purgine.Infrastructure;
using Purgine.Library;
using SteamKit2.Internal;

namespace Purgine.SteamNet;

/// <summary>Cómo están las partidas respecto a la nube, con la misma lógica que el cliente de Steam.</summary>
public enum CloudSyncKind
{
    /// <summary>El juego no usa la nube (o no hay nada ni en local ni en la nube).</summary>
    NoCloud,
    /// <summary>Nada cambió desde la última sincronización.</summary>
    Synced,
    /// <summary>Solo cambió lo local: se subirá.</summary>
    LocalNewer,
    /// <summary>Solo cambió la nube: se descargará.</summary>
    RemoteNewer,
    /// <summary>Cambiaron las dos partes desde la última sincronización: hay que elegir.</summary>
    Conflict,
    /// <summary>No se pudo consultar (sin conexión, sin sesión…).</summary>
    Unknown,
}

/// <summary>Resultado de comparar lo local con la nube.</summary>
public sealed record CloudSyncCheck(
    CloudSyncKind Kind,
    DateTime? LocalTimeUtc,
    DateTime? RemoteTimeUtc,
    string? RemoteMachine,
    int LocalChanged,
    int RemoteChanged,
    IReadOnlyList<string> LocalFiles,
    string? SaveFolder);

/// <summary>Un archivo anotado por Steam en <c>remotecache.vdf</c> tras la última sincronización.</summary>
public sealed record SteamCloudRecord(string Path, int Root, long Size, long LocalTime, long RemoteTime, string Sha);

/// <summary>
/// Estado de Steam Cloud de un juego, sin tocar nada (solo lectura), igual que lo decide el cliente de Steam:
/// <list type="bullet">
///   <item><c>userdata\&lt;cuenta&gt;\&lt;appid&gt;\remotecache.vdf</c>: lo que quedó sincronizado (por archivo: raíz,
///         tamaño, fecha local, fecha en la nube y SHA-1) y el <c>ChangeNumber</c> de la nube en ese momento.</item>
///   <item>Cambios locales: archivos cuyo tamaño o SHA-1 ya no coincide con lo anotado.</item>
///   <item>Cambios en la nube: <c>Cloud.GetAppFileChangelist</c> con ese ChangeNumber; si la nube va por uno más
///         alto y algún archivo tiene otro SHA-1, cambió desde otro equipo.</item>
/// </list>
/// Las dos cosas a la vez = conflicto (Steam muestra su ventana para elegir).
/// </summary>
public static class SteamCloudInspector
{
    /// <summary>Carpeta de cada raíz de Steam Cloud en Windows (ERemoteStorageFileRoot); null = no aplica.</summary>
    public static string? RootFolder(int root, string userAppDir, string? installDir)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return root switch
        {
            0 => Path.Combine(userAppDir, "remote"),
            1 => installDir,
            2 => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            3 => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            4 => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            9 => Path.Combine(profile, "Saved Games"),
            10 => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            12 => Path.Combine(profile, "AppData", "LocalLow"),
            _ => null,
        };
    }

    /// <summary>Lee <c>remotecache.vdf</c>: ChangeNumber y archivos sincronizados. Null si el juego no tiene.</summary>
    public static (ulong ChangeNumber, List<SteamCloudRecord> Files)? ReadRemoteCache(string userAppDir)
    {
        var path = Path.Combine(userAppDir, "remotecache.vdf");
        if (!File.Exists(path)) return null;
        try
        {
            var root = VdfParser.ParseFile(path).Children.Values.FirstOrDefault();
            if (root is null) return null;
            ulong.TryParse(root.GetValue("ChangeNumber"), out var change);
            var files = new List<SteamCloudRecord>();
            foreach (var (name, node) in root.Children)
            {
                files.Add(new SteamCloudRecord(name,
                    int.TryParse(node.GetValue("root"), out var r) ? r : 0,
                    long.TryParse(node.GetValue("size"), out var s) ? s : -1,
                    long.TryParse(node.GetValue("localtime"), out var lt) ? lt : 0,
                    long.TryParse(node.GetValue("remotetime"), out var rt) ? rt : long.TryParse(node.GetValue("time"), out var t) ? t : 0,
                    node.GetValue("sha") ?? string.Empty));
            }
            return (change, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Log.Warn($"Steam Cloud: remotecache ilegible ({path}): {ex.Message}");
            return null;
        }
    }

    public static async Task<CloudSyncCheck> CheckAsync(SteamSession session, string userDir, uint appId, string? installDir, CancellationToken ct)
    {
        var userAppDir = Path.Combine(userDir, appId.ToString());
        var cache = ReadRemoteCache(userAppDir);

        // ---- Lo local frente a lo anotado ----
        var localChanged = 0;
        DateTime? localNewest = null;
        var localFiles = new List<string>();
        string? folder = null;
        if (cache is { } c)
        {
            foreach (var rec in c.Files)
            {
                if (RootFolder(rec.Root, userAppDir, installDir) is not { } baseDir) continue;
                var full = Path.GetFullPath(Path.Combine(baseDir, rec.Path.Replace('/', '\\')));
                folder ??= FolderOf(baseDir, rec.Path);
                var info = new FileInfo(full);
                if (!info.Exists) continue;
                localFiles.Add(full);
                var mtime = info.LastWriteTimeUtc;
                if (localNewest is null || mtime > localNewest) localNewest = mtime;
                if (info.Length != rec.Size) { localChanged++; continue; }
                var unix = new DateTimeOffset(mtime).ToUnixTimeSeconds();
                if (Math.Abs(unix - rec.LocalTime) <= 2) continue; // misma fecha y tamaño: igual que lo sincronizado
                if (!Sha1Hex(full).Equals(rec.Sha, StringComparison.OrdinalIgnoreCase)) localChanged++;
            }
        }

        // ---- La nube frente a lo anotado ----
        var synced = cache?.ChangeNumber ?? 0;
        var response = await session.UseServiceAsync<Cloud, CCloud_GetAppFileChangelist_Response>(async (cloud, _) =>
        {
            var r = await cloud.GetAppFileChangelist(new CCloud_GetAppFileChangelist_Request
            {
                appid = appId,
                synced_change_number = synced,
            }).ToTask();
            return r.Result == SteamKit2.EResult.OK ? r.Body : null;
        }, "Steam Cloud", ct).ConfigureAwait(false);

        if (response is null)
            return new CloudSyncCheck(CloudSyncKind.Unknown, localNewest, null, null, localChanged, 0, localFiles, folder);

        var known = cache?.Files.ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, SteamCloudRecord>(StringComparer.OrdinalIgnoreCase);
        var remoteChanged = 0;
        DateTime? remoteNewest = null;
        string? machine = null;
        if (response.current_change_number != synced || cache is null)
        {
            foreach (var f in response.files)
            {
                if (f.persist_state == ECloudStoragePersistState.k_ECloudStoragePersistStateDeleted) continue;
                var sha = Convert.ToHexStringLower(f.sha_file ?? []);
                // La ruta completa es prefijo (carpeta, compartido entre archivos) + nombre.
                var name = f.path_prefix_index < response.path_prefixes.Count
                    ? response.path_prefixes[(int)f.path_prefix_index] + f.file_name
                    : f.file_name;
                // Fuera de «remote», el prefijo lleva la raíz delante («%WinAppDataLocalLow%TENSTACK/…»); el
                // registro local guarda la ruta sin ella.
                if (name.StartsWith('%') && name.IndexOf('%', 1) is var end and > 0) name = name[(end + 1)..];
                // Mismo SHA-1, o no es más nuevo que lo sincronizado (registros antiguos con otro tipo de hash): sin cambio.
                if (known.TryGetValue(name, out var rec) &&
                    (rec.Sha.Equals(sha, StringComparison.OrdinalIgnoreCase) || (long)f.time_stamp <= rec.RemoteTime + 2)) continue;
                remoteChanged++;
                var when = DateTimeOffset.FromUnixTimeSeconds((long)f.time_stamp).UtcDateTime;
                if (remoteNewest is null || when > remoteNewest)
                {
                    remoteNewest = when;
                    machine = f.machine_name_index < response.machine_names.Count ? response.machine_names[(int)f.machine_name_index] : null;
                    if (machine is { Length: 0 } or "<unknown>") machine = null;
                }
            }
        }
        if (remoteNewest is null && known.Count > 0)
            remoteNewest = DateTimeOffset.FromUnixTimeSeconds(known.Values.Max(r => r.RemoteTime)).UtcDateTime;

        CloudSyncKind kind;
        if (cache is null)
        {
            // Sin registro de Steam: la nube manda (Steam la descarga al jugar por primera vez en este equipo).
            kind = response.files.Count == 0 ? CloudSyncKind.NoCloud : CloudSyncKind.RemoteNewer;
        }
        else if (localChanged > 0 && remoteChanged > 0) kind = CloudSyncKind.Conflict;
        else if (remoteChanged > 0) kind = CloudSyncKind.RemoteNewer;
        else if (localChanged > 0) kind = CloudSyncKind.LocalNewer;
        else kind = cache.Value.Files.Count == 0 && response.files.Count == 0 ? CloudSyncKind.NoCloud : CloudSyncKind.Synced;

        return new CloudSyncCheck(kind, localNewest, remoteNewest, machine, localChanged, remoteChanged, localFiles, folder);
    }

    /// <summary>Carpeta «de partidas» para mostrar: la raíz más la primera carpeta de la ruta del archivo.</summary>
    private static string FolderOf(string baseDir, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        return parts.Length > 1 ? Path.Combine(baseDir, parts[0]) : baseDir;
    }

    private static string Sha1Hex(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexStringLower(SHA1.HashData(fs));
        }
        catch (IOException) { return string.Empty; }
    }
}
