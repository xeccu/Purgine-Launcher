namespace Purgine.Infrastructure;

/// <summary>Espacio que ocupa de verdad una carpeta en disco.</summary>
public static class DiskUsage
{
    /// <summary>
    /// Suma el tamaño de todos los archivos de <paramref name="dir"/> (sin seguir enlaces ni uniones, para no contar
    /// dos veces ni salirse de la carpeta). Carpetas sin permiso se saltan. 0 si no existe.
    /// </summary>
    public static long DirectorySize(string dir, CancellationToken ct = default)
    {
        if (!Directory.Exists(dir)) return 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                total += file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"No se pudo medir {dir}: {ex.Message}");
        }
        return total;
    }
}
