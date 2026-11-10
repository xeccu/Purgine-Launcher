using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Purgine.Infrastructure;

/// <summary>
/// Accesos directos (.lnk) a un juego. Apuntan a <c>Purgine.exe --run {appid}</c> y no a <c>steam://</c>, para
/// que la partida pase por Purgine (Steam se abre solo para jugar y se cierra al terminar, tiempo jugado…).
/// Usa la interfaz COM IShellLink de Windows directamente (sin WSH ni dependencias).
/// </summary>
public static class ShortcutWriter
{
    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Menú Inicio → Programas → Purgine.</summary>
    public static string StartMenuFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Purgine");

    /// <summary>Crea (o reemplaza) <c>{carpeta}\{nombre}.lnk</c>. Devuelve la ruta o null si falló.</summary>
    public static string? CreateGameShortcut(string folder, string gameName, string appId, string? iconPath)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return null;
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, SafeFileName(gameName) + ".lnk");
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(exe);
                link.SetArguments($"--run {appId}");
                link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
                link.SetDescription($"Jugar a {gameName} con Purgine");
                link.SetIconLocation(iconPath is not null && File.Exists(iconPath) ? iconPath : exe, 0);
                ((IPersistFile)link).Save(path, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
            Log.Info($"Acceso directo creado: {path}");
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo crear el acceso directo de {gameName} en {folder}: {ex.Message}");
            return null;
        }
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = name.Replace("™", string.Empty).Replace("®", string.Empty).Trim();
        return name.Length == 0 ? "Juego" : name;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
