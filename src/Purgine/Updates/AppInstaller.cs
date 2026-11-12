using System.Diagnostics;
using Microsoft.Win32;
using Purgine.Infrastructure;

namespace Purgine.Updates;

public enum SetupMode { Install, Update, Uninstall }

/// <summary>Qué tiene que hacer este proceso cuando se abre como instalador (ver <see cref="SetupRequest.FromArgs"/>).</summary>
public sealed record SetupRequest(SetupMode Mode, bool Silent, int? WaitPid, string? Target)
{
    /// <summary>
    /// El instalador es el propio Purgine.exe (un único archivo autocontenido). Entra en modo instalador si se le pasa
    /// <c>--install</c> / <c>--update</c> / <c>--uninstall</c> o si el archivo se llama como el instalador publicado
    /// (<c>Purgine-Setup-v0.1.0-alpha.exe</c>). Si no, null: arranque normal del launcher.
    /// </summary>
    public static SetupRequest? FromArgs(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        string? Value(string flag)
        {
            var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        SetupMode? mode = Has("--uninstall") ? SetupMode.Uninstall
            : Has("--update") ? SetupMode.Update
            : Has("--install") ? SetupMode.Install
            : AppInstaller.IsSetupFile(Environment.ProcessPath) ? SetupMode.Install
            : null;
        if (mode is null) return null;
        return new SetupRequest(mode.Value, Has("--silent"),
            int.TryParse(Value("--wait-pid"), out var pid) ? pid : null,
            Value("--target"));
    }
}

/// <summary>
/// Instalación para el usuario actual, sin permisos de administrador (como VS Code o Discord):
/// <c>%LocalAppData%\Programs\Purgine\Purgine.exe</c>, accesos directos en el menú Inicio y (opcional) el Escritorio,
/// y registro en «Aplicaciones instaladas» de Windows con su desinstalador. Los datos del usuario
/// (<c>%LocalAppData%\Purgine</c>) no se tocan al instalar ni al actualizar.
/// </summary>
public static class AppInstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Purgine";
    public const string Publisher = "XCC Studios";
    public const string Website = "https://xccstudios.es";

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Purgine");

    /// <summary>Carpeta registrada por una instalación anterior (o la de por defecto).</summary>
    public static string InstallDir
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string dir && dir.Length > 0) return dir;
            }
            catch (Exception) { /* sin registro: la de por defecto */ }
            return DefaultInstallDir;
        }
    }

    public static string InstalledExe => Path.Combine(InstallDir, "Purgine.exe");

    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Purgine.lnk");
    public static string DesktopShortcut => Path.Combine(ShortcutWriter.DesktopFolder, "Purgine.lnk");

    public static bool IsSetupFile(string? path) =>
        path is not null && Path.GetFileNameWithoutExtension(path).Contains("setup", StringComparison.OrdinalIgnoreCase);

    public static bool IsRegistered
    {
        get
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(UninstallKey); return key is not null; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Otros procesos de Purgine que usan ese ejecutable (o cualquier Purgine si no se puede saber su ruta).</summary>
    public static List<Process> RunningInstances(string exePath)
    {
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName("Purgine"))
        {
            if (p.Id == Environment.ProcessId) { p.Dispose(); continue; }
            string? path = null;
            try { path = p.MainModule?.FileName; } catch (Exception) { }
            if (path is null || string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                result.Add(p);
            else
                p.Dispose();
        }
        return result;
    }

    /// <summary>Espera a que termine el Purgine que lanzó la actualización; si no se cierra a tiempo, se finaliza.</summary>
    public static async Task WaitForProcessAsync(int pid, TimeSpan timeout, bool killIfStuck)
    {
        Process proc;
        try { proc = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; } // ya terminó
        using (proc)
        {
            using var cts = new CancellationTokenSource(timeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                if (!killIfStuck) throw new TimeoutException("Purgine sigue abierto.");
                Log.Warn($"Purgine (PID {pid}) no se cerró en {timeout.TotalSeconds:0} s; se finaliza para actualizar.");
                try { proc.Kill(entireProcessTree: false); proc.WaitForExit(10_000); } catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// Copia este ejecutable a <paramref name="targetExe"/> (reintentando si Windows aún lo tiene bloqueado), y si es la
    /// instalación registrada, actualiza el registro y los accesos directos.
    /// </summary>
    public static async Task InstallAsync(string targetExe, bool createDesktopShortcut, bool register, IProgress<string> status, CancellationToken ct)
    {
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("No se conoce la ruta del instalador.");
        var dir = Path.GetDirectoryName(targetExe)!;
        Directory.CreateDirectory(dir);

        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(targetExe), StringComparison.OrdinalIgnoreCase))
        {
            status.Report("Copiando archivos…");
            var staging = targetExe + ".new";
            await Task.Run(() => File.Copy(source, staging, overwrite: true), ct);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(staging, targetExe, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 30)
                {
                    status.Report("Esperando a que Windows libere Purgine.exe…");
                    await Task.Delay(500, ct);
                }
            }
            Log.Info($"Purgine {AppVersion.Display} copiado a {targetExe}.");
        }

        if (register)
        {
            status.Report("Creando accesos directos…");
            ShortcutWriter.CreateAppShortcut(StartMenuShortcut, targetExe, "Purgine: tus juegos de Steam, Epic Games e itch.io");
            if (createDesktopShortcut || File.Exists(DesktopShortcut))
                ShortcutWriter.CreateAppShortcut(DesktopShortcut, targetExe, "Purgine: tus juegos de Steam, Epic Games e itch.io");
            status.Report("Registrando Purgine en Windows…");
            Register(targetExe);
        }
    }

    private static void Register(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var dir = Path.GetDirectoryName(exe)!;
        key.SetValue("DisplayName", "Purgine");
        key.SetValue("DisplayVersion", AppVersion.Display);
        key.SetValue("Publisher", Publisher);
        key.SetValue("URLInfoAbout", Website);
        key.SetValue("HelpLink", UpdateService.ReleasesPage);
        key.SetValue("DisplayIcon", $"\"{exe}\",0");
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        try { key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord); } catch (IOException) { }
    }

    /// <summary>Si es la instalación registrada, refresca su versión en «Aplicaciones instaladas».</summary>
    public static bool IsRegisteredExe(string exe) =>
        IsRegistered && string.Equals(Path.GetFullPath(exe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Quita los accesos directos (los de Purgine y los de juegos que creó en el menú Inicio), el registro y, si se
    /// pide, los datos del usuario (perfiles, sesiones, ajustes). Los juegos instalados no se tocan: tampoco los de
    /// itch.io de <c>ItchLibrary</c>. La carpeta del programa se borra al salir (el .exe está en uso).
    /// </summary>
    public static void Uninstall(bool deleteUserData)
    {
        foreach (var lnk in new[] { StartMenuShortcut, DesktopShortcut })
            TryDelete(() => { if (File.Exists(lnk)) File.Delete(lnk); });
        TryDelete(() => { if (Directory.Exists(ShortcutWriter.StartMenuFolder)) Directory.Delete(ShortcutWriter.StartMenuFolder, recursive: true); });
        TryDelete(() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false));

        if (deleteUserData)
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Purgine");
            if (Directory.Exists(data))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(data))
                {
                    if (Path.GetFileName(entry).Equals("ItchLibrary", StringComparison.OrdinalIgnoreCase)) continue;
                    TryDelete(() =>
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                    });
                }
            }
        }
    }

    /// <summary>Borra la carpeta del programa cuando este proceso ya haya terminado (un .exe no puede borrarse a sí mismo).</summary>
    public static void ScheduleProgramFolderRemoval()
    {
        var self = Environment.ProcessPath;
        var dir = InstallDir;
        if (self is null || !Path.GetFullPath(self).StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(() => { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); });
            return;
        }
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{dir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        })?.Dispose();
    }

    public static void Launch(string exe) =>
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();

    private static void TryDelete(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Warn($"Desinstalar: {ex.Message}"); }
    }
}
