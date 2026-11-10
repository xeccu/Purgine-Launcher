namespace Purgine.Library;

/// <summary>Capturas y grabaciones locales de un juego.</summary>
public sealed record GameMedia(IReadOnlyList<string> Screenshots, string? ScreenshotsDir, int RecordingCount, string? RecordingsDir);

/// <summary>
/// Capturas de pantalla (F12) y grabaciones de juego que el cliente de Steam guarda en este equipo:
/// <list type="bullet">
///   <item>Capturas: <c>userdata\{cuenta}\760\remote\{appid}\screenshots\*.jpg</c> (miniaturas en <c>thumbnails</c>).</item>
///   <item>Grabaciones: <c>userdata\{cuenta}\gamerecordings\{clips,video}\*_{appid}_*</c> (clips y grabación de fondo).</item>
/// </list>
/// </summary>
public static class SteamMediaLocator
{
    public static GameMedia Find(string? steamPath, string appId)
    {
        var user = SteamPlaytimeReader.FindUserDir(steamPath);
        if (user is null) return new GameMedia([], null, 0, null);

        var shotsDir = Path.Combine(user, "760", "remote", appId, "screenshots");
        IReadOnlyList<string> shots = [];
        try
        {
            if (Directory.Exists(shotsDir))
                shots = Directory.EnumerateFiles(shotsDir, "*.*")
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
        }
        catch (Exception) { /* sin permiso o carpeta en uso */ }

        var recRoot = Path.Combine(user, "gamerecordings");
        var recordings = 0;
        try
        {
            foreach (var kind in new[] { "clips", "video" })
            {
                var dir = Path.Combine(recRoot, kind);
                if (Directory.Exists(dir))
                    recordings += Directory.EnumerateDirectories(dir).Count(d => Path.GetFileName(d).Contains($"_{appId}_", StringComparison.Ordinal));
            }
        }
        catch (Exception) { /* idem */ }

        return new GameMedia(shots, Directory.Exists(shotsDir) ? shotsDir : null, recordings,
            recordings > 0 && Directory.Exists(recRoot) ? recRoot : null);
    }

    /// <summary>Miniatura que guarda Steam junto a la captura (si existe); si no, la propia captura.</summary>
    public static string ThumbnailFor(string screenshot)
    {
        var thumb = Path.Combine(Path.GetDirectoryName(screenshot)!, "thumbnails", Path.GetFileName(screenshot));
        return File.Exists(thumb) ? thumb : screenshot;
    }
}
