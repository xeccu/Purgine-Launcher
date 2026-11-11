using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Purgine.EpicNet;

/// <summary>Partida empaquetada para la nube de Epic: el manifiesto y sus fragmentos, con su clave en la nube.</summary>
public sealed record EpicSavePackage(string ManifestKey, byte[] Manifest, IReadOnlyDictionary<string, byte[]> Chunks, int FileCount, long TotalBytes);

/// <summary>
/// Empaqueta una carpeta de partidas en el formato de la nube de Epic Games Launcher (el mismo que escribe
/// Legendary): fragmentos de 1 MiB comprimidos con zlib (cabecera 0xB1FE3AA2, hash rodante + SHA-1) y un
/// manifiesto binario versión 18 llamado con la fecha UTC (<c>manifests/aaaa.MM.dd-HH.mm.ss.manifest</c>).
/// El resultado se puede leer con <see cref="EpicManifest.Parse"/>.
/// </summary>
public static class EpicSavePackager
{
    public const int ChunkSize = 1024 * 1024;
    private const uint ManifestMagic = 0x44BEC00C;
    private const uint ChunkMagic = 0xB1FE3AA2;
    private const uint ManifestVersion = 18;

    /// <param name="appName">AppName del juego.</param>
    /// <param name="accountId">Id de la cuenta de Epic (va pegado al AppName en el manifiesto, como hace Epic).</param>
    /// <param name="cloudFolder">Plantilla CloudSaveFolder sin expandir (se guarda en el manifiesto).</param>
    /// <param name="timestampUtc">Fecha de la partida (nombre del manifiesto).</param>
    public static EpicSavePackage Package(string folder, string appName, string accountId, string cloudFolder,
        IReadOnlyList<string> include, IReadOnlyList<string> exclude, DateTime timestampUtc)
    {
        var buildVersion = timestampUtc.ToString("yyyy.MM.dd-HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
        var files = EnumerateSaveFiles(folder, include, exclude);

        var chunks = new List<(uint[] Guid, ulong Hash, byte[] Sha, byte Group, byte[] File)>();
        var fileEntries = new List<(string Name, byte[] Sha1, List<(uint[] Guid, uint Offset, uint Size)> Parts)>();
        var buffer = new byte[ChunkSize];
        var used = 0;
        var current = NewGuid();
        long total = 0;

        void Flush()
        {
            if (used == 0) return;
            chunks.Add(BuildChunk(current, buffer));
            Array.Clear(buffer);
            used = 0;
            current = NewGuid();
        }

        foreach (var (full, relative) in files)
        {
            var data = File.ReadAllBytes(full);
            total += data.Length;
            var parts = new List<(uint[] Guid, uint Offset, uint Size)>();
            var pos = 0;
            while (pos < data.Length)
            {
                var size = Math.Min(data.Length - pos, ChunkSize - used);
                Buffer.BlockCopy(data, pos, buffer, used, size);
                parts.Add((current, (uint)used, (uint)size));
                used += size;
                pos += size;
                if (used == ChunkSize) Flush();
            }
            fileEntries.Add((relative, SHA1.HashData(data), parts));
        }
        Flush();

        var chunkDir = "ChunksV4";
        var chunkFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var c in chunks)
            chunkFiles[$"{chunkDir}/{c.Group:D2}/{c.Hash:X16}_{GuidHex(c.Guid)}.chunk"] = c.File;

        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        WriteSection(w, s =>
        {
            s.Write((byte)0);                 // versión de datos
            s.Write(ManifestVersion);         // feature level
            s.Write((byte)0);                 // is_file_data
            s.Write(0u);                      // app_id
            FString(s, appName + accountId);
            FString(s, buildVersion);
            FString(s, string.Empty);         // ejecutable
            FString(s, string.Empty);         // argumentos
            s.Write(0u);                      // prerrequisitos (ids)
            FString(s, string.Empty);
            FString(s, string.Empty);
            FString(s, string.Empty);
        });
        WriteSection(w, s =>
        {
            s.Write((byte)0);
            s.Write(chunks.Count);
            foreach (var c in chunks) WriteGuid(s, c.Guid);
            foreach (var c in chunks) s.Write(c.Hash);
            foreach (var c in chunks) s.Write(c.Sha);
            foreach (var c in chunks) s.Write(c.Group);
            foreach (var _ in chunks) s.Write((uint)ChunkSize);
            foreach (var c in chunks) s.Write((long)c.File.Length);
        });
        WriteSection(w, s =>
        {
            s.Write((byte)0);
            s.Write(fileEntries.Count);
            foreach (var f in fileEntries) FString(s, f.Name);
            foreach (var _ in fileEntries) FString(s, string.Empty); // enlaces simbólicos
            foreach (var f in fileEntries) s.Write(f.Sha1);
            foreach (var _ in fileEntries) s.Write((byte)0);         // flags
            foreach (var _ in fileEntries) s.Write(0u);              // etiquetas
            foreach (var f in fileEntries)
            {
                s.Write(f.Parts.Count);
                foreach (var p in f.Parts)
                {
                    s.Write(28u);
                    WriteGuid(s, p.Guid);
                    s.Write(p.Offset);
                    s.Write(p.Size);
                }
            }
        });
        WriteSection(w, s =>
        {
            s.Write((byte)0);
            s.Write(1u);
            FString(s, "CloudSaveFolder");
            FString(s, cloudFolder);
        });
        w.Flush();

        var raw = body.ToArray();
        var compressed = Zlib(raw);
        var manifest = new MemoryStream();
        var mw = new BinaryWriter(manifest);
        mw.Write(ManifestMagic);
        mw.Write(41u);
        mw.Write((uint)raw.Length);
        mw.Write((uint)compressed.Length);
        mw.Write(SHA1.HashData(raw));
        mw.Write((byte)1);
        mw.Write(ManifestVersion);
        mw.Write(compressed);
        mw.Flush();

        return new EpicSavePackage($"manifests/{buildVersion}.manifest", manifest.ToArray(), chunkFiles, fileEntries.Count, total);
    }

    /// <summary>Archivos de partida (ruta completa y relativa con «/»), filtrados y en orden estable.</summary>
    public static List<(string Full, string Relative)> EnumerateSaveFiles(string folder, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        var result = new List<(string, string)>();
        if (!Directory.Exists(folder)) return result;
        foreach (var full in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, full).Replace('\\', '/');
            if (include.Count > 0 && !include.Any(p => Matches(relative, p))) continue;
            if (exclude.Count > 0 && exclude.Any(p => Matches(relative, p))) continue;
            result.Add((full, relative));
        }
        result.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>Lista CloudIncludeList / CloudExcludeList («a,b,c») sin entradas vacías.</summary>
    public static IReadOnlyList<string> ParseList(string? list) =>
        string.IsNullOrWhiteSpace(list) ? [] : list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Matches(string name, string pattern)
    {
        pattern = pattern.Replace('\\', '/');
        if (pattern.EndsWith('/') && name.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);
    }

    private static (uint[] Guid, ulong Hash, byte[] Sha, byte Group, byte[] File) BuildChunk(uint[] guid, byte[] data)
    {
        var hash = RollingHash(data);
        var sha = SHA1.HashData(data);
        var compressed = Zlib(data);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(ChunkMagic);
        w.Write(3u);                 // versión de cabecera
        w.Write(66u);                // tamaño de cabecera
        w.Write((uint)compressed.Length);
        WriteGuid(w, guid);
        w.Write(hash);
        w.Write((byte)1);            // comprimido
        w.Write(sha);
        w.Write((byte)3);            // hash rodante + SHA-1
        w.Write((uint)ChunkSize);
        w.Write(compressed);
        w.Flush();
        return (guid, hash, sha, GroupNumber(guid), ms.ToArray());
    }

    private static uint[] NewGuid()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        return [ReadBigEndian(b[..4]), ReadBigEndian(b[4..8]), ReadBigEndian(b[8..12]), ReadBigEndian(b[12..])];
    }

    private static uint ReadBigEndian(ReadOnlySpan<byte> b) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b);

    private static string GuidHex(uint[] g) => $"{g[0]:X8}{g[1]:X8}{g[2]:X8}{g[3]:X8}";

    private static void WriteGuid(BinaryWriter w, uint[] g) { foreach (var v in g) w.Write(v); }

    /// <summary>Grupo de la carpeta del fragmento: CRC-32 de los 16 bytes del GUID (little-endian) mod 100.</summary>
    private static byte GroupNumber(uint[] g)
    {
        Span<byte> b = stackalloc byte[16];
        for (var i = 0; i < 4; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b[(i * 4)..], g[i]);
        return (byte)(Crc32(b) % 100);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static readonly ulong[] RollTable = BuildRollTable();
    private static ulong[] BuildRollTable()
    {
        const ulong poly = 0xC96C5795D7870F42;
        var t = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            var v = i;
            for (var k = 0; k < 8; k++) v = (v & 1) != 0 ? (v >> 1) ^ poly : v >> 1;
            t[i] = v;
        }
        return t;
    }

    /// <summary>Hash rodante de Epic (variante de CRC-64-ECMA) sobre el fragmento completo de 1 MiB.</summary>
    public static ulong RollingHash(ReadOnlySpan<byte> data)
    {
        ulong h = 0;
        foreach (var b in data) h = ((h << 1) | (h >> 63)) ^ RollTable[b];
        return h;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static void WriteSection(BinaryWriter w, Action<BinaryWriter> write)
    {
        var ms = new MemoryStream();
        var s = new BinaryWriter(ms);
        write(s);
        s.Flush();
        w.Write((uint)(ms.Length + 4));
        w.Write(ms.ToArray());
    }

    /// <summary>FString de Unreal: ASCII con longitud positiva, si no UTF-16 con longitud negativa (con terminador).</summary>
    private static void FString(BinaryWriter w, string value)
    {
        if (value.Length == 0) { w.Write(0); return; }
        if (value.All(c => c < 128))
        {
            w.Write(value.Length + 1);
            w.Write(Encoding.ASCII.GetBytes(value));
            w.Write((byte)0);
        }
        else
        {
            w.Write(-(value.Length + 1));
            w.Write(Encoding.Unicode.GetBytes(value));
            w.Write((short)0);
        }
    }
}
