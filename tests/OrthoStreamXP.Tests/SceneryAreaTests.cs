using System.Buffers.Binary;
using System.Text;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Tests;

/// <summary>The scenery X-Plane loads around a departure: its DSF tiles, and the textures their TERT tables name.</summary>
public sealed class SceneryAreaTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>A minimal uncompressed DSF: header, a HEAD atom, DEFN with TERT (and an atom before it), the MD5 footer.</summary>
    internal static byte[] Dsf(params string[] terrain)
    {
        static byte[] Atom(string id, byte[] body)
        {
            byte[] atom = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(atom, (uint)(id[0] << 24 | id[1] << 16 | id[2] << 8 | id[3]));
            BinaryPrimitives.WriteUInt32LittleEndian(atom.AsSpan(4), (uint)atom.Length);
            body.CopyTo(atom, 8);
            return atom;
        }
        byte[] table = Encoding.UTF8.GetBytes(string.Concat(terrain.Select(t => t + "\0")));
        byte[] definitions = Atom("DEFN", [.. Atom("OBJT", Encoding.UTF8.GetBytes("lib/tree.obj\0")), .. Atom("TERT", table)]);
        byte[] header = [.. "XPLNEDSF"u8, 1, 0, 0, 0];
        return [.. header, .. Atom("HEAD", new byte[20]), .. definitions, .. Atom("GEOD", new byte[64]), .. new byte[16]];
    }

    [Fact]
    public void Reads_the_terrain_table_of_an_uncompressed_DSF_and_nothing_of_a_compressed_one()
    {
        string dsf = _tree.WriteBytes("a.dsf", Dsf("terrain_Water", "terrain/21552_34576_BI16_water_overlay.ter", "terrain/21584_34464_BI16.ter"));
        string zipped = _tree.WriteBytes("b.dsf", [.. "7z¼¯'\u001C"u8.ToArray(), .. new byte[64]]);

        Assert.Equal(["terrain_Water", "terrain/21552_34576_BI16_water_overlay.ter", "terrain/21584_34464_BI16.ter"], DsfTerrain.Read(dsf));
        Assert.Null(DsfTerrain.Read(zipped));
        Assert.Null(DsfTerrain.Read(_tree.PathOf("missing.dsf")));
    }

    [Fact]
    public void XPlane_loads_three_rows_by_four_columns_leaning_to_the_nearer_half()
    {
        // EDDV (52.46 N, 9.68 E): X-Plane 12 loaded +51..+53 x +008..+011.
        var tiles = SceneryLoads.TilesAround(52.46, 9.68);
        Assert.Equal(12, tiles.Count);
        Assert.Equal([51, 52, 53], tiles.Select(t => t.Lat).Distinct());
        Assert.Equal([8, 9, 10, 11], tiles.Select(t => t.Lon).Distinct());

        // In the western half of the column, one more to the west.
        Assert.Equal([7, 8, 9, 10], SceneryLoads.TilesAround(52.46, 9.2).Select(t => t.Lon).Distinct());
        // Across the antimeridian.
        Assert.Equal([178, 179, -180, -179], SceneryLoads.TilesAround(-17.7, 179.6).Select(t => t.Lon).Distinct());
    }

    [Fact]
    public void A_DSF_lives_in_its_ten_degree_folder()
    {
        Assert.Equal(Path.Join("pkg", "Earth nav data", "+50+000", "+52+009.dsf"), DsfTerrain.PathIn("pkg", 52, 9));
        Assert.Equal(Path.Join("pkg", "Earth nav data", "-40-060", "-34-059.dsf"), DsfTerrain.PathIn("pkg", -34, -59));
    }

    [Fact]
    public void Lists_each_texture_of_the_tiles_the_package_has_once()
    {
        string package = _tree.PathOf("pkg");
        _tree.WriteBytes("pkg/Earth nav data/+50+000/+52+009.dsf",
            Dsf("terrain_Water", "terrain/21552_34576_BI16.ter", "terrain/21552_34576_BI16_water_overlay.ter", "terrain/21584_34464_BI18.ter"));
        _tree.WriteBytes("pkg/Earth nav data/+50+010/+52+010.dsf", Dsf("terrain/21552_34576_BI16.ter", "terrain/21600_34608_BI16.ter"));

        IReadOnlyList<VirtualPath> textures = SceneryArea.Textures(package, 52.46, 9.68, out int tiles);

        Assert.Equal(2, tiles);
        Assert.Equal(["21552_34576_BI16.dds", "21584_34464_BI18.dds", "21600_34608_BI16.dds"], textures.Select(t => t.Name));
        Assert.Empty(SceneryArea.Textures(_tree.PathOf("none"), 52.46, 9.68, out int none));
        Assert.Equal(0, none);
    }
}
