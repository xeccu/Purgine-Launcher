using System.Diagnostics;

namespace Purgine.Infrastructure;

/// <summary>
/// Abrir carpetas en el Explorador de forma fiable. No se usa <c>ShellExecute</c> sobre la carpeta: su acción
/// predeterminada depende del registro y programas como WizTree, VS Code o VLC la cambian (Windows responde entonces
/// que «no encuentra la ruta»). Aquí se llama siempre a <c>explorer.exe</c> con la ruta completa entre comillas.
/// </summary>
public static class ShellOpen
{
    /// <summary>
    /// Abre una carpeta (o la que contiene un archivo, con el archivo seleccionado). Devuelve null si se abrió, o el
    /// motivo si no.
    /// </summary>
    public static string? Folder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No hay ninguna carpeta que abrir.";
        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"Ruta no válida: {path}";
        }

        string args;
        if (File.Exists(full)) args = $"/select,\"{full}\"";          // un archivo: su carpeta, con él seleccionado
        else if (Directory.Exists(full)) args = $"\"{full.TrimEnd('\\')}{(Path.GetPathRoot(full) == full ? "\\" : string.Empty)}\"";
        else return $"La carpeta no existe: {full}";

        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), args)
            {
                UseShellExecute = true,
            })?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo abrir {full}: {ex.Message}");
            return $"No se pudo abrir la carpeta: {ex.Message}";
        }
    }
}
