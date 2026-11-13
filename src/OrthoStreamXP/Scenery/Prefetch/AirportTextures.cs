using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// The photo textures a package has around a place, from the names of its terrain definitions (terrain\*.ter): the
/// same textures X-Plane will ask for when it loads the scenery there, at the zoom the package uses (ZL16, or ZL18
/// near airports), known before X-Plane opens anything.
/// <para>
/// A region's terrain folder holds hundreds of thousands of files (a whole continent), so it is never listed: each
/// texture cell around the place is looked up by its name (row_col_*.ter) at every zoom a package may use, a few
/// hundred indexed lookups instead of a scan of the whole folder.
/// </para>
/// </summary>
internal static class AirportTextures
{
    /// <summary>The zoom levels texture names are looked for at (Ortho4XP and AutoOrtho packages use 12-19).</summary>
    internal const int MinZoom = 12, MaxZoom = 19;

    /// <summary>
    /// The textures whose centre is within <paramref name="radiusKm"/>, nearest first, at most <paramref name="max"/>.
    /// Empty when the package has no terrain folder (its definitions are generated as X-Plane opens them).
    /// </summary>
    public static IReadOnlyList<VirtualPath> Near(string packageSource, double lat, double lon, double radiusKm, int max)
    {
        string folder = Path.Join(packageSource, "terrain");
        if (!Directory.Exists(folder)) return [];
        double radiusM = radiusKm * 1000;
        // The box around the circle; the cells in it are checked against the circle by their centre.
        double dLat = radiusKm / 110.574, dLon = radiusKm / (111.320 * Math.Max(0.01, Math.Cos(lat * Math.PI / 180)));
        var found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int zoom = MinZoom; zoom <= MaxZoom; zoom++)
        {
            var (west, north) = Tiles.LatLonToTile(lat + dLat, lon - dLon, zoom);
            var (east, south) = Tiles.LatLonToTile(lat - dLat, lon + dLon, zoom);
            for (int row = north / Tiles.TextureSpan * Tiles.TextureSpan; row <= south; row += Tiles.TextureSpan)
                for (int col = west / Tiles.TextureSpan * Tiles.TextureSpan; col <= east; col += Tiles.TextureSpan)
                {
                    var cell = new TextureName(row, col, "", zoom);
                    var (centreLat, centreLon) = cell.Center;
                    double distance = Geo.DistanceM(lat, lon, centreLat, centreLon);
                    if (distance > radiusM) continue;
                    // 21632_35680_*.ter: the plain definition and its water/sea overlays, whatever the provider code.
                    foreach (string file in Directory.EnumerateFiles(folder, $"{row}_{col}_*.ter"))
                        if (TerrainDefinitions.TextureOf(Path.GetFileName(file), out _, out _) is { } name && name.Zoom == zoom)
                            found.TryAdd(name.Stem, distance);
                }
        }
        return [.. found.OrderBy(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(max).Select(f => VirtualPath.Of("textures", f.Key + ".dds"))];
    }
}
