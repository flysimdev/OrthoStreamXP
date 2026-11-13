using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OrthoStreamXP.Scenery;

/// <param name="Port">Local UDP port the position arrives on.</param>
/// <param name="XPlane">Where X-Plane listens for requests (127.0.0.1:49000 by default).</param>
internal sealed record PositionOptions(int Port, IPEndPoint XPlane, double RadiusKm, int MaxTextures, double LookaheadSeconds);

/// <summary>A mounted package's loaded textures, and how to queue the ones ahead of the aircraft.</summary>
internal sealed record PrefetchTarget(TextureCatalog Catalog, Func<IReadOnlyList<VirtualPath>, int> Prefetch);

/// <summary>
/// The latest aircraft position X-Plane sent. <paramref name="GroundSpeedKt"/>: X-Plane's own, else worked out from the
/// positions; null until it is known. <paramref name="OnGround"/>: X-Plane's on-ground flag; null when it does not send it.
/// </summary>
internal sealed record AircraftReport(double Lat, double Lon, double? GroundSpeedKt, DateTime ReceivedUtc, bool? OnGround = null);

/// <summary>
/// Follows the aircraft and prefetches the textures of the loaded scenery along its predicted path.
/// <para>
/// No X-Plane setup is needed: every few seconds this asks X-Plane (UDP 49000) to send it the aircraft's
/// latitude and longitude 10 times a second (an RREF subscription), so it works whether X-Plane starts
/// before or after the mount and after X-Plane restarts. Data Output row 20 sent to the same port is
/// understood too. Nothing happens until a position arrives; a new flight or a teleport is picked up from
/// the first report at the new place.
/// </para>
/// </summary>
/// <param name="targets">One per mounted package: the position arrives once, on one port, and serves them all.</param>
/// <param name="onPosition">Also told every position (load-ahead builds the textures nearest the aircraft first).</param>
/// <param name="onCamera">Told every position as the camera sees it (streaming's distance bands, the camera log).</param>
/// <param name="onScreen">Told X-Plane's window width and horizontal field of view (the paging model's first guess).</param>
/// <param name="onDate">Told the sim's day of the year with every position (the seasons).</param>
/// <param name="daylight">Offered the sun's height with every position (shared with the photo streaming); its own when null.</param>
internal sealed class AircraftPositionListener(IReadOnlyList<PrefetchTarget> targets, PositionOptions options,
    Action<double, double>? onPosition = null, Action<CameraReport>? onCamera = null, Action<double, double>? onScreen = null,
    Action<int>? onDate = null, Daylight? daylight = null)
    : IDisposable
{
    private const double RetriggerDistanceM = 1500;
    private const int PositionsPerSecond = 10;
    private static readonly TimeSpan SubscribeEvery = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _stop = new();
    private readonly MotionTracker _motion = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Socket? _socket;
    private Task? _loop;
    private (double Lat, double Lon)? _lastTrigger;
    private TimeSpan? _lastLog;
    private Task? _subscriber;
    private float? _subscribedLat, _subscribedLon;
    /// <summary>The latest camera dataref values, by index - <see cref="XPlaneUdp.ElevationIndex"/>.</summary>
    private readonly float?[] _camera = new float?[XPlaneUdp.CameraDatarefs.Count];
    private (float Width, float FieldOfView)? _screen;
    private bool _receiving;
    private readonly Daylight _daylight = daylight ?? new Daylight();
    private volatile AircraftReport? _latest;

    /// <summary>The latest position from X-Plane, or null when none has arrived this session.</summary>
    public AircraftReport? Latest => _latest;

    public void Start()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        // X-Plane on this PC: accept only local traffic. On another PC: its answers arrive from the network.
        _socket.Bind(new IPEndPoint(IPAddress.IsLoopback(options.XPlane.Address) ? IPAddress.Loopback : IPAddress.Any, options.Port));
        _loop = Task.Run(RunAsync);
        _subscriber = Task.Run(SubscribeAsync);
        Log.Info($"Asking X-Plane at {options.XPlane} for the aircraft position (no X-Plane Data Output setup needed); " +
                 $"answers arrive on UDP port {options.Port}. Prefetch starts with the first position.");
    }

    /// <summary>Repeats the subscription, so X-Plane started (or restarted) after the mount is picked up too.</summary>
    private async Task SubscribeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Subscribe(PositionsPerSecond);
                await Task.Delay(SubscribeEvery, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private void Subscribe(int timesPerSecond)
    {
        try
        {
            _socket!.SendTo(XPlaneUdp.SubscribeRequest(XPlaneUdp.LatitudeDataref, XPlaneUdp.LatitudeIndex, timesPerSecond), options.XPlane);
            _socket.SendTo(XPlaneUdp.SubscribeRequest(XPlaneUdp.LongitudeDataref, XPlaneUdp.LongitudeIndex, timesPerSecond), options.XPlane);
            if (onCamera is null && onScreen is null && onDate is null) return;
            foreach (var (dataref, index) in XPlaneUdp.CameraDatarefs)
            {
                // The screen and field of view change rarely: once a second is plenty.
                int rate = timesPerSecond == 0 ? 0 : XPlaneUdp.Rare(index) ? 1 : timesPerSecond;
                _socket.SendTo(XPlaneUdp.SubscribeRequest(dataref, index, rate), options.XPlane);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // X-Plane not running, or the socket is closing: the next round tries again.
        }
    }

    public void Stop()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        Subscribe(0); // ask X-Plane to stop sending
        _socket?.Dispose();
        _loop?.Wait(TimeSpan.FromSeconds(2));
        _subscriber?.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        byte[] buffer = new byte[4096];
        while (!_stop.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await _socket!.ReceiveAsync(buffer, SocketFlags.None, _stop.Token);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // an ICMP "port unreachable" echo; the socket is still fine
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }
            if (ReadPosition(buffer, received) is not { } position) continue;
            try
            {
                OnPosition(position.Lat, position.Lon);
            }
            catch (Exception ex)
            {
                Log.Error("Aircraft prefetch failed", ex);
            }
        }
    }

    private void OnPosition(double lat, double lon)
    {
        onPosition?.Invoke(lat, lon);
        TimeSpan now = _clock.Elapsed;
        bool jumped = _motion.Add(now, lat, lon);
        double? xplaneKt = GroundSpeedKt();
        bool? onGround = OnGround();
        _latest = new AircraftReport(lat, lon, xplaneKt ?? _motion.Current?.SpeedMps * GroundWatch.KnotsPerMps, DateTime.UtcNow, onGround);
        onCamera?.Invoke(Camera(lat, lon, jumped, xplaneKt, onGround));
        if (onDate is not null && Value(XPlaneUdp.DateIndex) is { } day) onDate((int)Math.Round(day));
        if (Value(XPlaneUdp.SunPitchIndex) is { } sun) _daylight.Offer(sun);
        if (onScreen is not null && Value(XPlaneUdp.WindowWidthIndex) is { } width && Value(XPlaneUdp.FieldOfViewIndex) is { } fov && _screen != (width, fov))
        {
            _screen = (width, fov);
            onScreen(width, fov);
        }
        if (!jumped && _lastTrigger is { } previous && Geo.DistanceM(previous.Lat, previous.Lon, lat, lon) < RetriggerDistanceM) return;
        _lastTrigger = (lat, lon);

        Motion? motion = _motion.Current;
        int selected = 0, queued = 0, known = 0;
        foreach (PrefetchTarget target in targets)
        {
            var candidates = PrefetchPlanner.Select(target.Catalog.Entries, lat, lon, motion, options.RadiusKm, options.LookaheadSeconds, options.MaxTextures);
            selected += candidates.Count;
            known += target.Catalog.Count;
            if (candidates.Count > 0) queued += target.Prefetch(candidates);
        }
        if (jumped || _lastLog is null || now - _lastLog >= TimeSpan.FromSeconds(5))
        {
            string moving = motion is { SpeedMps: >= PrefetchPlanner.MovingSpeedMps } m
                ? $"{m.SpeedMps * 1.943844:F0} kt, track {m.TrackDeg:F0}°, looking {m.SpeedMps * options.LookaheadSeconds / 1000:F1} km ahead"
                : "slow or stationary";
            Log.Detail($"Aircraft at {lat:F5}, {lon:F5} ({moving}{(jumped ? ", new location" : "")}): " +
                     $"{selected} of {known} known textures selected, {queued} queued or already available");
            _lastLog = now;
        }
    }

    private float? Value(int index) => _camera[index - XPlaneUdp.ElevationIndex];

    /// <summary>X-Plane's ground speed in knots; null until it sends one.</summary>
    private double? GroundSpeedKt() => Value(XPlaneUdp.GroundSpeedIndex) is { } mps && mps >= 0 ? mps * GroundWatch.KnotsPerMps : null;

    /// <summary>X-Plane's on-ground flag; null until it sends one.</summary>
    private bool? OnGround() => Value(XPlaneUdp.OnGroundIndex) is { } flag ? flag >= 0.5f : null;

    /// <summary>
    /// The camera: the aircraft position moved by the view's offset from the aircraft (x east, z south), its height
    /// the aircraft's above the ground plus the view's above the aircraft. Without those values, the aircraft itself.
    /// The aircraft's ground speed and on-ground flag go with it, as X-Plane sent them (null when it did not).
    /// </summary>
    private CameraReport Camera(double lat, double lon, bool jumped, double? groundSpeedKt, bool? onGround)
    {
        double east = 0, north = 0, up = 0;
        int local = XPlaneUdp.LocalXIndex, view = XPlaneUdp.ViewXIndex;
        if (Value(local) is { } ax && Value(local + 1) is { } ay && Value(local + 2) is { } az
            && Value(view) is { } vx && Value(view + 1) is { } vy && Value(view + 2) is { } vz)
            (east, up, north) = (vx - ax, vy - ay, -(vz - az));
        double camLat = lat + north / 111_320.0, camLon = lon + east / (111_320.0 * Math.Max(0.01, Math.Cos(lat * Math.PI / 180)));
        double height = Math.Max(0, (Value(XPlaneUdp.HeightAglIndex) ?? 0) + up);
        return new CameraReport(camLat, camLon, height, Value(XPlaneUdp.ElevationIndex), _motion.Current, jumped, DateTime.UtcNow,
            GroundSpeedKt: groundSpeedKt, OnGround: onGround);
    }

    /// <summary>A position from an answer to our subscription, or from Data Output row 20.</summary>
    private (double Lat, double Lon)? ReadPosition(byte[] packet, int length)
    {
        if (XPlaneUdp.ReadDataRow20(packet.AsSpan(0, length)) is { } row20) return row20;
        bool updated = false;
        foreach (var (index, value) in XPlaneUdp.ReadSubscribed(packet, length))
        {
            if (index == XPlaneUdp.LatitudeIndex) (_subscribedLat, updated) = (value, true);
            else if (index == XPlaneUdp.LongitudeIndex) (_subscribedLon, updated) = (value, true);
            else if (index - XPlaneUdp.ElevationIndex is >= 0 and var slot && slot < _camera.Length && float.IsFinite(value)) _camera[slot] = value;
        }
        if (!updated || _subscribedLat is not { } lat || _subscribedLon is not { } lon || !XPlaneUdp.IsPosition(lat, lon)) return null;
        if (!_receiving)
        {
            _receiving = true;
            Log.Step("X-Plane is connected: it sends the aircraft position");
        }
        return (lat, lon);
    }
}
