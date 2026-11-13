using Fsp;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP;

/// <summary>
/// A running mount: every package of <see cref="MountSettings"/> mounted with its cache overlay, build
/// scheduler and load-ahead, the shared downloader and encoder, and the aircraft position listener.
/// </summary>
internal sealed class MountSession : IDisposable
{
    private readonly MountSettings _settings;
    private readonly List<PackageMount> _mounts = [];
    private readonly CancellationTokenSource _summaries = new();
    private readonly ViewTracker _view = new();
    /// <summary>Where the sun is in X-Plane: offered by the position listener, read by the streaming to skip the photos at night.</summary>
    private readonly Daylight _daylight;
    private readonly FlightRecorder _recorder = new();
    private readonly CpuMeter _cpu = new();
    private readonly SceneryLoads _loads;
    private readonly List<ReportSample> _timeline = [];
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private AreaUsage? _usage;
    private GraphicsMemoryMeter? _graphics;
    private TileSource? _tiles;
    private AircraftPositionListener? _listener;
    private ReadLog? _readLog;
    private SeasonClock? _seasons;
    private ColourMatcher? _colours;
    /// <summary>The night textures' painting, shared by every package.</summary>
    private NightPainting? _nightPainting;
    private Task? _summaryLoop, _sampleLoop;
    private HttpClient? _simBrief;
    /// <summary>The SimBrief side (plan, departure prefetch, ground pre-warm, reload); told every position X-Plane sends.</summary>
    private volatile SimBriefSession? _simBriefSession;
    private bool _stopped;

    private MountSession(MountSettings settings)
    {
        _settings = settings;
        _loads = new SceneryLoads(_view);
        _daylight = new Daylight(-settings.SkipPhotosSunBelow);
    }

    public MountSettings Settings => _settings;

    /// <summary>The flight tuning's review, once the session has stopped.</summary>
    public FlightReview? Review { get; private set; }

    /// <summary>The flight report, once the session has stopped (null when X-Plane never sent a position).</summary>
    public FlightReport? Report { get; private set; }

    /// <summary>
    /// Mounts every package and starts building. On failure everything already mounted is unmounted again
    /// before the exception is passed on.
    /// </summary>
    public static MountSession Start(MountSettings settings)
    {
        var session = new MountSession(settings);
        try
        {
            session.StartAll();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void StartAll()
    {
        MountSettings s = _settings;
        foreach (string folder in s.NotPackages) Log.Info($"Skipping {folder}: not a scenery package (it has no Earth nav data folder)");
        Log.Info($"Textures at level-change {s.Textures.LevelChange}: {s.Textures.Describe()}; cached in {s.PlanCache}");
        if (s.IgnoredTextureQuality is { } quality)
            Log.Info($"texture-quality = {quality} in the settings is no longer used: the texture size follows the level ({s.Textures.SizeLimit} px at level {s.Textures.LevelChange})");
        Directory.CreateDirectory(s.PlanCache);

        // Builds await downloads and run CPU work on the pool; keep enough threads ready for bursts.
        ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorkers, 64), Math.Max(minIo, 64));

        if (AtomicFile.DeleteStalePartials(s.PlanCache) is > 0 and var stale)
            Log.Info($"Removed {stale} unfinished file(s) left in the cache by an interrupted run");
        _usage = AreaUsage.Load(s.Cache);
        // Before the mount: no file of the cache is open yet.
        TrimCache();

        bool streaming = s.MipStreaming && s.Providers is not null;
        // The low-zoom base far scenery is drawn from: shared by every setup and package, kept when a setup is deleted.
        TileStore? store = streaming && s.Providers![0].Name != ImageryProvider.Synthetic ? new TileStore(Path.Join(s.Cache, TileStore.FolderName)) : null;
        _tiles = s.Providers is null ? null : new TileSource(s.Providers, s.DownloadWorkers, s.DownloadTimeout, s.DownloadAttempts) { Store = store };
        // One downloader and one encoder for all packages: the tile downloads and the CPU limits are shared.
        TextureBuilder? builder = _tiles is null ? null : new TextureBuilder(_tiles, s.EncoderWorkers);
        var textureOptions = new TextureOptions(_tiles?.Provider ?? "", s.Textures, s.DdsBuildWorkers, s.Background, s.ProvisionalLod, s.ProvisionalWait);
        var model = new PagingModel(s.PagingK);
        // Builds run within two budgets, each counted at its own size: --build-memory for the pictures they hold while their
        // tiles arrive (75 MB at 4096 px; the small levels far scenery needs almost nothing), and --build-tiles for what
        // they download, so finer imagery (4096 tiles for a ZL16 texture at zoom 18) does not fill the connection.
        long buildMemory = (long)s.BuildMemoryMb << 20;
        int largest = Math.Max(s.Textures.SizeFor(16), s.Textures.SizeFor(18));
        long fullTiles = TextureStreamer.BuildTiles(new TextureName(0, 0, "BI", 16), s.Textures.FetchZoom(16));
        long fullAtOnce = Math.Max(1, Math.Min(buildMemory / TextureStreamer.BuildWeight(largest), s.BuildTiles / fullTiles));
        long aheadAtOnce = Math.Max(1, Math.Min(fullAtOnce, TextureStreamer.AheadTiles(s.BuildTiles) / fullTiles));
        var streamingOptions = new StreamingOptions(s.NearWait, s.MaxWait, BuildMemoryBytes: buildMemory, BuildTiles: s.BuildTiles);

        if (_tiles is not null)
            Log.Info($"Texture generation: provider={_tiles.Provider}{(s.Providers!.Count > 1 ? " (tried in this order)" : "")}, DDS build workers={s.DdsBuildWorkers}{(s.Packages.Count > 1 ? " per package" : "")}, " +
                     $"download workers={_tiles.DownloadWorkers} (timeout {s.DownloadTimeout:0.#}s x {s.DownloadAttempts} attempts), encoder CPU workers={Math.Clamp(s.EncoderWorkers, 1, 16)}, " +
                     (streaming ? "mip streaming on" : $"mip streaming off, background={s.Background}"));
        if (streaming)
            Log.Info($"Mip streaming: X-Plane's reads of near and middle scenery wait up to {s.NearWait:0.#} s while a flight starts, up to {s.MaxWait:0.#} s in flight " +
                     $"before a stand-in; builds within {s.BuildMemoryMb} MB and {s.BuildTiles} tiles: {fullAtOnce} full-size ZL16 ({largest} px, {fullTiles} tiles each), {aheadAtOnce} of them ahead of X-Plane, or up to {TextureStreamer.DefaultMaxBuilds} smaller at once; paging k={(s.PagingK is { } k ? $"{k:F0}" : $"auto (until X-Plane sends its screen: {PagingModel.DefaultK:F0})")}; " +
                     (store is null ? "no low-zoom base (synthetic imagery)" : $"low-zoom base (zoom {store.MaxZoom} and coarser) in {store.Root}"));
        if (s.ReadLog) _readLog = ReadLog.Start(AppPaths.Logs);
        if (s.Seasons.On && s.Providers is not null)
        {
            _seasons = new SeasonClock(s.Seasons, Path.Join(AppPaths.History, SeasonClock.DayFileName));
            Log.Step($"Seasons: on at {s.Seasons.Strength:P0}, " +
                     (s.Seasons.Choice == SeasonChoice.Auto ? $"by X-Plane's date (until it sends one: {SeasonClock.Describe(_seasons.Day)})" : $"set to {s.Seasons.Choice} everywhere"));
        }
        if (s.MatchColours && store is not null && builder is not null)
        {
            _colours = new ColourMatcher(name => builder.TryStoredRegion(name, store.MaxZoom), builder.ResizeTo,
                Path.Join(AppPaths.History, ColourMatcher.FileName), s.MatchStrength);
            Log.Step($"Colour matching: on at {s.MatchStrength:P0}; photos from different captures are evened out against the low-zoom base");
        }
        else if (s.MatchColours)
        {
            Log.Warning("Colour matching needs mip streaming and an imagery provider with a low-zoom base; it is off for this run");
        }
        ushort[]? exposure = Exposure.TableFor(s.Exposure);
        if (exposure is not null) Log.Step($"Exposure: {s.Exposure:0} %; the photos are darkened as X-Plane reads them");
        if (s.TextureStore is not null) Log.Info($"Lazy local DDS store: {s.TextureStore}");
        if (s.LazyTerrainListing)
            Log.Info("Terrain directory listing is lazy; DSF-referenced definitions remain available by direct path");
        // X-Plane's own folder holds the decal libraries; with a plain mountpoint there is none.
        var decals = new TerrainDecals(Path.GetDirectoryName(s.CustomScenery), s.GroundDetail);
        Log.Info(s.GroundDetail switch
        {
            0 => "Ground detail: 0 %; X-Plane's grass and dirt decals are left out of the photo terrain (the packages are not changed)",
            TerrainDecals.Full => "Ground detail: 100 %; X-Plane draws its grass and dirt decals over the photos as the packages define",
            _ => $"Ground detail: {s.GroundDetail} %; X-Plane's grass and dirt decals are faded over the photos (the packages are not changed)",
        });
        NightPainting? nightPainting = s.NightLights ? StartNightLights(s) : null;
        Daylight? nightPause = s.SkipPhotosAtNight ? SkipPhotosAtNight(s, streaming) : null;

        foreach (PackagePlan package in s.Packages)
        {
            var roots = new SceneryRoots(package.Source, package.Cache, s.TextureStore, s.Textures);
            TerrainDefinitions.RemoveCachedCopies(package.Cache);
            if (NightTextures.RemoveOldFiles(package.Cache) is > 0 and var oldNights)
                Log.Info($"Removed {oldNights} night textures an earlier version kept whole; they are painted again, keeping only what X-Plane reads");
            roots.RenameToCacheNames();
            _mounts.Add(new PackageMount(package, roots, builder, textureOptions, s.TrackAircraft, s.LoadAhead, s.LazyTerrainListing, decals,
                streaming ? new TextureStreamer(roots, builder!, _view, model, streamingOptions, nightPause) : null, _view, _readLog, _seasons, _usage, _colours, _loads, exposure,
                nightPainting is null ? null : new NightTextures(package.Cache, s.Textures.SizeFor, nightPainting)));
            Log.Info($"Package {package.Name}: read-only from {package.Source}, cache overlay {package.Cache}, mounted at {package.Mountpoint}");
        }
        PrefetchTarget[] targets = [.. _mounts.Where(m => m.Catalog is not null)
            .Select(m => new PrefetchTarget(m.Catalog!, m.Warmer is { } warmer ? warmer.Prefetch : m.Scheduler!.Prefetch))];
        if (s.TrackAircraft && targets.Length > 0)
        {
            Action<double, double>? onPosition = null;
            foreach (PackageMount mount in _mounts)
                if (mount.LoadAhead is { } prefetcher) onPosition += prefetcher.OnAircraftPosition;
            SeasonClock? seasons = _seasons;
            ReadLog? readLog = _readLog;
            _listener = new AircraftPositionListener(targets, s.Position, onPosition,
                camera =>
                {
                    _view.Update(camera);
                    readLog?.Camera(camera);
                    _simBriefSession?.OnCamera(camera);
                },
                streaming ? model.UseScreen : null,
                seasons is null ? null : day => seasons.OfferDate(day, _view.Starting), _daylight);
        }

        if (s.CustomScenery is not null && s.UpdateSceneryPacks)
            Log.Info(SceneryPacks.MoveToEnd(s.CustomScenery, [.. s.Packages.Select(p => p.Name)])
                ? $"Moved {string.Join(", ", s.Packages.Select(p => p.Name))} to the end of {Path.Join(s.CustomScenery, SceneryPacks.FileName)} (previous file kept as {SceneryPacks.FileName}.bak)"
                : $"{SceneryPacks.FileName} already lists {string.Join(", ", s.Packages.Select(p => p.Name))} at the end");

        if (_tiles is not null) _ = Task.Run(_tiles.WarmUpAsync);
        foreach (PackageMount mount in _mounts) mount.Mount(s.FileSystemThreads);
        Log.Step($"OrthoStreamXP is running: {string.Join(", ", _mounts.Select(m => m.Plan.Name))} shown to X-Plane at {QualityName(s)}");
        Log.Info($"Mounted {(_mounts.Count == 1 ? "1 package" : $"{_mounts.Count} packages")} ({s.FileSystemThreads} file-system threads each).");
        foreach (PackageMount mount in _mounts)
        {
            mount.LoadAhead?.Start();
            mount.Warmer?.Start();
        }
        _listener?.Start();
        StartDeparturePrefetch();
        _summaryLoop = LogSummariesAsync(_summaries.Token);
        _sampleLoop = SampleAsync(_summaries.Token);
    }

    /// <summary>
    /// With a SimBrief user set: builds the textures around the latest flight plan's departure airport while X-Plane
    /// is still starting, skipping the ones in the cache. Runs in the background; a failure is only logged.
    /// </summary>
    private void StartDeparturePrefetch()
    {
        if (_settings.SimBriefUser is not { } user) return;
        DepartureTarget[] targets = [.. _mounts
            .Select(m => (Mount: m, Prefetch: m.Warmer is { } warmer ? warmer.Prefetch : m.Scheduler is { } scheduler ? scheduler.Prefetch : (Func<IReadOnlyList<VirtualPath>, int>?)null))
            .Where(m => m.Prefetch is not null)
            .Select(m => new DepartureTarget(m.Mount.Plan.Name, m.Mount.Plan.Source, path => m.Mount.Roots.CachedFile(path) is not null, m.Prefetch!,
                m.Mount.Warmer is { } warmer ? (textures, lat, lon) => warmer.PrefetchAround(textures, lat, lon) : null))];
        if (targets.Length == 0)
        {
            Log.Info("SimBrief: departure prefetch needs an imagery provider and load-ahead (or aircraft tracking); it is off for this run");
            return;
        }
        if (_mounts.All(m => m.Warmer is null))
            Log.Info("SimBrief: mip streaming is off, so only the departure is built (whole textures, as many as the background queue takes); " +
                     "not counted against the download limit and not dropped on the takeoff roll");
        _simBrief = new HttpClient();
        var client = new SimBriefClient(_simBrief);
        AircraftPositionListener? listener = _listener;
        PrefetchBudget? budget = null;
        if (_settings.SimBriefDownloadLimitMb > 0)
        {
            budget = new PrefetchBudget(_settings.SimBriefDownloadLimitMb * 1024L * 1024);
            foreach (PackageMount mount in _mounts) mount.Warmer?.UseBudget(budget);
        }
        // The ground pre-warm needs X-Plane's positions.
        GroundTarget[] ground = listener is null ? [] : [.. _mounts.Where(m => m.Warmer is not null)
            .Select(m => (Mount: m, Warmer: m.Warmer!))
            .Select(m => new GroundTarget(m.Mount.Plan.Name, m.Mount.Plan.Source, m.Warmer.PrefetchAround, m.Warmer.PrefetchGround, m.Warmer.CancelGround,
                m.Warmer.CancelDeparture, m.Warmer.RunningSummary))];
        if (ground.Length > 0) PrewarmMemory.Watch([.. _mounts.Select(m => m.Warmer).OfType<MipWarmer>()], _summaries.Token);
        var simBrief = new SimBriefSession(ct => client.FetchAsync(user, ct), targets, ground,
            () => listener?.Latest is { } at ? (at.Lat, at.Lon) : null, _settings.SimBriefRadiusKm,
            alternate: _settings.SimBriefAlternate, stop: _summaries.Token, budget: budget);
        _simBriefSession = simBrief;
        simBrief.Start();
    }

    /// <summary>Whether this session uses SimBrief (a SimBrief user is set and the prefetch is on).</summary>
    public bool UsesSimBrief => _simBriefSession is not null;

    /// <summary>The Home page's "Reload SimBrief flight plan": what came of it, in the window's words; null without SimBrief.</summary>
    public Task<string>? ReloadFlightPlanAsync() => _stopped ? null : _simBriefSession?.ReloadAsync();

    /// <summary>The flight plan fetched as streaming started, in the window's words; null without SimBrief.</summary>
    public Task<string>? FirstFlightPlanAsync() => _stopped ? null : _simBriefSession?.DescribeFirstAsync();

    /// <summary>The live numbers of every package together.</summary>
    public MountStatus Status()
    {
        long Sum(RequestOutcome outcome) => _mounts.Sum(m => m.Overlay.RequestStats.Count(outcome));
        return new MountStatus
        {
            Packages = _settings.Packages,
            Requested = _mounts.Sum(m => m.Overlay.RequestStats.Total),
            ReadyByPrefetch = Sum(RequestOutcome.ReadyByPrefetch),
            ReadyInCache = Sum(RequestOutcome.ReadyInCache),
            WaitedForPrefetch = Sum(RequestOutcome.WaitedForPrefetch),
            BuiltOnRequest = Sum(RequestOutcome.BuiltOnRequest),
            Provisional = Sum(RequestOutcome.Provisional),
            Unavailable = Sum(RequestOutcome.Unavailable),
            StreamedReads = _mounts.Sum(m => m.Streamer?.Reads ?? 0),
            RealReads = _mounts.Sum(m => m.Streamer?.RealReads ?? 0),
            Building = _mounts.Sum(m => m.Scheduler?.PendingCount ?? m.Streamer?.Building ?? 0),
            TilesDownloaded = _tiles?.TilesDownloaded ?? 0,
            BytesDownloaded = _tiles?.BytesDownloaded ?? 0,
            FailedTiles = _tiles?.FailedTiles ?? 0,
            Aircraft = _listener?.Latest,
        };
    }

    /// <summary>
    /// Unmounts, logs each package's summary and waits for the builds still running.
    /// </summary>
    /// <param name="hurry">Windows is about to end the process (window closed, shutdown): wait at most 2 s for builds.</param>
    public async Task StopAsync(bool hurry = false) => await StopAsync(hurry, summaries: true).ConfigureAwait(false);

    private async Task StopAsync(bool hurry, bool summaries)
    {
        if (_stopped) return;
        _stopped = true;
        await _summaries.CancelAsync().ConfigureAwait(false);
        foreach (PackageMount mount in _mounts) mount.Unmount();
        _listener?.Stop();
        foreach (PackageMount mount in _mounts)
        {
            mount.LoadAhead?.Stop();
            mount.Warmer?.Stop();
        }
        if (summaries)
        {
            foreach (PackageMount mount in _mounts)
            {
                string label = Label(mount);
                var (terrainCount, textureCount) = mount.Overlay.RequestCounts();
                Log.Info($"{label}X-Plane request summary: {terrainCount} unique .ter definitions, {textureCount} unique DDS textures");
                if (mount.Overlay.PrefetchSummary() is { } summary) Log.Info(label + summary);
                if (mount.Overlay.Night is { } night && night.Counts is var (painted, dark, time, kept) && painted + dark > 0)
                    Log.Info($"{label}Night lights: {painted} paintings in {time.TotalSeconds:F1} s ({kept / 1048576.0:F0} MB written to the cache, compressed), {dark} places without lights");
                if (mount.Warmer is { } warmer)
                    foreach (PrefetchMeter phase in warmer.Phases)
                        if (phase.Summary() is { } line) Log.Info(label + line);
            }
            Log.Info(_cpu.Summary());
            ReviewFlight();
            ReportFlight();
        }
        _usage?.Save();
        _colours?.Save();
        // The summaries go to disk before the wait for running builds: a closing window gives only a few seconds.
        Log.Flush(TimeSpan.FromSeconds(1));
        Task[] shutdowns = [.. _mounts.Select(m => m.Scheduler?.ShutdownAsync() ?? m.Streamer?.ShutdownAsync()).OfType<Task>()];
        if (shutdowns.Length > 0)
        {
            Task finished = Task.WhenAll(shutdowns);
            if (hurry && await Task.WhenAny(finished, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != finished)
                Log.Info("Closing: textures still being built are left unfinished (cleaned up at the next start)");
            else
                await finished.ConfigureAwait(false);
        }
        // SimBrief's departure prefetch, reload and ground pre-warm queue into the packages' load-ahead: they end before
        // the packages are disposed. A failure there is a bug, but only logged: the listener and the packages must still go.
        foreach (Task? loop in new[] { _summaryLoop, _sampleLoop })
            await EndAsync(loop, "").ConfigureAwait(false);
        await EndAsync(_simBriefSession?.WhenDone(), "SimBrief prefetch").ConfigureAwait(false);
        _listener?.Dispose();
        foreach (PackageMount mount in _mounts) mount.Dispose();
        _nightPainting?.Dispose();
        _tiles?.Dispose();
        _simBrief?.Dispose();
        _readLog?.Dispose();
        _summaries.Dispose();
        // Every file is closed now: the cache can be kept within its limit.
        if (summaries && !hurry) TrimCache();
    }

    /// <summary>The night lights' painting: X-Plane's roads from the overlay packages, and how many paintings run at once.</summary>
    private NightPainting StartNightLights(MountSettings s)
    {
        var roads = new RoadNetworks(RoadNetworks.FindOverlayPackages(s.Packages, s.CustomScenery));
        if (roads.Packages.Count > 0)
            Log.Step($"Night lights: on; towns are lit along X-Plane's roads (from {string.Join(", ", roads.Packages.Select(Path.GetFileName))})");
        else
            Log.Warning("Night lights: on, but no overlay package with X-Plane's roads was found (install a region on the Packages page); the photo tiles stay dark at night");
        return _nightPainting = new NightPainting(new NightPainter(roads), Math.Clamp(Environment.ProcessorCount / 4, 1, 4));
    }

    /// <summary>
    /// The sun the streaming follows to skip the photos at night, or null when it cannot: the photos are built whole
    /// without mip streaming, and without the aircraft's position X-Plane is never asked where the sun is.
    /// </summary>
    private Daylight? SkipPhotosAtNight(MountSettings s, bool streaming)
    {
        if (!streaming)
        {
            Log.Warning("Skip the photos at night needs mip streaming; the photos stream at night too in this run");
            return null;
        }
        if (!s.TrackAircraft)
        {
            Log.Warning("Skip the photos at night needs the aircraft's position from X-Plane (track-aircraft); the photos stream at night too in this run");
            return null;
        }
        Log.Step($"Skip the photos at night: on; while the sun in X-Plane is more than {s.SkipPhotosSunBelow:0.#}° below the horizon, " +
                 "the ground is drawn from the low-zoom base and no detailed photos are downloaded");
        return _daylight;
    }

    /// <summary>Waits for a background loop to end at stop; a failure of <paramref name="what"/> is logged, not thrown.</summary>
    private static async Task EndAsync(Task? loop, string what)
    {
        if (loop is null) return;
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
#pragma warning disable CA1031 // Stopping must finish whatever the SimBrief side threw: it is logged, then the rest is disposed.
        catch (Exception ex) when (what.Length > 0)
#pragma warning restore CA1031
        {
            Log.Warning($"{what} failed while streaming stopped ({ex.GetType().Name}: {ex.Message}); stopping carries on");
        }
    }

    /// <summary>"Extra (level +1)": the quality in the words of the window.</summary>
    private static string QualityName(MountSettings s) =>
        QualityPreset.ForLevel(s.Textures.LevelChange) is { } preset ? $"{preset.Name} quality" : $"level {s.Textures.LevelChange:+0;-0;0}";

    /// <summary>Keeps the cache within --cache-limit, removing the areas flown longest ago.</summary>
    private void TrimCache()
    {
        if (_settings.CacheLimitBytes <= 0 || _usage is null) return;
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var (removed, freed) = CacheAreas.Trim(_settings.Cache, _settings.CacheLimitBytes, _usage);
            if (removed.Count > 0)
                Log.Step($"Cache limit {CacheInventory.FormatBytes(_settings.CacheLimitBytes)}: removed {removed.Count} area(s) flown longest ago " +
                         $"({string.Join(", ", removed.Take(12))}{(removed.Count > 12 ? ", ..." : "")}), {CacheInventory.FormatBytes(freed)} freed " +
                         $"in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"The cache could not be kept within its limit: {ex.Message}");
        }
    }

    /// <summary>The flight report: the session's samples, saved for the window when X-Plane sent positions.</summary>
    private void ReportFlight()
    {
        ReportSample[] samples;
        lock (_timeline) samples = [.. _timeline];
        if (!samples.Any(s => s.Lat is not null)) return;
        Report = new FlightReport(_startedUtc, DateTime.UtcNow, _settings.Textures.FolderName, samples);
        // A flight long enough to say what X-Plane holds: the presets' graphics memory, as flown on this PC.
        if (_graphics is not null && Report.MinutesInFlight >= Tuner.MinMinutesInFlight)
        {
            GraphicsMemory memory = _graphics.Result();
            memory.Save(AppPaths.History);
            Log.Info("Photo textures in graphics memory at the peak: " +
                     string.Join(", ", GraphicsMemory.Sizes.Select(s => $"{s} px {CacheInventory.FormatBytes(memory.BytesAt(s))}")));
        }
        Report.Save(FlightReport.PathFor(AppPaths.History));
        Log.Step($"Flight report: {Report.Duration.TotalMinutes:F0} min, {Report.DistanceKm:F0} km, {CacheInventory.FormatBytes(Report.Bytes)} downloaded " +
                 $"(peak {Report.PeakMbits:F0} Mbit/s), {Report.Builds} textures built, {Report.StandIns} reads without their real picture (stand-ins or flat)");
    }

    /// <summary>Stops without the summaries, if <see cref="StopAsync(bool)"/> was not called.</summary>
    public void Dispose() => StopAsync(hurry: true, summaries: false).GetAwaiter().GetResult();

    /// <summary>The package name in front of a log line, when several packages are mounted.</summary>
    private string Label(PackageMount mount) => _mounts.Count > 1 ? $"{mount.Plan.Name}: " : "";

    /// <summary>The flight tuning: what this session measured, turned into suggestions, logged and saved for the window.</summary>
    private void ReviewFlight()
    {
        try
        {
            StreamingFacts? streaming = StreamingFacts.Combine([.. _mounts.Select(m => m.Streamer?.Facts()).OfType<StreamingFacts>()]);
            ReportSample[] samples;
            lock (_timeline) samples = [.. _timeline];
            double? peakMbits = samples.Length > 1 ? new FlightReport(_startedUtc, DateTime.UtcNow, "", samples).PeakMbits : null;
            ConnectionResult? test = ConnectionResult.Load(ConnectionResult.PathFor(SettingsDocument.DefaultPath));
            Review = Tuner.Review(_recorder.Facts(streaming, _tiles), CurrentSettings.From(_settings), DateTime.Now, test, peakMbits);
            Log.Step(Review.Describe());
            Review.Save(FlightReview.PathFor(AppPaths.History));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning($"Flight tuning skipped: {ex.Message}");
        }
    }

    /// <summary>Until the mount stops, notes every few seconds what the flight tuning needs over time.</summary>
    private async Task SampleAsync(CancellationToken stop)
    {
        while (true)
        {
            await Task.Delay(FlightRecorder.Interval, stop).ConfigureAwait(false);
            _cpu.Sample();
            bool inFlight = _listener?.Latest is { } aircraft && DateTime.UtcNow - aircraft.ReceivedUtc < TimeSpan.FromSeconds(10);
            GateLimit limit = _mounts.Select(m => m.Streamer?.BuildLimit ?? GateLimit.None).FirstOrDefault(l => l != GateLimit.None);
            _recorder.Add(inFlight, _tiles?.QueuedDownloads > 0, limit, SystemMemory.Now(), Environment.WorkingSet);
            AircraftReport? at = inFlight ? _listener!.Latest : null;
            var sample = new ReportSample(Math.Round((DateTime.UtcNow - _startedUtc).TotalSeconds, 1), at?.Lat, at?.Lon, at?.GroundSpeedKt,
                _tiles?.TilesDownloaded ?? 0, _tiles?.BytesDownloaded ?? 0,
                _mounts.Sum(m => m.Streamer?.BuildsDone ?? 0), _mounts.Sum(m => m.Streamer?.StandInReads ?? 0), _mounts.Sum(m => m.Streamer?.Reads ?? 0));
            lock (_timeline)
                if (_timeline.Count < 100_000) _timeline.Add(sample);
            if (_timeline.Count % 60 == 0) // every 5 minutes
            {
                _usage?.Save();
                _colours?.Save();
            }
            if (_timeline.Count % 12 == 0 && _mounts.Any(m => m.Streamer is not null)) // every minute
            {
                _graphics ??= new GraphicsMemoryMeter(Math.Max(_settings.Textures.SizeFor(16), _settings.Textures.SizeFor(18)));
                _graphics.Sample(_mounts.SelectMany(m => m.Streamer?.HeldDetail(GraphicsMemory.HeldFor) ?? []));
            }
        }
    }

    /// <summary>
    /// Until the mount stops, logs each package's prefetch summary every two minutes when it has changed, and the CPU
    /// and memory used in those two minutes.
    /// </summary>
    private async Task LogSummariesAsync(CancellationToken stop)
    {
        var logged = new long[_mounts.Count];
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stop).ConfigureAwait(false);
            for (int i = 0; i < _mounts.Count; i++)
            {
                SceneryOverlay overlay = _mounts[i].Overlay;
                if (overlay.SummaryVersion == logged[i] || overlay.PrefetchSummary() is not { } summary) continue;
                logged[i] = overlay.SummaryVersion;
                Log.Info(Label(_mounts[i]) + summary);
            }
            Log.Info(_cpu.Summary());
        }
    }
}
