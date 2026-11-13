using System.Net;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>
/// Mip streaming: X-Plane gets a full-size DDS at once, each read is answered by the levels it touches, and
/// only those levels are downloaded, at the zoom they need.
/// </summary>
public sealed class StreamingTests : IDisposable
{
    /// <summary>A ZL16 texture near 51° N, about 6.2 km across.</summary>
    private const string Stem = "21904_35824_BI16";
    private static readonly (byte R, byte G, byte B) TileColor = (40, 90, 160), FlatColor = (96, 102, 80);

    private readonly TempTree _tree = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly List<TaskCompletionSource> _holds = [];

    public void Dispose()
    {
        foreach (TaskCompletionSource hold in _holds) hold.TrySetResult(); // a failed test leaves no download hanging
        foreach (IDisposable item in Enumerable.Reverse(_disposables)) item.Dispose();
        _tree.Dispose();
    }

    private sealed record Setup(FakeTileServer Server, TileSource Tiles, SceneryRoots Roots, TextureBuilder Builder,
        ViewTracker View, TextureStreamer Streamer);

    /// <param name="level">-3: ZL16 textures are 1024 px from zoom 14, their base level 2 (zoom 12). -2: 2048 px from zoom 15, base level 3.</param>
    private Setup Streaming(int level = -3, double maxWait = 0.2, Func<int, TimeSpan>? delay = null, Setup? sharing = null, int detailedBuilds = 16,
        long buildTiles = TextureStreamer.DefaultBuildTiles, Func<Uri, Task?>? hold = null, Func<int, HttpStatusCode>? status = null, Daylight? nightPause = null,
        string cache = "cache")
    {
        FakeTileServer server = sharing?.Server ?? new FakeTileServer(status: status, delay: delay, hold: hold);
        TileSource tiles = sharing?.Tiles ?? Track(new TileSource("test", FakeTileServer.Template, 32, handler: server) { Store = new TileStore(_tree.PathOf("base")) });
        var plan = new TexturePlan(level, TextureSizing.MaxSize);
        var roots = new SceneryRoots(_tree.PathOf("source"), _tree.PathOf(cache), null, plan);
        Directory.CreateDirectory(roots.Source);
        var builder = new TextureBuilder(tiles, 2);
        var view = new ViewTracker();
        // detailedBuilds full-size builds fit in the build memory at once.
        long buildMemory = detailedBuilds * TextureStreamer.BuildWeight(plan.SizeFor(16));
        // k = 1830: the distances in these tests are laid out for it, whatever the default.
        var streamer = Track(new TextureStreamer(roots, builder, view, new PagingModel(1830),
            new StreamingOptions(NearWaitSeconds: 20, MaxWaitSeconds: maxWait, BuildMemoryBytes: buildMemory, BuildTiles: buildTiles), nightPause));
        return new Setup(server, tiles, roots, builder, view, streamer);
    }

    private T Track<T>(T item) where T : IDisposable
    {
        _disposables.Add(item);
        return item;
    }

    private static VirtualPath Texture(string stem = Stem) => VirtualPath.Of("textures", stem + ".dds");

    private static int[] Zooms(FakeTileServer server) =>
        [.. server.Requests.Select(u => int.Parse(u.Segments[1].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>The column of the ZL16 texture a tile belongs to (35824 for <see cref="Stem"/>), at zoom 16 or coarser.</summary>
    private static int ColumnOf(Uri tile)
    {
        int zoom = int.Parse(tile.Segments[1].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture);
        int x = int.Parse(tile.Segments[2].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture);
        return (x << (16 - zoom)) & ~15;
    }

    /// <summary>The row of the ZL16 texture a tile belongs to (21904 for <see cref="Stem"/>), at zoom 16 or coarser.</summary>
    private static int RowOf(Uri tile)
    {
        int zoom = int.Parse(tile.Segments[1].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture);
        int y = int.Parse(tile.Segments[3].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture);
        return (y << (16 - zoom)) & ~15;
    }

    /// <summary>Whether a tile of the texture in that column was asked for at that zoom.</summary>
    private static bool Requested(Setup s, int column, int zoom) =>
        s.Server.Requests.Any(u => ColumnOf(u) == column && int.Parse(u.Segments[1].TrimEnd('/'), System.Globalization.CultureInfo.InvariantCulture) == zoom);

    /// <summary>A download hold the test releases; released anyway when the test ends.</summary>
    private TaskCompletionSource Hold()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holds.Add(hold);
        return hold;
    }

    private static async Task Until(Func<bool> condition, double seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Reads one whole level the way X-Plane does: from its first byte to its last.</summary>
    private static (byte[] Bytes, ReadAnswer Answer) ReadLevel(TextureStreamer streamer, StreamedTexture t, int mip, ReadCursor? cursor = null)
    {
        byte[] bytes = new byte[t.Layout.LengthOf(mip)];
        ReadAnswer answer = streamer.Read(t, cursor ?? new ReadCursor(), t.Layout.Offset(mip), bytes);
        return (bytes, answer);
    }

    private static void AssertColor((byte R, byte G, byte B) expected, byte[] level, int size)
    {
        byte[] rgb = Dds.DecodeLevel(level, size, size);
        Assert.InRange(rgb[0], expected.R - 8, expected.R + 8);
        Assert.InRange(rgb[1], expected.G - 8, expected.G + 8);
        Assert.InRange(rgb[2], expected.B - 8, expected.B + 8);
    }

    // ---------------------------------------------------------------- the file

    [Fact]
    public void The_layout_matches_a_whole_encoded_dds_level_by_level()
    {
        byte[] rgb = new byte[64 * 64 * 3];
        new Random(1).NextBytes(rgb);
        byte[] dds = Dds.Encode(rgb, 64, 64, workers: 1, out _, out _);
        DdsLayout layout = DdsLayout.For(64);

        Assert.Equal(7, layout.Levels);
        Assert.Equal(dds.Length, layout.Length);
        Assert.Equal(dds[..Dds.HeaderSize], layout.Header.ToArray());
        Assert.Equal(dds[Dds.HeaderSize..], Dds.EncodeChain(rgb, layout, 0, workers: 1));
        Assert.Equal(dds[(int)layout.Offset(3)..], Dds.EncodeChain(Images.BoxHalf(Images.BoxHalf(Images.BoxHalf(rgb, 64, 64, 1), 32, 32, 1), 16, 16, 1), layout, 3, 1));
        Assert.Equal(0, layout.MipAt(Dds.HeaderSize));
        Assert.Equal(0, layout.MipAt(layout.Offset(1) - 1));
        Assert.Equal(1, layout.MipAt(layout.Offset(1)));
        Assert.Equal(6, layout.MipAt(layout.Length - 1));
    }

    [Fact]
    public void Chains_encoded_one_after_another_match_a_whole_dds_although_they_share_pooled_pictures()
    {
        DdsLayout layout = DdsLayout.For(256);
        foreach (int seed in new[] { 1, 2, 3 }) // the second and third get the pooled pictures the one before left dirty
        {
            byte[] rgb = new byte[256 * 256 * 3];
            new Random(seed).NextBytes(rgb);
            byte[] dds = Dds.Encode(rgb, 256, 256, workers: 1, out _, out _);
            Assert.Equal(dds[Dds.HeaderSize..], Dds.EncodeChain(rgb, layout, 0, workers: 2));
        }
    }

    [Fact]
    public void Box_halving_into_a_longer_pooled_array_writes_the_same_pixels()
    {
        byte[] rgb = new byte[32 * 32 * 3];
        new Random(4).NextBytes(rgb);
        byte[] longer = new byte[16 * 16 * 3 + 100];
        Array.Fill(longer, (byte)7);

        Images.BoxHalfInto(rgb, 32, 32, longer, 1);

        Assert.Equal(Images.BoxHalf(rgb, 32, 32, 1), longer[..(16 * 16 * 3)]);
        Assert.Throws<ArgumentException>(() => Images.BoxHalfInto(rgb, 32, 32, new byte[16 * 16 * 3 - 1], 1));
    }

    [Fact]
    public void A_flat_level_is_one_opaque_colour()
    {
        byte[] level = Dds.FlatLevel(16, 96, 102, 80);
        Assert.Equal(Dds.LevelLength(16), level.Length);
        AssertColor(FlatColor, level, 16);
    }

    [Fact]
    public void The_top_rows_of_a_stretched_level_are_the_whole_levels_first_bytes()
    {
        byte[] rgb = new byte[64 * 64 * 3];
        new Random(2).NextBytes(rgb);
        byte[] whole = Images.ResizeBicubic(rgb, 64, 64, 1024, 1024, workers: 2);

        byte[] top = Images.ResizeBicubicTop(rgb, 64, 64, 1024, 1024, rows: 32, workers: 2);
        Assert.Equal(1024 * 32 * 3, top.Length);
        Assert.Equal(whole[..top.Length], top);
        Assert.Equal(Dds.EncodeLevel(whole, 1024, workers: 2)[..(256 * 8 * 8)], Dds.EncodeRows(top, 1024, 32, workers: 2)); // 8 rows of 256 blocks
        Assert.Equal(Dds.FlatLevel(1024, 96, 102, 80)[..1000], Dds.FlatBlocks(1000, 96, 102, 80)[..1000]);
    }

    [Fact]
    public void Every_read_gets_the_header_and_its_full_length()
    {
        Setup s = Streaming();
        StreamedTexture t = s.Streamer.Get(Texture())!;
        Assert.Equal(1024, t.Layout.Size);

        byte[] header = new byte[Dds.HeaderSize];
        Assert.Equal(Dds.HeaderSize, s.Streamer.Read(t, new ReadCursor(), 0, header).Bytes);
        Assert.Equal(t.Layout.Header.ToArray(), header);

        // A read past the last level ends at the end of the file; one beyond it reads nothing.
        byte[] tail = new byte[4096];
        long lastLevel = t.Layout.Offset(t.Layout.Levels - 1);
        Assert.Equal((int)(t.Layout.Length - lastLevel), s.Streamer.Read(t, new ReadCursor(), lastLevel, tail).Bytes);
        Assert.Equal(0, s.Streamer.Read(t, new ReadCursor(), t.Layout.Length, tail).Bytes);
    }

    [Fact]
    public void Each_level_is_fetched_at_the_zoom_it_needs()
    {
        var plan = new TexturePlan(0, TextureSizing.MaxSize); // Ultra: ZL16 textures 4096 px from zoom 17
        var zl16 = new StreamedTexture(Texture(), TextureName.TryParseFile(Stem + ".dds", out var name) ? name : default, plan, 12);
        Assert.Equal(4096, zl16.Layout.Size);
        Assert.Equal([17, 15, 14, 13, 12], Enumerable.Range(0, 5).Select(zl16.ZoomFor));
        Assert.Equal(4, zl16.BaseMip); // level 4 of a ZL16 texture is one ZL12 tile

        var coarse = new StreamedTexture(Texture(), name, new TexturePlan(-2, TextureSizing.MaxSize), 12);
        Assert.Equal((2048, 15, 3), (coarse.Layout.Size, coarse.ZoomFor(0), coarse.BaseMip));
    }

    // ---------------------------------------------------------------- reads

    [Fact]
    public async Task A_far_read_is_answered_at_once_and_its_one_base_tile_is_fetched_behind_it()
    {
        Setup s = Streaming();
        StreamedTexture t = s.Streamer.Get(Texture())!;

        var (first, answer) = ReadLevel(s.Streamer, t, t.BaseMip);
        Assert.Equal((Band.Far, Served.Flat), (answer.Band, answer.Served)); // nothing of the place at hand yet
        Assert.Equal(t.Layout.LengthOf(t.BaseMip), answer.Bytes);
        AssertColor(FlatColor, first, t.Layout.SizeOf(t.BaseMip));

        await Until(() => s.Streamer.RealFrom(t) <= t.BaseMip);
        var (second, again) = ReadLevel(s.Streamer, t, t.BaseMip);
        Assert.Equal(Served.Real, again.Served);
        AssertColor(TileColor, second, t.Layout.SizeOf(t.BaseMip));
        Assert.Equal([12], Zooms(s.Server)); // one ZL12 tile, kept in the low-zoom base
        Assert.True(s.Tiles.Store!.Contains(35824 >> 4, 21904 >> 4, 12));
    }

    [Fact]
    public void A_near_read_waits_for_full_detail_and_the_whole_texture_is_cached()
    {
        Setup s = Streaming();
        StreamedTexture t = s.Streamer.Get(Texture())!;

        var (bytes, answer) = ReadLevel(s.Streamer, t, 0);
        Assert.Equal((Band.Near, Served.Real), (answer.Band, answer.Served));
        AssertColor(TileColor, bytes, 1024);
        Assert.Equal(16, Zooms(s.Server).Count(z => z == 14)); // 4 x 4 tiles at zoom 14, nothing else
        Assert.All(Zooms(s.Server), z => Assert.Equal(14, z));

        string cached = s.Roots.CachedFile(Texture())!;
        Assert.Equal(bytes, File.ReadAllBytes(cached).AsSpan((int)t.Layout.Offset(0), bytes.Length).ToArray());
    }

    [Fact]
    public void A_read_of_level_1_downloads_only_level_1s_zoom_and_keeps_the_chain()
    {
        Setup s = Streaming();
        StreamedTexture t = s.Streamer.Get(Texture())!;

        var (_, answer) = ReadLevel(s.Streamer, t, 1);
        Assert.Equal((Band.Near, Served.Real), (answer.Band, answer.Served));
        Assert.Equal([13, 13, 13, 13], Zooms(s.Server)); // 2 x 2 tiles at zoom 13 instead of 16 at zoom 14
        Assert.True(File.Exists(_tree.PathOf("cache/.xpstream/mips/5476_8956_PX14.mip1"))); // named for zoom 14, where it is downloaded
        Assert.Null(s.Roots.CachedFile(Texture())); // level 0 was never read, so never fetched
    }

    [Fact]
    public async Task A_middle_read_that_times_out_gets_a_stand_in_from_the_base_then_the_real_level()
    {
        Setup s = Streaming(level: -2, maxWait: 0.2, delay: z => z == 13 ? TimeSpan.FromSeconds(1.5) : TimeSpan.Zero);
        StreamedTexture t = s.Streamer.Get(Texture())!;
        Assert.Equal(3, t.BaseMip);
        ReadLevel(s.Streamer, t, 3);
        await Until(() => s.Streamer.RealFrom(t) <= 3); // the base tile is in
        s.View.Update(new CameraReport(t.Lat - 3, t.Lon, 300, null, new Motion(60, 0), false, DateTime.UtcNow)); // flying: max-wait

        var (standIn, answer) = ReadLevel(s.Streamer, t, 2);
        Assert.Equal((Band.Middle, Served.StandIn), (answer.Band, answer.Served));
        Assert.InRange(answer.WaitMs, 150, 1400);
        AssertColor(TileColor, standIn, 512); // stretched from the base: the right picture, only blurrier

        await Until(() => s.Streamer.RealFrom(t) <= 2);
        Assert.Equal(Served.Real, ReadLevel(s.Streamer, t, 2).Answer.Served);
        Assert.Equal(4, Zooms(s.Server).Count(z => z == 13));
    }

    [Fact]
    public async Task At_a_start_a_middle_read_waits_near_wait_for_the_real_level()
    {
        // The Extra start of 3 Nov: parked on the loading screen, middle reads got stand-ins after max-wait, and
        // X-Plane read none of them again until the aircraft moved.
        Setup s = Streaming(level: -2, maxWait: 0.2, delay: z => z == 13 ? TimeSpan.FromSeconds(1.5) : TimeSpan.Zero);
        StreamedTexture t = s.Streamer.Get(Texture())!;
        ReadLevel(s.Streamer, t, 3);
        await Until(() => s.Streamer.RealFrom(t) <= 3);
        Assert.True(s.View.Starting); // no position yet

        var (_, answer) = ReadLevel(s.Streamer, t, 2);

        Assert.Equal((Band.Middle, Served.Real), (answer.Band, answer.Served));
        Assert.True(answer.WaitMs > 1000, $"waited {answer.WaitMs} ms, past max-wait for the 1.5 s picture");
        StreamingFacts facts = s.Streamer.Facts();
        Assert.Equal((0, 0L), (facts.StartTimedOut, facts.FlightWaits)); // a start wait, not a flight one
    }

    [Fact]
    public async Task Real_levels_are_found_again_after_a_restart_without_downloads()
    {
        Setup first = Streaming(level: -2);
        StreamedTexture t = first.Streamer.Get(Texture())!;
        ReadLevel(first.Streamer, t, 2); // middle: fetched at zoom 13 and saved
        await Until(() => first.Streamer.RealFrom(t) <= 2);
        int downloads = first.Server.Requests.Count;

        Setup second = Streaming(level: -2, sharing: first);
        StreamedTexture again = second.Streamer.Get(Texture())!;
        Assert.Equal(2, second.Streamer.RealFrom(again));
        var (bytes, answer) = ReadLevel(second.Streamer, again, 2);
        Assert.Equal(Served.Real, answer.Served);
        AssertColor(TileColor, bytes, 512);
        Assert.Equal(downloads, first.Server.Requests.Count);
    }

    [Fact]
    public async Task A_level_read_in_pieces_stays_one_version_when_better_data_arrives_in_between()
    {
        Setup s = Streaming();
        StreamedTexture t = s.Streamer.Get(Texture())!;
        int mip = t.BaseMip, half = t.Layout.LengthOf(mip) / 2;
        var cursor = new ReadCursor();
        byte[] firstHalf = new byte[half], secondHalf = new byte[half];

        s.Streamer.Read(t, cursor, t.Layout.Offset(mip), firstHalf); // flat: nothing at hand
        await Until(() => s.Streamer.RealFrom(t) <= mip);
        s.Streamer.Read(t, cursor, t.Layout.Offset(mip) + half, secondHalf);
        Assert.Equal(Dds.FlatLevel(t.Layout.SizeOf(mip), 96, 102, 80)[half..(2 * half)], secondHalf);

        // The next load (a new handle) gets the real level.
        Assert.Equal(Served.Real, ReadLevel(s.Streamer, t, mip).Answer.Served);
    }

    [Fact]
    public void A_buffered_read_that_takes_a_little_of_the_finer_level_waits_only_for_the_one_it_is_after()
    {
        Setup s = Streaming();
        StreamedTexture t = s.Streamer.Get(Texture())!;
        long start = t.Layout.Offset(t.BaseMip) - 64; // the last 64 bytes of level 1, then the base levels
        byte[] bytes = new byte[t.Layout.Length - start];

        ReadAnswer answer = s.Streamer.Read(t, new ReadCursor(), start, bytes);
        Assert.Equal(Band.Far, answer.Band); // not near, as level 1 alone would be
        Assert.Equal(bytes.Length, answer.Bytes);
        Assert.DoesNotContain(13, Zooms(s.Server)); // level 1's zoom is never fetched for it
    }

    [Fact]
    public void The_camera_can_make_a_read_nearer_but_never_farther()
    {
        Setup s = Streaming();
        StreamedTexture t = s.Streamer.Get(Texture())!;
        Assert.Equal(Band.Far, s.Streamer.Classify(t, 5).Band); // no camera: the level read decides

        s.View.Update(new CameraReport(t.Lat, t.Lon, 2, null, null, false, DateTime.UtcNow));
        Assert.Equal(Band.Near, s.Streamer.Classify(t, 5).Band); // right above it

        s.View.Update(new CameraReport(t.Lat + 1, t.Lon, 2, null, null, false, DateTime.UtcNow));
        Assert.Equal(Band.Near, s.Streamer.Classify(t, 0).Band); // 110 km away, but X-Plane wants full detail
        Assert.Equal(Band.Far, s.Streamer.Classify(t, 5).Band);

        s.View.Update(new CameraReport(t.Lat, t.Lon, 2, null, null, false, DateTime.UtcNow - TimeSpan.FromMinutes(1)));
        Assert.Equal((Band.Far, (double?)null), s.Streamer.Classify(t, 5)); // an old position is not trusted
    }

    // ---------------------------------------------------------------- X-Plane's read pattern (from a real session)

    [Fact]
    public void Level_0_read_on_from_the_header_chunk_waits_for_full_detail_and_stays_real()
    {
        Setup s = Streaming();
        StreamedTexture t = s.Streamer.Get(Texture())!;
        var cursor = new ReadCursor();

        // X-Plane always reads the first 64 KB first: the header and the start of level 0. That waits for nothing.
        ReadAnswer header = s.Streamer.Read(t, cursor, 0, new byte[ReadPattern.HeaderChunk]);
        Assert.Equal((null, null), (header.Wanted, header.Band));
        Assert.Empty(s.Server.Requests);

        // Reading on from there means it wants level 0: that read waits for it, and the rest of level 0 is real too.
        ReadAnswer next = s.Streamer.Read(t, cursor, ReadPattern.HeaderChunk, new byte[65536]);
        Assert.Equal((0, Band.Near, Served.Real), (next.Wanted, next.Band, next.Served));
        Assert.All(Zooms(s.Server), z => Assert.Equal(14, z));
        long rest = t.Layout.Offset(1) - 2 * ReadPattern.HeaderChunk;
        byte[] tail = new byte[rest];
        ReadAnswer more = s.Streamer.Read(t, cursor, 2 * ReadPattern.HeaderChunk, tail);
        Assert.Equal(Served.Real, more.Served);
        AssertColor(TileColor, tail[^8..], 4); // the last block of level 0
        Assert.Equal(2, s.Streamer.Reads); // the header chunk is not a read of a level
    }

    [Fact]
    public async Task The_header_chunk_gets_the_first_rows_of_level_0s_stand_in_without_making_the_whole_level()
    {
        Setup s = Streaming(); // 1024 px: the header chunk holds the first 32 of level 0's rows
        StreamedTexture t = s.Streamer.Get(Texture())!;
        byte[] chunk = new byte[ReadPattern.HeaderChunk];
        int levelBytes = chunk.Length - Dds.HeaderSize;

        Assert.Equal(Served.Flat, s.Streamer.Read(t, new ReadCursor(), 0, chunk).Served); // nothing of the place at hand
        Assert.Equal(Dds.FlatLevel(1024, 96, 102, 80)[..levelBytes], chunk[Dds.HeaderSize..]);

        ReadLevel(s.Streamer, t, t.BaseMip);
        await Until(() => s.Streamer.RealFrom(t) <= t.BaseMip);
        Assert.Equal(Served.StandIn, s.Streamer.Read(t, new ReadCursor(), 0, chunk).Served);
        int baseSize = t.Layout.SizeOf(t.BaseMip);
        byte[] baseRgb = Dds.DecodeLevel(ReadLevel(s.Streamer, t, t.BaseMip).Bytes, baseSize, baseSize);
        byte[] wholeStandIn = Dds.EncodeLevel(s.Builder.ResizeTo(baseRgb, baseSize, 1024), 1024, workers: 1);
        Assert.Equal(wholeStandIn[..levelBytes], chunk[Dds.HeaderSize..]); // what the whole stand-in has there
        lock (t.Gate) Assert.False(t.StandIns.ContainsKey(0)); // made for those rows only, and not kept
    }

    [Fact]
    public void Level_1_read_after_the_header_chunk_downloads_only_level_1()
    {
        Setup s = Streaming(); // 1024 px: level 1 is zoom 13, 4 tiles; full detail would be 16 at zoom 14
        StreamedTexture t = s.Streamer.Get(Texture())!;
        var cursor = new ReadCursor();
        s.Streamer.Read(t, cursor, 0, new byte[ReadPattern.HeaderChunk]);

        long chunk = t.Layout.Offset(1) / 65536 * 65536; // X-Plane's 64 KB chunk holding level 1's start
        ReadAnswer answer = s.Streamer.Read(t, cursor, chunk, new byte[65536]);
        Assert.Equal((1, Band.Near, Served.Real), (answer.Wanted, answer.Band, answer.Served));
        Assert.Equal([13, 13, 13, 13], Zooms(s.Server));
        Assert.Null(s.Roots.CachedFile(Texture()));
    }

    [Fact]
    public void A_distance_from_the_estimated_position_is_not_logged_as_a_measurement()
    {
        Setup s = Streaming();
        StreamedTexture t = s.Streamer.Get(Texture())!;
        var (_, answer) = ReadLevel(s.Streamer, t, 0); // full detail: places the aircraft here
        Assert.True(s.View.Current!.Inferred);
        Assert.Null(answer.DistanceM);
    }

    [Fact]
    public async Task The_tail_read_of_the_loading_screen_asks_only_for_the_levels_after_its_first_bytes()
    {
        Setup s = Streaming(level: -2); // 2048 px, base level 3
        StreamedTexture t = s.Streamer.Get(Texture())!;
        var cursor = new ReadCursor();
        s.Streamer.Read(t, cursor, 0, new byte[ReadPattern.HeaderChunk]);

        // X-Plane's 64 KB buffering starts the read 20 KB inside level 2, but it is after level 3 and smaller.
        const int length = 64312;
        ReadAnswer answer = s.Streamer.Read(t, cursor, t.Layout.Length - length, new byte[length]);
        Assert.Equal((3, Band.Far), (answer.Wanted, answer.Band));
        await Until(() => s.Streamer.RealFrom(t) <= 3);
        Assert.Equal([12], Zooms(s.Server)); // one base tile, not level 2's 16 tiles at zoom 14
    }

    // ---------------------------------------------------------------- photos skipped at night

    [Fact]
    public async Task At_night_a_near_read_downloads_only_the_base_tile_and_is_not_counted_as_a_blurry_read()
    {
        var sun = new Daylight();
        sun.Offer(-20);
        Setup s = Streaming(maxWait: 5, nightPause: sun);
        StreamedTexture t = s.Streamer.Get(Texture())!;

        var (bytes, answer) = ReadLevel(s.Streamer, t, 0);
        Assert.True(s.Streamer.PhotosPaused);
        Assert.Equal((Band.Near, Served.StandIn), (answer.Band, answer.Served)); // the base, stretched
        AssertColor(TileColor, bytes, 1024);
        Assert.Equal([12], Zooms(s.Server));
        Assert.Null(s.Roots.CachedFile(Texture()));
        // Blurry by choice: not a read the tuning, the flight report or the Home page counts against the photos.
        Assert.Equal((0L, 0L, 1L), (s.Streamer.Reads, s.Streamer.StandInReads, s.Streamer.NightReads));

        await Until(() => s.Streamer.RealFrom(t) == t.BaseMip);
        Assert.Equal([12], Zooms(s.Server));
    }

    [Fact]
    public async Task At_night_every_piece_of_a_level_read_in_pieces_counts_as_a_night_read()
    {
        var sun = new Daylight();
        sun.Offer(-20);
        Setup s = Streaming(maxWait: 5, nightPause: sun);
        StreamedTexture t = s.Streamer.Get(Texture())!;
        ReadLevel(s.Streamer, t, t.BaseMip); // the base, so the night reads below are stretched from it
        await Until(() => s.Streamer.RealFrom(t) == t.BaseMip);
        (long reads, long standIns) = (s.Streamer.Reads, s.Streamer.StandInReads); // the base's own read, flat before it arrived

        var cursor = new ReadCursor();
        long start = t.Layout.Offset(1);
        int half = t.Layout.LengthOf(1) / 2;
        Assert.Equal(Served.StandIn, s.Streamer.Read(t, cursor, start, new byte[half]).Served);
        Assert.Equal(Served.StandIn, s.Streamer.Read(t, cursor, start + half, new byte[t.Layout.LengthOf(1) - half]).Served);

        Assert.Equal((reads, standIns, 2L), (s.Streamer.Reads, s.Streamer.StandInReads, s.Streamer.NightReads));
    }

    [Fact]
    public void At_night_a_texture_already_in_the_cache_is_served_sharp()
    {
        var sun = new Daylight();
        Setup s = Streaming(nightPause: sun);
        StreamedTexture t = s.Streamer.Get(Texture())!;
        ReadLevel(s.Streamer, t, 0); // by day: the whole texture
        int downloads = s.Server.Requests.Count;

        sun.Offer(-20);
        Setup again = Streaming(sharing: s, nightPause: sun); // the next session finds it in the cache
        StreamedTexture cached = again.Streamer.Get(Texture())!;
        var (_, answer) = ReadLevel(again.Streamer, cached, 0);
        Assert.Equal(Served.Real, answer.Served);
        Assert.Equal(downloads, s.Server.Requests.Count);
    }

    [Fact]
    public async Task At_night_load_ahead_fetches_only_base_tiles_even_beside_the_aircraft()
    {
        var sun = new Daylight();
        sun.Offer(-20);
        Setup s = Streaming(nightPause: sun);
        var catalog = new TextureCatalog();
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        StreamedTexture near = s.Streamer.Get(Texture())!;
        s.View.Update(new CameraReport(near.Lat, near.Lon, 2, null, null, false, DateTime.UtcNow));
        catalog.AddTerrain(VirtualPath.Of("terrain", Stem + ".ter"));

        warmer.StartWhatFits();
        await Until(() => s.Streamer.RealFrom(near) <= near.BaseMip);
        Assert.Equal([12], Zooms(s.Server));
    }

    [Fact]
    public void In_twilight_by_day_and_before_XPlane_sends_the_sun_the_photos_stream_in_full()
    {
        var sun = new Daylight();
        Setup s = Streaming(nightPause: sun);
        Assert.False(s.Streamer.PhotosPaused); // no sun yet
        sun.Offer(-3);
        Assert.False(s.Streamer.PhotosPaused); // dusk: the ground is still seen
        sun.Offer(-8);
        Assert.False(s.Streamer.PhotosPaused); // past civil twilight, but the sky still lights the ground

        StreamedTexture t = s.Streamer.Get(Texture())!;
        var (_, answer) = ReadLevel(s.Streamer, t, 0);
        Assert.Equal(Served.Real, answer.Served);
        Assert.Equal(16, Zooms(s.Server).Count(z => z == 14));
    }

    [Fact]
    public void When_the_sun_rises_the_next_read_gets_the_photo_again()
    {
        var sun = new Daylight();
        sun.Offer(-20);
        Setup s = Streaming(maxWait: 5, nightPause: sun);
        StreamedTexture t = s.Streamer.Get(Texture())!;
        Assert.Equal(Served.StandIn, ReadLevel(s.Streamer, t, 1).Answer.Served);

        sun.Offer(10);
        var (_, answer) = ReadLevel(s.Streamer, t, 1);
        Assert.Equal(Served.Real, answer.Served);
        Assert.Contains(13, Zooms(s.Server));
        Assert.Equal((1L, 1L), (s.Streamer.Reads, s.Streamer.NightReads));
    }

    // ---------------------------------------------------------------- load-ahead

    [Fact]
    public async Task Before_a_position_arrives_load_ahead_fetches_one_base_tile_per_texture()
    {
        Setup s = Streaming();
        var catalog = new TextureCatalog();
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        catalog.AddTerrain(VirtualPath.Of("terrain", "21904_35824_BI16.ter"));
        catalog.AddTerrain(VirtualPath.Of("terrain", "21904_35840_BI16.ter"));

        warmer.StartWhatFits();
        await Until(() => catalog.Entries.All(e => s.Streamer.RealFrom(s.Streamer.Get(e.Texture)!) <= 2));
        Assert.Equal([12, 12], Zooms(s.Server));
    }

    [Fact]
    public async Task The_departure_area_is_fetched_as_XPlane_will_want_it_from_the_gate()
    {
        // Before X-Plane sends a position: the texture at the departure in full (its base tile first), the far one as a base tile.
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        StreamedTexture near = s.Streamer.Get(Texture())!, far = s.Streamer.Get(Texture("21904_36144_BI16"))!; // 20 textures east

        var (queued, had) = warmer.PrefetchAround([Texture(), Texture("21904_36144_BI16")], near.Lat, near.Lon);
        await Until(() =>
        {
            warmer.StartWhatFits();
            return s.Streamer.RealFrom(near) == 0 && s.Streamer.RealFrom(far) <= far.BaseMip;
        });

        Assert.Equal((2, 0), (queued, had));
        Assert.Equal(16, Zooms(s.Server).Count(z => z == 14)); // only the near one in full
        Assert.Equal(2, Zooms(s.Server).Count(z => z == 12)); // a base tile each
        Assert.Equal((0, 2), warmer.PrefetchAround([Texture(), Texture("21904_36144_BI16")], near.Lat, near.Lon)); // the next start: nothing to do
    }

    [Fact]
    public void The_departure_prefetch_is_never_more_urgent_than_Warm()
    {
        // R2: full detail beside the gate is still a guess; it must not rank with load-ahead next to the camera.
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        StreamedTexture near = s.Streamer.Get(Texture())!;
        VirtualPath middle = Texture("21904_35856_BI16"), far = Texture("21904_36144_BI16");

        warmer.PrefetchAround([Texture(), middle, far], near.Lat, near.Lon);

        Assert.Equal(FetchPriority.Warm, warmer.WaitingFor(Texture())!.Value.Priority); // its base tile first, at Warm
        Assert.All(new[] { Texture(), middle, far }, texture => Assert.True(warmer.WaitingFor(texture)!.Value.Priority >= FetchPriority.Warm));
    }

    [Fact]
    public async Task The_end_of_the_ground_prewarm_is_told_once_it_is_all_built_but_not_after_a_takeoff_cancel()
    {
        // F3: the build buffers are given back while the aircraft still stands at the gate.
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        int done = 0;
        warmer.GroundDone += () => done++;
        (double, double) Far(double lat, double lon) => (500_000, 0); // a base tile only
        StreamedTexture arrival = s.Streamer.Get(Texture("21904_36144_BI16"))!;

        warmer.CheckGroundDone();
        Assert.Equal(0, done); // nothing queued yet

        Assert.Equal((1, 0), warmer.PrefetchGround([Texture("21904_36144_BI16")], Far, GroundPrewarm.Arrival));
        await Until(() =>
        {
            warmer.StartWhatFits();
            warmer.CheckGroundDone();
            return done > 0;
        });
        Assert.True(s.Streamer.RealFrom(arrival) <= arrival.BaseMip);
        warmer.CheckGroundDone();
        Assert.Equal(1, done); // once

        // Dropped on the takeoff roll: the aircraft moves, no compaction then.
        warmer.PrefetchGround([Texture("21824_35824_BI16")], Far, GroundPrewarm.Route);
        Assert.Equal(1, warmer.CancelGround());
        warmer.CheckGroundDone();
        Assert.Equal(1, done);
    }

    [Fact]
    public async Task Each_prefetch_phase_counts_its_builds_downloads_and_what_XPlane_loaded_later()
    {
        // R12: the numbers for the PR, per phase.
        Setup s = Streaming();
        var catalog = new TextureCatalog();
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        const string arrivalStem = "21904_36144_BI16";
        StreamedTexture arrival = s.Streamer.Get(Texture(arrivalStem))!;
        (double, double) Beside(double lat, double lon) => (0, 0);
        Assert.All(warmer.Phases, p => Assert.Null(p.Summary())); // nothing done: nothing logged

        warmer.PrefetchGround([Texture(arrivalStem)], Beside, GroundPrewarm.Arrival);
        await Until(() =>
        {
            warmer.StartWhatFits();
            return s.Streamer.RealFrom(arrival) == 0 && warmer.WaitingFor(Texture(arrivalStem)) is null;
        });
        PrefetchMeter meter = warmer.Phases.Single(p => p.Phase == "arrival");
        await Until(() => meter.Built == 2); // its base tile, then full detail
        Assert.Equal((1, 2L, 0L), (meter.Queued, meter.Started, meter.Failed));
        Assert.Equal(17, meter.Tiles); // 1 base tile + 16 at zoom 14
        Assert.True(meter.Bytes > 0);
        Assert.True(meter.BuildTime > TimeSpan.Zero);
        Assert.Equal(0, meter.Loaded);

        catalog.AddTerrain(VirtualPath.Of("terrain", arrivalStem + ".ter")); // X-Plane loads it on the approach
        Assert.Equal(1, meter.Loaded);
        Assert.Contains("X-Plane loaded 1 of the 1 queued (100 %)", meter.Summary(), StringComparison.Ordinal);

        // The departure, built already: counted as had, not queued; the other phases stay silent.
        warmer.PrefetchAround([Texture(arrivalStem)], arrival.Lat, arrival.Lon);
        PrefetchMeter departure = warmer.Phases.Single(p => p.Phase == "departure");
        Assert.Equal((0, 1L), (departure.Queued, departure.Had));
        Assert.Null(warmer.Phases.Single(p => p.Phase == "climb").Summary());
    }

    [Fact]
    public async Task Once_the_download_budget_is_spent_the_prefetch_stops_and_load_ahead_goes_on()
    {
        // R4: a 1-byte budget is spent by the first build; the rest of the prefetch is left to load-ahead.
        Setup s = Streaming();
        var catalog = new TextureCatalog();
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        var budget = new PrefetchBudget(1);
        warmer.UseBudget(budget);
        (double, double) Far(double lat, double lon) => (500_000, 0); // base tiles only: one build each
        VirtualPath first = Texture("21904_36144_BI16"), second = Texture("21824_35824_BI16");
        StreamedTexture firstTexture = s.Streamer.Get(first)!;

        warmer.PrefetchGround([first], Far, GroundPrewarm.Arrival);
        await Until(() =>
        {
            warmer.StartWhatFits();
            return budget.Exhausted;
        });
        Assert.True(s.Streamer.RealFrom(firstTexture) <= firstTexture.BaseMip);

        warmer.StartWhatFits(); // drops what is left, logs once
        Assert.Equal((0, 0), warmer.PrefetchGround([second], Far, GroundPrewarm.Route));
        Assert.Null(warmer.WaitingFor(second));

        catalog.AddTerrain(VirtualPath.Of("terrain", "21824_35824_BI16.ter")); // X-Plane loads it: load-ahead still fetches it
        Assert.NotNull(warmer.WaitingFor(second));
        long spent = budget.SpentBytes;
        StreamedTexture secondTexture = s.Streamer.Get(second)!;
        await Until(() =>
        {
            warmer.StartWhatFits();
            return s.Streamer.RealFrom(secondTexture) <= secondTexture.BaseMip;
        });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(spent, budget.SpentBytes); // load-ahead never counts

        budget.Restart(); // a reloaded new flight plan (a turnaround): the prefetch may queue again
        Assert.False(budget.Exhausted);
        Assert.NotEqual((0, 0), warmer.PrefetchGround([second], Far, GroundPrewarm.Arrival));
    }

    [Fact]
    public async Task A_texture_whose_base_tile_is_cached_already_spends_nothing_of_the_budget()
    {
        // R4: only real downloads count. X-Plane's own read put the base tile in the tile store (another package's
        // texture cache here, so the texture itself was never built for this one): the prefetch finds it there and
        // neither queues nor downloads anything for it.
        Setup flown = Streaming();
        StreamedTexture t = flown.Streamer.Get(Texture())!;
        ReadLevel(flown.Streamer, t, t.BaseMip);
        await Until(() => flown.Streamer.RealFrom(t) <= t.BaseMip);
        int downloads = flown.Server.Requests.Count;

        Setup s = Streaming(sharing: flown, cache: "cache2");
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        var budget = new PrefetchBudget(1_000_000);
        warmer.UseBudget(budget);

        Assert.Equal((0, 1), warmer.PrefetchGround([Texture()], (_, _) => (500_000, 0), GroundPrewarm.Arrival)); // far: its base tile only
        warmer.StartWhatFits();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(downloads, flown.Server.Requests.Count);
        Assert.Equal(0L, budget.SpentBytes);
    }

    [Fact]
    public void The_takeoff_roll_drops_only_the_departures_requests_still_waiting()
    {
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        StreamedTexture here = s.Streamer.Get(Texture())!;
        VirtualPath flightPath = Texture("21904_35856_BI16"), arrival = Texture("21904_36144_BI16");
        (double, double) Beside(double lat, double lon) => (0, 0);

        warmer.PrefetchAround([Texture()], here.Lat, here.Lon);
        warmer.Prefetch([flightPath]);
        warmer.PrefetchGround([arrival], Beside, GroundPrewarm.Arrival);

        Assert.Equal(1, warmer.CancelDeparture());
        Assert.Null(warmer.WaitingFor(Texture()));
        Assert.NotNull(warmer.WaitingFor(flightPath)); // the flight-path prefetch is not the departure's
        Assert.Equal(1, warmer.GroundWaiting);
        Assert.Equal(0, warmer.CancelDeparture());
    }

    [Fact]
    public void The_ground_prewarm_ranks_after_the_departure_and_is_dropped_for_the_takeoff()
    {
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        StreamedTexture here = s.Streamer.Get(Texture())!;
        VirtualPath arrival = Texture("21904_36144_BI16"), route = Texture("21824_35824_BI16");
        (double, double) Beside(double lat, double lon) => (0, 0);

        warmer.PrefetchAround([Texture()], here.Lat, here.Lon); // the departure
        Assert.Equal((1, 0), warmer.PrefetchGround([arrival], Beside, GroundPrewarm.Arrival));
        Assert.Equal((1, 0), warmer.PrefetchGround([route], Beside, GroundPrewarm.Route));
        Assert.Equal((0, 0), warmer.PrefetchGround([Texture()], Beside, GroundPrewarm.Route)); // the departure's request stays
        Assert.Equal(2, warmer.GroundWaiting);

        // Full detail asked for, nothing there yet: the base tile first, still below the departure and in phase order.
        int departure = warmer.WaitingFor(Texture())!.Value.Priority;
        int first = warmer.Target(arrival, 0, MipWarmer.GroundPriority(GroundPrewarm.Arrival))!.Value.Priority;
        int last = warmer.Target(route, 0, MipWarmer.GroundPriority(GroundPrewarm.Route))!.Value.Priority;
        Assert.True(departure < FetchPriority.Background && FetchPriority.Background < first && first < last, $"{departure} < {first} < {last}");

        Assert.Equal(2, warmer.CancelGround());
        Assert.Equal(0, warmer.GroundWaiting);
        Assert.Equal(0, warmer.CancelGround());
    }

    [Fact]
    public void A_texture_XPlane_loads_while_the_ground_prewarm_waits_for_it_gets_load_aheads_request()
    {
        Setup s = Streaming();
        var catalog = new TextureCatalog();
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        StreamedTexture here = s.Streamer.Get(Texture())!;
        (double, double) Beside(double lat, double lon) => (0, 0);
        s.View.Update(new CameraReport(here.Lat, here.Lon, 2, null, null, false, DateTime.UtcNow)); // parked beside it

        Assert.Equal((1, 0), warmer.PrefetchGround([Texture()], Beside, GroundPrewarm.Route));
        Assert.True(warmer.WaitingFor(Texture())!.Value.Ground);

        catalog.AddTerrain(VirtualPath.Of("terrain", Stem + ".ter")); // X-Plane loads it
        var request = warmer.WaitingFor(Texture())!.Value;
        Assert.False(request.Ground);
        Assert.Equal((0, FetchPriority.NearWarm), (request.Mip, request.Priority)); // full detail beside the camera, near priority
        Assert.Equal((0, 0), warmer.PrefetchGround([Texture()], Beside, GroundPrewarm.Route)); // and the ground pre-warm doesn't take it back

        Assert.Equal(0, warmer.CancelGround()); // the takeoff roll keeps it
        Assert.NotNull(warmer.WaitingFor(Texture()));
    }

    [Fact]
    public void A_texture_XPlane_loads_while_the_departure_waits_for_it_gets_load_aheads_request()
    {
        // Maintainer, #30 item 2 (H1 for the departure): the takeoff roll drops the departure's requests; a load-ahead
        // request that the departure's had kept out would go with it.
        Setup s = Streaming();
        var catalog = new TextureCatalog();
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        StreamedTexture here = s.Streamer.Get(Texture())!;
        s.View.Update(new CameraReport(here.Lat, here.Lon, 2, null, null, false, DateTime.UtcNow));

        Assert.Equal((1, 0), warmer.PrefetchAround([Texture()], here.Lat, here.Lon));
        catalog.AddTerrain(VirtualPath.Of("terrain", Stem + ".ter")); // X-Plane loads it
        Assert.Equal(FetchPriority.NearWarm, warmer.WaitingFor(Texture())!.Value.Priority);

        Assert.Equal(0, warmer.CancelDeparture()); // the takeoff roll keeps it
        Assert.NotNull(warmer.WaitingFor(Texture()));
    }

    [Fact]
    public void A_departure_queued_again_leaves_load_aheads_waiting_request_alone()
    {
        // H1 the other way round: "Reload" while X-Plane runs queues a departure over textures X-Plane has loaded.
        Setup s = Streaming();
        var catalog = new TextureCatalog();
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        StreamedTexture here = s.Streamer.Get(Texture())!;
        s.View.Update(new CameraReport(here.Lat, here.Lon, 2, null, null, false, DateTime.UtcNow));
        catalog.AddTerrain(VirtualPath.Of("terrain", Stem + ".ter"));
        VirtualPath other = Texture("21904_35856_BI16");

        Assert.Equal((1, 0), warmer.PrefetchAround([Texture(), other], here.Lat, here.Lon)); // only the one X-Plane has not loaded
        Assert.Equal(1, warmer.CancelDeparture());
        Assert.Equal(FetchPriority.NearWarm, warmer.WaitingFor(Texture())!.Value.Priority);

        // A departure request replaces a departure request (the newer plan), and outranks a waiting ground one.
        Assert.Equal((1, 0), warmer.PrefetchGround([other], (_, _) => (0, 0), GroundPrewarm.Arrival));
        Assert.Equal((1, 0), warmer.PrefetchAround([other], here.Lat, here.Lon));
        Assert.Equal((1, 0), warmer.PrefetchAround([other], here.Lat, here.Lon));
        Assert.Equal(0, warmer.GroundWaiting);
        Assert.Equal(1, warmer.CancelDeparture());
    }

    [Fact]
    public void Around_an_airport_the_order_is_load_aheads_nearest_first()
    {
        // R6: PrefetchPlanner.Select without motion orders the departure, the arrival and the alternate.
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        StreamedTexture near = s.Streamer.Get(Texture())!;
        VirtualPath middle = Texture("21904_35856_BI16"), far = Texture("21904_36144_BI16");

        warmer.PrefetchAround([far, middle, Texture()], near.Lat, near.Lon, GroundPrewarm.Arrival);

        double[] order = [.. new[] { Texture(), middle, far }.Select(t => warmer.WaitingFor(t)!.Value.Order)];
        Assert.Equal(order.Order(), order); // nearest first, in metres like load-ahead's own requests
        Assert.InRange(order[0], 0, 1);
        Assert.True(order[2] > 20_000);
        Assert.All(new[] { Texture(), middle, far }, t => Assert.True(warmer.WaitingFor(t)!.Value.Ground));
    }

    [Fact]
    public async Task The_build_memory_is_given_back_only_once_XPlane_is_quiet()
    {
        // R1: the compacting collection pauses every thread, X-Plane's reads too: it waits for a quiet moment.
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        using var stop = new CancellationTokenSource();
        int givenBack = 0;
        var timing = new PrewarmMemory.Timing(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10));
        var memory = new PrewarmMemory([warmer], stop.Token, timing, () => Interlocked.Increment(ref givenBack));
        StreamedTexture t = s.Streamer.Get(Texture())!;

        memory.OnGroundDone();
        var reading = System.Diagnostics.Stopwatch.StartNew();
        while (reading.ElapsedMilliseconds < 600)
        {
            ReadLevel(s.Streamer, t, t.BaseMip); // X-Plane reads: not quiet
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref givenBack));
        }
        await memory.Waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, givenBack);

        memory.OnGroundDone(); // once a session
        await memory.Waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, givenBack);
    }

    [Fact]
    public async Task New_prewarm_work_cancels_a_give_back_waiting_for_quiet()
    {
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        using var stop = new CancellationTokenSource();
        int givenBack = 0;
        var timing = new PrewarmMemory.Timing(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10));
        var memory = new PrewarmMemory([warmer], stop.Token, timing, () => givenBack++);

        memory.OnGroundDone();
        warmer.PrefetchGround([Texture("21904_36144_BI16")], (_, _) => (500_000, 0), GroundPrewarm.Arrival); // a reloaded plan's pre-warm
        await memory.Waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, givenBack);
    }

    [Fact]
    public void A_move_of_the_camera_outranks_a_waiting_ground_request_for_a_loaded_texture()
    {
        Setup s = Streaming();
        var catalog = new TextureCatalog();
        catalog.AddTerrain(VirtualPath.Of("terrain", Stem + ".ter")); // loaded before the warmer listens: only a move queues it
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        StreamedTexture here = s.Streamer.Get(Texture())!;
        (double, double) Beside(double lat, double lon) => (0, 0);
        Assert.Equal((1, 0), warmer.PrefetchGround([Texture()], Beside, GroundPrewarm.Route));
        Assert.True(warmer.WaitingFor(Texture())!.Value.Ground);

        s.View.Update(new CameraReport(here.Lat, here.Lon, 2, null, null, false, DateTime.UtcNow)); // the first position: OnMoved
        var request = warmer.WaitingFor(Texture())!.Value;
        Assert.False(request.Ground);
        Assert.Equal(FetchPriority.NearWarm, request.Priority);
        Assert.Equal(0, warmer.CancelGround());
        Assert.NotNull(warmer.WaitingFor(Texture()));
    }

    [Fact]
    public async Task A_texture_whose_base_tile_fails_leaves_the_queue_and_counts_one_build()
    {
        // M3: the base step kept its request whatever happened, so a texture without imagery was tried every pass all
        // session, and each try counted as a build started.
        Setup s = Streaming(status: zoom => zoom == 12 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);

        warmer.Prefetch([Texture()]);
        await Until(() =>
        {
            warmer.StartWhatFits();
            return warmer.WaitingFor(Texture()) is null;
        });
        for (int pass = 0; pass < 5; pass++) warmer.StartWhatFits(); // within the retry time: nothing more

        Assert.Equal(1, warmer.Queued);
        Assert.DoesNotContain(14, Zooms(s.Server)); // the level was never tried without its base
    }

    [Fact]
    public async Task A_whole_texture_prefetched_ahead_of_XPlane_gets_its_base_tile_too()
    {
        // The SimBrief departure: fetched whole before X-Plane loads it, then skipped by load-ahead as complete. Without
        // its base tile the low-zoom base (far scenery, colour matching, the report's map) would stay empty there.
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        StreamedTexture t = s.Streamer.Get(Texture())!;

        warmer.Prefetch([Texture()]);
        await Until(() =>
        {
            warmer.StartWhatFits(); // as its loop does: the base tile, then the level
            return s.Streamer.RealFrom(t) == 0 && s.Builder.StoreHasRegion(t.Name, t.ZoomFor(t.BaseMip));
        });
        int[] zooms = Zooms(s.Server);
        Assert.Equal(1, zooms.Count(z => z == 12));
        Assert.Equal(16, zooms.Count(z => z == 14));
        Assert.Equal(12, zooms[0]); // the base tile before the level's tiles
    }

    [Fact]
    public async Task With_the_camera_beside_a_texture_load_ahead_fetches_it_in_full_and_the_far_one_as_a_base_tile()
    {
        Setup s = Streaming();
        var catalog = new TextureCatalog();
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        StreamedTexture near = s.Streamer.Get(Texture())!, far = s.Streamer.Get(Texture("21904_36144_BI16"))!; // 20 textures east
        s.View.Update(new CameraReport(near.Lat, near.Lon, 2, null, null, false, DateTime.UtcNow));
        catalog.AddTerrain(VirtualPath.Of("terrain", Stem + ".ter"));
        catalog.AddTerrain(VirtualPath.Of("terrain", "21904_36144_BI16.ter"));

        warmer.StartWhatFits();
        await Until(() => s.Streamer.RealFrom(near) == 0 && s.Streamer.RealFrom(far) <= far.BaseMip);
        Assert.Equal(16, Zooms(s.Server).Count(z => z == 14));
        Assert.Equal(1, Zooms(s.Server).Count(z => z == 12));
    }

    [Fact]
    public void XPlanes_smallest_read_takes_level_4_of_a_4096_px_texture()
    {
        // Its last 64 KB chunk starts inside level 3 (levels 4 and smaller are 43 KB): X-Plane is after level 4 at least.
        Assert.Equal(4, ReadPattern.CoarsestRead(DdsLayout.For(4096)));
        // 2048 px: it starts inside level 2, so level 3 (256 px again).
        Assert.Equal(3, ReadPattern.CoarsestRead(DdsLayout.For(2048)));
        // 1024 px: the last chunk starts inside level 1.
        Assert.Equal(2, ReadPattern.CoarsestRead(DdsLayout.For(1024)));
    }

    [Fact]
    public async Task Before_a_position_a_ZL18_texture_gets_the_level_XPlane_reads_at_least_not_just_its_base_tile()
    {
        // At EDDV 926 ZL18 textures waited while X-Plane loaded them: their base tile is level 6, X-Plane takes level 4.
        Setup s = Streaming(level: 0);
        var catalog = new TextureCatalog();
        using var warmer = new MipWarmer(catalog, s.Streamer, s.View, heavy: 2);
        catalog.AddTerrain(VirtualPath.Of("terrain", "90384_137776_BI18.ter"));
        StreamedTexture t = s.Streamer.Get(Texture("90384_137776_BI18"))!;

        await Until(() =>
        {
            warmer.StartWhatFits();
            return s.Streamer.RealFrom(t) <= 4;
        });
        Assert.Equal(6, t.BaseMip);
        Assert.Equal([14], Zooms(s.Server).Distinct()); // level 4 is one tile at zoom 14
    }

    [Fact]
    public void Load_ahead_sorts_a_build_by_its_tiles_with_base_tiles_apart()
    {
        const MipWarmer.BuildKind Base = MipWarmer.BuildKind.Base, Small = MipWarmer.BuildKind.Small, Heavy = MipWarmer.BuildKind.Heavy;

        // 1024 px ZL16 textures: level 0 is 16 tiles at zoom 14, level 1 four at zoom 13, the base level 2 one at zoom 12.
        StreamedTexture zl16 = Streaming().Streamer.Get(Texture())!;
        Assert.Equal([Heavy, Small, Base, Base], Enumerable.Range(0, 4).Select(mip => MipWarmer.KindOf(zl16, mip)));

        // 4096 px ZL18 textures (Ultra): base level 6; level 4 is one tile at zoom 14, level 3 four, level 2 sixteen.
        // Level 4 costs what a base tile does, yet takes a small build's place, never a base tile's.
        StreamedTexture zl18 = Streaming(level: 0).Streamer.Get(Texture("90384_137776_BI18"))!;
        Assert.Equal(6, zl18.BaseMip);
        Assert.Equal([Heavy, Heavy, Heavy, Small, Small, Small, Base], Enumerable.Range(0, 7).Select(mip => MipWarmer.KindOf(zl18, mip)));
    }

    [Fact]
    public void Flying_towards_a_texture_load_ahead_plans_its_next_level_a_minute_before_XPlane_wants_it()
    {
        Setup s = Streaming();
        using var warmer = new MipWarmer(new TextureCatalog(), s.Streamer, s.View, heavy: 2);
        StreamedTexture here = s.Streamer.Get(Texture())!;
        VirtualPath ahead = Texture("21824_35824_BI16"); // five textures north, about 31 km to its centre
        // With k = 1830 this 1024 px texture is level 1 out to 25 km (22 km to its near edge): parked, it is level 2.
        s.View.Update(new CameraReport(here.Lat, here.Lon, 2, null, null, false, DateTime.UtcNow));
        Assert.Equal(2, warmer.Target(ahead)!.Value.Mip);

        // At 120 m/s straight towards it, it is within 25 km in a minute: fetch level 1 now, as near scenery.
        s.View.Update(new CameraReport(here.Lat, here.Lon, 2, null, new Motion(120, 0), false, DateTime.UtcNow));
        var target = warmer.Target(ahead)!.Value;
        Assert.Equal((1, FetchPriority.NearWarm), (target.Mip, target.Priority));
    }

    // The textures next to the aircraft take 16 tiles of 0.6 s: longer than max-wait (0.2 s), well within near-wait (20 s).
    private Setup SlowNearBuilds() => Streaming(maxWait: 0.2, delay: _ => TimeSpan.FromMilliseconds(600));

    [Fact]
    public void A_teleport_after_a_flight_lets_the_textures_next_to_the_aircraft_wait_as_at_a_start()
    {
        // The test flight of 26 Oct: flown from one airport to another, then moved to a third from X-Plane's menu.
        // X-Plane stopped sending positions while it loaded the new place, and read the textures next to the
        // aircraft there a minute before it sent the first position: they must not get max-wait and a stand-in.
        Setup s = SlowNearBuilds();
        StreamedTexture there = s.Streamer.Get(Texture())!;
        s.View.Update(new CameraReport(there.Lat - 3, there.Lon, 300, null, new Motion(60, 0), false, DateTime.UtcNow - TimeSpan.FromSeconds(70)));
        Assert.False(s.View.Starting); // it flew

        var (_, answer) = ReadLevel(s.Streamer, there, 0);

        Assert.Equal((Band.Near, Served.Real), (answer.Band, answer.Served));
        Assert.True(s.View.Starting);
    }

    [Fact]
    public void In_flight_a_read_next_to_the_aircraft_still_waits_only_max_wait()
    {
        Setup s = SlowNearBuilds();
        StreamedTexture here = s.Streamer.Get(Texture())!;
        s.View.Update(new CameraReport(here.Lat, here.Lon, 300, null, new Motion(60, 0), false, DateTime.UtcNow)); // flying, positions coming

        var (_, answer) = ReadLevel(s.Streamer, here, 0);

        Assert.Equal(Band.Near, answer.Band);
        Assert.NotEqual(Served.Real, answer.Served); // a stand-in after 0.2 s: X-Plane is not held up in flight
        Assert.False(s.View.Starting);
    }

    [Fact]
    public void Positions_coming_back_while_flying_end_the_start_that_a_gap_began()
    {
        Setup s = SlowNearBuilds();
        StreamedTexture here = s.Streamer.Get(Texture())!;
        s.View.Update(new CameraReport(here.Lat, here.Lon, 300, null, new Motion(60, 0), false, DateTime.UtcNow - TimeSpan.FromSeconds(30)));
        s.View.NoteNearRead(); // no position for 30 s: taken for a load
        Assert.True(s.View.Starting);

        s.View.Update(new CameraReport(here.Lat, here.Lon, 300, null, new Motion(60, 0), false, DateTime.UtcNow)); // still flying after all
        Assert.False(s.View.Starting);
    }

    [Fact]
    public async Task A_read_in_flight_that_got_a_stand_in_notes_how_long_its_picture_took()
    {
        Setup s = SlowNearBuilds();
        StreamedTexture here = s.Streamer.Get(Texture())!;
        s.View.Update(new CameraReport(here.Lat, here.Lon, 300, null, new Motion(60, 0), false, DateTime.UtcNow));

        ReadLevel(s.Streamer, here, 0); // a stand-in after max-wait (0.2 s); the picture follows

        await Until(() => s.Streamer.Facts().FlightNeeded.Count == 1);
        StreamingFacts facts = s.Streamer.Facts();
        Assert.Equal((1, 1, 0, 0), (facts.FlightWaits, facts.FlightTimedOut, facts.StartWaits, facts.StartTimedOut));
        Assert.True(facts.FlightNeeded[0] > 0.2, $"needed {facts.FlightNeeded[0]} s, more than the 0.2 s it waited");
    }

    [Fact]
    public void A_read_next_to_the_aircraft_at_a_start_counts_as_a_start_wait()
    {
        Setup s = SlowNearBuilds();
        StreamedTexture here = s.Streamer.Get(Texture())!;
        Assert.True(s.View.Starting); // no position yet

        ReadLevel(s.Streamer, here, 0); // waits for the picture: well within near-wait

        StreamingFacts facts = s.Streamer.Facts();
        Assert.Equal((1, 0, 0L), (facts.StartWaits, facts.StartTimedOut, facts.FlightWaits));
        Assert.Single(facts.StartNeeded);
    }

    [Fact]
    public async Task Detailed_builds_take_turns_with_XPlanes_reads_first()
    {
        // Level -3: ZL16 builds download zoom 14. The first and the later build's tiles are held, so the order is certain.
        TaskCompletionSource firstHeld = Hold(), laterHeld = Hold();
        Setup s = Streaming(detailedBuilds: 1, hold: tile => ColumnOf(tile) switch { 35824 => firstHeld.Task, 35840 => laterHeld.Task, _ => null });
        Task<bool> first = s.Streamer.Warm(Texture(), 0, FetchPriority.Warm)!;           // takes the only slot
        await Until(() => Requested(s, 35824, 14));
        Task<bool> later = s.Streamer.Warm(Texture("21904_35840_BI16"), 0, FetchPriority.Warm)!;
        Task<bool> urgent = s.Streamer.Warm(Texture("21904_35856_BI16"), 0, FetchPriority.NearRead)!;
        await Until(() => s.Streamer.BuildsWaiting == 2);

        firstHeld.SetResult();
        // Had the later build gone first, it would hold the slot and the urgent one would never finish.
        Assert.True(await urgent.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(first.IsCompleted);
        Assert.False(later.IsCompleted); // the read that X-Plane waits for went ahead of it
        laterHeld.SetResult();
        Assert.True(await later);
    }

    [Fact]
    public async Task Small_builds_run_together_beside_a_full_size_one_within_the_build_memory()
    {
        // Level -2: 2048 px textures (19 MB to build), their level 2 is 512 px (1.2 MB). Room for two full-size builds:
        // with one running, the six small ones fit beside it together. Counted as slots, they would go one by one.
        // The small builds' tiles are held until all six are downloading at once, so no clock is involved: one by one,
        // the first would wait for the others forever.
        TaskCompletionSource allSmallStarted = Hold();
        var startedSmall = new System.Collections.Concurrent.ConcurrentDictionary<(int Row, int Column), byte>();
        Setup s = Streaming(level: -2, delay: z => z == 15 ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero, detailedBuilds: 2,
            hold: tile =>
            {
                if (RowOf(tile) == 21904 && ColumnOf(tile) == 35824) return null; // the full-size build
                startedSmall.TryAdd((RowOf(tile), ColumnOf(tile)), 0);
                if (startedSmall.Count == 6) allSmallStarted.TrySetResult();
                return allSmallStarted.Task;
            });
        Task<bool> full = s.Streamer.Warm(Texture(), 0, FetchPriority.Warm)!;
        string[] names = ["21904_35840_BI16", "21904_35856_BI16", "21920_35824_BI16", "21920_35840_BI16", "21920_35856_BI16", "21936_35824_BI16"];
        Task<bool>[] small = [.. names.Select(n => s.Streamer.Warm(Texture(n), 2, FetchPriority.Warm)!)];

        await Until(() => allSmallStarted.Task.IsCompleted);
        Assert.All(await Task.WhenAll(small), Assert.True);
        Assert.True(await full);
    }

    [Fact]
    public async Task The_weighted_gate_keeps_the_budget_and_lets_no_small_work_past_a_waiting_big_build()
    {
        var gate = new WeightedPriorityGate(budget: 100, maxEntries: 10);
        await gate.WaitAsync(60, () => FetchPriority.Warm);              // a big build runs
        Task big = gate.WaitAsync(60, () => FetchPriority.Warm);          // a second big one must wait for memory
        Task sameLevel = gate.WaitAsync(10, () => FetchPriority.Warm);    // would fit, but must not pass the waiting big one
        Task urgent = gate.WaitAsync(10, () => FetchPriority.NearRead);   // more urgent: goes first

        Assert.True(urgent.IsCompleted);
        Assert.False(big.IsCompleted);
        Assert.False(sameLevel.IsCompleted);
        Assert.Equal(70, gate.Used);

        gate.Release(10); // the urgent one: still no room for 60 beside the running big build
        Assert.False(big.IsCompleted);
        gate.Release(60); // the first big one ends: the waiting big one, then the small one behind it
        await Task.WhenAll(big, sameLevel);
        Assert.Equal(70, gate.Used);
    }

    [Fact]
    public async Task The_weighted_gate_says_which_budget_holds_its_most_urgent_waiter_back()
    {
        var gate = new WeightedPriorityGate(budget: 100, maxEntries: 10, secondBudget: 100);
        Assert.Equal(GateLimit.None, gate.Limit);
        await gate.WaitAsync(80, 10, () => FetchPriority.Warm);
        Task memory = gate.WaitAsync(30, 10, () => FetchPriority.Warm);
        Assert.Equal(GateLimit.First, gate.Limit);

        Task tiles = gate.WaitAsync(10, 95, () => FetchPriority.NearRead); // more urgent: the one that counts
        Assert.Equal(GateLimit.Second, gate.Limit);

        gate.Release(80, 10);
        await tiles;
        Assert.Equal(GateLimit.Second, gate.Limit); // the other one now waits for tiles
        gate.Release(10, 95);
        await memory;
        Assert.Equal(GateLimit.None, gate.Limit);
    }

    [Fact]
    public async Task The_weighted_gate_lets_work_bigger_than_the_budget_in_alone_and_caps_the_count()
    {
        var gate = new WeightedPriorityGate(budget: 100, maxEntries: 2);
        await gate.WaitAsync(500, () => 0); // too big for the budget, but nothing else runs
        Task second = gate.WaitAsync(1, () => 0);
        Assert.False(second.IsCompleted);
        gate.Release(500);
        await second;
        await gate.WaitAsync(1, () => 0);
        Task third = gate.WaitAsync(1, () => 0); // two places only
        Assert.False(third.IsCompleted);
        gate.Release(1);
        await third;
    }

    [Fact]
    public void A_build_costs_16_x_16_tiles_at_the_textures_zoom_and_four_times_as_many_per_zoom_finer()
    {
        var zl16 = new TextureName(21904, 35824, "BI", 16);
        Assert.Equal(256, TextureStreamer.BuildTiles(zl16, 16));
        Assert.Equal(1024, TextureStreamer.BuildTiles(zl16, 17)); // Ultra
        Assert.Equal(4096, TextureStreamer.BuildTiles(zl16, 18)); // Extra
        Assert.Equal(4, TextureStreamer.BuildTiles(zl16, 13));    // a far level
        Assert.Equal(1, TextureStreamer.BuildTiles(zl16, 10));    // part of one tile
    }

    [Fact]
    public async Task The_weighted_gate_holds_work_back_when_the_tiles_run_out_though_memory_is_free()
    {
        var gate = new WeightedPriorityGate(budget: 1000, maxEntries: 10, secondBudget: 8192);
        await gate.WaitAsync(75, 4096, () => FetchPriority.Warm);
        await gate.WaitAsync(75, 4096, () => FetchPriority.Warm);      // the tiles are all in use
        Task third = gate.WaitAsync(75, 4096, () => FetchPriority.Warm); // memory has room, the tiles do not
        Assert.False(third.IsCompleted);
        Assert.Equal((150, 8192), (gate.Used, gate.SecondUsed));
        gate.Release(75, 4096);
        await third;
    }

    [Fact]
    public async Task Full_size_builds_take_turns_when_their_tiles_fill_the_budget()
    {
        // Level -2: a full-size ZL16 build downloads 64 tiles at zoom 15. Memory for ten, tiles for one: they go one by one.
        TaskCompletionSource firstHeld = Hold();
        Setup s = Streaming(level: -2, detailedBuilds: 10, buildTiles: 64, hold: tile => ColumnOf(tile) == 35824 ? firstHeld.Task : null);
        Task<bool> first = s.Streamer.Warm(Texture(), 0, FetchPriority.Warm)!;
        await Until(() => Requested(s, 35824, 15));
        Task<bool> second = s.Streamer.Warm(Texture("21904_35840_BI16"), 0, FetchPriority.Warm)!;
        await Until(() => s.Streamer.BuildsWaiting == 1);

        Assert.False(Requested(s, 35840, 15)); // the second waits while the first holds the tiles
        firstHeld.SetResult();
        Assert.True(await first);
        Assert.True(await second); // and starts when the first gives them back
    }

    [Fact]
    public async Task Builds_ahead_of_XPlane_leave_half_the_tiles_free_for_its_reads()
    {
        // Level -3: full-size builds of 16 tiles; tiles for two. Two ahead builds: only one runs, the other waits for the
        // ahead half; a read X-Plane waits for then starts at once beside the running one, without waiting for it.
        // The running ahead build's tiles are held until the read is done, so the read cannot have waited for it.
        TaskCompletionSource aheadHeld = Hold();
        Setup s = Streaming(level: -3, detailedBuilds: 10, buildTiles: 32, hold: tile => ColumnOf(tile) == 35824 ? aheadHeld.Task : null);
        Task<bool> ahead1 = s.Streamer.Warm(Texture(), 0, FetchPriority.Warm)!;
        await Until(() => Requested(s, 35824, 14));
        Task<bool> ahead2 = s.Streamer.Warm(Texture("21904_35840_BI16"), 0, FetchPriority.Warm)!;
        await Until(() => s.Streamer.BuildsWaiting == 1);

        StreamedTexture near = s.Streamer.Get(Texture("21904_35856_BI16"))!;
        var (_, answer) = await Task.Run(() => ReadLevel(s.Streamer, near, 0), TestContext.Current.CancellationToken) // X-Plane's read next to the aircraft
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(Served.Real, answer.Served);
        Assert.False(ahead1.IsCompleted);
        Assert.False(Requested(s, 35840, 14)); // the second ahead build still waits for the ahead half
        aheadHeld.SetResult();
        Assert.True(await ahead1);
        Assert.True(await ahead2);
    }

    // ---------------------------------------------------------------- the rest of the mount

    [Fact]
    public void The_overlay_streams_photo_textures_and_serves_complete_ones_as_files()
    {
        Setup s = Streaming();
        var overlay = new SceneryOverlay(s.Roots, null, lazyTerrainListing: false, streamer: s.Streamer);

        OverlayEntry entry = overlay.Open(@"\textures\" + Stem + ".dds");
        Assert.NotNull(entry.Streamed);
        Assert.Null(entry.Backing);

        ReadLevel(s.Streamer, entry.Streamed, 0); // near: the whole texture is built and cached
        Assert.Equal(s.Roots.CachedFile(Texture()), overlay.Open(@"\textures\" + Stem + ".dds").Backing);

        _tree.WritePngHeader("source/textures/20624_36576_ZL16.png", 1024); // a mask's name is never streamed
        Assert.Throws<NtStatusException>(() => overlay.Open(@"\textures\20624_36576_ZL16.dds"));
    }

    [Fact]
    public void A_package_definition_without_LOAD_CENTER_gets_one()
    {
        _tree.WriteText($"source/terrain/{Stem}.ter", "A\n800\nTERRAIN\n\nBASE_TEX_NOWRAP ../textures/" + Stem + ".dds\nNO_ALPHA\n");
        var roots = new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(-3, TextureSizing.MaxSize));
        string definition = System.Text.Encoding.UTF8.GetString(new TerrainDefinitions(roots, decals: null).Resolve(VirtualPath.Of("terrain", Stem + ".ter"))!);
        Assert.Matches(@"TERRAIN\nLOAD_CENTER 51\.0\d+ 16\.8\d+ 6\d{3} 1024\n\nBASE_TEX_NOWRAP", definition);
        Assert.Contains("BASE_TEX_NOWRAP ../textures/" + Stem + ".dds", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_turn_streaming_on_by_default_and_check_its_waits()
    {
        MountSettings Parse(params string[] extra)
        {
            Directory.CreateDirectory(_tree.PathOf("source"));
            return MountSettings.FromValues(TestSettings.Of(
                [$"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {_tree.PathOf("mount")}", "texture-provider = bing", .. extra]));
        }
        MountSettings defaults = Parse();
        Assert.True(defaults.MipStreaming);
        Assert.Equal((60.0, 6.0, (double?)null, false), (defaults.NearWait, defaults.MaxWait, defaults.PagingK, defaults.ReadLog)); // the read log only on demand
        Assert.False(Parse("mip-streaming = off").MipStreaming);
        Assert.Equal(1700, Parse("paging-k = 1700").PagingK);
        Assert.Null(Parse("paging-k = auto").PagingK);
        Assert.Throws<SettingsException>(() => Parse("max-wait = 99"));
        Assert.Throws<SettingsException>(() => Parse("paging-k = 5"));
    }
}

/// <summary>The paging model, the download queue's order and the low-zoom base.</summary>
public sealed class StreamingPartsTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void The_paging_model_doubles_each_level_from_the_textures_near_edge()
    {
        Assert.Equal(4570, PagingModel.GuessK(2560, 70), 0);
        Assert.Equal(7989, PagingModel.GuessK(3840, 62), 0); // the measured screen: about the default and the measured k (7500-8203)
        Assert.Equal(8000, new PagingModel().K);
        // As measured in flight (k = 7650): a 2048 px, 6159 m ZL16 texture was read at full detail from 25.8 km to its
        // centre, level 1 from 48.4 km, level 2 from 93 km: 22.7, 45.3 and 89.9 km to its near edge, each twice the last.
        var model = new PagingModel(7650);
        Assert.Equal(26.1, model.Threshold(0, 6159, 2048) / 1000, 1);
        Assert.Equal(49.1, model.Threshold(1, 6159, 2048) / 1000, 1);
        Assert.Equal(95.1, model.Threshold(2, 6159, 2048) / 1000, 1);
        Assert.Equal([0, 0, 1, 1, 2, 3], new[] { 5_000.0, 25_800, 26_200, 48_400, 49_200, 96_000 }.Select(d => model.MipFor(d, 6159, 2048)));
        Assert.Equal(0, PagingModel.X(2_000, 6159, 2048)); // inside the texture itself
        Assert.Equal(7650, PagingModel.Estimate(model.Threshold(0, 6159, 2048), 0, 6159, 2048), 6);
        Assert.Equal(7650, PagingModel.Estimate(model.Threshold(3, 6159, 2048), 3, 6159, 2048), 6);
    }

    [Fact]
    public void A_guess_from_XPlanes_screen_never_replaces_a_k_the_user_set()
    {
        var auto = new PagingModel();
        auto.UseScreen(1920, 90);
        Assert.Equal(2400, auto.K, 0);
        var set = new PagingModel(1500);
        set.UseScreen(1920, 90);
        Assert.Equal(1500, set.K);
    }

    [Fact]
    public async Task Downloads_wait_in_priority_order_and_a_raised_one_moves_up()
    {
        var gate = new PriorityGate(1);
        await gate.WaitAsync(() => 0); // the only slot is taken
        var order = new List<string>();
        Task Waiter(string name, FetchTicket ticket) => gate.WaitAsync(ticket).ContinueWith(_ =>
        {
            lock (order) order.Add(name);
            gate.Release();
        }, TaskScheduler.Default);

        var background = new FetchTicket(FetchPriority.Background);
        Task[] waiters =
        [
            Waiter("background", background),
            Waiter("middle read", new FetchTicket(FetchPriority.ForRead(2))),
            Waiter("big middle read", new FetchTicket(FetchPriority.ForRead(1))),
            Waiter("near read", new FetchTicket(FetchPriority.NearRead)),
            Waiter("warm", new FetchTicket(FetchPriority.Warm)),
        ];
        background.Raise(FetchPriority.NearWarm); // X-Plane now waits for what it was fetching
        gate.Release();
        await Task.WhenAll(waiters);
        Assert.Equal(["near read", "background", "big middle read", "middle read", "warm"], order);
    }

    [Fact]
    public async Task The_low_zoom_base_keeps_tiles_up_to_zoom_12_for_later_sessions()
    {
        var server = new FakeTileServer();
        var store = new TileStore(_tree.PathOf("base"));
        using (var tiles = new TileSource("test", FakeTileServer.Template, 4, handler: server) { Store = store })
        {
            await tiles.GetTileAsync(10, 20, 12, false, new TileStats());
            await tiles.GetTileAsync(10, 20, 13, false, new TileStats());
        }
        using var later = new TileSource("test", FakeTileServer.Template, 4, handler: server) { Store = store };
        Assert.NotNull(later.TryGetStored(10, 20, 12));
        Assert.Null(later.TryGetStored(10, 20, 13)); // finer zooms are not kept
        byte[] tile = await later.GetTileAsync(10, 20, 12, false, new TileStats());
        Assert.Equal([40, 90, 160], tile[..3]);
        Assert.Equal(2, server.Requests.Count); // the zoom 12 tile came from disk the second time
    }

    [Fact]
    public void The_read_log_writes_reads_and_the_camera_as_csv()
    {
        string folder = _tree.PathOf("logs");
        string reads, camera;
        using (ReadLog log = ReadLog.Start(folder))
        {
            log.Open("My, package", VirtualPath.Of("textures", "a.dds"), "streamed");
            log.Read(new ReadRecord("My, package", VirtualPath.Of("textures", "a.dds"), 128, 4096, 0, 0, "near", "real", 12, 2500, 6000, 4096));
            log.Camera(new CameraReport(51.1, 16.9, 300, 420, new Motion(60, 90), false, DateTime.UtcNow));
            (reads, camera) = (log.ReadsPath, log.CameraPath);
        }
        string[] lines = File.ReadAllLines(reads);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(",\"My, package\",read,textures/a.dds,128,4096,0,0,near,real,12,2500,6000,4096,", lines[2], StringComparison.Ordinal);
        Assert.Contains(",\"My, package\",open,textures/a.dds,", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",51.100000,16.900000,300.0,420.0,117,90", File.ReadAllLines(camera)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_fit_finds_k_from_where_each_level_was_read()
    {
        var model = new PagingModel(7800);
        var random = new Random(3);
        var samples = new List<(double X, int Mip)>();
        for (int i = 0; i < 2000; i++)
        {
            double distance = 1000 + random.NextDouble() * 190_000, size = 6159;
            samples.Add((PagingModel.X(distance, size, 2048), model.MipFor(distance, size, 2048)));
        }
        PagingFit fit = PagingCalibration.Fit(samples)!;
        Assert.InRange(fit.K, 7400, 8200);
        Assert.Equal(1, fit.Agreement, 3);
        Assert.Null(PagingCalibration.Fit(samples.Take(5).ToList())); // too few reads
    }

    /// <summary>X-Plane's reads of one handle, in order, and the level each one asks for.</summary>
    private static int?[] Wanted(DdsLayout layout, params (long Offset, int Length)[] reads)
    {
        var cursor = new ReadCursor();
        return [.. reads.Select(r =>
        {
            long end = r.Offset + r.Length;
            bool header = ReadPattern.IsHeaderChunk(layout, r.Offset, end);
            int? wanted = ReadPattern.WantedLevel(layout, cursor, Math.Max(r.Offset, Dds.HeaderSize), end, header).Wanted;
            ReadPattern.NoteRead(cursor, end, header);
            return wanted;
        })];
    }

    [Fact]
    public void The_level_XPlane_is_after_follows_its_64_KB_chunks()
    {
        DdsLayout layout = DdsLayout.For(2048);
        long tail = layout.Length - 64312;
        // Full detail (a real sequence): the header chunk, the coarse tail, then level 0 read on from 65536.
        Assert.Equal([null, 3, 0, null, null], Wanted(layout, (0, 65536), (tail, 64312), (65536, 65536), (131072, 98304), (229376, 196608)));
        // Level 1 (a real sequence): the chunk from 2097152 starts with level 0's last 128 bytes, but X-Plane is after
        // level 1 (taking it for full detail cost 256 tiles instead of 16); then the next pieces of that read.
        Assert.Equal([null, 1, null, null], Wanted(layout, (0, 65536), (2097152, 65536), (2162688, 65536), (2228224, 131072)));
        // Level 3 on the loading screen: the tail chunk starts 20 KB inside level 2.
        Assert.Equal([null, 3], Wanted(layout, (0, 65536), (tail, 64312)));
    }

    [Fact]
    public void Full_detail_reads_place_the_aircraft_until_XPlane_sends_its_position()
    {
        var view = new ViewTracker();
        var moves = new List<CameraReport>();
        view.Moved += moves.Add;
        view.NoteSharpRead(51.0, 17.0);
        view.NoteSharpRead(51.2, 17.2);
        Assert.True(view.Current!.Inferred);
        Assert.Equal(51.1, view.Current.Lat, 6);
        Assert.Equal(2, moves.Count); // the first estimate, then one that moved by more than 1.5 km

        view.Update(new CameraReport(51.1, 17.1, 2, null, null, false, DateTime.UtcNow));
        Assert.False(view.Current!.Inferred);
        Assert.Equal(3, moves.Count); // the real position replaces the estimate, however close
        view.NoteSharpRead(52, 18); // ignored while X-Plane sends positions
        Assert.False(view.Current!.Inferred);
    }

    [Fact]
    public void The_estimate_averages_every_full_detail_read_of_the_last_minute()
    {
        // X-Plane reads one scenery tile after another: the first reads all lie to one side of the aircraft.
        var view = new ViewTracker();
        for (int i = 0; i < 40; i++) view.NoteSharpRead(50.8, 17.3);
        for (int i = 0; i < 40; i++) view.NoteSharpRead(51.6, 16.7);
        Assert.Equal(51.2, view.Current!.Lat, 6);
        Assert.Equal(17.0, view.Current.Lon, 6);
    }
}
