using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OrthoStreamXP.Scenery;

/// <summary>Reads the terrain definitions a DSF uses (its TERT table), without reading the rest of the file.</summary>
internal static class DsfTerrain
{
    /// <summary>A DSF's path in a package: Earth nav data\+50+000\+52+009.dsf.</summary>
    public static string PathIn(string packageSource, int lat, int lon)
    {
        static int Floor10(int v) => (int)Math.Floor(v / 10.0) * 10;
        static string Signed(int v, int digits) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString(new string('0', digits), CultureInfo.InvariantCulture);
        return Path.Join(packageSource, "Earth nav data", Signed(Floor10(lat), 2) + Signed(Floor10(lon), 3), Signed(lat, 2) + Signed(lon, 3) + ".dsf");
    }

    private static readonly byte[] Magic = "XPLNEDSF"u8.ToArray();
    private const int HeaderSize = 12, AtomHeader = 8;
    // Atom IDs are four characters stored as a little-endian integer, so they read backwards on disk.
    private const uint Definitions = 'D' << 24 | 'E' << 16 | 'F' << 8 | 'N', TerrainTable = 'T' << 24 | 'E' << 16 | 'R' << 8 | 'T';

    /// <summary>
    /// The TERT names ("terrain/21552_34576_BI16.ter", "terrain_Water", ...), or null when the file is not an
    /// uncompressed DSF (a 7-Zip DSF X-Plane unpacks itself) or cannot be read.
    /// </summary>
    public static IReadOnlyList<string>? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            Span<byte> header = stackalloc byte[HeaderSize];
            if (file.Read(header) < HeaderSize || !header[..Magic.Length].SequenceEqual(Magic)) return null;
            long end = file.Length - 16; // the MD5 footer
            for (long at = HeaderSize; at + AtomHeader <= end;)
            {
                var (id, length) = AtomAt(file, at);
                if (length < AtomHeader) return null;
                if (id == Definitions) return TerrainIn(file, at + AtomHeader, Math.Min(at + length, end));
                at += length;
            }
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? TerrainIn(FileStream file, long start, long end)
    {
        for (long at = start; at + AtomHeader <= end;)
        {
            var (id, length) = AtomAt(file, at);
            if (length < AtomHeader || at + length > end) return null;
            if (id == TerrainTable)
            {
                byte[] table = new byte[length - AtomHeader];
                file.Position = at + AtomHeader;
                file.ReadExactly(table);
                return [.. Encoding.UTF8.GetString(table).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
            }
            at += length;
        }
        return [];
    }

    private static (uint Id, long Length) AtomAt(FileStream file, long at)
    {
        Span<byte> atom = stackalloc byte[AtomHeader];
        file.Position = at;
        file.ReadExactly(atom);
        return (BinaryPrimitives.ReadUInt32LittleEndian(atom), BinaryPrimitives.ReadUInt32LittleEndian(atom[4..]));
    }
}
