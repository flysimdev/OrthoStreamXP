using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Tests;

/// <summary>The cache by area: measuring, the limit that removes the areas flown longest ago, the map's pictures, and the flight report.</summary>
public sealed class CacheAreasTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private string Cache => _tree.PathOf("cache");

    // ZL16 textures: one near Wrocław (+51+017), one west of Prague (+50+013).
    private const string Wroclaw = "21840_35872_BI16", Prague = "22208_35296_BI16";

    private void File(string relative, int bytes) => _tree.WriteBytes("cache/" + relative, new byte[bytes]);

    [Fact]
    public void Areas_are_named_as_X_Plane_names_its_scenery_tiles()
    {
        Assert.Equal("+51+017", CacheAreas.AreaOf(Wroclaw + ".dds").ToString());
        Assert.Equal("+50+013", CacheAreas.AreaOf(Prague + ".mip2").ToString());
        Assert.Null(CacheAreas.AreaOf("100000_148176_BI18_sea_overlay.ter"));
        Assert.True(AreaName.TryParse("-33-071", out AreaName south));
        Assert.Equal(new AreaName(-33, -71), south);
    }

    [Fact]
    public void Every_file_of_an_area_counts_in_every_quality_but_the_shared_folders_do_not()
    {
        File($"level0_4096-4096/XPME_Europe/textures/{Wroclaw}.dds", 1000);
        File($"level0_4096-4096/XPME_Europe/.xpstream/mips/{Wroclaw}.mip2", 100);
        File($"level+1_4096-4096/XPME_Europe/terrain/{Wroclaw}.ter", 10);
        File($"level0_4096-4096/XPME_Europe/textures/{Prague}.dds", 500);
        File(".orthostreamxp-base/12/2200/1400.jpg", 9999);
        File(".orthostreamxp-logs/orthostreamxp.log", 9999);

        IReadOnlyList<CacheArea> areas = CacheAreas.Scan(Cache, null, TestContext.Current.CancellationToken);

        Assert.Equal(["+50+013", "+51+017"], areas.Select(a => a.Name.ToString()));
        CacheArea wroclaw = areas[1];
        Assert.Equal(1110, wroclaw.Bytes);
        Assert.Equal(new Dictionary<string, long> { ["level0_4096-4096"] = 1100, ["level+1_4096-4096"] = 10 }, wroclaw.BytesBySetup);
    }

    [Fact]
    public void Cache_names_are_read_with_their_quality_folders_level()
    {
        // Wroclaw's ZL16 texture: downloaded at zoom 16 at level -1, at zoom 17 at level +1.
        File("level-1_2048-2048/P/textures/21840_35872_PX16.dds", 1000);
        File("level+1_4096-4096/P/.xpstream/mips/43680_71744_PX17.mip2", 100);

        CacheArea wroclaw = Assert.Single(CacheAreas.Scan(Cache, null, TestContext.Current.CancellationToken));
        Assert.Equal("+51+017", wroclaw.Name.ToString());
        Assert.Equal(1100, wroclaw.Bytes);
        Assert.Equal(1100, CacheAreas.Delete(Cache, new HashSet<AreaName> { wroclaw.Name }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Over_the_limit_the_areas_flown_longest_ago_go_first_down_to_90_percent()
    {
        File($"level0_4096-4096/P/textures/{Wroclaw}.dds", 600);
        File($"level0_4096-4096/P/textures/{Prague}.dds", 600);
        AreaUsage usage = AreaUsage.Load(Cache);
        usage.Note(new AreaName(50, 13));
        Thread.Sleep(20);
        usage.Note(new AreaName(51, 17)); // flown last

        var (removed, freed) = CacheAreas.Trim(Cache, 1000, usage, TestContext.Current.CancellationToken);

        Assert.Equal([new AreaName(50, 13)], removed);
        Assert.Equal(600, freed);
        Assert.Single(CacheAreas.Scan(Cache, usage, TestContext.Current.CancellationToken));
        Assert.Equal(([], 0L), CacheAreas.Trim(Cache, 1000, usage, TestContext.Current.CancellationToken)); // within it now
        Assert.Null(AreaUsage.Load(Cache).LastUsed(new AreaName(50, 13)));                                   // forgotten on disk too
    }

    [Fact]
    public void An_area_only_prefetched_goes_before_an_area_flown_even_when_its_files_are_newer()
    {
        // R5: an area was prefetched (files written just now) but never flown; Wrocław was flown earlier.
        File($"level0_4096-4096/P/textures/{Wroclaw}.dds", 600);
        AreaUsage usage = AreaUsage.Load(Cache);
        usage.Note(new AreaName(51, 17));
        Thread.Sleep(20);
        File($"level0_4096-4096/P/textures/{Prague}.dds", 600); // the pre-warm writes it after the flight's last use

        var (removed, _) = CacheAreas.Trim(Cache, 1000, usage, TestContext.Current.CancellationToken);

        Assert.Equal([new AreaName(50, 13)], removed);
    }

    [Fact]
    public void An_area_prefetched_and_flown_later_is_trimmed_by_its_age_like_any_other()
    {
        // R5: a pre-warmed area counts as never flown only until X-Plane opens it; then the oldest use goes first again.
        File($"level0_4096-4096/P/textures/{Prague}.dds", 600); // pre-warmed first...
        AreaUsage usage = AreaUsage.Load(Cache);
        usage.Note(new AreaName(51, 17)); // ...Wrocław flown...
        Thread.Sleep(20);
        File($"level0_4096-4096/P/textures/{Wroclaw}.dds", 600);
        Thread.Sleep(20);
        usage.Note(new AreaName(50, 13)); // ...and then Prague flown too, last

        var (removed, _) = CacheAreas.Trim(Cache, 1000, usage, TestContext.Current.CancellationToken);

        Assert.Equal([new AreaName(51, 17)], removed); // the area flown longest ago, not the one first only prefetched
    }

    [Fact]
    public void An_area_is_pictured_from_its_textures_smallest_levels()
    {
        byte[] rgb = new byte[64 * 64 * 3];
        for (int i = 0; i < rgb.Length; i += 3) (rgb[i], rgb[i + 1], rgb[i + 2]) = (40, 120, 60);
        _tree.WriteBytes($"cache/level0_4096-4096/P/textures/{Wroclaw}.dds", Dds.Encode(rgb, 64, 64, 1, out _, out _));

        byte[] picture = CacheAreas.Picture(Cache, new AreaName(51, 17), 48, TestContext.Current.CancellationToken)!;
        Assert.True(TextureName.TryParseStem(Wroclaw, out TextureName name));
        var (lat, lon) = name.Center;
        int x = (int)((lon - 17) * 48), y = (int)((52 - lat) * 48);

        Assert.InRange(picture[(y * 48 + x) * 4 + 1], 110, 130); // the texture's green, where it lies (BGRA)
        Assert.Equal(0, picture[3]);                               // elsewhere: see-through
        Assert.Null(CacheAreas.Picture(Cache, new AreaName(50, 13), 48, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void An_area_without_finished_textures_is_pictured_from_the_low_zoom_base()
    {
        using var tile = new SkiaSharp.SKBitmap(256, 256);
        tile.Erase(new SkiaSharp.SKColor(200, 60, 40));
        using SkiaSharp.SKData png = tile.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var store = new TileStore(Path.Join(Cache, TileStore.FolderName));
        var (x, y) = Tiles.LatLonToTile(51.5, 17.5, store.MaxZoom);
        store.Save(x, y, store.MaxZoom, png.ToArray());

        byte[] picture = CacheAreas.Picture(Cache, new AreaName(51, 17), 48, TestContext.Current.CancellationToken)!;

        Assert.Equal((40, 60, 200, 255), (picture[(24 * 48 + 24) * 4], picture[(24 * 48 + 24) * 4 + 1], picture[(24 * 48 + 24) * 4 + 2], picture[(24 * 48 + 24) * 4 + 3])); // the tile at the middle, BGRA
        Assert.Equal(0, picture[3]); // the corner's tile is not on this PC: see-through
    }

    [Fact]
    public void A_flight_report_sums_up_the_route_and_keeps_the_earlier_ones()
    {
        ReportSample[] samples =
        [
            new(0, 51.10, 16.88, 0, 0, 0, 0, 0),
            new(60, 51.20, 16.88, 120, 1000, 30_000_000, 40, 0),
            new(120, 51.30, 16.88, 120, 2000, 60_000_000, 80, 5),
            new(180, 48.00, 11.00, 0, 2000, 60_000_000, 80, 5), // moved to another place: not flown
        ];
        var report = new FlightReport(DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow, "level0_4096-4096", samples);

        Assert.InRange(report.DistanceKm, 21, 23);
        Assert.Equal((2000L, 80L, 5L, 60_000_000L), (report.Tiles, report.Builds, report.StandIns, report.Bytes));
        Assert.InRange(report.PeakMbits, 3.9, 4.1); // 30 MB a minute

        string path = FlightReport.PathFor(Cache);
        report.Save(path);
        report.Save(path);
        Assert.Equal(samples, FlightReport.Load(path)!.Samples);
        Assert.Single(Directory.GetFiles(Path.Join(Path.GetDirectoryName(path)!, "flights")));
    }

    private static FlightReport Flight(long reads, long standIns, double standInsAt) =>
        new(DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow, "level0_4096-4096",
        [
            new(0, 51.1, 16.9, 0, 0, 0, 0, 0, 0),
            new(standInsAt, 51.2, 16.9, 120, 100, 1_000_000, 10, standIns, reads / 2),
            new(1200, 51.3, 16.9, 120, 200, 2_000_000, 20, standIns, reads),
        ]);

    [Theory]
    [InlineData(10_000, 0, "Good", "Smooth flight", "sharp all the way")]
    [InlineData(10_000, 50, "Good", "Smooth flight", "at the start")]
    [InlineData(10_000, 300, "Fair", "A few blurry moments", "at the start")]
    [InlineData(10_000, 900, "Poor", "Often blurry", "lower quality")]
    public void The_verdict_follows_the_share_of_reads_that_got_the_real_picture(long reads, long standIns, string rating, string title, string says)
    {
        ReportVerdict verdict = ReportSummary.Verdict(Flight(reads, standIns, standInsAt: 60));
        Assert.Equal((rating, title), (verdict.Rating.ToString(), verdict.Title));
        Assert.Contains(says, verdict.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Blurry_moments_in_flight_are_told_apart_from_those_while_the_area_loaded()
    {
        Assert.Contains("in flight", ReportSummary.Verdict(Flight(10_000, 300, standInsAt: 900)).Text, StringComparison.Ordinal);
        ReportStat sharp = ReportSummary.Stats(Flight(10_000, 300, standInsAt: 900))[1];
        Assert.Equal(("Sharp scenery", "97 %", Rating.Fair), (sharp.Title, sharp.Value, sharp.Rating));
        Assert.Equal("99.9 %", ReportSummary.Stats(Flight(10_000, 6, standInsAt: 60))[1].Value); // never rounded up to 100 %
    }

    [Fact]
    public void The_cache_limit_is_a_size_in_GB()
    {
        SettingValues args = TestSettings.Of("cache-limit = 100");
        Assert.Equal(100.0, args.Double("cache-limit", 0));
        Assert.Throws<SettingsException>(() => MountSettings.FromValues(TestSettings.Of($"cache = {Cache}", "cache-limit = -1")));
    }
}
