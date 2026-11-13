namespace OrthoStreamXP.Scenery;

/// <summary>A mounted package as the departure prefetch sees it: where its terrain definitions are, what its cache has, and its build queue.</summary>
/// <param name="IsCached">True when the full-detail texture is in the cache already (nothing to fetch).</param>
/// <param name="Prefetch">Queues whole textures ahead of X-Plane (the load-ahead's or the build scheduler's queue).</param>
/// <param name="PrefetchArea">
/// With mip streaming: queues every texture of the scenery around the departure, each to the level X-Plane will want
/// from there (<see cref="MipWarmer.PrefetchAround"/>); null when textures are built whole only.
/// </param>
internal sealed record DepartureTarget(string Name, string Source, Func<VirtualPath, bool> IsCached, Func<IReadOnlyList<VirtualPath>, int> Prefetch,
    Func<IReadOnlyList<VirtualPath>, double, double, (int Queued, int Had)>? PrefetchArea = null);

/// <summary>
/// Builds the scenery around the departure airport of the latest SimBrief flight plan as the mount starts, before
/// X-Plane loads it: X-Plane's first load at the gate then finds it in the cache. With mip streaming that is every
/// texture of the DSF tiles X-Plane loads there (<see cref="SceneryArea"/>), each to the level X-Plane will want;
/// otherwise (or when the package's DSFs cannot be read) the textures within <c>radiusKm</c> at full detail. What
/// the cache has already is left alone; X-Plane's own reads still come first in the download queue.
/// </summary>
/// <param name="aircraft">Where the aircraft is now, when X-Plane has sent a position.</param>
/// <param name="clock">The time now, for the plan's age (<see cref="FlightPlan.MaxAge"/>); the system's when null.</param>
internal sealed class DeparturePrefetch(Func<(double Lat, double Lon)?> aircraft, IReadOnlyList<DepartureTarget> targets, double radiusKm,
    TimeProvider? clock = null)
{
    /// <summary>The most textures queued per package: a 10 km circle holds a few hundred ZL18 textures at most.</summary>
    public const int MaxTextures = 600;

    /// <summary>Queues the departure textures of <paramref name="flight"/> the cache does not have; returns how many were queued.</summary>
    public int Run(FlightPlan? flight, CancellationToken ct)
    {
        if (flight is null) return 0;
        FlightOrigin from = flight.Origin;
        string place = from.Icao.Length > 0 ? from.Icao : $"{from.Lat:F3}, {from.Lon:F3}";
        DateTimeOffset now = (clock ?? TimeProvider.System).GetUtcNow();
        if (flight.StaleAt(now))
        {
            Log.Info($"SimBrief: the latest flight plan ({place}) is {flight.AgeAt(now)!.Value.TotalHours:0} h old; its departure is not prefetched");
            return 0;
        }
        if (aircraft() is { } at && Geo.DistanceM(at.Lat, at.Lon, from.Lat, from.Lon) > FlightPlan.NearDepartureKm * 1000)
        {
            Log.Info($"SimBrief: {place} is the latest flight plan's departure, but the aircraft is more than {FlightPlan.NearDepartureKm:0} km away; nothing prefetched");
            return 0;
        }

        int queued = 0;
        foreach (DepartureTarget target in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (target.PrefetchArea is { } prefetchArea)
            {
                IReadOnlyList<VirtualPath> area = SceneryArea.Textures(target.Source, from.Lat, from.Lon, out int tiles);
                if (area.Count > 0)
                {
                    var (areaQueued, had) = prefetchArea(area, from.Lat, from.Lon);
                    queued += areaQueued;
                    Log.Info($"SimBrief: {place} - {area.Count} textures in the {tiles} scenery tiles X-Plane loads there: " +
                             $"{had} already have what X-Plane will want, {areaQueued} queued ({target.Name})");
                    continue;
                }
                Log.Info($"SimBrief: {target.Name} has no readable scenery tiles around {place}; building what is within {radiusKm:0.#} km instead");
            }
            IReadOnlyList<VirtualPath> near = AirportTextures.Near(target.Source, from.Lat, from.Lon, radiusKm, MaxTextures);
            if (near.Count == 0)
            {
                Log.Info($"SimBrief: {target.Name} has no photo textures listed within {radiusKm:0.#} km of {place}");
                continue;
            }
            VirtualPath[] missing = [.. near.Where(t => !target.IsCached(t))];
            int cached = near.Count - missing.Length;
            if (missing.Length == 0)
            {
                Log.Info($"SimBrief: {place} - all {near.Count} textures within {radiusKm:0.#} km already cached ({target.Name})");
                continue;
            }
            int added = target.Prefetch(missing);
            queued += added;
            Log.Info($"SimBrief: {place} - {near.Count} textures within {radiusKm:0.#} km: {cached} already cached, {added} queued ({target.Name})");
        }
        return queued;
    }
}
