using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Setup;

/// <summary>
/// The cache by area: each file of a setup folder belongs to the area of the texture it is named after
/// (textures\43808_71648_PX17.dds, .xpstream\mips\43808_71648_PX17.mip2; earlier versions: 21904_35824_BI16.dds). The shared
/// low-zoom base and the logs are not areas: they stay.
/// </summary>
internal static class CacheAreas
{
    private static readonly EnumerationOptions AllFiles = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    /// <summary>Every area in the cache's setup folders (reads every file's size: call it off the UI thread).</summary>
    public static IReadOnlyList<CacheArea> Scan(string cache, AreaUsage? usage, CancellationToken cancel = default)
    {
        var bytes = new Dictionary<AreaName, Dictionary<string, long>>();
        var files = new Dictionary<AreaName, int>();
        var written = new Dictionary<AreaName, DateTime>();
        foreach (string setup in SetupFolders(cache))
        {
            string setupName = Path.GetFileName(setup);
            TexturePlan plan = PlanOf(setup);
            int n = 0;
            foreach (FileInfo file in new DirectoryInfo(setup).EnumerateFiles("*", AllFiles))
            {
                if ((++n & 1023) == 0) cancel.ThrowIfCancellationRequested();
                if (AreaOf(file.Name, plan) is not { } area) continue;
                if (!bytes.TryGetValue(area, out var bySetup)) bytes[area] = bySetup = [];
                bySetup[setupName] = bySetup.GetValueOrDefault(setupName) + file.Length;
                files[area] = files.GetValueOrDefault(area) + 1;
                if (file.LastWriteTimeUtc > written.GetValueOrDefault(area)) written[area] = file.LastWriteTimeUtc;
            }
        }
        return [.. bytes.Select(b => new CacheArea(b.Key, b.Value, files[b.Key], usage?.LastUsed(b.Key) ?? written[b.Key]))
            .OrderBy(a => a.Name.Lat).ThenBy(a => a.Name.Lon)];
    }

    /// <summary>
    /// The area a cache file belongs to, from the texture name it starts with; null for other files. A cache name
    /// (43760_71720_PX17) is read with its setup folder's <paramref name="plan"/>; a package name (as earlier versions kept) without.
    /// </summary>
    public static AreaName? AreaOf(string fileName, TexturePlan? plan = null) => TextureOf(fileName, plan) is { } name ? AreaName.Of(name) : null;

    /// <summary>The texture a cache file is of, at its package zoom (see <see cref="AreaOf"/>).</summary>
    private static TextureName? TextureOf(string fileName, TexturePlan? plan)
    {
        int dot = fileName.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? fileName : fileName[..dot];
        return plan?.FromCacheStem(stem) ?? (TextureName.TryParseStem(stem, out TextureName name) ? name : null);
    }

    /// <summary>The texture plan of a setup folder such as level-1_2048-2048.</summary>
    private static TexturePlan PlanOf(string setup) => new(CacheInventory.ParseSetupName(Path.GetFileName(setup))?.Level ?? 0);

    /// <summary>Deletes the areas' files from every setup folder; returns the bytes freed.</summary>
    public static long Delete(string cache, IReadOnlySet<AreaName> areas, CancellationToken cancel = default)
    {
        long freed = 0;
        foreach (string setup in SetupFolders(cache))
        {
            TexturePlan plan = PlanOf(setup);
            foreach (FileInfo file in new DirectoryInfo(setup).EnumerateFiles("*", AllFiles))
            {
                cancel.ThrowIfCancellationRequested();
                if (AreaOf(file.Name, plan) is not { } area || !areas.Contains(area)) continue;
                try
                {
                    long length = file.Length;
                    file.Delete();
                    freed += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or read-only: left for the next time.
                }
            }
        }
        return freed;
    }

    /// <summary>
    /// Keeps the cache within <paramref name="limitBytes"/>: when over it, removes the areas flown longest ago, down to 90%
    /// of the limit. Returns the areas removed and the bytes freed (none when within the limit).
    /// </summary>
    public static (IReadOnlyList<AreaName> Removed, long Freed) Trim(string cache, long limitBytes, AreaUsage usage, CancellationToken cancel = default)
    {
        IReadOnlyList<CacheArea> areas = Scan(cache, usage, cancel);
        long total = areas.Sum(a => a.Bytes);
        if (limitBytes <= 0 || total <= limitBytes) return ([], 0);
        long target = limitBytes * 9 / 10;
        var remove = new HashSet<AreaName>();
        // An area X-Plane never opened (prefetched for a flight that never went there)
        // goes before every area really flown: its newest file write would otherwise make it look used just now.
        foreach (CacheArea area in areas.OrderBy(a => usage.LastUsed(a.Name) is null ? 0 : 1).ThenBy(a => a.LastUsedUtc))
        {
            if (total <= target) break;
            remove.Add(area.Name);
            total -= area.Bytes;
        }
        long freed = Delete(cache, remove, cancel);
        usage.Forget(remove);
        usage.Save();
        return ([.. remove], freed);
    }

    /// <summary>
    /// A small picture of an area, <paramref name="size"/> pixels square (BGRA rows from the north; see-through where
    /// nothing of it is on this PC), from the low-zoom base and the smallest levels of its finished textures; null when
    /// it has none.
    /// </summary>
    public static byte[]? Picture(string cache, AreaName area, int size, CancellationToken cancel = default)
    {
        var bounds = new GeoBounds(area.Lat, area.Lon, area.Lat + 1, area.Lon + 1);
        byte[] bgra = new byte[size * size * 4];
        // First the low-zoom base: X-Plane's far scenery is drawn from it, so every area flown has it.
        bool any = PaintBase(bgra, size, size, bounds, cache, cancel);
        Span<byte> header = stackalloc byte[Dds.HeaderSize];
        foreach (string setup in SetupFolders(cache).OrderByDescending(s => CacheInventory.ParseSetupName(Path.GetFileName(s))?.Level ?? 0))
        {
            TexturePlan plan = PlanOf(setup);
            foreach (string file in Directory.EnumerateFiles(setup, "*.dds", AllFiles))
            {
                cancel.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(file);
                if (TextureOf(fileName, plan) is not { } name || name.Zoom != 16 || AreaName.Of(name) != area
                    || file.Contains(Path.DirectorySeparatorChar + ".xpstream" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                try
                {
                    using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (RandomAccess.Read(handle, header, 0) < Dds.HeaderSize || ColourTable.BlockBytes(header.Slice(84, 4)) != 8) continue;
                    int width = BitConverter.ToInt32(header[16..]);
                    if (width < 8 || (width & (width - 1)) != 0) continue;
                    var layout = DdsLayout.For(width);
                    int mip = System.Numerics.BitOperations.Log2((uint)(width / 8));
                    byte[] blocks = new byte[layout.LengthOf(mip)];
                    if (RandomAccess.Read(handle, blocks, layout.Offset(mip)) < blocks.Length) continue;
                    Paint(bgra, size, area, name, Dds.DecodeLevel(blocks, 8, 8));
                    any = true;
                }
                catch (IOException)
                {
                    // Being replaced: the next picture has it.
                }
            }
        }
        return any ? bgra : null;
    }

    /// <summary>
    /// A picture of any place from the low-zoom base, <paramref name="width"/> x <paramref name="height"/> pixels of
    /// BGRA (north up, longitudes even), see-through where the base has nothing; null when it has nothing at all.
    /// </summary>
    public static byte[]? BasePicture(string cache, GeoBounds bounds, int width, int height, CancellationToken cancel = default)
    {
        byte[] bgra = new byte[width * height * 4];
        return PaintBase(bgra, width, height, bounds, cache, cancel) ? bgra : null;
    }

    /// <summary>
    /// Paints a place from the low-zoom base's tiles (zoom 12: about 11 x 18 tiles a degree at 51°), each decoded at
    /// 1/8 of its size (1/2 when the picture is finer than that); returns whether it had any.
    /// </summary>
    private static bool PaintBase(byte[] bgra, int width, int height, GeoBounds bounds, string cache, CancellationToken cancel)
    {
        var store = new TileStore(Path.Join(cache, TileStore.FolderName));
        int zoom = store.MaxZoom;
        double n = 1 << zoom;
        double TileX(double lon) => (lon + 180) / 360 * n;
        double TileY(double lat)
        {
            double r = lat * Math.PI / 180;
            return (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * n;
        }
        // Pixels a tile spans in the picture: at most 32, a 1/8 decode is plenty; more, decode it larger.
        float scale = (TileX(bounds.East) - TileX(bounds.West)) * 32 >= width ? 0.125f : 0.5f;
        var tiles = new Dictionary<(int, int), (byte[] Rgb, int Width, int Height)?>();
        bool any = false;
        for (int py = 0; py < height; py++)
        {
            cancel.ThrowIfCancellationRequested();
            double ty = TileY(bounds.North - (py + 0.5) / height * (bounds.North - bounds.South));
            for (int px = 0; px < width; px++)
            {
                double tx = TileX(bounds.West + (px + 0.5) / width * (bounds.East - bounds.West));
                var key = ((int)tx, (int)ty);
                if (!tiles.TryGetValue(key, out var tile))
                {
                    try
                    {
                        tile = store.TryRead(key.Item1, key.Item2, zoom) is { } encoded ? Images.Decode(encoded, scale) : null;
                    }
                    catch (InvalidDataException)
                    {
                        tile = null;
                    }
                    tiles[key] = tile;
                }
                if (tile is not { } t) continue;
                int sx = Math.Clamp((int)((tx - key.Item1) * t.Width), 0, t.Width - 1), sy = Math.Clamp((int)((ty - key.Item2) * t.Height), 0, t.Height - 1);
                Set(bgra, (py * width + px) * 4, t.Rgb.AsSpan((sy * t.Width + sx) * 3, 3));
                any = true;
            }
        }
        return any;
    }

    /// <summary>Paints a texture's 8 x 8 picture where it lies in the area's picture.</summary>
    private static void Paint(byte[] bgra, int size, AreaName area, TextureName name, byte[] small)
    {
        var (north, west) = Tiles.TileToLatLon(name.Col, name.Row, name.Zoom);
        var (south, east) = Tiles.TileToLatLon(name.Col + Tiles.TextureSpan, name.Row + Tiles.TextureSpan, name.Zoom);
        int x0 = (int)Math.Floor((west - area.Lon) * size), x1 = (int)Math.Ceiling((east - area.Lon) * size);
        int y0 = (int)Math.Floor((area.Lat + 1 - north) * size), y1 = (int)Math.Ceiling((area.Lat + 1 - south) * size);
        for (int y = Math.Max(0, y0); y < Math.Min(size, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(size, x1); x++)
            {
                int sx = Math.Clamp((x - x0) * 8 / Math.Max(1, x1 - x0), 0, 7), sy = Math.Clamp((y - y0) * 8 / Math.Max(1, y1 - y0), 0, 7);
                Set(bgra, (y * size + x) * 4, small.AsSpan((sy * 8 + sx) * 3, 3));
            }
    }

    /// <summary>One opaque pixel from RGB.</summary>
    private static void Set(byte[] bgra, int at, ReadOnlySpan<byte> rgb) =>
        (bgra[at], bgra[at + 1], bgra[at + 2], bgra[at + 3]) = (rgb[2], rgb[1], rgb[0], 255);

    /// <summary>
    /// The finished photo textures in the cache's setup folders (whole DDS files, not the streaming's level chains), at
    /// their package names (reads the folders: call it off the UI thread).
    /// </summary>
    public static IEnumerable<(string Path, TextureName Name)> PhotoTextures(string cache)
    {
        string streaming = Path.DirectorySeparatorChar + ".xpstream" + Path.DirectorySeparatorChar;
        foreach (string setup in SetupFolders(cache))
        {
            TexturePlan plan = PlanOf(setup);
            foreach (string file in Directory.EnumerateFiles(setup, "*.dds", AllFiles))
                if (!file.Contains(streaming, StringComparison.Ordinal) && TextureOf(Path.GetFileName(file), plan) is { } name)
                    yield return (file, name);
        }
    }

    private static IEnumerable<string> SetupFolders(string cache) =>
        Directory.Exists(cache)
            ? Directory.EnumerateDirectories(cache).Where(d => CacheInventory.ParseSetupName(Path.GetFileName(d)) is not null)
            : [];
}
