using System.Net;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP;

/// <summary>One package to mount: its folder, its cache overlay and where X-Plane sees it.</summary>
internal sealed record PackagePlan(string Name, string Source, string Cache, string Mountpoint);

/// <summary>
/// Everything a mount needs, read from the mount options and checked: the window
/// both start a <see cref="MountSession"/> from it.
/// </summary>
internal sealed record MountSettings
{
    public required string Cache { get; init; }
    public required TexturePlan Textures { get; init; }
    /// <summary>The texture plan's own cache subfolder (<see cref="TexturePlan.FolderName"/>).</summary>
    public required string PlanCache { get; init; }
    public required IReadOnlyList<PackagePlan> Packages { get; init; }
    /// <summary>X-Plane's Custom Scenery folder when the packages are mounted there.</summary>
    public string? CustomScenery { get; init; }
    /// <summary>Subfolders of the base packages folder that are not scenery packages.</summary>
    public IReadOnlyList<string> NotPackages { get; init; } = [];
    public bool UpdateSceneryPacks { get; init; }
    public LogOutput LogOutput { get; init; }
    public required string LogPath { get; init; }
    public IReadOnlyList<ImageryProvider>? Providers { get; init; }
    public int DownloadWorkers { get; init; }
    public double DownloadTimeout { get; init; }
    public int DownloadAttempts { get; init; }
    public int DdsBuildWorkers { get; init; }
    public int EncoderWorkers { get; init; }
    public int FileSystemThreads { get; init; }
    public bool TrackAircraft { get; init; }
    public bool LoadAhead { get; init; }
    public bool Background { get; init; }
    public required PositionOptions Position { get; init; }
    public int ProvisionalLod { get; init; }
    public double ProvisionalWait { get; init; }
    public string? TextureStore { get; init; }
    public bool LazyTerrainListing { get; init; }
    /// <summary>How strong X-Plane's detail decals are on the photo terrain, 0 (left out) to 100 percent (as the packages define).</summary>
    public int GroundDetail { get; init; }
    /// <summary>The photo tiles get night textures: street lamps and lit windows along X-Plane's roads.</summary>
    public bool NightLights { get; init; }
    /// <summary>At night in X-Plane only the low-zoom base is downloaded: no detailed photos under the night lights.</summary>
    public bool SkipPhotosAtNight { get; init; }
    /// <summary>How far below the horizon, in degrees, the sun must be before the photos are skipped (0-18).</summary>
    public double SkipPhotosSunBelow { get; init; } = -Scenery.Daylight.DefaultDarkBelow;
    /// <summary>A texture-quality value from an older settings file: no longer used, noted in the log.</summary>
    public string? IgnoredTextureQuality { get; init; }
    /// <summary>Photo textures are served level by level as X-Plane reads them (off: built whole first).</summary>
    public bool MipStreaming { get; init; } = true;
    public double NearWait { get; init; } = DefaultNearWait;
    public double MaxWait { get; init; } = DefaultMaxWait;
    /// <summary>MB the detailed builds may hold together (build-memory).</summary>
    public int BuildMemoryMb { get; init; } = DefaultBuildMemoryMb;
    /// <summary>Tiles the builds may download together (build-tiles).</summary>
    public int BuildTiles { get; init; } = Scenery.TextureStreamer.DefaultBuildTiles;
    /// <summary>The paging model's constant, or null for a guess from X-Plane's screen.</summary>
    public double? PagingK { get; init; }
    /// <summary>The most the setup folders of the cache may hold, in bytes (0: no limit).</summary>
    public long CacheLimitBytes { get; init; }
    /// <summary>Seasonal colours for the photo textures.</summary>
    public SeasonOptions Seasons { get; init; } = SeasonOptions.Off;
    /// <summary>Photo colours evened out between captures, by the low-zoom base.</summary>
    public bool MatchColours { get; init; }
    /// <summary>How much of the colour matching is made, 0 to 1.</summary>
    public double MatchStrength { get; init; } = 1;
    /// <summary>The photos' exposure in percent, <see cref="OrthoStreamXP.Textures.Exposure.Min"/> to 100 (100: as they are).</summary>
    public double Exposure { get; init; } = OrthoStreamXP.Textures.Exposure.Full;
    /// <summary>Every open and read, and the camera, logged as CSV beside the log.</summary>
    public bool ReadLog { get; init; }
    /// <summary>Every texture and terrain file X-Plane asks for, and every build, in the log too (for a bug hunt).</summary>
    public bool LogDetail { get; init; }
    /// <summary>SimBrief username or Pilot ID: the latest flight plan's departure is prefetched (null: off).</summary>
    public string? SimBriefUser { get; init; }
    /// <summary>How far around the SimBrief departure textures are prefetched.</summary>
    public double SimBriefRadiusKm { get; init; } = DefaultSimBriefRadiusKm;
    /// <summary>The most the SimBrief prefetch may download in a session, in MB; 0: no limit.</summary>
    public int SimBriefDownloadLimitMb { get; init; } = DefaultSimBriefDownloadLimitMb;
    /// <summary>The ground pre-warm builds the alternate's scenery too (rarely flown: off unless chosen).</summary>
    public bool SimBriefAlternate { get; init; }

    public const int DefaultFileSystemThreads = 64;
    public const double DefaultNearWait = 60, DefaultMaxWait = 6, DefaultLookaheadSeconds = 120;
    public const int DefaultBuildMemoryMb = 512;
    public const double DefaultSimBriefRadiusKm = 10;
    public const int DefaultSimBriefDownloadLimitMb = 1000;

    /// <summary>Reads and checks the mount options; a mistake is a <see cref="SettingsException"/> in plain words.</summary>
    /// <param name="console">False for the window: the log never goes to a console there.</param>
    public static MountSettings FromValues(SettingValues values, bool console = true)
    {
        string cache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(values.Required("cache")));
        int levelChange = values.Int("level-change", 0);
        if (levelChange is < -4 or > 2) throw new SettingsException("level-change must be between -4 and 2");
        // The texture size follows the level (Balanced 512 ... Extra 4096); an older settings file's texture-quality is ignored.
        var texturePlan = new TexturePlan(levelChange);
        // One cache folder for every setup: each texture plan keeps its files in its own subfolder.
        string planCache = Path.Join(cache, texturePlan.FolderName);
        // The regions an earlier version installed take their new names before the packages are listed (nothing is mounted yet).
        Setup.LegacyPackageNames.Move(values.String("base-packages"), cache, values.String("xplane"));
        IReadOnlyList<PackagePlan> packages = PlanPackages(values, planCache, out string? customScenery, out IReadOnlyList<string> notPackages);
        string sceneryPacksValue = values.String("scenery-packs") ?? "on";
        bool updateSceneryPacks = ParseOnOff(sceneryPacksValue) ?? throw new SettingsException($"scenery-packs must be on or off, not '{sceneryPacksValue}'");
        string logValue = values.String("log-output") ?? "both";
        LogOutput logOutput = Log.ParseOutput(logValue)
            ?? throw new SettingsException($"log-output must be none, console, log-file or both, not '{logValue}'");
        if (!console) logOutput &= ~LogOutput.Console;
        Setup.LegacyNames.MoveCache(cache, Setup.AppPaths.Logs, Setup.AppPaths.History); // before the log and the cache are opened
        // One log per session in the app's logs folder (the last ones kept, see Log.Configure), unless a file is named.
        string logPath = values.String("log-file") is { } logFile
            ? Path.GetFullPath(logFile)
            : Path.Join(Setup.AppPaths.Logs, $"orthostreamxp-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        string detailValue = values.String("log-detail") ?? "off";
        bool logDetail = ParseOnOff(detailValue) ?? throw new SettingsException($"log-detail must be on or off, not '{detailValue}'");

        string? provider = values.String("texture-provider");
        int downloadWorkers = values.Int("download-workers", 8);
        double downloadTimeout = values.Double("download-timeout", TileSource.DefaultTimeoutSeconds);
        int downloadAttempts = values.Int("download-attempts", TileSource.DefaultAttempts);
        int ddsBuildWorkers = values.Int("dds-build-workers", 2);
        int encoderWorkers = values.Int("encoder-workers", 4);
        int fileSystemThreads = values.Int("filesystem-threads", DefaultFileSystemThreads);
        bool trackAircraft = values.Flag("track-aircraft");
        string loadAheadValue = values.String("prefetch-loaded-scenery") ?? "on";
        bool loadAhead = (ParseOnOff(loadAheadValue) ?? throw new SettingsException($"prefetch-loaded-scenery must be on or off, not '{loadAheadValue}'"))
                         && provider is not null;
        int positionPort = values.Int("position-port", 49005);
        string xplaneValue = values.String("xplane-address") ?? $"127.0.0.1:{XPlaneUdp.DefaultPort}";
        double radiusKm = values.Double("prefetch-radius-km", 5.0);
        double lookaheadSeconds = values.Double("prefetch-lookahead-seconds", DefaultLookaheadSeconds);
        int prefetchTextures = values.Int("prefetch-textures", 8);
        int provisionalLod = values.Int("provisional-lod", 2);
        double provisionalWait = values.Double("provisional-wait", 2.0);
        string streamingValue = values.String("mip-streaming") ?? "on";
        bool mipStreaming = ParseOnOff(streamingValue) ?? throw new SettingsException($"mip-streaming must be on or off, not '{streamingValue}'");
        double nearWait = values.Double("near-wait", DefaultNearWait);
        double maxWait = values.Double("max-wait", DefaultMaxWait);
        int buildMemoryMb = values.Int("build-memory", DefaultBuildMemoryMb);
        int buildTiles = values.Int("build-tiles", Scenery.TextureStreamer.DefaultBuildTiles);
        string? pagingValue = values.String("paging-k");
        double? pagingK = pagingValue is null or "auto" ? null : values.Double("paging-k", PagingModel.DefaultK);
        SeasonOptions seasons = ParseSeasons(values);
        double cacheLimitGb = values.Double("cache-limit", 0);
        if (cacheLimitGb is < 0 or > 100_000) throw new SettingsException("cache-limit must be 0 (no limit) or a size in GB");
        string matchValue = values.String("match-colours") ?? "off";
        bool matchColours = ParseOnOff(matchValue) ?? throw new SettingsException($"match-colours must be on or off, not '{matchValue}'");
        double matchStrength = values.Double("match-strength", 100);
        if (matchStrength is < 0 or > 100) throw new SettingsException("match-strength must be between 0 and 100");
        double exposure = values.Double("exposure", OrthoStreamXP.Textures.Exposure.Full);
        if (exposure is < OrthoStreamXP.Textures.Exposure.Min or > OrthoStreamXP.Textures.Exposure.Full)
            throw new SettingsException($"exposure must be between {OrthoStreamXP.Textures.Exposure.Min} and {OrthoStreamXP.Textures.Exposure.Full}");
        string readLogValue = values.String("read-log") ?? "off";
        bool readLog = ParseOnOff(readLogValue) ?? throw new SettingsException($"read-log must be on or off, not '{readLogValue}'");
        int groundDetail = values.Int("ground-detail", 0);
        if (groundDetail is < 0 or > Scenery.TerrainDecals.Full) throw new SettingsException("ground-detail must be between 0 and 100");
        string nightValue = values.String("night-lights") ?? "off";
        bool nightLights = ParseOnOff(nightValue) ?? throw new SettingsException($"night-lights must be on or off, not '{nightValue}'");
        string skipValue = values.String("skip-photos-at-night") ?? "off";
        bool skipPhotosAtNight = ParseOnOff(skipValue) ?? throw new SettingsException($"skip-photos-at-night must be on or off, not '{skipValue}'");
        double skipSunBelow = values.Double("skip-photos-sun-below", -Scenery.Daylight.DefaultDarkBelow);
        if (skipSunBelow is < 0 or > 18) throw new SettingsException("skip-photos-sun-below must be between 0 and 18 degrees");
        string? simBriefUser = values.String("simbrief-user")?.Trim() is { Length: > 0 } user ? user : null;
        double simBriefRadiusKm = values.Double("simbrief-radius-km", DefaultSimBriefRadiusKm);
        if (simBriefRadiusKm is < 1 or > 50) throw new SettingsException("simbrief-radius-km must be between 1 and 50");
        int simBriefDownloadLimitMb = values.Int("simbrief-download-limit", DefaultSimBriefDownloadLimitMb);
        string alternateValue = values.String("simbrief-alternate") ?? "off";
        bool simBriefAlternate = ParseOnOff(alternateValue) ?? throw new SettingsException($"simbrief-alternate must be on or off, not '{alternateValue}'");
        if (simBriefDownloadLimitMb is < 0 or > 100_000) throw new SettingsException("simbrief-download-limit must be between 0 (no limit) and 100000");

        if (downloadWorkers < 1) throw new SettingsException("download-workers must be at least 1");
        if (downloadTimeout is < 1 or > 120) throw new SettingsException("download-timeout must be between 1 and 120 seconds");
        if (downloadAttempts is < 1 or > 10) throw new SettingsException("download-attempts must be between 1 and 10");
        IReadOnlyList<ImageryProvider>? providers = provider is null ? null : ParseProviders(provider, values.String("texture-url"));
        if (encoderWorkers is < 1 or > 16) throw new SettingsException("encoder-workers must be between 1 and 16");
        if (ddsBuildWorkers < 1) throw new SettingsException("dds-build-workers must be at least 1");
        if (fileSystemThreads is < 2 or > 512) throw new SettingsException("filesystem-threads must be between 2 and 512");
        if (trackAircraft && provider is null) throw new SettingsException("track-aircraft requires texture-provider");
        if (positionPort is < 1 or > 65535) throw new SettingsException("position-port must be between 1 and 65535");
        if (!IPEndPoint.TryParse(xplaneValue, out IPEndPoint? xplane) || xplane.Port == 0)
            throw new SettingsException($"xplane-address must be an IP address and port such as 127.0.0.1:49000, not '{xplaneValue}'");
        if (radiusKm <= 0 || prefetchTextures < 1) throw new SettingsException("Prefetch radius and texture count must be positive");
        if (lookaheadSeconds is < 0 or > 1800) throw new SettingsException("prefetch-lookahead-seconds must be between 0 and 1800");
        if (provisionalLod is < 0 or > 3) throw new SettingsException("provisional-lod must be between 0 and 3");
        if (nearWait is < 0 or > 300) throw new SettingsException("near-wait must be between 0 and 300 seconds");
        if (maxWait is < 0 or > 60) throw new SettingsException("max-wait must be between 0 and 60 seconds");
        if (buildMemoryMb is < 128 or > 8192) throw new SettingsException("build-memory must be between 128 and 8192 MB");
        if (buildTiles is < 1024 or > 262144) throw new SettingsException("build-tiles must be between 1024 and 262144");
        if (pagingK is < 100 or > 100_000) throw new SettingsException("paging-k must be between 100 and 100000 (or auto)");

        string? textureStore = values.String("lazy-texture-store") is { } store ? Path.GetFullPath(store) : null;
        if (textureStore is not null && !Directory.Exists(textureStore))
            throw new SettingsException($"Texture store is not a directory: {textureStore}");

        return new MountSettings
        {
            Cache = cache,
            Textures = texturePlan,
            PlanCache = planCache,
            Packages = packages,
            CustomScenery = customScenery,
            NotPackages = notPackages,
            UpdateSceneryPacks = updateSceneryPacks,
            LogOutput = logOutput,
            LogPath = logPath,
            Providers = providers,
            DownloadWorkers = downloadWorkers,
            DownloadTimeout = downloadTimeout,
            DownloadAttempts = downloadAttempts,
            DdsBuildWorkers = ddsBuildWorkers,
            EncoderWorkers = encoderWorkers,
            FileSystemThreads = fileSystemThreads,
            TrackAircraft = trackAircraft,
            LoadAhead = loadAhead,
            Background = values.Flag("background-textures") || trackAircraft || loadAhead,
            Position = new PositionOptions(positionPort, xplane, radiusKm, prefetchTextures, lookaheadSeconds),
            ProvisionalLod = provisionalLod,
            ProvisionalWait = provisionalWait,
            TextureStore = textureStore,
            LazyTerrainListing = values.Flag("lazy-terrain-listing"),
            GroundDetail = groundDetail,
            NightLights = nightLights,
            SkipPhotosAtNight = skipPhotosAtNight,
            SkipPhotosSunBelow = skipSunBelow,
            IgnoredTextureQuality = values.String("texture-quality"),
            MipStreaming = mipStreaming,
            NearWait = nearWait,
            MaxWait = maxWait,
            BuildMemoryMb = buildMemoryMb,
            BuildTiles = buildTiles,
            PagingK = pagingK,
            Seasons = seasons,
            MatchColours = matchColours,
            MatchStrength = matchStrength / 100,
            Exposure = exposure,
            CacheLimitBytes = (long)(cacheLimitGb * (1L << 30)),
            ReadLog = readLog,
            LogDetail = logDetail,
            SimBriefUser = simBriefUser,
            SimBriefRadiusKm = simBriefRadiusKm,
            SimBriefDownloadLimitMb = simBriefDownloadLimitMb,
            SimBriefAlternate = simBriefAlternate,
        };
    }

    /// <summary>
    /// The packages to mount: source alone, or every package in base-packages (each with its own cache
    /// subfolder), mounted at mountpoint or as X-Plane's Custom Scenery\&lt;name&gt;.
    /// </summary>
    /// <param name="customScenery">X-Plane's Custom Scenery folder when xplane is given.</param>
    /// <param name="notPackages">Subfolders of base-packages that are not scenery packages.</param>
    internal static IReadOnlyList<PackagePlan> PlanPackages(SettingValues values, string cache, out string? customScenery, out IReadOnlyList<string> notPackages)
    {
        string? source = FullPath(values.String("source"));
        string? basePackages = FullPath(values.String("base-packages"));
        string? mountpoint = FullPath(values.String("mountpoint"));
        string? xplane = FullPath(values.String("xplane"));
        if ((source is null) == (basePackages is null))
            throw new SettingsException("give either source (one scenery package) or base-packages (a folder of them)");
        if (basePackages is not null && mountpoint is not null)
            throw new SettingsException("base-packages mounts every package in X-Plane's Custom Scenery: give xplane instead of mountpoint");
        if ((mountpoint is null) == (xplane is null))
            throw new SettingsException("give either xplane (the X-Plane folder: the package is mounted in its Custom Scenery) or mountpoint");

        customScenery = xplane is null ? null : Path.Join(xplane, "Custom Scenery");
        if (customScenery is not null && !Directory.Exists(customScenery))
            throw new SettingsException($"Not an X-Plane folder (it has no Custom Scenery folder): {xplane}");
        if (xplane is not null && (source ?? basePackages) is { } packages && SceneryPacks.IsInCustomScenery(packages, xplane))
            throw new SettingsException($"{InCustomScenery}: {packages}");
        notPackages = [];
        List<PackagePlan> plan;
        if (source is not null)
        {
            if (!Directory.Exists(source)) throw new SettingsException($"Source must be a scenery package directory: {source}");
            string name = Path.GetFileName(source);
            plan = [new PackagePlan(name, source, cache, mountpoint ?? Path.Join(customScenery, name))];
        }
        else
        {
            if (!Directory.Exists(basePackages)) throw new SettingsException($"Base packages folder does not exist: {basePackages}");
            plan = [];
            foreach (string folder in SceneryPacks.FindPackages(basePackages, out notPackages))
            {
                string name = Path.GetFileName(folder);
                plan.Add(new PackagePlan(name, folder, Path.Join(cache, name), Path.Join(customScenery, name)));
            }
            if (plan.Count == 0)
                throw new SettingsException($"No scenery packages (subfolders with an Earth nav data folder) in {basePackages}");
        }

        foreach (PackagePlan package in plan)
        {
            string? parent = Path.GetDirectoryName(package.Mountpoint);
            if (parent is null || !Directory.Exists(parent))
                throw new SettingsException($"Parent directory for mount point must exist: {parent}");
            if (Path.Exists(package.Mountpoint))
                throw new SettingsException($"Mount point path must not already exist; WinFsp creates it while mounted: {package.Mountpoint}" +
                                         (customScenery is null ? "" : " (Custom Scenery already has a folder of that name: remove or rename it)"));
            if (SamePath(package.Mountpoint, package.Source) || SamePath(package.Mountpoint, package.Cache) || SamePath(package.Mountpoint, cache))
                throw new SettingsException("Mount point must differ from both source and cache directories");
        }
        return plan;
    }

    /// <summary>The provider list of texture-provider, with its errors as usage errors.</summary>
    public static IReadOnlyList<ImageryProvider> ParseProviders(string list, string? customTemplate)
    {
        try
        {
            return ImageryProvider.ParseList(list, customTemplate);
        }
        catch (ArgumentException ex)
        {
            throw new SettingsException(ex.Message);
        }
    }

    internal static SeasonOptions ParseSeasons(SettingValues values)
    {
        string onValue = values.String("seasons") ?? "off";
        bool on = ParseOnOff(onValue) ?? throw new SettingsException($"seasons must be on or off, not '{onValue}'");
        double strength = values.Double("season-strength", SeasonOptions.DefaultStrength);
        if (strength is < 0 or > 100) throw new SettingsException("season-strength must be between 0 and 100");
        string choiceValue = values.String("season") ?? "auto";
        if (!SeasonModel.Names.TryGetValue(choiceValue, out SeasonChoice choice))
            throw new SettingsException($"season must be one of {string.Join(", ", SeasonModel.Names.Keys)}, not '{choiceValue}'");
        return new SeasonOptions(on, strength / 100, choice);
    }

    internal static bool? ParseOnOff(string value) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "yes" => true,
        "off" or "false" or "no" => false,
        _ => null,
    };

    /// <summary>The start of the error for scenery packages kept in X-Plane's Custom Scenery.</summary>
    internal const string InCustomScenery = "Scenery packages must not be in X-Plane's Custom Scenery folder";

    private static string? FullPath(string? path) => path is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
