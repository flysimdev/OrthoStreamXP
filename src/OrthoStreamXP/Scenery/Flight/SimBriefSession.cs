using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// One streaming session's SimBrief side: the flight plan (fetched once as streaming starts, and again only when the
/// pilot presses "Reload SimBrief flight plan" on Home: no polling), the departure prefetch, and the ground pre-warm.
/// A reload with a newer plan drops the old plan's requests still waiting, prefetches the new departure when it moved,
/// and watches for the new flight's gate from the start. Failures are only logged: SimBrief never fails streaming.
/// </summary>
internal sealed class SimBriefSession
{
    private readonly Func<CancellationToken, Task<SimBriefAnswer>> _fetch;
    private readonly DeparturePrefetch _departure;
    private readonly CancellationToken _stop;
    private readonly PrefetchBudget? _budget;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly List<Task> _work = [];
    private FlightPlan? _current;
    private Task<string>? _reload;
    private string _firstNoPlan = "";

    /// <param name="fetch">Asks SimBrief for the latest flight plan (<see cref="SimBriefClient.FetchAsync"/>).</param>
    /// <param name="groundTargets">The packages the ground pre-warm queues in; empty: no ground pre-warm.</param>
    /// <param name="aircraft">Where the aircraft is now, when X-Plane has sent a position.</param>
    /// <param name="run">Runs the ground pre-warm's queuing (tests run it inline).</param>
    /// <param name="alternate">The ground pre-warm builds the alternate too (<c>simbrief-alternate</c>).</param>
    /// <param name="budget">The download limit (<c>simbrief-download-limit</c>); a reloaded new plan starts it again. Null: no limit.</param>
    public SimBriefSession(Func<CancellationToken, Task<SimBriefAnswer>> fetch, IReadOnlyList<DepartureTarget> departureTargets,
        IReadOnlyList<GroundTarget> groundTargets, Func<(double Lat, double Lon)?> aircraft, double radiusKm,
        TimeProvider? clock = null, Func<Action, Task>? run = null, bool alternate = false, PrefetchBudget? budget = null,
        CancellationToken stop = default)
    {
        _fetch = fetch;
        _budget = budget;
        _clock = clock ?? TimeProvider.System;
        _departure = new DeparturePrefetch(aircraft, departureTargets, radiusKm, _clock);
        _stop = stop;
        FirstPlan = FetchFirstAsync();
        if (groundTargets.Count > 0) Ground = new GroundPrewarm(FirstPlan, groundTargets, run, alternate, stop);
    }

    /// <summary>The plan fetched as streaming started (null when there was none).</summary>
    public Task<FlightPlan?> FirstPlan { get; }

    public GroundPrewarm? Ground { get; }

    /// <summary>The plan in use now: the first, or the latest reloaded.</summary>
    public FlightPlan? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    /// <summary>Starts the departure prefetch for the first plan, in the background.</summary>
    public void Start()
    {
        lock (_gate) _work.Add(PrefetchDepartureAsync(FirstPlan));
    }

    /// <summary>Every position X-Plane sends (from its UDP thread).</summary>
    public void OnCamera(CameraReport camera) => Ground?.OnCamera(camera);

    /// <summary>
    /// The Home page's "Reload SimBrief flight plan": asks SimBrief again and says what came of it, in the window's
    /// words. A press while a reload runs gets that reload's answer (one request).
    /// </summary>
    public Task<string> ReloadAsync()
    {
        lock (_gate)
        {
            if (_reload is { IsCompleted: false } running) return running;
            _reload = ReloadOnceAsync();
            _work.Add(_reload);
            return _reload;
        }
    }

    /// <summary>Everything still running in the background; the session awaits it before the packages go away.</summary>
    public Task WhenDone()
    {
        lock (_gate) return Task.WhenAll([.. _work, Ground?.Running ?? Task.CompletedTask]);
    }

    private async Task<FlightPlan?> FetchFirstAsync()
    {
        SimBriefAnswer answer = await FetchAsync().ConfigureAwait(false);
        if (answer.Problem is { } problem) Log.Warning($"SimBrief: {problem}");
        lock (_gate)
        {
            _current ??= answer.Plan;
            _firstNoPlan = answer.NoPlan;
        }
        return answer.Plan;
    }

    /// <summary>
    /// The plan fetched as streaming started, in the window's words ("Flight plan: EDDV → ENGM, generated 12 min ago."),
    /// or why there is none: Home shows it while the SimBrief switch is on.
    /// </summary>
    public async Task<string> DescribeFirstAsync()
    {
        try
        {
            if (await FirstPlan.ConfigureAwait(false) is not { } plan)
                lock (_gate) return _firstNoPlan;
            return $"Flight plan: {plan.Describe()}{Age(plan)}.";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return "Streaming stopped.";
        }
    }

    private async Task<string> ReloadOnceAsync()
    {
        try
        {
            await FirstPlan.ConfigureAwait(false); // its answer first: the reload compares against it
            SimBriefAnswer answer = await FetchAsync().ConfigureAwait(false);
            if (answer.Plan is not { } plan)
            {
                Log.Info($"SimBrief: reloading the flight plan: {answer.Problem}");
                return answer.NoPlan;
            }
            FlightPlan? old;
            lock (_gate)
            {
                old = _current;
                _current = plan;
            }
            string flight = plan.Describe();
            if (plan.SameOfp(old))
            {
                Log.Info($"SimBrief: reloaded the flight plan; unchanged ({flight})");
                return $"Unchanged: {flight} is still your latest flight plan{Age(plan)}.";
            }
            bool moved = old is null || !FlightPlan.SamePlace(old.Origin, plan.Origin);
            // A stale old plan had its departure skipped: a fresh one from the same airport still needs it built.
            bool buildDeparture = moved || old!.StaleAt(_clock.GetUtcNow());
            int ground = Ground?.DropGround() ?? 0;
            int departure = moved ? Ground?.DropDeparture() ?? 0 : 0;
            _budget?.Restart(); // a turnaround: the new flight gets the whole limit, whatever the last one spent
            Ground?.UsePlan(plan);
            Log.Info($"SimBrief: new flight plan {flight}{Age(plan)}; the old one's textures still waiting were dropped " +
                     $"({ground} of the ground pre-warm{(moved ? $", {departure} of the departure" : "")})" +
                     (_budget is null ? "" : $"; the download limit of {CacheInventory.FormatBytes(_budget.LimitBytes)} starts again"));
            if (buildDeparture) await PrefetchDepartureAsync(Task.FromResult<FlightPlan?>(plan)).ConfigureAwait(false);
            return $"Loaded {flight}{Age(plan)}." + (buildDeparture ? " Its departure is being built." : "");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return "Streaming stopped.";
        }
    }

    /// <summary>
    /// Asks SimBrief. The client answers its failures as a problem; anything it throws besides the stop (a bug) becomes
    /// one too, so neither the first plan nor a reload ever ends faulted (the stop awaits them).
    /// </summary>
    private async Task<SimBriefAnswer> FetchAsync()
    {
        try
        {
            return await _fetch(_stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The flight plan is a hint: whatever went wrong is logged as "no plan", never a reason for streaming to fail.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return SimBriefAnswer.Failed($"the flight plan could not be read ({ex.GetType().Name})");
        }
    }

    /// <summary>The departure prefetch for a plan once it is there; a failure is only logged.</summary>
    private async Task PrefetchDepartureAsync(Task<FlightPlan?> plan)
    {
        try
        {
            FlightPlan? flight = await plan.WaitAsync(_stop).ConfigureAwait(false);
            await Task.Run(() => _departure.Run(flight, _stop), _stop).ConfigureAwait(false); // reads DSFs: off the caller's thread
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Stopped before the flight plan arrived.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"SimBrief: departure prefetch failed ({ex.Message})");
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
            // Streaming stopped while a package was queuing.
        }
#pragma warning disable CA1031 // Anything else is a bug, but on a background task nobody would see it: log it, don't lose it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Warning($"SimBrief: departure prefetch failed unexpectedly ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>", generated 12 min ago" when the plan says when.</summary>
    private string Age(FlightPlan plan) => plan.AgeAt(_clock.GetUtcNow()) is not { } age ? ""
        : age < TimeSpan.FromHours(1) ? $", generated {Math.Max(0, age.TotalMinutes):0} min ago"
        : $", generated {age.TotalHours:0} h ago";
}
