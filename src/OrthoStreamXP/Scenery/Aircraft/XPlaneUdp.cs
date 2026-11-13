using System.Buffers.Binary;
using System.Text;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// The parts of X-Plane's UDP interface used to follow the aircraft (see "Exchanging Data with X-Plane"
/// in X-Plane's Instructions folder). X-Plane listens on UDP 49000 by default.
/// </summary>
internal static class XPlaneUdp
{
    public const int DefaultPort = 49000;
    public const string LatitudeDataref = "sim/flightmodel/position/latitude";
    public const string LongitudeDataref = "sim/flightmodel/position/longitude";
    public const int LatitudeIndex = 4001, LongitudeIndex = 4002;

    /// <summary>
    /// The camera, for the paging model's distances: the aircraft's height, and where the view is relative to the
    /// aircraft in X-Plane's local coordinates (x east, y up, z south, metres), plus the screen and field of view
    /// for the model's first guess, and the date for the seasons.
    /// </summary>
    public static readonly IReadOnlyList<(string Dataref, int Index)> CameraDatarefs =
    [
        ("sim/flightmodel/position/elevation", 4003),
        ("sim/flightmodel/position/y_agl", 4004),
        ("sim/flightmodel/position/local_x", 4005),
        ("sim/flightmodel/position/local_y", 4006),
        ("sim/flightmodel/position/local_z", 4007),
        ("sim/graphics/view/view_x", 4008),
        ("sim/graphics/view/view_y", 4009),
        ("sim/graphics/view/view_z", 4010),
        ("sim/graphics/view/field_of_view_deg", 4011),
        ("sim/graphics/view/window_width", 4012),
        // The sim's date, for the seasons: days since 1 January.
        ("sim/time/local_date_days", 4013),
        // The sun's height above the horizon in degrees: day, dusk and night in the log.
        ("sim/graphics/scenery/sun_pitch_degrees", 4014),
        // The aircraft's own ground speed (m/s) and whether it stands on the ground (1): the takeoff roll and the gate,
        // independent of where the camera is (an outside view moves and lifts it).
        ("sim/flightmodel/position/groundspeed", 4015),
        ("sim/flightmodel/failures/onground_any", 4016),
    ];

    public const int ElevationIndex = 4003, HeightAglIndex = 4004, LocalXIndex = 4005, ViewXIndex = 4008, FieldOfViewIndex = 4011, WindowWidthIndex = 4012,
        DateIndex = 4013, SunPitchIndex = 4014, GroundSpeedIndex = 4015, OnGroundIndex = 4016;

    /// <summary>The values that change rarely (screen, field of view, date): asked for once a second, not with every position.</summary>
    public static bool Rare(int index) => index is FieldOfViewIndex or WindowWidthIndex or DateIndex;

    /// <summary>
    /// An RREF request: X-Plane then sends the dataref's value <paramref name="timesPerSecond"/> times a second to
    /// the address the request came from, tagged with <paramref name="index"/>. 0 times a second unsubscribes.
    /// Layout: "RREF\0", int frequency, int index, 400-byte null-padded dataref name.
    /// </summary>
    public static byte[] SubscribeRequest(string dataref, int index, int timesPerSecond)
    {
        byte[] packet = new byte[5 + 4 + 4 + 400];
        "RREF\0"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(5), timesPerSecond);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(9), index);
        Encoding.ASCII.GetBytes(dataref, packet.AsSpan(13, 399));
        return packet;
    }

    /// <summary>The (index, value) pairs of an RREF answer: "RREF" + 1 byte, then 8 bytes per value.</summary>
    public static IEnumerable<(int Index, float Value)> ReadSubscribed(byte[] packet, int length)
    {
        if (length < 5 || (length - 5) % 8 != 0 || !packet.AsSpan(0, 4).SequenceEqual("RREF"u8)) yield break;
        for (int offset = 5; offset + 8 <= length; offset += 8)
            yield return (BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset)), BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(offset + 4)));
    }

    /// <summary>Latitude/longitude from a Data Output row 20 record ("DATA" + 1 byte, then 36-byte rows).</summary>
    public static (double Lat, double Lon)? ReadDataRow20(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 5 || !packet[..4].SequenceEqual("DATA"u8) || (packet.Length - 5) % 36 != 0) return null;
        for (int offset = 5; offset < packet.Length; offset += 36)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(packet[offset..]) != 20) continue;
            double lat = BinaryPrimitives.ReadSingleLittleEndian(packet[(offset + 4)..]);
            double lon = BinaryPrimitives.ReadSingleLittleEndian(packet[(offset + 8)..]);
            if (IsPosition(lat, lon)) return (lat, lon);
        }
        return null;
    }

    /// <summary>A real position: finite, in range, and not the 0/0 X-Plane reports before a flight is loaded.</summary>
    public static bool IsPosition(double lat, double lon) =>
        double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180 && (lat != 0 || lon != 0);
}
