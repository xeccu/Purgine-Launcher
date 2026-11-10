using System.Text;
using Purgine.Infrastructure;
using Purgine.Library;

namespace Purgine.SteamNet;

/// <summary>
/// Registra en Steam un juego descargado por Purgine, para que el cliente oficial lo reconozca como
/// instalado y al día (y pueda lanzarlo sin volver a descargarlo):
/// <list type="bullet">
///   <item><c>steamapps/appmanifest_{appid}.acf</c> con StateFlags = 4, build e <c>InstalledDepots</c>.</item>
///   <item>Los manifiestos de depot en <c>Steam/depotcache</c>, como los guarda el propio Steam.</item>
/// </list>
/// Solo con Steam cerrado: abierto, mantiene su propia copia en memoria y la reescribiría.
/// </summary>
public static class AppManifestWriter
{
    /// <summary>DLC desactivados por el usuario (<c>UserConfig/DisabledDLC</c> del appmanifest, lista separada por comas).</summary>
    public static HashSet<uint> ReadDisabledDlc(string acfPath)
    {
        try
        {
            if (!File.Exists(acfPath)) return [];
            var raw = VdfParser.ParseFile(acfPath)["AppState"]?["UserConfig"]?.GetValue("DisabledDLC") ?? string.Empty;
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => uint.TryParse(s, out var id) ? id : 0).Where(id => id != 0).ToHashSet();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer DisabledDLC de {acfPath}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Guarda qué DLC están desactivados en <c>UserConfig/DisabledDLC</c> (como hace el cliente en Propiedades →
    /// DLC). Edita solo esa clave y conserva el resto del archivo. Hay que hacerlo con Steam cerrado (abierto,
    /// reescribe el manifiesto). Steam y Purgine dejan de instalar o actualizar los depots de esos DLC.
    /// </summary>
    public static void WriteDisabledDlc(string acfPath, IEnumerable<uint> disabled)
    {
        var text = File.ReadAllText(acfPath);
        var value = string.Join(",", disabled.Distinct().Order());
        var line = $"\t\t\"DisabledDLC\"\t\t\"{value}\"\n";

        var user = text.IndexOf("\"UserConfig\"", StringComparison.Ordinal);
        if (user >= 0)
        {
            var open = text.IndexOf('{', user);
            var close = text.IndexOf('}', open);
            var block = text[(open + 1)..close];
            var kept = block.Split('\n')
                .Where(l => l.Trim().Length > 0 && !l.Contains("\"DisabledDLC\"", StringComparison.Ordinal))
                .Select(l => l.TrimEnd('\r') + "\n");
            var newBlock = "\n" + string.Concat(kept) + (value.Length > 0 ? line : string.Empty);
            text = text[..(open + 1)] + newBlock + "\t" + text[close..];
        }
        else if (value.Length > 0)
        {
            var end = text.LastIndexOf('}');
            text = text[..end] + "\t\"UserConfig\"\n\t{\n" + line + "\t}\n" + text[end..];
        }

        var tmp = acfPath + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Copy(acfPath, acfPath + ".purgine.bak", overwrite: true);
        File.Move(tmp, acfPath, overwrite: true);
        Log.Info($"DLC desactivados en {Path.GetFileName(acfPath)}: {(value.Length == 0 ? "ninguno" : value)}");
    }

    public static void Write(string steamPath, uint appId, string name, DepotDownloadResult result, ulong? ownerSteamId64)
    {
        var acfPath = Path.Combine(result.LibraryPath, "steamapps", $"appmanifest_{appId}.acf");

        // Ajustes del usuario que Steam guarda en el manifiesto (se conservan al actualizar).
        string autoUpdate = "0", language = string.Empty, disabledDlc = string.Empty;
        if (File.Exists(acfPath))
        {
            try
            {
                var old = VdfParser.ParseFile(acfPath)["AppState"];
                autoUpdate = old?.GetValue("AutoUpdateBehavior") ?? "0";
                language = old?["UserConfig"]?.GetValue("language") ?? string.Empty;
                disabledDlc = old?["UserConfig"]?.GetValue("DisabledDLC") ?? string.Empty;
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
        if (language.Length > 0 || disabledDlc.Length > 0)
        {
            sb.Append("\t\"UserConfig\"\n\t{\n");
            if (language.Length > 0) Kv("\t\t", "language", language);
            if (disabledDlc.Length > 0) Kv("\t\t", "DisabledDLC", disabledDlc); // DLC desactivados (Propiedades)
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
