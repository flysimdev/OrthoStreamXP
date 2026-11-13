using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// How X-Plane reads a DDS texture, as pure functions of the file layout and one handle's <see cref="ReadCursor"/>.
/// Static because it holds no state, and the streamer, the read-log calibration and the tests all apply it.
/// </summary>
internal static class ReadPattern
{
    /// <summary>The first chunk X-Plane reads of every texture: the header and the start of level 0.</summary>
    public const int HeaderChunk = 65536;

    /// <summary>
    /// The level X-Plane is after with this read, or null when it is after nothing new. X-Plane reads a texture in
    /// 64 KB chunks aligned to 64 KB: first the header chunk, then from the chunk that holds the start of the finest
    /// level it wants, to the end. So a read starting inside a level is one of three things: level 0 read on from
    /// exactly where the header chunk ended (only then does X-Plane want level 0); the next piece of a read (it
    /// starts where the latest read ended, or inside a level this handle has the start of); or the chunk before the
    /// level it wants, which begins with the tail of the finer one (level 1 is read from the chunk holding the last
    /// 128 bytes of level 0).
    /// </summary>
    /// <returns>The level X-Plane is after (or null), and the first level of the read that X-Plane is reading at all.</returns>
    internal static (int? Wanted, int Reading) WantedLevel(DdsLayout layout, ReadCursor cursor, long dataStart, long end, bool headerChunk)
    {
        int first = layout.MipAt(dataStart), last = layout.MipAt(end - 1);
        if (headerChunk) return (null, last + 1);
        if (dataStart == layout.Offset(first)) return (first, first);
        if (first == 0 && !cursor.Has(0) && dataStart == (cursor.HeaderChunkEnd ?? HeaderChunk)) return (0, 0);
        if (dataStart == cursor.LastEnd || cursor.Has(first)) return (null, first);
        return last > first ? (first + 1, first + 1) : (null, last + 1);
    }

    /// <summary>
    /// The coarsest level X-Plane can be after: its smallest read is the file's last 64 KB chunk, which begins inside a
    /// finer level whose tail it holds (<see cref="WantedLevel"/>), so it is after the level that follows. A 4096 px
    /// texture's last chunk starts in level 3: X-Plane takes at least level 4 (256 px) of every texture it loads, however
    /// far, which for a ZL18 texture is two levels finer than its base tile.
    /// </summary>
    internal static int CoarsestRead(DdsLayout layout)
    {
        long start = Math.Max(Dds.HeaderSize, (layout.Length - 1) / HeaderChunk * HeaderChunk);
        int first = layout.MipAt(start);
        return start == layout.Offset(first) ? first : Math.Min(first + 1, layout.Levels - 1);
    }

    /// <summary>Notes a read on the handle for <see cref="WantedLevel"/>: where it ended, and where the header chunk did.</summary>
    internal static void NoteRead(ReadCursor cursor, long end, bool headerChunk)
    {
        if (headerChunk) cursor.HeaderChunkEnd = end;
        cursor.LastEnd = end;
    }

    /// <summary>Whether a read is X-Plane's header chunk: from the start of the file, only a little of level 0.</summary>
    internal static bool IsHeaderChunk(DdsLayout layout, long offset, long end) =>
        offset < Dds.HeaderSize && end > Dds.HeaderSize && layout.MipAt(end - 1) == 0 && (end - Dds.HeaderSize) * 2 < layout.LengthOf(0);
}
