using Fsp;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP;

/// <summary>
/// What one mounted package has of its own: the cache overlay, the build scheduler (or with streaming, the
/// streamer and its load-ahead), the catalog of its loaded textures, load-ahead and the WinFsp mount. The
/// downloader and encoder are shared.
/// </summary>
internal sealed class PackageMount : IDisposable
{
    private readonly FileSystemHost _host;
    private bool _mounted;

    public PackageMount(PackagePlan plan, SceneryRoots roots, TextureBuilder? builder, TextureOptions textureOptions,
        bool trackAircraft, bool loadAhead, bool lazyTerrainListing, TerrainDecals decals, TextureStreamer? streamer, ViewTracker view, ReadLog? readLog,
        SeasonClock? seasons, AreaUsage? usage, ColourMatcher? colours, SceneryLoads loads, ushort[]? exposure, NightTextures? night)
    {
        Plan = plan;
        Roots = roots;
        Streamer = streamer;
        Scheduler = builder is null || streamer is not null ? null : new TextureScheduler(roots, builder, textureOptions);
        Catalog = builder is not null && (trackAircraft || loadAhead) ? new TextureCatalog() : null;
        if (streamer is not null)
        {
            // The flight-path prefetch goes through the warmer too, so detailed builds stay limited.
            Warmer = Catalog is null ? null : new MipWarmer(Catalog, streamer, view, textureOptions.DdsBuildWorkers, warmLoadedScenery: loadAhead);
        }
        else
        {
            // Two builds queued per worker keep every worker busy; the rest wait in load-ahead's own priority order.
            LoadAhead = Catalog is null || !loadAhead ? null : new LoadAheadPrefetcher(Catalog, Scheduler!, roots, textureOptions.DdsBuildWorkers * 2);
        }
        Overlay = new SceneryOverlay(roots, Scheduler, lazyTerrainListing, Catalog, streamer, decals, night);
        _host = new FileSystemHost(new SceneryFileSystem(Overlay, "OrthoStreamXP", readLog, plan.Name, seasons, usage, colours, loads, exposure));
    }

    public PackagePlan Plan { get; }
    public SceneryRoots Roots { get; }
    public TextureScheduler? Scheduler { get; }
    public TextureStreamer? Streamer { get; }
    public TextureCatalog? Catalog { get; }
    public LoadAheadPrefetcher? LoadAhead { get; }
    public MipWarmer? Warmer { get; }
    public SceneryOverlay Overlay { get; }

    public void Mount(int fileSystemThreads)
    {
        int status = _host.MountEx(Plan.Mountpoint, (uint)fileSystemThreads, null, false, 0);
        if (status < 0) throw new IOException($"WinFsp could not mount {Plan.Mountpoint} (NTSTATUS 0x{status:X8}); is WinFsp installed?");
        _mounted = true;
    }

    public void Unmount()
    {
        if (!_mounted) return;
        _host.Unmount();
        _mounted = false;
    }

    public void Dispose()
    {
        Unmount();
        _host.Dispose();
        LoadAhead?.Dispose();
        Warmer?.Dispose();
        Scheduler?.Dispose();
        Streamer?.Dispose();
    }
}
