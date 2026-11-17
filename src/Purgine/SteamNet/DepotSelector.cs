using Microsoft.Win32;
using SteamKit2;

namespace Purgine.SteamNet;

/// <summary>
/// Un depot que se instalaría: id, manifiesto público, tamaño instalado y de descarga (bytes) según PICS.
/// <see cref="DlcAppId"/> ≠ 0: pertenece a ese DLC. <see cref="Optional"/>: DLC opcional (p. ej. texturas en alta
/// resolución), que Steam no instala salvo que se marque. <see cref="SizeKnown"/> = false si PICS no da su tamaño.
/// </summary>
public sealed record SelectedDepot(uint DepotId, ulong ManifestId, ulong Size, ulong DownloadSize = 0,
    uint DlcAppId = 0, bool Optional = false, bool SizeKnown = true);

/// <summary>
/// Qué depots de una app se instalan en este equipo (Windows, 64 bits si hay, idioma de Steam o inglés, DLC
/// propios y solo los depots que incluyen las licencias de la cuenta). Lo usan el motor de descargas, el cálculo
/// del tamaño y el selector de componentes, para que el tamaño mostrado sea exactamente el de lo que se descargaría.
/// </summary>
public static class DepotSelector
{
    /// <param name="ownsDepot">
    /// Depots que incluyen las licencias de la cuenta (PICS de los paquetes, <c>depotids</c>). Algunos juegos publican
    /// una copia completa por región (Black Desert tiene nueve de ~85 GB) y solo se descarga la de tu licencia; sin
    /// este filtro se sumaban todas (750 GB). Null o sin datos de esta app = sin filtrar.
    /// </param>
    public static List<SelectedDepot> Select(KeyValue depotsNode, string language, Func<uint, bool> ownsDlc,
        Func<uint, bool>? ownsDepot = null)
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
            if (config["realm"].Value is { Length: > 0 } realm && !realm.Equals("steamglobal", StringComparison.OrdinalIgnoreCase)) continue; // Steam China

            var dlc = depot["dlcappid"].AsUnsignedInteger();
            if (dlc != 0 && !ownsDlc(dlc)) continue;
            var optional = config["optionaldlc"].AsUnsignedInteger() != 0;
            if (optional && dlc == 0)
            {
                dlc = config["optionaldlc"].AsUnsignedInteger();
                if (!ownsDlc(dlc)) continue;
            }

            var publicManifest = depot["manifests"]["public"];
            var gid = publicManifest["gid"].Value ?? publicManifest.Value;
            if (!ulong.TryParse(gid, out var manifestId)) continue; // sin manifiesto público (p. ej. cifrado/beta)

            var sizeKnown = publicManifest["size"] != KeyValue.Invalid;
            var size = publicManifest["size"].AsUnsignedLong();
            var download = publicManifest["download"].AsUnsignedLong();
            candidates.Add((new SelectedDepot(depotId, manifestId, size, download, dlc, optional, sizeKnown),
                config["osarch"].Value, config["language"].Value));
        }

        // Licencias: solo los depots que incluye alguna licencia de la cuenta (los de los DLC propios, siempre).
        if (ownsDepot is not null && candidates.Any(c => c.Depot.DlcAppId == 0 && ownsDepot(c.Depot.DepotId)))
            candidates.RemoveAll(c => c.Depot.DlcAppId == 0 && !ownsDepot(c.Depot.DepotId));

        // Idioma: los neutros + el de Steam; si el juego no lo tiene, inglés.
        var hasLanguage = candidates.Any(c => string.Equals(c.Lang, language, StringComparison.OrdinalIgnoreCase));
        var chosenLanguage = hasLanguage ? language : "english";
        candidates.RemoveAll(c => !string.IsNullOrEmpty(c.Lang) && !string.Equals(c.Lang, chosenLanguage, StringComparison.OrdinalIgnoreCase));

        // Arquitectura: si hay depots de 64 bits, se descartan los de 32.
        if (candidates.Any(c => c.Arch == "64")) candidates.RemoveAll(c => c.Arch == "32");

        return candidates.Select(c => c.Depot).ToList();
    }

    /// <summary>
    /// Tamaño de lo que se instala por defecto (todo menos los DLC opcionales y los desactivados), o null si algún
    /// depot no publica su tamaño: así nunca se muestra una cifra incompleta.
    /// </summary>
    public static (ulong Install, ulong Download)? DefaultSize(IEnumerable<SelectedDepot> depots, ICollection<uint>? disabledDlc = null,
        Func<uint, bool>? optionalDlc = null)
    {
        ulong install = 0, download = 0;
        foreach (var d in depots)
        {
            if (d.Optional || (d.DlcAppId != 0 && (disabledDlc?.Contains(d.DlcAppId) == true || optionalDlc?.Invoke(d.DlcAppId) == true))) continue;
            if (!d.SizeKnown) return null;
            install += d.Size;
            download += d.DownloadSize;
        }
        return install == 0 ? null : (install, download);
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
