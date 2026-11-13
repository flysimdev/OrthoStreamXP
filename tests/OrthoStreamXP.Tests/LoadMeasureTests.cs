using OrthoStreamXP.Scenery;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Tests;

/// <summary>What the log says about a flight's busiest minutes: the CPU used, and the scenery tiles X-Plane loads.</summary>
public sealed class LoadMeasureTests
{
    [Theory]
    [InlineData("Earth nav data/+40+010/+47+010.dsf", 47, 10)]
    [InlineData("Earth nav data/-40-080/-33-071.DSF", -33, -71)]
    [InlineData("Earth nav data/+00+000/+00-001.dsf", 0, -1)]
    public void A_dsf_path_names_its_tile(string path, int lat, int lon)
    {
        Assert.True(SceneryTile.TryParse(VirtualPath.Parse(path), out SceneryTile tile));
        Assert.Equal(new SceneryTile(lat, lon), tile);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), tile.ToString(), ignoreCase: true);
    }

    [Theory]
    [InlineData("Earth nav data/+40+010/+47+010.txt")]
    [InlineData("terrain/+47+010.dsf")]
    [InlineData("Earth nav data/+40+010/47+010.dsf")]
    [InlineData("Earth nav data/+40+010/+97+010.dsf")]
    public void Other_files_are_no_tile(string path) => Assert.False(SceneryTile.TryParse(VirtualPath.Parse(path), out _));

    [Fact]
    public void A_tile_is_logged_once_however_often_it_is_opened()
    {
        var lines = new List<string>();
        Action<string> sink = line => { lock (lines) lines.Add(line); };
        Log.Sink += sink;
        try
        {
            Log.Configure(LogOutput.None, null);
            var loads = new SceneryLoads(new ViewTracker());
            loads.Opened(VirtualPath.Parse("Earth nav data/+40+010/+47+010.dsf"), "Europe");
            loads.Opened(VirtualPath.Parse("Earth nav data/+40+010/+47+010.dsf"), "Europe");
            loads.Opened(VirtualPath.Parse("Earth nav data/+40+010/+47+010.dsf"), "Other");
            loads.Opened(VirtualPath.Parse("Earth nav data/+40+010/+46+010.dsf"), "Europe");
            loads.Opened(VirtualPath.Parse("textures/21904_35824_BI16.dds"), "Europe");
            Assert.True(Log.Flush(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Log.Sink -= sink;
            Log.Configure(LogOutput.Console, null);
        }
        lock (lines)
        {
            string[] loaded = [.. lines.Where(l => l.Contains("X-Plane loads the scenery tile", StringComparison.Ordinal))]; // not the lines of tests running beside it
            Assert.Equal(2, loaded.Length);
            Assert.Contains("+47+010 of Europe; no position from X-Plane yet", loaded[0], StringComparison.Ordinal);
            Assert.Contains("+46+010", loaded[1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_camera_is_described_with_its_motion_and_age()
    {
        DateTime now = DateTime.UtcNow;
        var moving = new CameraReport(47.2481, 12.3349, 450, null, new Motion(57.6, 265), false, now);
        Assert.Equal("camera at 47.248, 12.335, 450 m above the ground, track 265° at 112 kt", SceneryLoads.Where(moving, now));
        var stale = moving with { Motion = null, ReceivedUtc = now - TimeSpan.FromSeconds(40) };
        Assert.EndsWith("not moving (40 s ago)", SceneryLoads.Where(stale, now), StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_busy_share_is_what_was_not_idle()
    {
        Assert.Equal(0.75, CpuMeter.Busy((1000, 10_000), (1250, 11_000))!.Value, 6);
        Assert.Equal(0, CpuMeter.Busy((0, 0), (500, 500))!.Value, 6);
        Assert.Null(CpuMeter.Busy((0, 100), (0, 100)));
    }

    [Fact]
    public void A_process_share_counts_every_processor()
    {
        Assert.Equal(0.25, CpuMeter.Share(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), 16)!.Value, 6);
        Assert.Equal(1, CpuMeter.Share(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(5), 16)!.Value, 6);
        Assert.Null(CpuMeter.Share(TimeSpan.FromSeconds(1), TimeSpan.Zero, 16));
    }

    [Fact]
    public void The_summary_reports_average_and_busiest_sample()
    {
        var meter = new CpuMeter();
        Assert.Contains("CPU not measured", meter.Summary(), StringComparison.Ordinal);
        meter.Sample();
        string summary = meter.Summary();
        Assert.Contains("average", summary, StringComparison.Ordinal);
        Assert.Contains("full garbage collection", summary, StringComparison.Ordinal);
    }
}
