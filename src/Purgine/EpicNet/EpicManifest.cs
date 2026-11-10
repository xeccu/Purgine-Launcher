using System.IO.Compression;
using System.Text;

namespace Purgine.EpicNet;

/// <summary>Un fragmento (chunk) de la CDN de Epic.</summary>
public sealed record EpicChunk(Guid Guid, string GuidHex, ulong Hash, byte GroupNum, uint WindowSize, long FileSize);

/// <summary>Trozo de un archivo: <see cref="Size"/> bytes desde <see cref="Offset"/> dentro del fragmento.</summary>
public sealed record EpicChunkPart(string ChunkGuidHex, uint Offset, uint Size);

/// <summary>Un archivo de la instalación: ruta relativa, SHA-1 y los trozos de fragmentos que lo forman, en orden.</summary>
public sealed record EpicFile(string Path, byte[] Sha1, IReadOnlyList<EpicChunkPart> Parts)
{
    public long Size => Parts.Sum(p => (long)p.Size);
}

/// <summary>
/// Manifiesto binario de Epic (formato de Epic Games Launcher, el mismo que interpreta Legendary):
/// cabecera (0x44BEC00C) + cuerpo, normalmente comprimido con zlib, con metadatos, lista de fragmentos y lista
/// de archivos. Los manifiestos antiguos en JSON no se admiten.
/// </summary>
public sealed class EpicManifest
{
    private const uint HeaderMagic = 0x44BEC00C;

    public uint FeatureLevel { get; private set; }
    public string AppName { get; private set; } = string.Empty;
    public string BuildVersion { get; private set; } = string.Empty;
    public string LaunchExe { get; private set; } = string.Empty;
    public string LaunchCommand { get; private set; } = string.Empty;
    public Dictionary<string, EpicChunk> Chunks { get; } = new(StringComparer.Ordinal);
    public List<EpicFile> Files { get; } = [];

    public long InstallSize => Files.Sum(f => f.Size);

    /// <summary>Carpeta de fragmentos en la CDN según la versión del formato.</summary>
    public string ChunkDir => FeatureLevel >= 15 ? "ChunksV4" : FeatureLevel >= 6 ? "ChunksV3" : FeatureLevel >= 3 ? "ChunksV2" : "Chunks";

    /// <summary>Ruta del fragmento relativa a la URL base de la CDN.</summary>
    public string ChunkPath(EpicChunk c) => $"{ChunkDir}/{c.GroupNum:D2}/{c.Hash:X16}_{c.GuidHex}.chunk";

    public static EpicManifest Parse(byte[] data)
    {
        if (data.Length > 0 && data[0] == (byte)'{')
            throw new NotSupportedException("Este juego usa un manifiesto antiguo (JSON) que Purgine aún no admite.");

        using var header = new BinaryReader(new MemoryStream(data, writable: false));
        if (header.ReadUInt32() != HeaderMagic) throw new InvalidDataException("No es un manifiesto de Epic.");
        var headerSize = header.ReadUInt32();
        var sizeUncompressed = header.ReadUInt32();
        var sizeCompressed = header.ReadUInt32();
        header.ReadBytes(20); // SHA-1 del cuerpo
        var storedAs = header.ReadByte();
        var version = header.ReadUInt32();

        byte[] body;
        if ((storedAs & 1) != 0)
        {
            using var z = new ZLibStream(new MemoryStream(data, (int)headerSize, (int)sizeCompressed), CompressionMode.Decompress);
            body = new byte[sizeUncompressed];
            z.ReadExactly(body);
        }
        else
        {
            body = data.AsSpan((int)headerSize, (int)sizeUncompressed).ToArray();
        }

        var m = new EpicManifest { FeatureLevel = version };
        using var r = new BinaryReader(new MemoryStream(body, writable: false));
        m.ReadMeta(r);
        m.ReadChunkList(r);
        m.ReadFileList(r);
        return m;
    }

    private void ReadMeta(BinaryReader r)
    {
        var start = r.BaseStream.Position;
        var size = r.ReadUInt32();
        r.ReadByte();                    // versión de datos
        FeatureLevel = r.ReadUInt32();
        r.ReadByte();                    // is_file_data
        r.ReadUInt32();                  // app_id
        AppName = FString(r);
        BuildVersion = FString(r);
        LaunchExe = FString(r);
        LaunchCommand = FString(r);
        r.BaseStream.Position = start + size; // el resto (prerrequisitos, build id…) no hace falta
    }

    private void ReadChunkList(BinaryReader r)
    {
        var start = r.BaseStream.Position;
        var size = r.ReadUInt32();
        r.ReadByte();
        var count = r.ReadInt32();
        var guids = new (Guid Guid, string Hex)[count];
        for (var i = 0; i < count; i++) guids[i] = ReadGuid(r);
        var hashes = new ulong[count];
        for (var i = 0; i < count; i++) hashes[i] = r.ReadUInt64();
        for (var i = 0; i < count; i++) r.ReadBytes(20);        // SHA-1 de cada fragmento
        var groups = r.ReadBytes(count);
        var windows = new uint[count];
        for (var i = 0; i < count; i++) windows[i] = r.ReadUInt32();
        var fileSizes = new long[count];
        for (var i = 0; i < count; i++) fileSizes[i] = r.ReadInt64();
        for (var i = 0; i < count; i++)
            Chunks[guids[i].Hex] = new EpicChunk(guids[i].Guid, guids[i].Hex, hashes[i], groups[i], windows[i], fileSizes[i]);
        r.BaseStream.Position = start + size;
    }

    private void ReadFileList(BinaryReader r)
    {
        var start = r.BaseStream.Position;
        var size = r.ReadUInt32();
        r.ReadByte();
        var count = r.ReadInt32();
        var names = new string[count];
        for (var i = 0; i < count; i++) names[i] = FString(r);
        for (var i = 0; i < count; i++) FString(r);             // destino de enlaces simbólicos
        var shas = new byte[count][];
        for (var i = 0; i < count; i++) shas[i] = r.ReadBytes(20);
        r.ReadBytes(count);                                      // flags
        for (var i = 0; i < count; i++)                          // etiquetas de instalación
        {
            var tags = r.ReadInt32();
            for (var t = 0; t < tags; t++) FString(r);
        }
        for (var i = 0; i < count; i++)
        {
            var parts = new List<EpicChunkPart>();
            var n = r.ReadInt32();
            for (var p = 0; p < n; p++)
            {
                var partStart = r.BaseStream.Position;
                var partSize = r.ReadUInt32();
                var (_, hex) = ReadGuid(r);
                var offset = r.ReadUInt32();
                var length = r.ReadUInt32();
                parts.Add(new EpicChunkPart(hex, offset, length));
                r.BaseStream.Position = partStart + partSize;
            }
            Files.Add(new EpicFile(names[i].Replace('/', '\\'), shas[i], parts));
        }
        r.BaseStream.Position = start + size;
    }

    private static (Guid Guid, string Hex) ReadGuid(BinaryReader r)
    {
        uint a = r.ReadUInt32(), b = r.ReadUInt32(), c = r.ReadUInt32(), d = r.ReadUInt32();
        var hex = $"{a:X8}{b:X8}{c:X8}{d:X8}";
        return (new Guid(hex), hex);
    }

    /// <summary>FString de Unreal: longitud con signo (negativa = UTF-16) incluido el terminador nulo.</summary>
    private static string FString(BinaryReader r)
    {
        var length = r.ReadInt32();
        if (length == 0) return string.Empty;
        if (length < 0)
        {
            var bytes = r.ReadBytes(-length * 2);
            return Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2);
        }
        var ascii = r.ReadBytes(length);
        return Encoding.ASCII.GetString(ascii, 0, ascii.Length - 1);
    }

    /// <summary>
    /// Datos descomprimidos de un fragmento descargado (cabecera 0xB1FE3AA2 + datos, zlib si procede).
    /// </summary>
    public static byte[] DecodeChunk(byte[] file)
    {
        using var r = new BinaryReader(new MemoryStream(file, writable: false));
        if (r.ReadUInt32() != 0xB1FE3AA2) throw new InvalidDataException("Fragmento de Epic no válido.");
        r.ReadUInt32();                         // versión de cabecera
        var headerSize = r.ReadUInt32();
        var compressedSize = r.ReadUInt32();
        r.ReadBytes(16);                        // guid
        r.ReadUInt64();                         // hash
        var storedAs = r.ReadByte();
        var data = new MemoryStream(file, (int)headerSize, (int)compressedSize, writable: false);
        if ((storedAs & 1) == 0) return data.ToArray();
        using var z = new ZLibStream(data, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }
}
