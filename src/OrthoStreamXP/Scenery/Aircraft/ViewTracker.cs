namespace OrthoStreamXP.Scenery;

/// <summary>
/// Where X-Plane's camera is: the aircraft position moved by the view offset (outside views), with the camera's
/// height above the ground. <paramref name="Motion"/> is the aircraft's; <paramref name="Jumped"/> marks a new
/// flight or a teleport. <paramref name="Inferred"/>: placed from X-Plane's reads, not sent by X-Plane.
/// </summary>
internal sealed record CameraReport(double Lat, double Lon, double HeightAglM, double? ElevationM, Motion? Motion, bool Jumped, DateTime ReceivedUtc,
    bool Inferred = false, double? GroundSpeedKt = null, bool? OnGround = null);

/// <summary>
/// The latest camera position for the distance bands and the read log, and whether X-Plane is still starting a
/// flight (the loading screen and the time parked after it), when near and middle reads may wait longer.
/// </summary>
internal sealed class ViewTracker
{
    /// <summary>A position older than this is not trusted: X-Plane may be loading another flight.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);
    private const double MoveTriggerM = 1500;

    /// <summary>
    /// How long X-Plane's full-detail reads place the aircraft. All of them in that time are averaged: X-Plane reads
    /// them one scenery tile after another, so the first ones lie to one side (in a test the first 16 averaged 27 km
    /// off, all of the first minute's 0.6 km).
    /// </summary>
    private static readonly TimeSpan SharpReadsKept = TimeSpan.FromSeconds(60);
    private const int SharpReadsAveraged = 5000;

    private readonly Lock _gate = new();
    private readonly Queue<(double Lat, double Lon, DateTime At)> _sharpReads = new();
    private volatile CameraReport? _latest, _inferred;
    private (double Lat, double Lon)? _lastTrigger;
    private volatile bool _starting = true;

    /// <summary>The first position, a jump, and every 1.5 km after that.</summary>
    public event Action<CameraReport>? Moved;

    /// <summary>The latest position X-Plane sent.</summary>
    public CameraReport? Latest => _latest;

    /// <summary>
    /// The latest position X-Plane sent if it is recent; otherwise, for a minute after X-Plane last read a texture at
    /// full detail, the middle of those textures (see <see cref="NoteSharpRead"/>); otherwise null.
    /// </summary>
    public CameraReport? Current =>
        _latest is { } latest && DateTime.UtcNow - latest.ReceivedUtc < Fresh ? latest
        : _inferred is { } inferred && DateTime.UtcNow - inferred.ReceivedUtc < SharpReadsKept ? inferred
        : null;

    /// <summary>
    /// X-Plane read a texture at full detail: it is next to the aircraft (within about 20 km). Right after a flight's
    /// loading screen X-Plane reads the textures around the aircraft at full detail some seconds before it sends a
    /// position; until it does, the middle of the latest of them stands in for the camera, so load-ahead can fetch
    /// the textures around them before X-Plane asks.
    /// </summary>
    public void NoteSharpRead(double lat, double lon)
    {
        if (_latest is { } latest && DateTime.UtcNow - latest.ReceivedUtc < Fresh) return;
        CameraReport report;
        bool trigger;
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            _sharpReads.Enqueue((lat, lon, now));
            while (_sharpReads.Count > SharpReadsAveraged || now - _sharpReads.Peek().At > SharpReadsKept) _sharpReads.Dequeue();
            report = new CameraReport(_sharpReads.Average(r => r.Lat), _sharpReads.Average(r => r.Lon), 0, null, null, false, now, Inferred: true);
            bool first = _inferred is null;
            _inferred = report;
            trigger = _lastTrigger is not { } last || Geo.DistanceM(last.Lat, last.Lon, report.Lat, report.Lon) >= MoveTriggerM;
            if (trigger) _lastTrigger = (report.Lat, report.Lon);
            if (first) Log.Info($"X-Plane reads full detail around {report.Lat:F3}, {report.Lon:F3}: the aircraft is placed there until X-Plane sends its position");
        }
        if (trigger) Moved?.Invoke(report);
    }

    /// <summary>
    /// True from the start until the aircraft first moves faster than taxiing, and again while X-Plane loads another
    /// place (<see cref="NoteNearRead"/>) or after a jump: X-Plane then loads the scenery around a parked aircraft,
    /// and nothing next to it will be read again for a while.
    /// </summary>
    public bool Starting => _starting;

    /// <summary>
    /// X-Plane read a texture next to the aircraft. Without a recent position it is loading a flight or another place:
    /// it stops sending positions while it loads, and reads the textures next to the aircraft a minute or more before
    /// it sends the first position from there (test of 26 Oct: a teleport after a flight). So from here on it is
    /// starting again, and those reads may wait as at a start; positions coming back while flying end that.
    /// </summary>
    public void NoteNearRead()
    {
        if (_starting) return;
        CameraReport? latest = _latest;
        if (latest is not null && DateTime.UtcNow - latest.ReceivedUtc < Fresh) return;
        _starting = true;
        Log.Step(latest is null
            ? "X-Plane is loading a flight"
            : $"X-Plane is loading another place (no aircraft position for {(DateTime.UtcNow - latest.ReceivedUtc).TotalSeconds:F0} s)");
    }

    public void Update(CameraReport report)
    {
        bool trigger;
        lock (_gate)
        {
            // The first position after one placed from X-Plane's reads replaces that estimate: load-ahead plans again.
            bool replacesEstimate = _inferred is not null;
            _latest = report;
            _inferred = null;
            _sharpReads.Clear();
            if (report.Jumped) _starting = true;
            else if (report.Motion is { SpeedMps: >= PrefetchPlanner.MovingSpeedMps }) _starting = false;
            trigger = report.Jumped || replacesEstimate || _lastTrigger is not { } last
                      || Geo.DistanceM(last.Lat, last.Lon, report.Lat, report.Lon) >= MoveTriggerM;
            if (trigger) _lastTrigger = (report.Lat, report.Lon);
        }
        if (trigger) Moved?.Invoke(report);
    }

    /// <summary>Straight-line distance from the camera to a point on the ground, the camera's height included.</summary>
    public static double DistanceM(CameraReport camera, double lat, double lon)
    {
        double ground = Geo.DistanceM(camera.Lat, camera.Lon, lat, lon);
        return Math.Sqrt(ground * ground + camera.HeightAglM * camera.HeightAglM);
    }
}
