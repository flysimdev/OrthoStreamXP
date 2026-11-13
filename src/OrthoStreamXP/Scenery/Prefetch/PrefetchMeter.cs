using System.Collections.Concurrent;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// What one phase of the SimBrief prefetch (the departure, the arrival, the alternate, the climb) cost and brought: the
/// textures it queued or found built already, the builds it started (tiles, bytes, build time), and how many of the
/// textures it queued X-Plane loaded later (the hit rate). Logged when streaming stops; the numbers for the PR (R12)
/// and for the download budget (R4). Thread-safe: builds report from the build threads.
/// </summary>
/// <param name="ground">A phase of the ground pre-warm (dropped on the takeoff roll), not the departure.</param>
internal sealed class PrefetchMeter(string phase, bool ground)
{
    private readonly ConcurrentDictionary<string, byte> _queued = new(), _loaded = new();
    private long _had, _started, _built, _failed, _tiles, _bytes, _buildTicks;

    public string Phase { get; } = phase;

    public bool Ground { get; } = ground;

    /// <summary>The session's download budget its builds spend from; null when there is none.</summary>
    public PrefetchBudget? Budget { get; set; }

    public int Queued => _queued.Count;

    public long Had => Interlocked.Read(ref _had);

    public long Started => Interlocked.Read(ref _started);

    public long Built => Interlocked.Read(ref _built);

    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Tiles downloaded by its builds (the low-zoom base or the RAM cache had the rest).</summary>
    public long Tiles => Interlocked.Read(ref _tiles);

    public long Bytes => Interlocked.Read(ref _bytes);

    public TimeSpan BuildTime => TimeSpan.FromTicks(Interlocked.Read(ref _buildTicks));

    /// <summary>Of the textures it queued, how many X-Plane loaded afterwards.</summary>
    public int Loaded => _loaded.Count;

    public void NoteQueued(VirtualPath texture) => _queued.TryAdd(texture.Key, 0);

    public void NoteHad() => Interlocked.Increment(ref _had);

    public void NoteStarted() => Interlocked.Increment(ref _started);

    /// <summary>A build it started ended (from the build's thread).</summary>
    public void NoteBuild(bool ok, long tiles, long bytes, TimeSpan took)
    {
        Interlocked.Increment(ref ok ? ref _built : ref _failed);
        Interlocked.Add(ref _tiles, tiles);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _buildTicks, took.Ticks);
        Budget?.Spend(bytes);
    }

    /// <summary>X-Plane loaded a texture: a hit when this phase queued it.</summary>
    public void NoteLoaded(VirtualPath texture)
    {
        if (_queued.ContainsKey(texture.Key)) _loaded.TryAdd(texture.Key, 0);
    }

    /// <summary>The line logged at stop; null when the phase did nothing this session.</summary>
    public string? Summary()
    {
        if (Queued == 0 && Had == 0) return null;
        string hits = Queued > 0 ? $"X-Plane loaded {Loaded} of the {Queued} queued ({100.0 * Loaded / Queued:0} %)" : "nothing queued";
        return $"SimBrief prefetch, {Phase}: {Queued} textures queued, {Had} already had what X-Plane wants; " +
               $"{Started} builds started, {Built} built, {Failed} failed; {Tiles} tiles, {CacheInventory.FormatBytes(Bytes)} downloaded; " +
               $"{BuildTime.TotalMinutes:0.0} min of build time; {hits}";
    }
}
