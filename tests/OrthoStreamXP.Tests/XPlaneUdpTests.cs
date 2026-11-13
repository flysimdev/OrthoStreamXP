using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Tests;

/// <summary>OrthoStreamXP asks X-Plane for the aircraft position itself, so nothing has to be set up in X-Plane.</summary>
public sealed class XPlaneUdpTests
{
    [Fact]
    public void A_subscription_request_has_XPlanes_layout()
    {
        byte[] packet = XPlaneUdp.SubscribeRequest("sim/flightmodel/position/latitude", 4001, 10);

        Assert.Equal(413, packet.Length);
        Assert.Equal("RREF\0"u8.ToArray(), packet[..5]);
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(5)));
        Assert.Equal(4001, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(9)));
        Assert.Equal("sim/flightmodel/position/latitude", Encoding.ASCII.GetString(packet, 13, 400).TrimEnd('\0'));
    }

    [Fact]
    public void Subscribed_values_are_read_from_the_answer()
    {
        byte[] answer = Answer((4001, 51.1f), (4002, 16.9f));
        Assert.Equal([(4001, 51.1f), (4002, 16.9f)], XPlaneUdp.ReadSubscribed(answer, answer.Length));
        Assert.Empty(XPlaneUdp.ReadSubscribed(answer, 7)); // truncated
    }

    [Theory]
    [InlineData(51.1, 16.9, true)]
    [InlineData(0, 0, false)]     // X-Plane before a flight is loaded
    [InlineData(91, 16.9, false)]
    public void Only_real_positions_count(double lat, double lon, bool real) => Assert.Equal(real, XPlaneUdp.IsPosition(lat, lon));

    [Fact]
    public async Task The_listener_subscribes_and_follows_what_XPlane_sends()
    {
        // A stand-in for X-Plane on a free local port: it answers every subscription with a position.
        using var xplane = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var xplaneAddress = (IPEndPoint)xplane.Client.LocalEndPoint!;
        var requests = new ConcurrentQueue<(string Dataref, int Frequency)>();
        using var stop = new CancellationTokenSource();
        Task fake = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult request;
                try
                {
                    request = await xplane.ReceiveAsync(stop.Token);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // an answer went to the listener after it had closed
                }
                byte[] r = request.Buffer;
                requests.Enqueue((Encoding.ASCII.GetString(r, 13, 400).TrimEnd('\0'), BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(5))));
                byte[] answer = Answer((XPlaneUdp.LatitudeIndex, 51.10872f), (XPlaneUdp.LongitudeIndex, 16.86611f));
                await xplane.SendAsync(answer, request.RemoteEndPoint, stop.Token);
            }
        }, stop.Token);

        var positions = new ConcurrentQueue<(double Lat, double Lon)>();
        var options = new PositionOptions(FreePort(), xplaneAddress, RadiusKm: 5, MaxTextures: 8, LookaheadSeconds: 180);
        using (var listener = new AircraftPositionListener([new PrefetchTarget(new TextureCatalog(), _ => 0)], options, (lat, lon) => positions.Enqueue((lat, lon))))
        {
            listener.Start();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (positions.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(positions.TryPeek(out var position), "no position arrived");
            Assert.Equal(51.10872, position.Lat, 4);
            Assert.Equal(16.86611, position.Lon, 4);
        } // stopping unsubscribes

        await Task.Delay(200, TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        Assert.Contains((XPlaneUdp.LatitudeDataref, 10), requests);
        Assert.Contains((XPlaneUdp.LongitudeDataref, 10), requests);
        Assert.Contains((XPlaneUdp.LatitudeDataref, 0), requests); // "stop sending"
        try { await fake; } catch (OperationCanceledException) { }
    }

    [Fact]
    public void The_aircrafts_ground_speed_and_on_ground_flag_are_asked_for_with_every_position()
    {
        // R6: X-Plane's own values for the gate and the takeoff roll; only the screen and the date are asked for once a second.
        Assert.Contains(("sim/flightmodel/position/groundspeed", XPlaneUdp.GroundSpeedIndex), XPlaneUdp.CameraDatarefs);
        Assert.Contains(("sim/flightmodel/failures/onground_any", XPlaneUdp.OnGroundIndex), XPlaneUdp.CameraDatarefs);
        Assert.False(XPlaneUdp.Rare(XPlaneUdp.GroundSpeedIndex));
        Assert.False(XPlaneUdp.Rare(XPlaneUdp.OnGroundIndex));
        Assert.True(XPlaneUdp.Rare(XPlaneUdp.FieldOfViewIndex) && XPlaneUdp.Rare(XPlaneUdp.WindowWidthIndex) && XPlaneUdp.Rare(XPlaneUdp.DateIndex));
        Assert.Equal(XPlaneUdp.CameraDatarefs.Count, XPlaneUdp.CameraDatarefs.Select(d => d.Index).Distinct().Count());
        Assert.All(XPlaneUdp.CameraDatarefs, d => Assert.InRange(d.Index, XPlaneUdp.ElevationIndex, XPlaneUdp.ElevationIndex + XPlaneUdp.CameraDatarefs.Count - 1));
    }

    [Fact]
    public async Task The_camera_report_carries_XPlanes_ground_speed_and_on_ground_flag()
    {
        using var xplane = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var xplaneAddress = (IPEndPoint)xplane.Client.LocalEndPoint!;
        using var stop = new CancellationTokenSource();
        Task fake = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult request;
                try
                {
                    request = await xplane.ReceiveAsync(stop.Token);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue;
                }
                byte[] answer = Answer((XPlaneUdp.GroundSpeedIndex, 10.29f), (XPlaneUdp.OnGroundIndex, 1f),
                    (XPlaneUdp.LatitudeIndex, 51.10872f), (XPlaneUdp.LongitudeIndex, 16.86611f));
                await xplane.SendAsync(answer, request.RemoteEndPoint, stop.Token);
            }
        }, stop.Token);

        var cameras = new ConcurrentQueue<CameraReport>();
        var options = new PositionOptions(FreePort(), xplaneAddress, RadiusKm: 5, MaxTextures: 8, LookaheadSeconds: 180);
        using (var listener = new AircraftPositionListener([new PrefetchTarget(new TextureCatalog(), _ => 0)], options, onCamera: cameras.Enqueue))
        {
            listener.Start();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (cameras.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(cameras.TryPeek(out CameraReport? camera), "no position arrived");
            Assert.Equal(20, camera.GroundSpeedKt!.Value, 0); // 10.29 m/s
            Assert.True(camera.OnGround);
            Assert.Equal(20, listener.Latest!.GroundSpeedKt!.Value, 0);
            Assert.True(listener.Latest.OnGround);
        }

        await stop.CancelAsync();
        try { await fake; } catch (OperationCanceledException) { }
    }

    private static byte[] Answer(params (int Index, float Value)[] values)
    {
        byte[] packet = new byte[5 + 8 * values.Length];
        "RREF,"u8.CopyTo(packet);
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(5 + 8 * i), values[i].Index);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(9 + 8 * i), values[i].Value);
        }
        return packet;
    }

    private static int FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
