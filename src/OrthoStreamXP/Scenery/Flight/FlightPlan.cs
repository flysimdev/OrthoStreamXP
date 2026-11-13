namespace OrthoStreamXP.Scenery;

/// <summary>An airport of a flight plan; <paramref name="ElevationFt"/> is 0 when the plan does not say.</summary>
internal sealed record FlightOrigin(string Icao, double Lat, double Lon, double ElevationFt = 0);

/// <summary>A point of the flight plan's navlog: where, how high (feet above sea level), when, and in which stage.</summary>
/// <param name="Minutes">Flight time from takeoff to here.</param>
/// <param name="Stage">SimBrief's stage: CLB, CRZ or DSC; empty when the plan has none.</param>
internal sealed record RoutePoint(string Ident, double Lat, double Lon, double AltitudeFt, double Minutes, string Stage);

/// <summary>The latest SimBrief flight plan as far as the scenery goes: the airports and the route.</summary>
/// <param name="Generated">When the pilot generated it (SimBrief's <c>params.time_generated</c>); null when the plan does not say.</param>
/// <param name="Id">SimBrief's id of the OFP (<c>params.request_id</c>): the same id is the same plan; null when the plan does not say.</param>
internal sealed record FlightPlan(FlightOrigin Origin, FlightOrigin? Destination, FlightOrigin? Alternate, IReadOnlyList<RoutePoint> Route,
    DateTimeOffset? Generated = null, string? Id = null)
{
    /// <summary>The same OFP as <paramref name="other"/>, by SimBrief's id, else by when it was generated; never when neither is known.</summary>
    public bool SameOfp(FlightPlan? other) =>
        other is not null && (Id is not null && other.Id is not null ? Id == other.Id
            : Generated is not null && Generated == other.Generated);

    /// <summary>
    /// An older plan is taken for an earlier flight: its departure is not prefetched. At mount start X-Plane usually
    /// sends no position yet, so the age is the only check there is.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>"EDDH → EGPH" (or the departure alone), as the window and the log name the flight.</summary>
    public string Describe()
    {
        string from = Origin.Icao.Length > 0 ? Origin.Icao : "your departure";
        return Destination is { Icao.Length: > 0 } to ? $"{from} → {to.Icao}" : from;
    }

    /// <summary>How long ago it was generated; null when unknown.</summary>
    public TimeSpan? AgeAt(DateTimeOffset now) => Generated is { } at ? now - at : null;

    /// <summary>Older than <see cref="MaxAge"/>: taken for an earlier flight, its departure is not prefetched.</summary>
    public bool StaleAt(DateTimeOffset now) => AgeAt(now) is { } age && age > MaxAge;

    /// <summary>
    /// An aircraft within this of the plan's departure is at it (the ground pre-warm starts there); farther away it flies
    /// another flight and the plan is not this flight's (the departure prefetch is skipped or dropped).
    /// </summary>
    public const double NearDepartureKm = 50;

    /// <summary>Two airports this close share their scenery (the 3 x 4 tiles X-Plane loads round each are the same).</summary>
    public const double SamePlaceKm = 10;

    public static bool SamePlace(FlightOrigin a, FlightOrigin b) =>
        (a.Icao.Length > 0 && a.Icao.Equals(b.Icao, StringComparison.OrdinalIgnoreCase))
        || Geo.DistanceM(a.Lat, a.Lon, b.Lat, b.Lon) < SamePlaceKm * 1000;
}
