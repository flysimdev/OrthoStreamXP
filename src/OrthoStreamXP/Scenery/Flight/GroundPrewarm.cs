namespace OrthoStreamXP.Scenery;

/// <summary>
/// What the ground pre-warm does now. <see cref="Stop"/>: it ran, and the aircraft rolls for takeoff.
/// <see cref="Rolled"/>: the aircraft rolls for takeoff before it ever ran (said once); only the departure's requests go.
/// </summary>
internal enum GroundDecision { None, Start, Stop, Rolled }

/// <summary>
/// When the ground pre-warm runs: once, after the aircraft has stood still at the flight plan's departure for a while
/// (an airliner spends 10-15 minutes there before pushback), until it rolls for takeoff. Taxiing does not stop it; a
/// position X-Plane did not send (placed from its reads) never starts it. Speed and on-ground are the aircraft's, as
/// X-Plane sends them (<c>groundspeed</c>, <c>onground_any</c>), so moving or lifting the camera in an outside view
/// changes nothing. Without them (an X-Plane that does not send them) it falls back to the speed worked out from the
/// positions and the camera's height, which an outside view lifts: then the height only keeps a paused aircraft in the
/// air from starting it.
/// </summary>
internal sealed class GroundWatch(double departureLat, double departureLon)
{
    public const double ParkedKt = 3, RollingKt = 40, OnGroundM = 300;
    /// <summary>Metres per second to knots.</summary>
    public const double KnotsPerMps = 1.943844;
    public static readonly TimeSpan ParkedFor = TimeSpan.FromSeconds(30);

    private enum State { Waiting, Running, Done }

    private State _state;
    private DateTime? _parkedSince;
    private bool _rolled;

    /// <param name="speedKt">Ground speed; null when X-Plane's positions have not moved enough to tell (standing still).</param>
    /// <param name="heightM">The camera's height above the ground (only used when <paramref name="onGround"/> is unknown).</param>
    /// <param name="onGround">X-Plane's on-ground flag; null when X-Plane does not send it.</param>
    public GroundDecision Update(double lat, double lon, double? speedKt, double heightM, bool inferred, DateTime at, bool? onGround = null)
    {
        double speed = speedKt ?? 0;
        switch (_state)
        {
            case State.Running:
                // Airborne without a takeoff roll seen (a position or speed missed) ends it too.
                if (speed <= RollingKt && (inferred || onGround != false)) return GroundDecision.None;
                _state = State.Done;
                return GroundDecision.Stop;
            case State.Waiting when !_rolled && !inferred && speed > RollingKt:
                // Not parked long enough to start (or not at the departure): the roll still ends the departure's requests.
                _rolled = true;
                _parkedSince = null;
                return GroundDecision.Rolled;
            case State.Waiting:
                bool parked = !inferred && speed <= ParkedKt && (onGround ?? heightM <= OnGroundM)
                              && Geo.DistanceM(departureLat, departureLon, lat, lon) <= FlightPlan.NearDepartureKm * 1000;
                if (!parked)
                {
                    _parkedSince = null;
                    return GroundDecision.None;
                }
                _parkedSince ??= at;
                if (at - _parkedSince < ParkedFor) return GroundDecision.None;
                _state = State.Running;
                return GroundDecision.Start;
            default:
                return GroundDecision.None;
        }
    }
}

/// <summary>A mounted package as the ground pre-warm sees it: its DSFs, and how to queue and drop textures in its load-ahead.</summary>
/// <param name="PrefetchAround">Queues the textures around an airport (lat, lon) at a phase, nearest first (<see cref="MipWarmer.PrefetchAround"/>).</param>
/// <param name="PrefetchAlong">Queues textures placed by (distance X-Plane will see them from, order) at a phase: the climb (<see cref="MipWarmer.PrefetchGround"/>).</param>
/// <param name="Cancel">Drops the ground pre-warm's textures still waiting; returns how many.</param>
/// <param name="CancelDeparture">Drops the SimBrief departure's textures still waiting; returns how many.</param>
/// <param name="Running">The builds running now, for the takeoff roll's log line (<see cref="MipWarmer.RunningSummary"/>).</param>
internal sealed record GroundTarget(string Name, string Source,
    Func<IReadOnlyList<VirtualPath>, double, double, int?, (int Queued, int Had)> PrefetchAround,
    Func<IReadOnlyList<VirtualPath>, Func<double, double, (double DistanceM, double Order)>, int, (int Queued, int Had)> PrefetchAlong,
    Func<int> Cancel, Func<int> CancelDeparture, Func<string>? Running = null);

/// <summary>
/// The ground pre-warm: while the aircraft stands at the departure, the scenery of the rest of the flight is built into
/// the cache, below everything X-Plane and the departure need: first the arrival and the alternate (where missing
/// photos are noticed first), each as X-Plane will want it from the gate there; then the climb from the departure to
/// the top of climb (<see cref="ClimbRoute"/>), each texture as X-Plane will want it when the aircraft passes.
/// </summary>
/// <param name="stop">The session's stop: no queuing after it, and a <see cref="Queue"/> in progress ends early.</param>
/// <param name="run">Runs the queuing off X-Plane's UDP thread (<see cref="Task.Run(Action)"/> when null; tests run it inline).</param>
/// <param name="alternate">The alternate is built too (<c>simbrief-alternate</c>); off, it is left out with a log line.</param>
internal sealed class GroundPrewarm(Task<FlightPlan?> plan, IReadOnlyList<GroundTarget> targets, Func<Action, Task>? run = null,
    bool alternate = true, CancellationToken stop = default)
{
    public const int Arrival = 0, Alternate = 1, Route = 2;

    private readonly Lock _gate = new();
    private Task<FlightPlan?> _plan = plan;
    private GroundWatch? _watch;
    private volatile bool _stopped;
    private bool _positionChecked;
    /// <summary>Counts the plans: a queuing for an earlier one stops (see <see cref="UsePlan"/>).</summary>
    private volatile int _generation;

    /// <summary>The queuing once the pre-warm started; the session awaits it before the packages go away.</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// A newer flight plan (the Home page's reload): watched from the start again, so the pre-warm runs for it once the
    /// aircraft stands at its departure. The caller drops the old plan's requests; a queuing for it still running stops.
    /// </summary>
    public void UsePlan(FlightPlan flight)
    {
        lock (_gate)
        {
            _plan = Task.FromResult<FlightPlan?>(flight);
            _watch = null;
            _positionChecked = false;
            _stopped = false;
            _generation++;
        }
    }

    /// <summary>Every position X-Plane sends (from its UDP thread): starts or stops the pre-warm when the watch says so.</summary>
    public void OnCamera(CameraReport camera)
    {
        lock (_gate) OnCameraLocked(camera);
    }

    private void OnCameraLocked(CameraReport camera)
    {
        if (stop.IsCancellationRequested || !_plan.IsCompletedSuccessfully || _plan.Result is not { } flight) return;
        if (!camera.Inferred && !_positionChecked)
        {
            _positionChecked = true;
            DropDepartureIfElsewhere(flight, camera);
        }
        _watch ??= new GroundWatch(flight.Origin.Lat, flight.Origin.Lon);
        double? speedKt = camera.GroundSpeedKt ?? camera.Motion?.SpeedMps * GroundWatch.KnotsPerMps;
        switch (_watch.Update(camera.Lat, camera.Lon, speedKt, camera.HeightAglM, camera.Inferred, camera.ReceivedUtc, camera.OnGround))
        {
            case GroundDecision.Start:
                int generation = _generation;
                Task queuing = (run ?? Task.Run)(() => QueueInBackground(flight, generation));
                Running = Running.IsCompleted ? queuing : Task.WhenAll(Running, queuing);
                break;
            case GroundDecision.Stop:
                _stopped = true;
                Cancel();
                break;
            case GroundDecision.Rolled:
                int dropped = DropDeparture();
                Log.Info($"SimBrief: the aircraft is rolling for takeoff; {dropped} departure textures still waiting were dropped, load-ahead takes over");
                break;
        }
    }

    /// <summary>
    /// <see cref="Queue"/> off the UDP thread. Never throws: a failure is logged (the pre-warm is a hint, never a reason
    /// for streaming to fail), and the session's stop ends it quietly.
    /// </summary>
    private void QueueInBackground(FlightPlan flight, int generation)
    {
        try
        {
            Queue(flight, generation);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return; // streaming stopped
        }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested)
        {
            return; // streaming stopped while a package was queuing
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Ground pre-warm failed ({ex.Message})");
        }
#pragma warning disable CA1031 // Anything else is a bug, but on a background thread nobody would see it: log it, don't lose it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Warning($"Ground pre-warm failed unexpectedly ({ex.GetType().Name}: {ex.Message})");
        }
        if (!_stopped && generation == _generation) return;
        // The aircraft rolled (or a newer plan came) while it was being queued: what went in after the cancel goes too.
        int late = DropGround();
        if (late > 0) Log.Info($"Ground pre-warm: {late} more textures queued during the takeoff roll were dropped");
    }

    /// <summary>Queues the three phases in every package; returns how many textures were queued.</summary>
    internal int Queue(FlightPlan flight) => Queue(flight, _generation);

    private int Queue(FlightPlan flight, int generation)
    {
        Log.Info($"Ground pre-warm: the aircraft stands at {Name(flight.Origin)}; building the rest of the flight's scenery until it rolls for takeoff");
        int queued = 0;
        foreach (GroundTarget target in targets)
        {
            stop.ThrowIfCancellationRequested();
            if (flight.Destination is { } to)
            {
                if (FlightPlan.SamePlace(to, flight.Origin))
                    Log.Info($"Ground pre-warm: the arrival {Name(to)} is the departure; its scenery is the departure's ({target.Name})");
                else
                    queued += Area(target, to, Arrival, "arrival", generation);
            }
            if (flight.Alternate is { } other)
            {
                if (!alternate)
                    Log.Info($"Ground pre-warm: the alternate {Name(other)} is left out (simbrief-alternate = off) ({target.Name})");
                else if (flight.Destination is { } arrival && FlightPlan.SamePlace(other, arrival))
                    Log.Info($"Ground pre-warm: the alternate {Name(other)} is the arrival; nothing more to build ({target.Name})");
                else if (FlightPlan.SamePlace(other, flight.Origin))
                    Log.Info($"Ground pre-warm: the alternate {Name(other)} is the departure; its scenery is the departure's ({target.Name})");
                else
                    queued += Area(target, other, Alternate, "alternate", generation);
            }
            queued += Climb(target, flight, generation);
        }
        return queued;
    }

    /// <summary>The aircraft rolled, or a newer plan replaced the one being queued: queue no more of it.</summary>
    private bool Stale(int generation) => _stopped || generation != _generation;

    private int Area(GroundTarget target, FlightOrigin airport, int phase, string what, int generation)
    {
        if (Stale(generation)) return 0;
        stop.ThrowIfCancellationRequested();
        IReadOnlyList<VirtualPath> textures = SceneryArea.Textures(target.Source, airport.Lat, airport.Lon, out int tiles);
        if (textures.Count == 0)
        {
            Log.Info($"Ground pre-warm: {target.Name} has no readable scenery tiles around the {what} {Name(airport)}");
            return 0;
        }
        var (queued, had) = target.PrefetchAround(textures, airport.Lat, airport.Lon, phase);
        Log.Info($"Ground pre-warm: {what} {Name(airport)} - {textures.Count} textures in {tiles} scenery tiles: {had} already have what X-Plane will want, {queued} queued ({target.Name})");
        return queued;
    }

    private int Climb(GroundTarget target, FlightPlan flight, int generation)
    {
        if (Stale(generation)) return 0;
        stop.ThrowIfCancellationRequested();
        ClimbRoute.Climb climb = ClimbRoute.Of(flight);
        if (climb.Samples.Count < 2)
        {
            Log.Info($"Ground pre-warm: the flight plan has no route from {Name(flight.Origin)}; the climb is left to load-ahead ({target.Name})");
            return 0;
        }
        IReadOnlyList<VirtualPath> textures = SceneryArea.TexturesIn(target.Source, ClimbRoute.Tiles(climb.Samples), out int tiles);
        var (queued, had) = target.PrefetchAlong(textures, (lat, lon) => ClimbRoute.Nearest(climb.Samples, lat, lon), Route);
        Log.Info($"Ground pre-warm: the climb to {climb.End} ({climb.Nm:0} NM, {climb.Minutes:0} min) - {textures.Count} textures in {tiles} scenery tiles: " +
                 $"{had} already have what X-Plane will want, {queued} queued ({target.Name})");
        return queued;
    }

    /// <summary>
    /// The first position X-Plane sends: an aircraft far from the plan's departure flies another flight (the plan is
    /// stale), so the departure's textures still waiting are dropped. Before X-Plane runs this is the only place check.
    /// </summary>
    private void DropDepartureIfElsewhere(FlightPlan flight, CameraReport camera)
    {
        double km = Geo.DistanceM(flight.Origin.Lat, flight.Origin.Lon, camera.Lat, camera.Lon) / 1000;
        if (km <= FlightPlan.NearDepartureKm) return;
        int dropped = DropDeparture();
        Log.Info($"SimBrief: the aircraft is {km:0} km from the flight plan's departure {Name(flight.Origin)}, so the plan is not this flight's; " +
                 $"{dropped} departure textures still waiting were dropped");
    }

    private void Cancel()
    {
        foreach (GroundTarget target in targets)
            if (target.Running is { } running) Log.Info($"Load-ahead at the takeoff roll: {running()} ({target.Name})");
        int dropped = DropGround(), departure = DropDeparture();
        Log.Info($"Ground pre-warm: the aircraft is rolling for takeoff; {dropped} textures still waiting were dropped " +
                 $"(and {departure} of the departure), load-ahead takes over");
    }

    /// <summary>Drops the ground pre-warm's textures still waiting in every package; returns how many.</summary>
    public int DropGround() => targets.Sum(t => t.Cancel());

    /// <summary>Drops the SimBrief departure's textures still waiting in every package; returns how many.</summary>
    public int DropDeparture() => targets.Sum(t => t.CancelDeparture());

    private static string Name(FlightOrigin airport) => airport.Icao.Length > 0 ? airport.Icao : $"{airport.Lat:F3}, {airport.Lon:F3}";
}
