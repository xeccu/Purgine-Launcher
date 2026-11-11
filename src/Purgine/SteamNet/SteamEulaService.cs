using System.Text;
using Purgine.Core;
using Purgine.Infrastructure;
using Purgine.Library;
using SteamKit2;

namespace Purgine.SteamNet;

/// <summary>Contrato de licencia (EULA) de un juego de Steam, tal como lo publica Steam en los datos del juego.</summary>
public sealed record SteamEula(string Id, string Name, string Url, int Version);

/// <summary>
/// Contratos de licencia de los juegos de Steam sin abrir el cliente:
/// <list type="bullet">
///   <item>Qué contratos tiene un juego: PICS, <c>common/eulas</c> (id, nombre, enlace y versión).</item>
///   <item>Cuáles aceptó ya el usuario: Steam lo anota en <c>userdata\&lt;cuenta&gt;\config\localconfig.vdf</c>,
///         en <c>UserLocalConfigStore/Software/Valve/Steam/apps/&lt;appid&gt;</c>, como
///         <c>"&lt;id&gt;" "&lt;versión aceptada&gt;"</c> (p. ej. <c>"359550_eula_1" "7"</c>).</item>
///   <item>Aceptar (siempre tras el «Aceptar» del usuario en Purgine): se escribe esa misma línea, con Steam
///         cerrado (si no, la sobrescribiría al salir) y con copia previa del archivo. Al abrirse, Steam lo da por
///         aceptado y no muestra su ventana de contrato.</item>
/// </list>
/// </summary>
public sealed class SteamEulaService(SteamSession session, Func<string?> steamPath, string backupDir)
{
    /// <summary>Contratos del juego que faltan por aceptar (o cuya versión es más nueva que la aceptada).</summary>
    public async Task<IReadOnlyList<SteamEula>> GetPendingAsync(uint appId, CancellationToken ct)
    {
        var all = await GetEulasAsync(appId, ct).ConfigureAwait(false);
        if (all.Count == 0) return all;
        var config = SteamPlaytimeReader.FindLocalConfig(steamPath());
        if (config is null) return []; // sin datos de usuario no se puede saber: que decida Steam
        var accepted = ReadAccepted(config, appId);
        return all.Where(e => !accepted.TryGetValue(e.Id, out var v) || v < e.Version).ToList();
    }

    public async Task<IReadOnlyList<SteamEula>> GetEulasAsync(uint appId, CancellationToken ct)
    {
        // La sesión se desconecta tras unos minutos sin uso: hay que conectarla antes de pedir datos del juego.
        if (!await session.LoginWithSavedSessionAsync(ct).ConfigureAwait(false)) return [];
        Dictionary<uint, KeyValue> info;
        session.Hold();
        try { info = await session.GetAppInfoAsync([appId], ct).ConfigureAwait(false); }
        finally { session.Release(); }
        if (!info.TryGetValue(appId, out var kv)) return [];
        var eulas = kv["common"]["eulas"];
        if (eulas == KeyValue.Invalid) return [];
        var list = new List<SteamEula>();
        foreach (var e in eulas.Children)
        {
            var id = e["id"].Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            list.Add(new SteamEula(id, e["name"].Value ?? id,
                (e["url"].Value ?? $"https://store.steampowered.com/eula/{id}").Replace("//eula", "/eula"),
                int.TryParse(e["version"].Value, out var v) ? v : 0));
        }
        return list;
    }

    /// <summary>Contratos aceptados de un juego según <c>localconfig.vdf</c> (id → versión).</summary>
    public static Dictionary<string, int> ReadAccepted(string localConfig, uint appId)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var app = VdfParser.ParseFile(localConfig)["UserLocalConfigStore"]?["Software"]?["Valve"]?["Steam"]?["apps"]?[appId.ToString()];
            if (app is null) return map;
            foreach (var (key, value) in app.Values)
                if (key.Contains("_eula_", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var v))
                    map[key] = v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Log.Warn($"Contratos de Steam: no se pudo leer {localConfig}: {ex.Message}");
        }
        return map;
    }

    /// <summary>
    /// Anota en <c>localconfig.vdf</c> los contratos aceptados por el usuario. Steam debe estar cerrado.
    /// Devuelve null si todo fue bien o el motivo del fallo.
    /// </summary>
    public string? Accept(uint appId, IReadOnlyList<SteamEula> eulas)
    {
        if (SteamLocator.IsClientRunning()) return "Steam sigue abierto: no se puede anotar el contrato.";
        var config = SteamPlaytimeReader.FindLocalConfig(steamPath());
        if (config is null) return "No se encontró la configuración de usuario de Steam (localconfig.vdf).";
        try
        {
            Directory.CreateDirectory(backupDir);
            File.Copy(config, Path.Combine(backupDir, $"localconfig-{DateTime.Now:yyyyMMdd-HHmmss}.vdf"), overwrite: true);
            foreach (var old in Directory.GetFiles(backupDir, "localconfig-*.vdf").OrderByDescending(f => f).Skip(10)) File.Delete(old);

            var text = File.ReadAllText(config);
            var updated = SetAppValues(text, appId, eulas.Select(e => (e.Id, e.Version.ToString())).ToList());
            var temp = config + ".purgine-tmp";
            File.WriteAllText(temp, updated, new UTF8Encoding(false));
            // Comprobación antes de sustituir: el archivo nuevo debe leerse y tener los contratos.
            var check = ReadAccepted(temp, appId);
            if (eulas.Any(e => !check.TryGetValue(e.Id, out var v) || v < e.Version))
            {
                File.Delete(temp);
                return "No se pudo verificar el cambio en localconfig.vdf (no se ha tocado).";
            }
            File.Move(temp, config, overwrite: true);
            Log.Info($"Contratos de Steam aceptados por el usuario para {appId}: {string.Join(", ", eulas.Select(e => $"{e.Id} v{e.Version}"))}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("No se pudo anotar el contrato de Steam", ex);
            return $"No se pudo anotar el contrato: {ex.Message}";
        }
    }

    /// <summary>
    /// Pone (o actualiza) valores en <c>UserLocalConfigStore/Software/Valve/Steam/apps/&lt;appid&gt;</c> editando el
    /// texto en su sitio: el resto del archivo queda byte a byte igual. Crea los bloques que falten.
    /// </summary>
    public static string SetAppValues(string text, uint appId, IReadOnlyList<(string Key, string Value)> values)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(nl).ToList();
        string[] path = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps", appId.ToString()];

        // Busca el bloque path[0..depth] y devuelve (línea de «{», línea de «}») o null.
        (int Open, int Close)? FindBlock(int depth)
        {
            int start = 0, end = lines.Count - 1;
            (int, int)? found = null;
            for (var level = 0; level <= depth; level++)
            {
                found = null;
                var nesting = 0;
                for (var i = start; i <= end; i++)
                {
                    var t = lines[i].Trim();
                    if (t == "{") { nesting++; continue; }
                    if (t == "}") { nesting--; continue; }
                    if (nesting != 0) continue;
                    if (!IsKeyLine(t, path[level])) continue;
                    var open = i + 1;
                    while (open <= end && lines[open].Trim().Length == 0) open++;
                    if (open > end || lines[open].Trim() != "{") continue;
                    var close = MatchClose(open);
                    if (close < 0) return null;
                    found = (open, close);
                    break;
                }
                if (found is null) return null;
                (start, end) = (found.Value.Item1 + 1, found.Value.Item2 - 1);
            }
            return found;
        }

        int MatchClose(int open)
        {
            var nesting = 0;
            for (var i = open; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t == "{") nesting++;
                else if (t == "}" && --nesting == 0) return i;
            }
            return -1;
        }

        string Indent(int depth) => new('\t', depth);

        // Asegura que existe cada nivel (crea los que falten dentro del nivel anterior).
        for (var depth = 0; depth < path.Length; depth++)
        {
            if (FindBlock(depth) is not null) continue;
            if (depth == 0) throw new FormatException("localconfig.vdf no tiene UserLocalConfigStore.");
            var parent = FindBlock(depth - 1)!.Value;
            lines.InsertRange(parent.Open + 1,
            [
                $"{Indent(depth)}\"{path[depth]}\"",
                $"{Indent(depth)}{{",
                $"{Indent(depth)}}}",
            ]);
        }

        var app = FindBlock(path.Length - 1)!.Value;
        foreach (var (key, value) in values)
        {
            var replaced = false;
            var nesting = 0;
            for (var i = app.Open + 1; i < app.Close; i++)
            {
                var t = lines[i].Trim();
                if (t == "{") { nesting++; continue; }
                if (t == "}") { nesting--; continue; }
                if (nesting != 0 || !IsKeyLine(t, key, valueLine: true)) continue;
                lines[i] = $"{Indent(path.Length)}\"{key}\"\t\t\"{value}\"";
                replaced = true;
                break;
            }
            if (replaced) continue;
            lines.Insert(app.Open + 1, $"{Indent(path.Length)}\"{key}\"\t\t\"{value}\"");
            app = (app.Open, app.Close + 1);
        }
        return string.Join(nl, lines);
    }

    /// <summary>¿La línea es <c>"clave"</c> (bloque) o <c>"clave" "valor"</c>? Sin distinguir mayúsculas, como Steam.</summary>
    private static bool IsKeyLine(string trimmed, string key, bool valueLine = false)
    {
        if (!trimmed.StartsWith('"')) return false;
        var end = trimmed.IndexOf('"', 1);
        if (end < 0 || !trimmed[1..end].Equals(key, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = trimmed[(end + 1)..].Trim();
        return valueLine ? rest.StartsWith('"') : rest.Length == 0;
    }
}
