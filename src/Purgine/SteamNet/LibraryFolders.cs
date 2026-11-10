using System.Text;
using Purgine.Infrastructure;
using Purgine.Library;

namespace Purgine.SteamNet;

/// <summary>
/// Bibliotecas de Steam por unidad: elegir dónde instalar en un disco concreto y, si ese disco aún no tiene
/// biblioteca, crearla (<c>X:\SteamLibrary</c>) y registrarla en <c>steamapps\libraryfolders.vdf</c> para que
/// el cliente oficial vea los juegos que Purgine instale allí.
/// </summary>
public static class LibraryFolders
{
    private static readonly object Gate = new();

    /// <summary>Biblioteca existente en esa unidad o, si no hay, la que se crearía (<c>X:\SteamLibrary</c>).</summary>
    public static string ForDrive(IEnumerable<string> existingLibraries, string driveRoot)
    {
        var root = Path.GetPathRoot(driveRoot) ?? driveRoot;
        var onDrive = existingLibraries.FirstOrDefault(l =>
            string.Equals(Path.GetPathRoot(l), root, StringComparison.OrdinalIgnoreCase));
        return onDrive ?? Path.Combine(root, "SteamLibrary");
    }

    /// <summary>
    /// Asegura que la biblioteca existe en disco y figura en <c>libraryfolders.vdf</c>. Steam reescribe ese
    /// archivo al cerrarse, así que se llama cada vez que se registra un juego (también al cerrarse Steam).
    /// </summary>
    public static void EnsureRegistered(string steamPath, string libraryPath)
    {
        lock (Gate)
        {
            libraryPath = Path.GetFullPath(libraryPath).TrimEnd(Path.DirectorySeparatorChar);
            Directory.CreateDirectory(Path.Combine(libraryPath, "steamapps", "common"));

            if (string.Equals(libraryPath, Path.GetFullPath(steamPath).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return; // la biblioteca principal no se declara aparte

            // Marca de biblioteca que el cliente crea en cada una (la usa para reconocerla).
            var marker = Path.Combine(libraryPath, "libraryfolder.vdf");
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker,
                    $"\"libraryfolder\"\n{{\n\t\"contentid\"\t\t\"{NewContentId()}\"\n\t\"label\"\t\t\"\"\n}}\n");
            }

            var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) return; // instalación de Steam anómala: no se inventa el archivo

            var text = File.ReadAllText(vdfPath);
            var libs = VdfParser.Parse(text)["libraryfolders"];
            if (libs is null) return;
            if (libs.Children.Values.Any(c => SamePath(c.GetValue("path"), libraryPath))) return;

            var next = libs.Children.Keys.Select(k => int.TryParse(k, out var n) ? n : -1).DefaultIfEmpty(-1).Max() + 1;
            var entry = new StringBuilder()
                .Append($"\t\"{next}\"\n\t{{\n")
                .Append($"\t\t\"path\"\t\t\"{libraryPath.Replace("\\", "\\\\")}\"\n")
                .Append("\t\t\"label\"\t\t\"\"\n")
                .Append($"\t\t\"contentid\"\t\t\"{NewContentId()}\"\n")
                .Append("\t\t\"totalsize\"\t\t\"0\"\n")
                .Append("\t\t\"update_clean_bytes_tally\"\t\t\"0\"\n")
                .Append("\t\t\"time_last_update_verified\"\t\t\"0\"\n")
                .Append("\t\t\"apps\"\n\t\t{\n\t\t}\n")
                .Append("\t}\n")
                .ToString();

            var close = text.LastIndexOf('}');
            if (close < 0) return;
            var updated = text[..close] + entry + text[close..];
            var tmp = vdfPath + ".purgine.tmp";
            File.WriteAllText(tmp, updated);
            File.Copy(vdfPath, vdfPath + ".purgine.bak", overwrite: true); // copia de seguridad del original
            File.Move(tmp, vdfPath, overwrite: true);
            Log.Info($"Biblioteca de Steam registrada: {libraryPath}");
        }
    }

    private static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a.Replace('/', '\\')).TrimEnd('\\'), b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private static string NewContentId() => ((ulong)Random.Shared.NextInt64(long.MaxValue)).ToString();
}
