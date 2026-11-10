using System.Text.Json;
using Purgine.Infrastructure;

namespace Purgine.Library;

/// <summary>
/// Juegos instalados con Epic Games Launcher, leídos de sus manifiestos locales
/// (<c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item</c>, JSON). Solo lectura y sin red:
/// nombre, carpeta, ejecutable, tamaño, categorías e identificadores de catálogo (para lanzarlo por URI).
/// Se omiten los DLC/contenidos (MainGameAppName distinto), las instalaciones a medias y lo oculto.
/// </summary>
public static class EpicLibraryScanner
{
    public static string ManifestsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    /// <summary>¿Hay Epic Games Launcher en este equipo (carpeta de manifiestos)?</summary>
    public static bool IsInstalled => Directory.Exists(ManifestsDir);

    public static List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();
        if (!Directory.Exists(ManifestsDir)) return games;
        foreach (var file in SafeFiles(ManifestsDir))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                var appName = Str(r, "AppName");
                var name = Str(r, "DisplayName");
                var install = Str(r, "InstallLocation");
                if (appName is null || name is null || install is null) continue;
                if (Bool(r, "bIsIncompleteInstall")) continue;
                var main = Str(r, "MainGameAppName");
                if (!string.IsNullOrEmpty(main) && main != appName) continue; // DLC o contenido de otro juego

                var categories = r.TryGetProperty("AppCategories", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
                    : [];
                if (categories.Contains("hidden")) continue;

                var exe = Str(r, "LaunchExecutable");
                games.Add(new GameEntry
                {
                    Id = GameEntry.MakeEpicId(appName),
                    Platform = GamePlatform.Epic,
                    AppId = appName,
                    Name = name.Trim(),
                    InstallDir = install,
                    ExecutablePath = exe is null ? null : Path.GetFullPath(Path.Combine(install, exe)),
                    Arguments = Str(r, "LaunchCommand"), // argumentos propios del juego (se añaden al lanzarlo)
                    SizeOnDisk = r.TryGetProperty("InstallSize", out var s) && s.TryGetInt64(out var size) ? size : 0,
                    EpicNamespace = Str(r, "CatalogNamespace"),
                    EpicCatalogItemId = Str(r, "CatalogItemId"),
                    EpicCategories = categories,
                    IsInstalled = Directory.Exists(install),
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Warn($"Manifiesto de Epic ilegible ({Path.GetFileName(file)}): {ex.Message}");
            }
        }
        Log.Info($"Biblioteca Epic: {games.Count} juegos instalados.");
        return games;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.item").ToList(); }
        catch (Exception) { return []; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
