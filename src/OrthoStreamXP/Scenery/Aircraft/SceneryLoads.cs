using System.Globalization;

namespace OrthoStreamXP.Scenery;

/// <summary>A 1° x 1° scenery tile (one DSF), named by its south-west corner as X-Plane does: "+47+010".</summary>
internal readonly record struct SceneryTile(int Lat, int Lon)
{
    /// <summary>The tile of a DSF under Earth nav data, e.g. "Earth nav data/+40+010/+47+010.dsf".</summary>
    public static bool TryParse(VirtualPath path, out SceneryTile tile)
    {
        tile = default;
        if (!path.IsUnder("Earth nav data") || !path.HasExtension(".dsf")) return false;
        string name = Path.GetFileNameWithoutExtension(path.Name);
        if (name.Length != 7 || name[0] is not ('+' or '-') || name[3] is not ('+' or '-')) return false;
        if (!int.TryParse(name.AsSpan(0, 3), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lat) ||
            !int.TryParse(name.AsSpan(3, 4), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lon)) return false;
        if (lat is < -90 or > 89 || lon is < -180 or > 179) return false;
        tile = new SceneryTile(lat, lon);
        return true;
    }

    public override string ToString() => $"{(Lat < 0 ? '-' : '+')}{Math.Abs(Lat):00}{(Lon < 0 ? '-' : '+')}{Math.Abs(Lon):000}";
}

/// <summary>
/// The scenery tiles X-Plane loads, logged once each with where the camera was. X-Plane keeps a block of tiles
/// around the aircraft and loads a new row or column of them as it moves on; a load brings hundreds of new
/// textures at once, so these lines mark the busiest minutes of a flight.
/// </summary>
internal sealed class SceneryLoads(ViewTracker view)
{
    /// <summary>X-Plane opens a DSF twice in a row, and the same tile of every package: one line for all of them.</summary>
    private static readonly TimeSpan Again = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<SceneryTile, DateTime> _loaded = [];

    /// <summary>
    /// The DSF tiles X-Plane 12 loads around a place: three rows (the place's and one either side) by four columns,
    /// leaning towards the nearer half of the place's column (52.46°N 9.68°E: latitudes 51-53, longitudes 8-11).
    /// </summary>
    public static IReadOnlyList<(int Lat, int Lon)> TilesAround(double lat, double lon)
    {
        int row = (int)Math.Floor(lat), col = (int)Math.Floor(lon);
        int west = lon - col >= 0.5 ? col - 1 : col - 2;
        var tiles = new List<(int, int)>(12);
        for (int r = row - 1; r <= row + 1; r++)
            for (int c = west; c <= west + 3; c++)
                if (r is >= -90 and < 90) tiles.Add((r, ((c + 180) % 360 + 360) % 360 - 180));
        return tiles;
    }

    /// <summary>Notes a file X-Plane opened; a DSF not opened in the last minutes is logged.</summary>
    public void Opened(VirtualPath path, string package)
    {
        if (!SceneryTile.TryParse(path, out SceneryTile tile)) return;
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_loaded.TryGetValue(tile, out DateTime last) && now - last < Again) return;
            _loaded[tile] = now;
        }
        Log.Info($"X-Plane loads the scenery tile {tile}{(package.Length > 0 ? $" of {package}" : "")}; {Where(view.Latest, now)}");
    }

    /// <summary>The camera's place, height and motion, or that X-Plane has sent none.</summary>
    internal static string Where(CameraReport? camera, DateTime now)
    {
        if (camera is null) return "no position from X-Plane yet";
        TimeSpan age = now - camera.ReceivedUtc;
        string motion = camera.Motion is { } m ? $", track {m.TrackDeg:F0}° at {m.SpeedMps * 3600 / 1852:F0} kt" : ", not moving";
        return $"camera at {camera.Lat:F3}, {camera.Lon:F3}, {camera.HeightAglM:F0} m above the ground{motion}" +
               (age > ViewTracker.Fresh ? $" ({age.TotalSeconds:F0} s ago)" : "");
    }
}
