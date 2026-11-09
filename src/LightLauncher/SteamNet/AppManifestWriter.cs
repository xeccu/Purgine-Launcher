using System.Text;
using LightLauncher.Infrastructure;
using LightLauncher.Library;

namespace LightLauncher.SteamNet;

/// <summary>
/// Registra en Steam un juego descargado por LightLauncher, para que el cliente oficial lo reconozca como
/// instalado y al día (y pueda lanzarlo sin volver a descargarlo):
/// <list type="bullet">
///   <item><c>steamapps/appmanifest_{appid}.acf</c> con StateFlags = 4, build e <c>InstalledDepots</c>.</item>
///   <item>Los manifiestos de depot en <c>Steam/depotcache</c>, como los guarda el propio Steam.</item>
/// </list>
/// Solo con Steam cerrado: abierto, mantiene su propia copia en memoria y la reescribiría.
/// </summary>
public static class AppManifestWriter
{
    public static void Write(string steamPath, uint appId, string name, DepotDownloadResult result, ulong? ownerSteamId64)
    {
        var acfPath = Path.Combine(result.LibraryPath, "steamapps", $"appmanifest_{appId}.acf");

        // Ajustes del usuario que Steam guarda en el manifiesto (se conservan al actualizar).
        string autoUpdate = "0", language = string.Empty;
        if (File.Exists(acfPath))
        {
            try
            {
                var old = VdfParser.ParseFile(acfPath)["AppState"];
                autoUpdate = old?.GetValue("AutoUpdateBehavior") ?? "0";
                language = old?["UserConfig"]?.GetValue("language") ?? string.Empty;
            }
            catch (Exception ex) { Log.Warn($"Manifiesto anterior ilegible ({acfPath}): {ex.Message}"); }
        }

        var installPath = Path.Combine(result.LibraryPath, "steamapps", "common", result.InstallDirName);
        var sizeOnDisk = DirectorySize(installPath);

        var sb = new StringBuilder();
        sb.Append("\"AppState\"\n{\n");
        void Kv(string indent, string k, string v) => sb.Append(indent).Append('"').Append(k).Append("\"\t\t\"").Append(Escape(v)).Append("\"\n");
        Kv("\t", "appid", appId.ToString());
        Kv("\t", "Universe", "1");
        Kv("\t", "name", name);
        Kv("\t", "StateFlags", "4");
        Kv("\t", "installdir", result.InstallDirName);
        Kv("\t", "LastUpdated", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        Kv("\t", "SizeOnDisk", sizeOnDisk.ToString());
        Kv("\t", "StagingSize", "0");
        Kv("\t", "buildid", result.BuildId.ToString());
        if (ownerSteamId64 is { } owner) Kv("\t", "LastOwner", owner.ToString());
        Kv("\t", "BytesToDownload", "0");
        Kv("\t", "BytesDownloaded", "0");
        Kv("\t", "BytesToStage", "0");
        Kv("\t", "BytesStaged", "0");
        Kv("\t", "TargetBuildID", "0");
        Kv("\t", "AutoUpdateBehavior", autoUpdate);
        Kv("\t", "AllowOtherDownloadsWhileRunning", "0");
        Kv("\t", "ScheduledAutoUpdate", "0");
        sb.Append("\t\"InstalledDepots\"\n\t{\n");
        foreach (var (depotId, manifestId, size, _) in result.Depots)
        {
            sb.Append("\t\t\"").Append(depotId).Append("\"\n\t\t{\n");
            Kv("\t\t\t", "manifest", manifestId.ToString());
            Kv("\t\t\t", "size", size.ToString());
            sb.Append("\t\t}\n");
        }
        sb.Append("\t}\n");
        if (language.Length > 0)
        {
            sb.Append("\t\"UserConfig\"\n\t{\n");
            Kv("\t\t", "language", language);
            sb.Append("\t}\n");
        }
        sb.Append("}\n");

        var tmp = acfPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, acfPath, overwrite: true);

        // Manifiestos de depot para que Steam no tenga que descargarlos ni verificar.
        var depotCache = Path.Combine(steamPath, "depotcache");
        Directory.CreateDirectory(depotCache);
        foreach (var (depotId, manifestId, _, manifest) in result.Depots)
        {
            try { manifest.SaveToFile(Path.Combine(depotCache, $"{depotId}_{manifestId}.manifest")); }
            catch (Exception ex) { Log.Warn($"No se pudo guardar el manifiesto {depotId}_{manifestId}: {ex.Message}"); }
        }

        Log.Info($"Juego registrado en Steam: {name} ({appId}), build {result.BuildId}, {result.Depots.Count} depots.");
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                : 0;
        }
        catch (Exception) { return 0; }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
