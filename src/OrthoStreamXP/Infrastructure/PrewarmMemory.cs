using System.Diagnostics;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP;

/// <summary>
/// Gives back the memory the ground pre-warm's builds left behind. In the EDDV to ENGM flight the working set stayed at
/// 4 GB (3.5 GB of it the large-object heap: build buffers) for the 24 minutes from the end of the pre-warm to the
/// takeoff, while the streamer held 0.9 GB of textures. One compacting collection once every package's pre-warm is
/// built, while the aircraft still stands at the gate, gives that back; at most once a session, off the warmer's thread.
/// <para>
/// The collection stops every thread of the process for its length, the file-system threads serving X-Plane included
/// (R1). So it waits until it is quiet: no build running in any package and no read from X-Plane for
/// <see cref="Timing.QuietFor"/> (10 s). If that does not happen within <see cref="Timing.GiveUpAfter"/> (10 min), or the aircraft rolls or new
/// pre-warm work comes first, it is skipped (and may run after the next pre-warm). The pause is logged, and the working
/// set again a little later, to see what the next builds take back.
/// </para>
/// </summary>
internal sealed class PrewarmMemory
{
    /// <summary>How long it waits for quiet, for how long, how often it looks, and when it logs the working set again.</summary>
    internal sealed record Timing(TimeSpan QuietFor, TimeSpan GiveUpAfter, TimeSpan Poll, TimeSpan LookAgainAfter)
    {
        public static readonly Timing Default = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
    }

    private readonly Timing _timing;

    private readonly IReadOnlyList<MipWarmer> _warmers;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _stop;
    private readonly Action _giveBack;
    private int _state; // 0 waiting for the pre-warm, 1 waiting for quiet, 2 given back

    internal PrewarmMemory(IReadOnlyList<MipWarmer> warmers, CancellationToken stop, Timing? timing = null, Action? giveBack = null, TimeProvider? clock = null)
    {
        _timing = timing ?? Timing.Default;
        _warmers = warmers;
        _stop = stop;
        _clock = clock ?? TimeProvider.System;
        _giveBack = giveBack ?? GiveBack;
    }

    /// <summary>Compacts once every one of <paramref name="warmers"/> has built its ground pre-warm and it is quiet.</summary>
    public static void Watch(IReadOnlyList<MipWarmer> warmers, CancellationToken stop)
    {
        var memory = new PrewarmMemory(warmers, stop);
        foreach (MipWarmer warmer in warmers) warmer.GroundDone += memory.OnGroundDone;
    }

    /// <summary>The task waiting for quiet, once the pre-warm is built (tests await it).</summary>
    internal Task Waiting { get; private set; } = Task.CompletedTask;

    internal void OnGroundDone()
    {
        if (_warmers.Any(w => w.GroundPending)) return; // another package still builds
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
        Waiting = Task.Run(WhenQuietAsync);
    }

    private async Task WhenQuietAsync()
    {
        try
        {
            if (await QuietAsync().ConfigureAwait(false) is not { } quiet)
            {
                Interlocked.Exchange(ref _state, 0); // the next pre-warm may try again
                return;
            }
            long before = Environment.WorkingSet, heap = GC.GetTotalMemory(false);
            TimeSpan paused = GC.GetTotalPauseDuration();
            long started = Stopwatch.GetTimestamp();
            _giveBack();
            TimeSpan took = Stopwatch.GetElapsedTime(started), pause = GC.GetTotalPauseDuration() - paused;
            Interlocked.Exchange(ref _state, 2);
            Log.Info($"Ground pre-warm: everything is built and X-Plane read nothing for {quiet.TotalSeconds:0} s; unused build memory given back " +
                     $"(working set {CacheInventory.FormatBytes(before)} -> {CacheInventory.FormatBytes(Environment.WorkingSet)}, " +
                     $".NET heap {CacheInventory.FormatBytes(heap)} -> {CacheInventory.FormatBytes(GC.GetTotalMemory(false))}; " +
                     $"every thread paused {pause.TotalMilliseconds:0} ms, {took.TotalMilliseconds:0} ms in all)");
            long readsThen = Reads(), buildsThen = Builds();
            await Task.Delay(_timing.LookAgainAfter, _clock, _stop).ConfigureAwait(false);
            Log.Info($"Ground pre-warm: {_timing.LookAgainAfter.TotalSeconds:0} s after the memory was given back: working set {CacheInventory.FormatBytes(Environment.WorkingSet)}, " +
                     $"{Reads() - readsThen} reads from X-Plane and {Builds() - buildsThen} builds since");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Streaming stopped: the stop gives the memory back anyway.
        }
    }

    /// <summary>
    /// Waits until nothing builds and X-Plane has read nothing for <see cref="Timing.QuietFor"/>; returns how long it was quiet,
    /// or null when it gave up (new pre-warm work, or no quiet within <see cref="Timing.GiveUpAfter"/>).
    /// </summary>
    private async Task<TimeSpan?> QuietAsync()
    {
        DateTimeOffset start = _clock.GetUtcNow(), quietSince = start;
        long reads = Reads();
        while (_clock.GetUtcNow() - start < _timing.GiveUpAfter)
        {
            await Task.Delay(_timing.Poll, _clock, _stop).ConfigureAwait(false);
            DateTimeOffset now = _clock.GetUtcNow();
            if (_warmers.Any(w => w.GroundPending)) return Skip("new pre-warm work came first");
            long readsNow = Reads();
            if (readsNow != reads || _warmers.Any(w => w.Building))
            {
                (reads, quietSince) = (readsNow, now);
                continue;
            }
            if (now - quietSince >= _timing.QuietFor) return now - quietSince;
        }
        return Skip($"X-Plane kept reading or building for {_timing.GiveUpAfter.TotalMinutes:0} min");
    }

    private static TimeSpan? Skip(string why)
    {
        Log.Info($"Ground pre-warm: the build memory is not given back now ({why}): the collection would pause X-Plane's reads");
        return null;
    }

    private long Reads() => _warmers.Sum(w => w.XPlaneReads);

    private long Builds() => _warmers.Sum(w => (long)w.Queued);

    /// <summary>
    /// The large build and texture arrays: an ordinary collection frees them but keeps the memory for reuse; an aggressive
    /// one gives it back to Windows (also used when streaming stops).
    /// </summary>
    public static void GiveBack() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
}
