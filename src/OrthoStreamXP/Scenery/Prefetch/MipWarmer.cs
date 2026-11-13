using System.Collections.Concurrent;
using System.Diagnostics;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Load-ahead for streamed textures. X-Plane opens every terrain definition of a DSF 20-45 s before it reads the
/// textures; in that time each texture is fetched to the level the paging model says X-Plane will want: the
/// camera's distance, shortened by how far the aircraft closes in on it in <see cref="LeadSeconds"/>. Next to the
/// camera that is full detail, in the middle distance a reduced zoom, and far away a single tile of the low-zoom
/// base, so a cold start downloads about one small tile per far texture. Before X-Plane sends a position, every
/// texture gets its base tile. The flight-path prefetch adds whole textures through the same queue.
/// <para>
/// Next to the camera goes first, then by distance. Builds of many tiles are limited to <c>heavy</c> at once (each
/// holds its picture in memory while it builds); builds of a few tiles and base tiles run many at a time, each kind
/// in places of its own, so neither holds up the other.
/// </para>
/// </summary>
internal sealed class MipWarmer : IDisposable
{
    /// <summary>
    /// A level is fetched when the aircraft will need it this soon. X-Plane asks for full detail the moment a texture
    /// crosses its line; a full-detail fetch takes 1-2 s, and the plan is renewed only every 1.5 km (27 s at 110 kt),
    /// so the lead must cover both with room to spare (3.3 km at 110 kt, 14 km at 450 kt).
    /// </summary>
    public const double LeadSeconds = 60;
    /// <summary>
    /// Base tiles at once. They have places of their own: a far texture X-Plane reads without its base tile gets one
    /// flat colour, and in one test flight the middle-distance builds of a newly loaded scenery tile, sharing these
    /// places, kept 48 far textures from their base tile for the 5 s until X-Plane read them.
    /// </summary>
    private const int BaseInFlight = 64;
    /// <summary>
    /// A build of at most this many tiles (one for level 4 of a ZL18 texture, 2 x 2 for level 3) is small. Counted
    /// with the full-size builds, the hundreds of middle-distance textures of a newly loaded scenery tile were fetched
    /// a few at a time, and X-Plane's reads of them waited 5 s.
    /// </summary>
    internal const long SmallTiles = 4;
    /// <summary>Small builds at once: as many as the streamer lets builds ahead of X-Plane run.</summary>
    private const int SmallInFlight = TextureStreamer.DefaultMaxBuilds / 2;

    /// <summary>The places a load-ahead build takes, each kind with its own limit.</summary>
    internal enum BuildKind { Base, Small, Heavy }
    /// <summary>How often the progress is logged while X-Plane loads scenery, and otherwise.</summary>
    private static readonly TimeSpan LoadingReportInterval = TimeSpan.FromSeconds(10), QuietReportInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A texture to warm; <paramref name="Mip"/> and <paramref name="Priority"/> fixed for the flight-path and departure
    /// prefetch. <paramref name="Recheck"/>: queued again because the aircraft moved, not because X-Plane loaded it.
    /// <paramref name="Distance"/>: the order among the prefetch's own requests (from the departure, or along the route).
    /// <paramref name="Meter"/>: the SimBrief phase that queued it (see <see cref="PrefetchMeter"/>); null for every
    /// other request. A ground phase's (<see cref="Ground"/>) is dropped when the aircraft rolls for takeoff, and so is the
    /// departure's (<see cref="Departure"/>): its level and priority were chosen for the gate.
    /// </summary>
    private readonly record struct Request(VirtualPath Texture, int? Mip = null, int? Priority = null, bool Recheck = false, double? Distance = null,
        PrefetchMeter? Meter = null)
    {
        public bool Ground => Meter is { Ground: true };

        public bool Departure => Meter is { Ground: false };
    }

    /// <summary>The SimBrief prefetch's phases: the departure, then the ground pre-warm's (index = its phase + 1).</summary>
    private readonly PrefetchMeter[] _phases =
        [new("departure", ground: false), new("arrival", ground: true), new("alternate", ground: true), new("climb", ground: true)];
    /// <summary>The SimBrief prefetch queued something: X-Plane's loads are checked against it (the hit rate).</summary>
    private volatile bool _prefetched;
    /// <summary>The ground pre-warm's builds running now (for the takeoff roll's log line, H3).</summary>
    private int _groundRunning;
    private PrefetchBudget? _budget;
    /// <summary>The budget's round (<see cref="PrefetchBudget.Round"/>) in which this package's SimBrief requests were dropped; -1: none yet.</summary>
    private int _budgetSpentRound = -1;
    /// <summary>Passes of <see cref="StartWhatFits"/> and their longest time since the last log (H2).</summary>
    private int _passes;
    private TimeSpan _longestPass;
    private const int PassesPerReport = 100;

    private readonly TextureCatalog _catalog;
    private readonly TextureStreamer _streamer;
    private readonly ViewTracker _view;
    private readonly int _heavyLimit;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Request> _waiting = [];
    /// <summary>Textures whose base tile is being fetched before the level asked for (see <see cref="Target"/>).</summary>
    private readonly ConcurrentDictionary<string, byte> _stepping = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task? _loop;
    /// <summary>Builds running, by <see cref="BuildKind"/>.</summary>
    private readonly int[] _running = new int[3];
    private int _queued, _alreadyHad;
    /// <summary>The ground pre-warm queued textures that have not all been built yet (see <see cref="GroundDone"/>).</summary>
    private volatile bool _groundPending;
    private (int Known, int Waiting, int Queued, int AlreadyHad) _reported;
    private TimeSpan _reportedAt;

    /// <param name="heavy">Builds of more than <see cref="SmallTiles"/> tiles at once.</param>
    /// <param name="warmLoadedScenery">Load-ahead: warm every texture X-Plane loads; off, only the flight-path prefetch comes here.</param>
    public MipWarmer(TextureCatalog catalog, TextureStreamer streamer, ViewTracker view, int heavy, bool warmLoadedScenery = true)
    {
        _catalog = catalog;
        _streamer = streamer;
        _view = view;
        _heavyLimit = Math.Max(1, heavy);
        WarmsLoadedScenery = warmLoadedScenery;
        if (!warmLoadedScenery) return;
        catalog.EntryAdded += OnEntryAdded;
        view.Moved += OnMoved;
    }

    public bool WarmsLoadedScenery { get; }

    /// <summary>
    /// When everything the ground pre-warm queued has been built and nothing else is building (again after a later
    /// phase was queued, if the earlier ones finished first): the aircraft
    /// still stands at the gate, and the build buffers can be given back. Not raised when the takeoff roll dropped
    /// requests still waiting (the aircraft moves then). Raised on the warmer's own thread.
    /// </summary>
    public event Action? GroundDone;

    /// <summary>Warm builds started this session.</summary>
    public int Queued => Volatile.Read(ref _queued);

    public void Start()
    {
        _loop = Task.Run(RunAsync);
        if (WarmsLoadedScenery)
            Log.Info($"Load-ahead on: each texture of the scenery X-Plane loads is fetched to the level it will need " +
                     $"({_heavyLimit} detailed textures, {SmallInFlight} of a few tiles and {BaseInFlight} base tiles at a time)");
    }

    private void OnEntryAdded(TextureCatalog.Entry entry)
    {
        if (_prefetched)
            foreach (PrefetchMeter meter in _phases) meter.NoteLoaded(entry.Texture);
        Add(new Request(entry.Texture));
    }

    /// <summary>The SimBrief prefetch spends from <paramref name="budget"/> (shared by every package); set before it queues anything.</summary>
    public void UseBudget(PrefetchBudget budget)
    {
        _budget = budget;
        foreach (PrefetchMeter meter in _phases) meter.Budget = budget;
    }

    /// <summary>The SimBrief prefetch's phases this session, in their order.</summary>
    public IReadOnlyList<PrefetchMeter> Phases => _phases;

    /// <summary>A new position: what every loaded texture will need has changed.</summary>
    private void OnMoved(CameraReport camera)
    {
        lock (_gate)
            foreach (TextureCatalog.Entry entry in _catalog.Entries)
                AddOrOutrank(new Request(entry.Texture, Recheck: true));
        Wake();
    }

    /// <summary>
    /// The flight-path prefetch (and the SimBrief departure without streaming's levels): whole textures ahead of
    /// X-Plane. Returns how many were queued. Each gets its base tile first (see <see cref="Target"/>).
    /// </summary>
    public int Prefetch(IReadOnlyList<VirtualPath> textures)
    {
        lock (_gate)
            foreach (VirtualPath texture in textures)
                _waiting[texture.Key] = new Request(texture, 0, FetchPriority.Warm);
        Wake();
        return textures.Count;
    }

    /// <summary>
    /// The scenery X-Plane loads around an airport of the SimBrief flight plan: the departure (<paramref name="phase"/>
    /// null), or the ground pre-warm's arrival or alternate (<see cref="GroundPrewarm.Arrival"/>, <see cref="GroundPrewarm.Alternate"/>).
    /// Every texture goes to the level X-Plane will want with the aircraft there (full detail beside it, the middle levels
    /// further out, a base tile far away), the same choice load-ahead makes once X-Plane sends a position, only made
    /// before X-Plane opens anything. The order is load-ahead's around a point: <see cref="PrefetchPlanner.Select"/>
    /// without motion, nearest first. Returns how many were queued and how many had what they need already.
    /// </summary>
    public (int Queued, int Had) PrefetchAround(IReadOnlyList<VirtualPath> textures, double lat, double lon, int? phase = null)
    {
        var entries = new List<TextureCatalog.Entry>(textures.Count);
        foreach (VirtualPath texture in textures)
            if (_streamer.Get(texture) is { } t) entries.Add(new TextureCatalog.Entry(texture, t.Lat, t.Lon));
        IReadOnlyList<VirtualPath> nearestFirst = PrefetchPlanner.Select(entries, lat, lon, motion: null, AroundRadiusKm, lookaheadSeconds: 0, int.MaxValue);
        // The order is the distance in metres, as load-ahead's own requests are ranked by: Select's order, on the same scale.
        var placed = new List<(VirtualPath Texture, double DistanceM, double Order)>(nearestFirst.Count);
        foreach (VirtualPath texture in nearestFirst)
            if (_streamer.Get(texture) is { } t)
            {
                double distance = Geo.DistanceM(lat, lon, t.Lat, t.Lon);
                placed.Add((texture, distance, distance));
            }
        return PrefetchPlaced(placed, phase);
    }

    /// <summary>
    /// The radius <see cref="PrefetchAround"/> selects in: wider than the 3 x 4 DSF tiles X-Plane loads round a place
    /// (<see cref="SceneryLoads.TilesAround"/>) reach from it anywhere, so the tiles alone decide what is queued.
    /// </summary>
    internal const double AroundRadiusKm = 500;

    /// <summary>
    /// The ground pre-warm's climb (<see cref="GroundPrewarm"/>): each texture to the level X-Plane will want from
    /// <paramref name="place"/>'s distance (to the nearest point of the climb: a line, not a point, so not
    /// <see cref="PrefetchPlanner.Select"/>'s order), ordered by its second value (along the climb). A texture already
    /// waiting for anything else keeps that request. Returns how many were queued and how many had what they need already.
    /// </summary>
    public (int Queued, int Had) PrefetchGround(IReadOnlyList<VirtualPath> textures, Func<double, double, (double DistanceM, double Order)> place, int phase)
    {
        var placed = new List<(VirtualPath Texture, double DistanceM, double Order)>(textures.Count);
        foreach (VirtualPath texture in textures)
            if (_streamer.Get(texture) is { } t)
            {
                var (distance, order) = place(t.Lat, t.Lon);
                placed.Add((texture, distance, order));
            }
        return PrefetchPlaced(placed, phase);
    }

    /// <summary>Drops the ground pre-warm's requests still waiting (the takeoff roll, a newer plan); returns how many.</summary>
    public int CancelGround()
    {
        int dropped = Drop(static r => r.Ground);
        if (dropped > 0) _groundPending = false; // not all built: nothing to give back for
        return dropped;
    }

    /// <summary>H3, for the takeoff roll's log line: the builds running now, by kind, and how many are the ground pre-warm's.</summary>
    public string RunningSummary() =>
        $"{Volatile.Read(ref _running[(int)BuildKind.Heavy])} detailed, {Volatile.Read(ref _running[(int)BuildKind.Small])} small and " +
        $"{Volatile.Read(ref _running[(int)BuildKind.Base])} base builds running, {Volatile.Read(ref _groundRunning)} of them the ground pre-warm's";

    /// <summary>A build runs in this package: one of load-ahead's, or one for X-Plane's reads.</summary>
    public bool Building
    {
        get
        {
            for (int kind = 0; kind < _running.Length; kind++)
                if (Volatile.Read(ref _running[kind]) > 0) return true;
            return _streamer.Building > 0;
        }
    }

    /// <summary>X-Plane's reads of this package's textures so far (<see cref="TextureStreamer.Reads"/>).</summary>
    public long XPlaneReads => _streamer.Reads;

    /// <summary>The ground pre-warm queued work that has not all been built yet.</summary>
    public bool GroundPending => _groundPending;

    /// <summary>Raises <see cref="GroundDone"/> when the ground pre-warm's work is all built (called after each pass).</summary>
    internal void CheckGroundDone()
    {
        if (!_groundPending) return;
        for (int kind = 0; kind < _running.Length; kind++)
            if (Volatile.Read(ref _running[kind]) > 0) return; // the cheap check first: while it builds, this is the answer
        if (GroundWaiting > 0) return;
        _groundPending = false;
        GroundDone?.Invoke();
    }

    /// <summary>
    /// Drops the SimBrief departure's requests still waiting (the aircraft rolls for takeoff): their level and priority
    /// were chosen for the gate, and after takeoff they would compete with load-ahead for the climb. Returns how many.
    /// </summary>
    public int CancelDeparture() => Drop(static r => r.Departure);

    private int Drop(Func<Request, bool> which)
    {
        int dropped = 0;
        lock (_gate)
            foreach (var (key, request) in _waiting)
                if (which(request))
                {
                    _waiting.Remove(key); // allowed while enumerating a Dictionary
                    dropped++;
                }
        Wake();
        return dropped;
    }

    /// <summary>The ground pre-warm's requests still waiting.</summary>
    public int GroundWaiting
    {
        get
        {
            lock (_gate) return _waiting.Values.Count(w => w.Ground);
        }
    }

    /// <summary>
    /// Queues the SimBrief prefetch's textures, each placed by the distance X-Plane will see it from (its level) and its
    /// order among them. Every phase gives way to what load-ahead and the flight path want (<see cref="AddOrOutrank"/>).
    /// </summary>
    private (int Queued, int Had) PrefetchPlaced(List<(VirtualPath Texture, double DistanceM, double Order)> placed, int? phase)
    {
        var requests = new List<Request>(placed.Count);
        int had = 0;
        PrefetchMeter meter = _phases[phase is int which ? which + 1 : 0];
        if (_budget is { Exhausted: true }) return (0, 0); // spent: left to load-ahead
        foreach (var (texture, distance, order) in placed)
        {
            if (_streamer.Get(texture) is not { } t) continue;
            int wanted = _streamer.Model.MipFor(distance, t.SizeM, t.Layout.Size);
            int mip = Math.Min(wanted, Coarsest(t));
            int real = _streamer.RealFrom(t);
            if (real <= mip)
            {
                had++;
                meter.NoteHad();
                continue;
            }
            // Far ones are only their base tile: before the middle levels, as X-Plane reads far scenery first. Nothing of
            // the departure is more urgent than Warm: near the gate it is still a guess, and it must not hold up what
            // X-Plane really asks for or load-ahead next to the camera. The ground pre-warm's phases come after all of
            // it, in their order.
            int priority = phase is int p ? GroundPriority(p)
                : mip >= Coarsest(t) ? FetchPriority.Warm
                : Math.Max(FetchPriority.Warm, PriorityOf(t, wanted));
            requests.Add(new Request(texture, mip, priority, Distance: order, Meter: meter));
        }
        var queued = new List<VirtualPath>(requests.Count);
        lock (_gate)
            foreach (Request request in requests)
                if (AddOrOutrank(request)) queued.Add(request.Texture);
        foreach (VirtualPath texture in queued) meter.NoteQueued(texture); // off the gate: X-Plane's loads wait on it
        if (queued.Count > 0) _prefetched = true;
        if (meter.Ground && queued.Count > 0) _groundPending = true;
        Wake();
        return (queued.Count, had);
    }

    /// <summary>
    /// The coarsest level worth fetching ahead: the base tile, or the finer level X-Plane's smallest read takes anyway
    /// when that is finer (ZL18 textures: level 4, not their base level 6). Fetching only the base tile there made every
    /// such texture wait while X-Plane loaded it (926 of 3,554 at EDDV).
    /// </summary>
    private static int Coarsest(StreamedTexture t) => Math.Min(t.BaseMip, ReadPattern.CoarsestRead(t.Layout));

    /// <summary>A ground pre-warm phase's priority: after everything else, the phases in their order.</summary>
    internal static int GroundPriority(int phase) => FetchPriority.Background + 1 + phase;

    /// <summary>A load-ahead build's priority by the band X-Plane will read the texture in.</summary>
    private static int PriorityOf(StreamedTexture t, int wanted) => TextureStreamer.BandOf(t, wanted) switch
    {
        Band.Near => FetchPriority.NearWarm,
        Band.Middle => FetchPriority.Warm + wanted,
        _ => FetchPriority.Background,
    };

    private void Add(Request request)
    {
        lock (_gate) AddOrOutrank(request);
        Wake();
    }

    /// <summary>
    /// Queues <paramref name="request"/> unless a request of the same or a higher rank waits for the texture already
    /// (<see cref="Rank"/>: load-ahead and the flight path, then the SimBrief departure, then the ground pre-warm). The
    /// SimBrief requests give way because their level is a guess made at the gate, and the takeoff roll drops them
    /// (<see cref="CancelGround"/>, <see cref="CancelDeparture"/>): taking a waiting load-ahead request with them would
    /// leave a texture X-Plane loaded without one (H1). A newer departure request replaces a waiting one (a reloaded
    /// plan); a ground request never replaces one. Under the gate.
    /// </summary>
    private bool AddOrOutrank(Request request)
    {
        string key = request.Texture.Key;
        if (_waiting.TryGetValue(key, out Request existing))
        {
            int rank = Rank(request), current = Rank(existing);
            if (rank < current || (rank == current && (request.Ground || existing.Meter is null))) return false;
        }
        _waiting[key] = request;
        return true;
    }

    /// <summary>Which request keeps a texture: 2 load-ahead and the flight path, 1 the SimBrief departure, 0 the ground pre-warm.</summary>
    private static int Rank(Request request) => request.Meter is null ? 2 : request.Ground ? 0 : 1;

    /// <summary>The level and priority the request waiting for <paramref name="texture"/> fetches now; null when none waits.</summary>
    internal (int Mip, int Priority, bool Ground, double Order)? WaitingFor(VirtualPath texture)
    {
        Request request;
        lock (_gate)
            if (!_waiting.TryGetValue(texture.Key, out request)) return null;
        return Target(request.Texture, request.Mip, request.Priority, request.Distance) is { } t ? (t.Mip, t.Priority, request.Ground, t.Distance) : null;
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>The level to fetch and its priority, and the distance (for the order); null when it is not a photo texture.</summary>
    internal (StreamedTexture Texture, int Mip, int Priority, double Distance)? Target(VirtualPath path, int? mip = null, int? priority = null,
        double? departureDistance = null)
    {
        if (_streamer.Get(path) is not { } t) return null;
        // A level asked for ahead (flight path, departure) on a texture with nothing yet: its base tile first, in the
        // base tiles' own places and before the middle levels. Load-ahead gives every texture one, but skips a texture
        // that is whole already: the low-zoom base there (far scenery, colour matching, the report's map) would stay
        // empty. And a far texture X-Plane reads before its base tile arrives gets one flat colour.
        if (mip is int asked && asked < t.BaseMip && _streamer.RealFrom(t) > t.BaseMip)
            return (t, t.BaseMip, priority is int ground && ground > FetchPriority.Background ? ground : Math.Min(priority ?? FetchPriority.Warm, FetchPriority.Warm),
                departureDistance ?? (_view.Current is { } at ? ViewTracker.DistanceM(at, t.Lat, t.Lon) : double.MaxValue));
        if (_view.Current is not { } camera)
            return (t, mip ?? Coarsest(t), priority ?? FetchPriority.Background, departureDistance ?? double.MaxValue);
        double distance = ViewTracker.DistanceM(camera, t.Lat, t.Lon);
        if (mip is int fixedMip) return (t, fixedMip, priority ?? FetchPriority.Warm, departureDistance ?? distance);

        double soon = distance;
        if (camera.Motion is { SpeedMps: >= PrefetchPlanner.MovingSpeedMps } motion)
        {
            double bearing = Geo.BearingDeg(camera.Lat, camera.Lon, t.Lat, t.Lon);
            double closing = motion.SpeedMps * Math.Cos((bearing - motion.TrackDeg) * Math.PI / 180);
            soon = Math.Max(0, distance - Math.Max(0, closing) * LeadSeconds);
        }
        int wanted = _streamer.Model.MipFor(soon, t.SizeM, t.Layout.Size);
        int target = Math.Min(wanted, Coarsest(t));
        return (t, target, PriorityOf(t, wanted), distance);
    }

    private async Task RunAsync()
    {
        CancellationToken stop = _stop.Token;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                long pass = Stopwatch.GetTimestamp();
                StartWhatFits();
                NotePass(Stopwatch.GetElapsedTime(pass));
                CheckGroundDone();
                ReportProgress();
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Unmounting.
        }
    }

    /// <summary>Starts the most urgent waiting textures while there is room for them.</summary>
    internal void StartWhatFits()
    {
        DropPrefetchOverBudget();
        Request[] waiting;
        lock (_gate) waiting = [.. _waiting.Values];
        if (waiting.Length == 0) return;
        var ranked = waiting
            .Select(r => (Request: r, Target: Target(r.Texture, r.Mip, r.Priority, r.Distance)))
            .OrderBy(x => x.Target?.Priority ?? int.MaxValue).ThenBy(x => x.Target?.Distance ?? double.MaxValue);
        foreach (var (request, target) in ranked)
        {
            if (Full(BuildKind.Base) && Full(BuildKind.Small) && Full(BuildKind.Heavy)) break;
            if (target is not { } t)
            {
                Remove(request);
                continue;
            }
            if (_stepping.ContainsKey(request.Texture.Key)) continue; // its base tile is on the way
            BuildKind kind = KindOf(t.Texture, t.Mip);
            if (Full(kind)) continue;
            if (_streamer.RealFrom(t.Texture) <= t.Mip)
            {
                Remove(request);
                if (!request.Recheck) Interlocked.Increment(ref _alreadyHad);
                continue;
            }
            if (_streamer.Warm(request.Texture, t.Mip, t.Priority, request.Meter) is not { } build)
            {
                Remove(request);
                continue;
            }
            // A base step keeps the request: its level follows once the base tile is in. If the base tile fails (a
            // provider gap, the sea, a 404), so would the level: the request goes, instead of being ranked and tried
            // again every pass for the rest of the session.
            bool baseStep = request.Mip is int final && final < t.Mip;
            if (!baseStep) Remove(request);
            if (build.IsCompleted)
            {
                // Nothing started: it was there already, or it failed within the retry time.
                if (baseStep && !Succeeded(build)) Remove(request);
                continue;
            }
            Interlocked.Increment(ref _queued);
            if (baseStep) _stepping[request.Texture.Key] = 0;
            Interlocked.Increment(ref _running[(int)kind]);
            bool ground = request.Ground;
            if (ground) Interlocked.Increment(ref _groundRunning);
            string key = request.Texture.Key;
            build.ContinueWith(done =>
            {
                if (baseStep && !Succeeded(done)) Remove(request);
                if (ground) Interlocked.Decrement(ref _groundRunning);
                Interlocked.Decrement(ref _running[(int)kind]);
                _stepping.TryRemove(key, out byte _);
                Wake();
            }, TaskScheduler.Default);
        }
    }

    private static bool Succeeded(Task<bool> build) => build.IsCompletedSuccessfully && build.Result;

    /// <summary>Once the download budget is spent: the SimBrief requests still waiting go, and that is logged once.</summary>
    private void DropPrefetchOverBudget()
    {
        if (_budget is not { Exhausted: true } budget || _budgetSpentRound == budget.Round) return;
        _budgetSpentRound = budget.Round;
        int dropped = Drop(static r => r.Meter is not null);
        if (dropped > 0) _groundPending = false; // not all built: nothing to give back for
        if (budget.FirstToReport())
            Log.Info($"SimBrief prefetch: the download budget of {CacheInventory.FormatBytes(budget.LimitBytes)} is used up " +
                     $"(simbrief-download-limit); {dropped} textures still waiting are left to load-ahead");
    }

    /// <summary>
    /// H2: while the ground pre-warm has work queued (the biggest queue), how long a pass takes ranking and probing it,
    /// logged every <see cref="PassesPerReport"/> passes with the queue's size.
    /// </summary>
    private void NotePass(TimeSpan took)
    {
        if (!_groundPending) return;
        if (took > _longestPass) _longestPass = took;
        if (++_passes < PassesPerReport) return;
        int waiting;
        lock (_gate) waiting = _waiting.Count;
        Log.Info($"Load-ahead: {waiting} textures waiting; the last pass took {took.TotalMilliseconds:0} ms, " +
                 $"the longest of the last {_passes} {_longestPass.TotalMilliseconds:0} ms");
        (_passes, _longestPass) = (0, TimeSpan.Zero);
    }

    private bool Full(BuildKind kind) => Volatile.Read(ref _running[(int)kind]) >= kind switch
    {
        BuildKind.Base => BaseInFlight,
        BuildKind.Small => SmallInFlight,
        _ => _heavyLimit,
    };

    /// <summary>Which places fetching <paramref name="mip"/> takes: a base tile, a build of a few tiles, or of many.</summary>
    internal static BuildKind KindOf(StreamedTexture texture, int mip) =>
        mip >= texture.BaseMip ? BuildKind.Base
        : TextureStreamer.BuildTiles(texture.Name, texture.ZoomFor(mip)) <= SmallTiles ? BuildKind.Small
        : BuildKind.Heavy;

    private void Remove(Request request)
    {
        lock (_gate)
            if (_waiting.TryGetValue(request.Texture.Key, out Request current) && current == request) _waiting.Remove(request.Texture.Key);
    }

    /// <summary>
    /// Logs the progress when it changed: every few seconds while X-Plane loads scenery (new textures known), else
    /// every few minutes. The aircraft's moves queue every texture again, so the waiting ones are counted after a pass.
    /// </summary>
    private void ReportProgress()
    {
        int waiting;
        lock (_gate) waiting = _waiting.Count;
        var now = (Known: _catalog.Count, Waiting: waiting, Queued: Volatile.Read(ref _queued), AlreadyHad: Volatile.Read(ref _alreadyHad));
        if (now == _reported) return;
        TimeSpan interval = now.Known != _reported.Known ? LoadingReportInterval : QuietReportInterval;
        if (_clock.Elapsed - _reportedAt < interval) return;
        (_reported, _reportedAt) = (now, _clock.Elapsed);
        Log.Info($"Load-ahead: {Count(now.Known, "texture")} of the loaded scenery known, {now.Waiting} waiting for a place; " +
                 $"{Count(now.Queued, "build")} started so far, {now.AlreadyHad} had what they needed when X-Plane loaded them");
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    public void Stop()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _loop?.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        if (WarmsLoadedScenery)
        {
            _catalog.EntryAdded -= OnEntryAdded;
            _view.Moved -= OnMoved;
        }
        _stop.Dispose();
        _wake.Dispose();
    }
}
