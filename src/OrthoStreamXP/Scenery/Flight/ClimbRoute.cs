namespace OrthoStreamXP.Scenery;

/// <summary>
/// The part of a flight plan's route the ground pre-warm covers: from the departure along the navlog to the top of
/// climb, where X-Plane still wants real detail. At cruise altitude X-Plane reads only coarse levels, which load-ahead
/// keeps up with on its own. Sampled every <see cref="StepKm"/>, each sample with its height above the departure.
/// </summary>
internal static class ClimbRoute
{
    /// <summary>The climb ends at the earliest of the top of climb, this far along the route, and this much flight time.</summary>
    public const double MaxNm = 200, MaxMinutes = 25;
    public const double StepKm = 5;
    private const double KmPerNm = 1.852, MetresPerFoot = 0.3048;

    /// <summary>A point of the climb: where, how high above the departure, and how far along the route.</summary>
    internal readonly record struct Sample(double Lat, double Lon, double HeightM, double AlongKm);

    /// <summary>The climb's samples, what ended it, and how long it is.</summary>
    internal sealed record Climb(IReadOnlyList<Sample> Samples, string End, double Nm, double Minutes);

    public static Climb Of(FlightPlan plan)
    {
        FlightOrigin from = plan.Origin;
        var points = new List<(double Lat, double Lon, double HeightM, double Minutes)> { (from.Lat, from.Lon, 0, 0) };
        double maxKm = MaxNm * KmPerNm, along = 0;
        string end = "the end of the route";
        foreach (RoutePoint fix in plan.Route)
        {
            var previous = points[^1];
            double leg = Geo.DistanceM(previous.Lat, previous.Lon, fix.Lat, fix.Lon) / 1000;
            if (along == 0 && leg < 1) continue; // the departure airport itself, as the navlog's first point
            // A plan with stages but no TOC point: the climb ends where its last CLB point is.
            if (fix.Stage.Length > 0 && !fix.Stage.Equals("CLB", StringComparison.OrdinalIgnoreCase) && !IsTopOfClimb(fix))
            {
                end = "the end of the climb";
                break;
            }
            double height = Math.Max(0, fix.AltitudeFt - from.ElevationFt) * MetresPerFoot;
            // The caps cut the leg where they are reached (time only when the plan has times).
            double byDistance = along + leg > maxKm ? (maxKm - along) / leg : 1;
            double byTime = fix.Minutes > MaxMinutes && fix.Minutes > previous.Minutes ? (MaxMinutes - previous.Minutes) / (fix.Minutes - previous.Minutes) : 1;
            double f = Math.Clamp(Math.Min(byDistance, byTime), 0, 1);
            if (f < 1)
            {
                points.Add((previous.Lat + (fix.Lat - previous.Lat) * f, previous.Lon + (fix.Lon - previous.Lon) * f,
                    previous.HeightM + (height - previous.HeightM) * f, previous.Minutes + (fix.Minutes - previous.Minutes) * f));
                along += leg * f;
                end = byDistance <= byTime ? $"{MaxNm:0} NM" : $"{MaxMinutes:0} min";
                break;
            }
            points.Add((fix.Lat, fix.Lon, height, fix.Minutes));
            along += leg;
            if (IsTopOfClimb(fix))
            {
                end = "the top of climb";
                break;
            }
        }
        return new Climb(Sampled(points), points.Count > 1 ? end : "no route", along / KmPerNm, points[^1].Minutes);
    }

    private static bool IsTopOfClimb(RoutePoint fix) => fix.Ident.Equals("TOC", StringComparison.OrdinalIgnoreCase);

    /// <summary>The points every <see cref="StepKm"/> along the legs (the ends included).</summary>
    private static List<Sample> Sampled(List<(double Lat, double Lon, double HeightM, double Minutes)> points)
    {
        var samples = new List<Sample> { new(points[0].Lat, points[0].Lon, points[0].HeightM, 0) };
        double along = 0;
        for (int i = 1; i < points.Count; i++)
        {
            var (a, b) = (points[i - 1], points[i]);
            double leg = Geo.DistanceM(a.Lat, a.Lon, b.Lat, b.Lon) / 1000;
            int steps = Math.Max(1, (int)Math.Ceiling(leg / StepKm));
            for (int s = 1; s <= steps; s++)
            {
                double f = (double)s / steps;
                samples.Add(new Sample(a.Lat + (b.Lat - a.Lat) * f, a.Lon + (b.Lon - a.Lon) * f, a.HeightM + (b.HeightM - a.HeightM) * f, along + leg * f));
            }
            along += leg;
        }
        return samples;
    }

    /// <summary>The DSF tiles under the climb and one either side (every neighbour of a tile it crosses).</summary>
    public static IReadOnlyList<(int Lat, int Lon)> Tiles(IReadOnlyList<Sample> samples)
    {
        var tiles = new HashSet<(int, int)>();
        foreach (Sample s in samples)
        {
            int row = (int)Math.Floor(s.Lat), col = (int)Math.Floor(s.Lon);
            for (int r = row - 1; r <= row + 1; r++)
                for (int c = col - 1; c <= col + 1; c++)
                    if (r is >= -90 and < 90) tiles.Add((r, ((c + 180) % 360 + 360) % 360 - 180));
        }
        return [.. tiles];
    }

    /// <summary>
    /// How close the aircraft comes to a place on the climb (straight-line distance, its height included), and how far
    /// along the route that is: the level X-Plane will want there, and the order to fetch it in.
    /// </summary>
    public static (double DistanceM, double AlongKm) Nearest(IReadOnlyList<Sample> samples, double lat, double lon)
    {
        double best = double.MaxValue, at = 0;
        foreach (Sample s in samples)
        {
            double ground = Geo.DistanceM(s.Lat, s.Lon, lat, lon);
            double d = Math.Sqrt(ground * ground + s.HeightM * s.HeightM);
            if (d < best) (best, at) = (d, s.AlongKm);
        }
        return (best, at);
    }
}
