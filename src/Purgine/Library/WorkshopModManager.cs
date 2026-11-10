using System.Text.RegularExpressions;
using Purgine.Infrastructure;

namespace Purgine.Library;

/// <summary>Cómo carga el juego un mod (y, por tanto, cómo se activa o desactiva).</summary>
public enum ModControl
{
    /// <summary>El juego lee <c>steamapps\workshop\content\{appid}\{id}</c>: se desactiva sacando la carpeta de ahí.</summary>
    WorkshopFolder,
    /// <summary>
    /// El juego copia cada mod a su propia carpeta <c>{juego}\mods\{nombre}_{id}</c> y lo desactiva con un archivo
    /// <c>disable.it</c> dentro (The Binding of Isaac y juegos con la misma convención). Es su mecanismo propio,
    /// el mismo que usa el menú de mods del juego.
    /// </summary>
    GameModsFolder,
}

/// <summary>Un mod de Workshop en este equipo.</summary>
public sealed record WorkshopMod(ulong Id, string Path, long SizeBytes, bool Enabled, ModControl Control, string? Title);

/// <summary>
/// Mods de Workshop de un juego, activados y desactivados en disco de la forma en que el juego los lee:
/// <list type="bullet">
///   <item>Juegos con carpeta <c>mods</c> propia (metadata.xml con el id de Workshop): archivo <c>disable.it</c>.</item>
///   <item>Resto: la carpeta del mod en <c>workshop\content\{appid}</c> se mueve a
///         <c>workshop\purgine-disabled\{appid}</c> al desactivarlo y vuelve al activarlo (nunca se borra).
///         Steam puede volver a descargar un mod así desactivado mientras sigas suscrito.</item>
/// </list>
/// </summary>
public static partial class WorkshopModManager
{
    private const string DisableFile = "disable.it";

    [GeneratedRegex(@"<id>\s*(\d+)\s*</id>", RegexOptions.IgnoreCase)]
    private static partial Regex IdTag();

    [GeneratedRegex(@"<name>\s*(.*?)\s*</name>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NameTag();

    [GeneratedRegex(@"_(\d{6,})$")]
    private static partial Regex IdSuffix();

    public static string ContentDir(string libraryPath, string appId) =>
        System.IO.Path.Combine(libraryPath, "steamapps", "workshop", "content", appId);

    public static string DisabledDir(string libraryPath, string appId) =>
        System.IO.Path.Combine(libraryPath, "steamapps", "workshop", "purgine-disabled", appId);

    /// <summary>Mods del juego. <paramref name="installDir"/>: carpeta del juego (para detectar su carpeta <c>mods</c>).</summary>
    public static List<WorkshopMod> List(string libraryPath, string appId, string? installDir)
    {
        var mods = new Dictionary<ulong, WorkshopMod>();

        // 1) Carpeta de mods propia del juego (manda: es lo que el juego carga de verdad).
        var gameMods = installDir is null ? null : System.IO.Path.Combine(installDir, "mods");
        if (gameMods is not null && Directory.Exists(gameMods))
        {
            foreach (var dir in SafeDirectories(gameMods))
            {
                var (id, title) = ReadMetadata(dir);
                if (id == 0 || mods.ContainsKey(id)) continue;
                mods[id] = new WorkshopMod(id, dir, DirectorySize(dir), !File.Exists(System.IO.Path.Combine(dir, DisableFile)),
                    ModControl.GameModsFolder, title);
            }
        }

        // 2) Carpeta de Workshop (y los desactivados por Purgine), para lo que el juego aún no ha copiado.
        foreach (var (dir, enabled) in new[] { (ContentDir(libraryPath, appId), true), (DisabledDir(libraryPath, appId), false) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var sub in SafeDirectories(dir))
            {
                if (!ulong.TryParse(System.IO.Path.GetFileName(sub), out var id) || mods.ContainsKey(id)) continue;
                mods[id] = new WorkshopMod(id, sub, DirectorySize(sub), enabled, ModControl.WorkshopFolder, ReadMetadata(sub).Title);
            }
        }
        return mods.Values.ToList();
    }

    /// <summary>Activa o desactiva un mod en disco. Devuelve null si fue bien o el motivo del fallo.</summary>
    public static string? SetEnabled(string libraryPath, string appId, WorkshopMod mod, bool enabled)
    {
        try
        {
            if (mod.Control == ModControl.GameModsFolder)
            {
                var flag = System.IO.Path.Combine(mod.Path, DisableFile);
                if (enabled) { if (File.Exists(flag)) File.Delete(flag); }
                else if (!File.Exists(flag)) File.WriteAllBytes(flag, []);
            }
            else
            {
                var from = System.IO.Path.Combine(enabled ? DisabledDir(libraryPath, appId) : ContentDir(libraryPath, appId), mod.Id.ToString());
                var to = System.IO.Path.Combine(enabled ? ContentDir(libraryPath, appId) : DisabledDir(libraryPath, appId), mod.Id.ToString());
                if (Directory.Exists(from))
                {
                    if (Directory.Exists(to)) return $"Ya existe {to}.";
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
                    Directory.Move(from, to);
                }
            }
            Log.Info($"Workshop {appId}: mod {mod.Id} {(enabled ? "activado" : "desactivado")} ({mod.Control}).");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{mod.Title ?? mod.Id.ToString()}: {ex.Message}";
        }
    }

    /// <summary>Ruta del mod tras cambiar su estado (solo cambia en los de carpeta de Workshop).</summary>
    public static string PathAfter(string libraryPath, string appId, WorkshopMod mod, bool enabled) =>
        mod.Control == ModControl.GameModsFolder ? mod.Path
            : System.IO.Path.Combine(enabled ? ContentDir(libraryPath, appId) : DisabledDir(libraryPath, appId), mod.Id.ToString());

    /// <summary>Id de Workshop y nombre desde metadata.xml (o el sufijo «_123456» de la carpeta).</summary>
    private static (ulong Id, string? Title) ReadMetadata(string dir)
    {
        ulong id = 0;
        string? title = null;
        try
        {
            var meta = System.IO.Path.Combine(dir, "metadata.xml");
            if (File.Exists(meta))
            {
                var xml = File.ReadAllText(meta);
                if (IdTag().Match(xml) is { Success: true } m) ulong.TryParse(m.Groups[1].Value, out id);
                if (NameTag().Match(xml) is { Success: true } n) title = System.Net.WebUtility.HtmlDecode(n.Groups[1].Value);
            }
        }
        catch (Exception) { /* metadata ilegible */ }
        if (id == 0 && IdSuffix().Match(System.IO.Path.GetFileName(dir)) is { Success: true } s) ulong.TryParse(s.Groups[1].Value, out id);
        return (id, string.IsNullOrWhiteSpace(title) ? null : title);
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception) { return []; }
    }

    private static long DirectorySize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception) { return 0; }
    }
}
