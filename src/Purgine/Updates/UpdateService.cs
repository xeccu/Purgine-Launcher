using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Purgine.Infrastructure;

namespace Purgine.Updates;

/// <summary>Instalador publicado como archivo de una Release de GitHub.</summary>
public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl, string? Sha256);

/// <summary>Una versión publicada en GitHub Releases con su instalador para Windows.</summary>
public sealed record ReleaseInfo(string Tag, SemVersion Version, string Title, string Notes, string PageUrl,
    DateTimeOffset? PublishedAt, ReleaseAsset Installer);

/// <summary>
/// Auto-actualizador: consulta las Releases del repositorio oficial en GitHub, elige la versión más alta que tenga
/// instalador (<c>Purgine-Setup-*.exe</c>) y lo descarga verificando tamaño y SHA-256. La instalación la hace el propio
/// instalador en modo <c>--update</c> (ver <see cref="AppInstaller"/>), con Purgine ya cerrado.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Owner = "xeccu";
    public const string Repository = "Purgine-Launcher";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repository}/releases";

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Purgine-Updater", AppVersion.Display));
    }

    /// <summary>
    /// Versión más reciente publicada, o null si no hay ninguna más nueva que esta. Se usa la lista de Releases (y no
    /// solo <c>/releases/latest</c>) para tener en cuenta también las alfas y betas marcadas como preversión.
    /// </summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=15");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // aún no hay Releases
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

        ReleaseInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString();
            if (!SemVersion.TryParse(tag, out var version)) continue;
            if (best is not null && !(version > best.Version)) continue;
            var asset = FindInstaller(r);
            if (asset is null) continue;
            best = new ReleaseInfo(tag!, version,
                Str(r, "name") is { Length: > 0 } n ? n : tag!,
                Str(r, "body") ?? string.Empty,
                Str(r, "html_url") ?? ReleasesPage,
                r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTimeOffset(out var at) ? at : null,
                asset);
        }

        if (best is null) return null;
        Log.Info($"Actualizaciones: última publicada {best.Tag}, esta es {AppVersion.Display}.");
        return best.Version > AppVersion.Current ? best : null;
    }

    private static ReleaseAsset? FindInstaller(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = Str(a, "name") ?? string.Empty;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !name.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;
            var digest = Str(a, "digest");
            return new ReleaseAsset(name, a.GetProperty("size").GetInt64(), Str(a, "browser_download_url")!,
                digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Descarga el instalador a una carpeta temporal y devuelve su ruta (verificado).</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Purgine-update");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(release.Installer.Name));
        var part = target + ".part";

        using (var response = await _http.GetAsync(release.Installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.Installer.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            long done = 0;
            var lastReport = 0L;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                done += read;
                if (done - lastReport > 256 * 1024 || done == total) { progress.Report((done, total)); lastReport = done; }
            }
            progress.Report((done, total));

            if (release.Installer.Size > 0 && done != release.Installer.Size)
                throw new InvalidDataException($"La descarga está incompleta ({done:N0} de {release.Installer.Size:N0} bytes).");
            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (release.Installer.Sha256 is { } expected && !expected.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("El instalador descargado no coincide con el publicado (SHA-256 distinto).");
        }

        File.Move(part, target, overwrite: true);
        Log.Info($"Actualización {release.Tag} descargada: {target}");
        return target;
    }

    public void Dispose() => _http.Dispose();
}
