namespace OrthoStreamXP.Scenery;

/// <summary>
/// The most the SimBrief prefetch (the departure and the ground pre-warm, every package together) may download in one
/// session (<c>simbrief-download-limit</c>, R4): an arrival, alternate and climb can mean thousands of tiles, and a provider
/// that rate-limits or blocks the program would hit every user. Spent by the prefetch's own builds only, in the
/// phases' order (their priorities); load-ahead and X-Plane's reads never count. Builds already running finish.
/// A reloaded new flight plan (a turnaround) starts it again: the limit is per flight plan within the session.
/// </summary>
internal sealed class PrefetchBudget(long limitBytes)
{
    private long _spent;
    private int _reported;
    private int _round;

    public long LimitBytes { get; } = limitBytes;

    public long SpentBytes => Interlocked.Read(ref _spent);

    public bool Exhausted => SpentBytes >= LimitBytes;

    /// <summary>How often it was started again (<see cref="Restart"/>): a package drops its requests once per round.</summary>
    public int Round => Volatile.Read(ref _round);

    /// <summary>A new flight plan: nothing spent yet, and the "used up" line may be logged again.</summary>
    public void Restart()
    {
        Interlocked.Exchange(ref _spent, 0);
        Interlocked.Exchange(ref _reported, 0);
        Interlocked.Increment(ref _round);
    }

    /// <summary>A prefetch build downloaded <paramref name="bytes"/> (from its build thread).</summary>
    public void Spend(long bytes) => Interlocked.Add(ref _spent, bytes);

    /// <summary>True the first time only: the "used up" line is logged once a round, whichever package sees it first.</summary>
    public bool FirstToReport() => Interlocked.Exchange(ref _reported, 1) == 0;
}
