using Purgine.Infrastructure;

namespace Purgine.Library;

/// <summary>Un mod de Workshop descargado en este equipo.</summary>
public sealed record WorkshopMod(ulong Id, string Path, long SizeBytes, bool Enabled);

/// <summary>
/// Mods de Workshop de un juego: <c>steamapps\workshop\content\{appid}\{id}</c>. Desactivar un mod lo mueve a
/// <c>steamapps\workshop\purgine-disabled\{appid}\{id}</c> (fuera de la carpeta que lee el juego) y activarlo lo
/// devuelve; no se borra nada. Steam puede volver a descargar un mod desactivado mientras sigas suscrito a él:
/// para quitarlo para siempre hay que cancelar la suscripción en Workshop.
/// </summary>
public static class WorkshopModManager
{
    public static string ContentDir(string libraryPath, string appId) =>
        System.IO.Path.Combine(libraryPath, "steamapps", "workshop", "content", appId);

    public static string DisabledDir(string libraryPath, string appId) =>
        System.IO.Path.Combine(libraryPath, "steamapps", "workshop", "purgine-disabled", appId);

    public static List<WorkshopMod> List(string libraryPath, string appId)
    {
        var mods = new List<WorkshopMod>();
        Collect(ContentDir(libraryPath, appId), enabled: true, mods);
        Collect(DisabledDir(libraryPath, appId), enabled: false, mods);
        return mods;
    }

    private static void Collect(string dir, bool enabled, List<WorkshopMod> into)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (!ulong.TryParse(System.IO.Path.GetFileName(sub), out var id)) continue;
            into.Add(new WorkshopMod(id, sub, DirectorySize(sub), enabled));
        }
    }

    /// <summary>Activa o desactiva un mod moviendo su carpeta. Devuelve null si fue bien o el motivo del fallo.</summary>
    public static string? SetEnabled(string libraryPath, string appId, ulong id, bool enabled)
    {
        var from = System.IO.Path.Combine(enabled ? DisabledDir(libraryPath, appId) : ContentDir(libraryPath, appId), id.ToString());
        var to = System.IO.Path.Combine(enabled ? ContentDir(libraryPath, appId) : DisabledDir(libraryPath, appId), id.ToString());
        try
        {
            if (!Directory.Exists(from)) return null; // ya estaba en su sitio
            if (Directory.Exists(to)) return $"Ya existe {to}.";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
            Log.Info($"Workshop {appId}: mod {id} {(enabled ? "activado" : "desactivado")}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Mod {id}: {ex.Message}";
        }
    }

    private static long DirectorySize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception) { return 0; }
    }
}
