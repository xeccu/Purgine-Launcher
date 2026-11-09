using System.Text;
using LightLauncher.Infrastructure;

namespace LightLauncher.Library;

/// <summary>Datos básicos de una app según la caché local de Steam.</summary>
/// <param name="Type">"game", "tool", "dlc", "demo", "music", "application"… (en minúsculas).</param>
/// <param name="InstallDir">Nombre de la carpeta bajo <c>steamapps/common</c> que usará Steam.</param>
public sealed record SteamAppBasics(string AppId, string Name, string Type, string? InstallDir);

/// <summary>
/// Lector del formato binario de <c>appcache/appinfo.vdf</c> (versiones 27, 28 y 29), donde Steam
/// cachea la metainformación de las apps: nombre, tipo y carpeta de instalación. Solo lectura;
/// se extraen esos tres campos y se descarta el resto.
/// <para>Formato: cabecera (magic, universo[, offset de la tabla de cadenas en v29]) y una lista de
/// entradas (appid, tamaño, metadatos, KeyValues binario). En v29 las claves son índices a una tabla
/// de cadenas situada al final del archivo.</para>
/// </summary>
public static class SteamAppInfoReader
{
    private const uint MagicV27 = 0x07564427;
    private const uint MagicV28 = 0x07564428;
    private const uint MagicV29 = 0x07564429;

    // Tipos del KeyValues binario de Valve.
    private const byte KvObject = 0x00, KvString = 0x01, KvInt32 = 0x02, KvFloat = 0x03, KvPointer = 0x04,
        KvWideString = 0x05, KvColor = 0x06, KvUInt64 = 0x07, KvEnd = 0x08, KvInt64 = 0x0A, KvEndAlt = 0x0B;

    public static IReadOnlyDictionary<string, SteamAppBasics> Read(string? steamPath)
    {
        var result = new Dictionary<string, SteamAppBasics>();
        if (steamPath is null) return result;
        var file = Path.Combine(steamPath, "appcache", "appinfo.vdf");
        if (!File.Exists(file)) return result;

        try
        {
            // Copia en memoria: Steam puede reescribir el archivo mientras lo leemos.
            var data = File.ReadAllBytes(file);
            Parse(data, result);
            Log.Info($"appinfo.vdf: {result.Count} apps.");
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer appinfo.vdf: {ex.Message}");
        }
        return result;
    }

    public static void Parse(byte[] data, Dictionary<string, SteamAppBasics> result)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);

        var magic = r.ReadUInt32();
        if (magic is not (MagicV27 or MagicV28 or MagicV29))
            throw new InvalidDataException($"Versión de appinfo.vdf no soportada (0x{magic:X8}).");
        r.ReadUInt32(); // universo

        string[]? strings = null;
        if (magic == MagicV29)
        {
            var tableOffset = r.ReadInt64();
            var back = ms.Position;
            ms.Position = tableOffset;
            var count = r.ReadUInt32();
            strings = new string[count];
            for (int i = 0; i < count; i++) strings[i] = ReadCString(r);
            ms.Position = back;
        }

        while (ms.Position + 8 <= ms.Length)
        {
            var appId = r.ReadUInt32();
            if (appId == 0) break;
            var size = r.ReadUInt32();
            var next = ms.Position + size;

            try
            {
                // infoState, lastUpdated, picsToken, sha1, changeNumber[, binarySha1]
                ms.Position += 4 + 4 + 8 + 20 + 4 + (magic >= MagicV28 ? 20 : 0);
                var root = new VdfNode();
                ReadObject(r, root, strings);

                var info = root["appinfo"] ?? root;
                var common = info["common"];
                var name = common?.GetValue("name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var id = appId.ToString();
                    result[id] = new SteamAppBasics(
                        id, name.Trim(),
                        (common!.GetValue("type") ?? string.Empty).ToLowerInvariant(),
                        info["config"]?.GetValue("installdir"));
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IndexOutOfRangeException or InvalidDataException)
            {
                // Entrada corrupta: se salta usando el tamaño declarado.
            }
            ms.Position = next;
        }
    }

    private static void ReadObject(BinaryReader r, VdfNode node, string[]? strings)
    {
        while (true)
        {
            var type = r.ReadByte();
            if (type is KvEnd or KvEndAlt) return;

            var key = strings is null ? ReadCString(r) : strings[r.ReadInt32()];
            switch (type)
            {
                case KvObject:
                    var child = new VdfNode();
                    ReadObject(r, child, strings);
                    node.Children[key] = child;
                    break;
                case KvString:
                    node.Values[key] = ReadCString(r);
                    break;
                case KvWideString:
                    node.Values[key] = ReadWideCString(r);
                    break;
                case KvInt32:
                case KvPointer:
                case KvColor:
                    node.Values[key] = r.ReadInt32().ToString();
                    break;
                case KvFloat:
                    node.Values[key] = r.ReadSingle().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case KvUInt64:
                    node.Values[key] = r.ReadUInt64().ToString();
                    break;
                case KvInt64:
                    node.Values[key] = r.ReadInt64().ToString();
                    break;
                default:
                    throw new InvalidDataException($"Tipo KeyValues desconocido: 0x{type:X2}");
            }
        }
    }

    private static string ReadCString(BinaryReader r)
    {
        var bytes = new List<byte>(32);
        byte b;
        while ((b = r.ReadByte()) != 0) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string ReadWideCString(BinaryReader r)
    {
        var sb = new StringBuilder();
        char c;
        while ((c = (char)r.ReadUInt16()) != '\0') sb.Append(c);
        return sb.ToString();
    }
}
