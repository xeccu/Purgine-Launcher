namespace LightLauncher.Infrastructure;

/// <summary>
/// Logger a fichero minimalista (sin dependencias). Rota borrando el archivo al superar 512 KB.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                File.Delete(path);
        }
        catch
        {
            // El log nunca debe tumbar la aplicación.
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(_path, line); }
            catch { /* disco lleno, permisos... ignorar */ }
        }
    }
}
