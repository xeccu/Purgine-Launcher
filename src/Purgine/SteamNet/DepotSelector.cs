using Microsoft.Win32;
using SteamKit2;

namespace Purgine.SteamNet;

/// <summary>Un depot que se instalaría: id, manifiesto público y tamaño instalado (bytes, 0 si PICS no lo da).</summary>
public sealed record SelectedDepot(uint DepotId, ulong ManifestId, ulong Size);

/// <summary>
/// Qué depots de una app se instalan en este equipo (Windows, 64 bits si hay, idioma de Steam o inglés, DLC
/// propios). Lo usan el motor de descargas y el cálculo del «espacio necesario», para que el tamaño mostrado
/// sea exactamente el de lo que se descargaría.
/// </summary>
public static class DepotSelector
{
    public static List<SelectedDepot> Select(KeyValue depotsNode, string language, Func<uint, bool> ownsDlc)
    {
        var candidates = new List<(SelectedDepot Depot, string? Arch, string? Lang)>();
        foreach (var depot in depotsNode.Children)
        {
            if (!uint.TryParse(depot.Name, out var depotId)) continue; // "branches", "baselanguages"…
            if (depot["depotfromapp"] != KeyValue.Invalid || depot["sharedinstall"].AsBoolean()) continue; // redistribuibles compartidos

            var config = depot["config"];
            var os = config["oslist"].Value;
            if (!string.IsNullOrEmpty(os) && !os.Contains("windows", StringComparison.OrdinalIgnoreCase)) continue;
            if (config["lowviolence"].AsBoolean()) continue;

            var dlc = depot["dlcappid"].AsUnsignedInteger();
            if (dlc != 0 && !ownsDlc(dlc)) continue;

            var publicManifest = depot["manifests"]["public"];
            var gid = publicManifest["gid"].Value ?? publicManifest.Value;
            if (!ulong.TryParse(gid, out var manifestId)) continue; // sin manifiesto público (p. ej. cifrado/beta)

            var size = publicManifest["size"].AsUnsignedLong();
            candidates.Add((new SelectedDepot(depotId, manifestId, size), config["osarch"].Value, config["language"].Value));
        }

        // Idioma: los neutros + el de Steam; si el juego no lo tiene, inglés.
        var hasLanguage = candidates.Any(c => string.Equals(c.Lang, language, StringComparison.OrdinalIgnoreCase));
        var chosenLanguage = hasLanguage ? language : "english";
        candidates.RemoveAll(c => !string.IsNullOrEmpty(c.Lang) && !string.Equals(c.Lang, chosenLanguage, StringComparison.OrdinalIgnoreCase));

        // Arquitectura: si hay depots de 64 bits, se descartan los de 32.
        if (candidates.Any(c => c.Arch == "64")) candidates.RemoveAll(c => c.Arch == "32");

        return candidates.Select(c => c.Depot).ToList();
    }

    /// <summary>Idioma configurado en Steam (Registro); inglés si no se conoce.</summary>
    public static string SteamLanguage()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return (key?.GetValue("Language") as string) is { Length: > 0 } lang ? lang : "english";
        }
        catch (Exception) { return "english"; }
    }
}
