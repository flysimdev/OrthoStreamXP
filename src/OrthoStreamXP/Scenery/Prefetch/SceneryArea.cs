namespace OrthoStreamXP.Scenery;

/// <summary>
/// The scenery X-Plane loads around a place (a departure, an arrival, an alternate) or along a route: its 1° DSF tiles,
/// and the photo textures they use, read from the package's own DSF files. These are exactly the textures X-Plane will
/// ask for there, known before it opens anything.
/// </summary>
internal static class SceneryArea
{
    /// <summary>
    /// The photo textures of the package's DSFs around the place, each once; <paramref name="tiles"/> is how many of
    /// its DSF tiles the package has and could read.
    /// </summary>
    public static IReadOnlyList<VirtualPath> Textures(string packageSource, double lat, double lon, out int tiles) =>
        TexturesIn(packageSource, SceneryLoads.TilesAround(lat, lon), out tiles);

    /// <summary>The photo textures of the package's DSFs among <paramref name="dsfTiles"/>, each once.</summary>
    public static IReadOnlyList<VirtualPath> TexturesIn(string packageSource, IEnumerable<(int Lat, int Lon)> dsfTiles, out int tiles)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textures = new List<VirtualPath>();
        tiles = 0;
        foreach (var (tileLat, tileLon) in dsfTiles.Distinct())
        {
            string dsf = DsfTerrain.PathIn(packageSource, tileLat, tileLon);
            if (!File.Exists(dsf) || DsfTerrain.Read(dsf) is not { } names) continue;
            tiles++;
            foreach (string terrain in names)
                if (TerrainDefinitions.TextureOf(Path.GetFileName(terrain), out _, out _) is { } name && stems.Add(name.Stem))
                    textures.Add(VirtualPath.Of("textures", name.Stem + ".dds"));
        }
        return textures;
    }
}
