namespace Purgine.Infrastructure;

/// <summary>
/// Límite de copias de seguridad locales: de cada cosa se guardan como mucho <see cref="MaxCopies"/> (la más reciente y
/// dos anteriores) y el resto se borra, al crear cada copia y al arrancar Purgine. Así nunca se acumulan copias sin
/// fin ni se llena el disco:
/// <list type="bullet">
///   <item>Partidas guardadas (copias antes de sincronizar con la nube): <c>save-backups\&lt;juego&gt;\*.zip</c>.</item>
///   <item>Copias de <c>localconfig.vdf</c> de Steam (al aceptar contratos): <c>steam-config-backups</c>.</item>
///   <item>Copias de archivos de configuración dañados (<c>*.corrupt-*</c>), por archivo.</item>
///   <item>Instaladores de actualizaciones ya aplicadas y descargas a medias en <c>%TEMP%\Purgine-update</c>.</item>
/// </list>
/// </summary>
public static class BackupRetention
{
    /// <summary>Copias que se conservan de cada cosa: la actual y dos anteriores.</summary>
    public const int MaxCopies = 3;

    /// <summary>Deja en <paramref name="dir"/> solo las <see cref="MaxCopies"/> más recientes que cumplen <paramref name="pattern"/>.</summary>
    public static int Prune(string dir, string pattern, int keep = MaxCopies)
    {
        if (!Directory.Exists(dir)) return 0;
        var removed = 0;
        foreach (var old in new DirectoryInfo(dir).EnumerateFiles(pattern)
                     .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
                     .Skip(keep))
        {
            try { old.Delete(); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"No se pudo borrar la copia {old.FullName}: {ex.Message}"); }
        }
        return removed;
    }

    /// <summary>Copias de archivos dañados (<c>settings.json.corrupt-…</c>): como mucho tres por archivo.</summary>
    public static int PruneCorruptCopies(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        var removed = 0;
        foreach (var group in Directory.EnumerateFiles(dir, "*.corrupt-*", SearchOption.AllDirectories)
                     .GroupBy(f => f[..f.LastIndexOf(".corrupt-", StringComparison.Ordinal)], StringComparer.OrdinalIgnoreCase))
            removed += Prune(Path.GetDirectoryName(group.Key)!, Path.GetFileName(group.Key) + ".corrupt-*");
        return removed;
    }

    /// <summary>
    /// Al arrancar Purgine con normalidad (no como instalador): el instalador de la actualización ya está aplicado y
    /// las descargas a medias no sirven, así que la carpeta temporal de actualizaciones se vacía.
    /// </summary>
    public static int CleanUpdateDownloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Purgine-update");
        if (!Directory.Exists(dir)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try { File.Delete(file); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* el instalador aún se está cerrando */ }
        }
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (IOException) { }
        return removed;
    }

    /// <summary>Repaso completo al arrancar (en segundo plano): aplica el límite a todo lo que ya hubiera en disco.</summary>
    public static void PruneAll()
    {
        try
        {
            var removed = 0;
            if (Directory.Exists(AppPaths.SaveBackupsDir))
                foreach (var game in Directory.EnumerateDirectories(AppPaths.SaveBackupsDir))
                    removed += Prune(game, "*.zip");
            removed += Prune(AppPaths.SteamConfigBackupDir, "localconfig-*.vdf");
            removed += PruneCorruptCopies(AppPaths.DataDir);
            var updates = CleanUpdateDownloads();
            if (removed + updates > 0)
                Log.Info($"Copias locales: {removed} copias antiguas y {updates} archivos de actualización borrados (máximo {MaxCopies} de cada).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Limpieza de copias locales: {ex.Message}");
        }
    }
}
