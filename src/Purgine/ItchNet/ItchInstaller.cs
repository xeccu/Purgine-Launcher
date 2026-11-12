using System.Diagnostics;
using System.IO.Compression;
using Purgine.Infrastructure;
using Purgine.Library;

namespace Purgine.ItchNet;

/// <summary>Juego de itch.io instalado por Purgine.</summary>
public sealed class ItchInstallRecord
{
    public string Key { get; set; } = string.Empty;          // «autor/juego»
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public long? GameId { get; set; }
    public long? DownloadKeyId { get; set; }
    public long? UploadId { get; set; }
    public string InstallDir { get; set; } = string.Empty;
    public string? Executable { get; set; }
    public string? CoverUrl { get; set; }
    public long SizeOnDisk { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
}

/// <summary>Registro (<c>itch-installs.json</c>) de los juegos de itch.io instalados por Purgine.</summary>
public static class ItchRegistry
{
    private static readonly object Gate = new();

    public static List<ItchInstallRecord> Load()
    {
        lock (Gate) return JsonFileStore.Load<List<ItchInstallRecord>>(AppPaths.ItchInstallsFile);
    }

    public static void Upsert(ItchInstallRecord record)
    {
        lock (Gate)
        {
            var all = JsonFileStore.Load<List<ItchInstallRecord>>(AppPaths.ItchInstallsFile);
            all.RemoveAll(r => r.Key == record.Key);
            all.Add(record);
            JsonFileStore.Save(AppPaths.ItchInstallsFile, all);
        }
    }

    public static void Remove(string key)
    {
        lock (Gate)
        {
            var all = JsonFileStore.Load<List<ItchInstallRecord>>(AppPaths.ItchInstallsFile);
            if (all.RemoveAll(r => r.Key == key) > 0) JsonFileStore.Save(AppPaths.ItchInstallsFile, all);
        }
    }

    /// <summary>Juegos instalados (con su carpeta en disco) como entradas de la biblioteca.</summary>
    public static List<GameEntry> ScanInstalled() =>
        Load().Where(r => Directory.Exists(r.InstallDir)).Select(r => new GameEntry
        {
            Id = GameEntry.MakeItchId(r.Key),
            Platform = GamePlatform.Itch,
            AppId = r.Key,
            Name = r.Title,
            InstallDir = r.InstallDir,
            ExecutablePath = r.Executable,
            WorkingDirectory = r.Executable is null ? null : Path.GetDirectoryName(r.Executable),
            SizeOnDisk = r.SizeOnDisk,
            ItchGameId = r.GameId,
            ItchDownloadKeyId = r.DownloadKeyId,
            ItchUrl = r.Url,
            ItchCoverUrl = r.CoverUrl,
        }).ToList();
}

/// <summary>
/// Instalación de un archivo de itch.io: descomprime <c>.zip</c> (nativo) y <c>.7z</c> / <c>.rar</c> / <c>.tar.*</c>
/// (con <c>tar.exe</c> de Windows, basado en libarchive, o 7-Zip si está instalado) en su carpeta, borra el
/// comprimido y localiza el ejecutable principal (descartando desinstaladores, crash handlers y redistribuibles).
/// </summary>
public static class ItchInstaller
{
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz"];

    private static readonly string[] IgnoredExe =
    [
        "unins", "uninstall", "crashhandler", "crashreport", "crashpad", "redist", "vc_redist", "vcredist", "dxsetup",
        "dxwebsetup", "directx", "dotnet", "ue4prereq", "ueprereq", "prereq", "setup", "installer", "updater", "notification_helper",
        "unitycrashhandler", "crs-handler", "python", "pythonw", "java", "javaw", "nwjc", "payload", "helper", "elevate",
    ];

    public static bool IsArchive(string path)
    {
        var lower = path.ToLowerInvariant();
        return ArchiveExtensions.Any(lower.EndsWith);
    }

    /// <summary>
    /// Deja el juego listo en <paramref name="installDir"/>: descomprime el archivo (y lo borra) o lo mueve allí si
    /// ya es el ejecutable. Devuelve el ejecutable principal (o null si no se encontró ninguno).
    /// </summary>
    public static string? Install(string downloadedFile, string installDir, string title)
    {
        Directory.CreateDirectory(installDir);
        if (IsArchive(downloadedFile))
        {
            Extract(downloadedFile, installDir);
            TryDelete(downloadedFile);
            // .tar.gz / .tar.xz con 7-Zip: la primera pasada deja un único .tar dentro → otra pasada.
            for (var i = 0; i < 2 && Directory.GetFileSystemEntries(installDir) is [var only] && File.Exists(only) && IsArchive(only); i++)
            {
                Extract(only, installDir);
                TryDelete(only);
            }
            FlattenSingleFolder(installDir);
        }
        else
        {
            var target = Path.Combine(installDir, Path.GetFileName(downloadedFile));
            if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(downloadedFile), StringComparison.OrdinalIgnoreCase))
                File.Move(downloadedFile, target, overwrite: true);
        }
        return FindMainExecutable(installDir, title);
    }

    private static void Extract(string archive, string dest)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, dest, overwriteFiles: true);
            return;
        }
        // 7-Zip si está instalado (el más compatible con .rar modernos); si no, tar.exe de Windows (libarchive).
        var sevenZip = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        }.FirstOrDefault(File.Exists);
        var (exe, args) = sevenZip is not null
            ? (sevenZip, $"x -y \"-o{dest}\" \"{archive}\"")
            : (Path.Combine(Environment.SystemDirectory, "tar.exe"), $"-xf \"{archive}\" -C \"{dest}\"");
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        }) ?? throw new IOException("No se pudo iniciar el descompresor.");
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new IOException($"No se pudo descomprimir {Path.GetFileName(archive)} ({Path.GetFileName(exe)}: {err.Result.Trim()}). " +
                                  "Instala 7-Zip para los .rar/.7z que Windows no abre.");
    }

    /// <summary>Si todo quedó dentro de una única carpeta, se sube un nivel (rutas más cortas y limpias).</summary>
    private static void FlattenSingleFolder(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        if (entries.Length != 1 || !Directory.Exists(entries[0])) return;
        var inner = entries[0];
        foreach (var e in Directory.GetFileSystemEntries(inner))
        {
            var target = Path.Combine(dir, Path.GetFileName(e));
            if (Directory.Exists(e)) Directory.Move(e, target); else File.Move(e, target);
        }
        Directory.Delete(inner);
    }

    /// <summary>
    /// Ejecutable principal: se descartan desinstaladores, crash handlers, instaladores de requisitos y ayudantes;
    /// gana el que se parece al nombre del juego y, después, el menos profundo y el más grande.
    /// </summary>
    public static string? FindMainExecutable(string dir, string title)
    {
        if (!Directory.Exists(dir)) return null;
        var norm = Normalize(title);
        var candidates = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
                var rel = Path.GetRelativePath(dir, f.FullName).ToLowerInvariant();
                return !IgnoredExe.Any(name.Contains) && !rel.Contains("redist") && !rel.Contains("_commonredist") &&
                       !rel.Contains("\\engine\\binaries\\thirdparty");
            })
            .ToList();
        if (candidates.Count == 0) return null;
        return candidates
            .OrderByDescending(f => Normalize(Path.GetFileNameWithoutExtension(f.Name)) is var n && n.Length > 2 &&
                                    (norm.Contains(n) || n.Contains(norm)))
            .ThenBy(f => Path.GetRelativePath(dir, f.FullName).Count(c => c == '\\'))
            .ThenByDescending(f => f.Length)
            .First().FullName;
    }

    /// <summary>Carpeta de un juego dentro de la biblioteca de itch.io.</summary>
    public static string FolderFor(string libraryRoot, string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(title.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return Path.Combine(libraryRoot, clean.Length == 0 ? "Juego" : clean);
    }

    public static long DirectorySize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch (Exception) { return 0; }
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException ex) { Log.Warn($"No se pudo borrar {path}: {ex.Message}"); }
    }
}
