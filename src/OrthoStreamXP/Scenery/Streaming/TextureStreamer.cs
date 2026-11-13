using System.Collections.Concurrent;
using System.Diagnostics;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Serves photo DDS textures the way X-Plane reads them: the header at once, then each read's byte range mapped to
/// mip levels, answered with the best data at that moment. Real data is fetched at exactly the zoom the level
/// needs (level 4 of a ZL16 texture is one ZL12 tile, not 256 ZL16 tiles). How long a read may wait depends on its
/// band: next to the aircraft it waits for real imagery (long at the start of a flight, which X-Plane will not
/// read again while the aircraft is parked), in the middle distance briefly, and far away not at all. A read
/// that cannot wait gets a stand-in, stretched from the best coarser imagery at hand (the low-zoom base, or one
/// flat colour when there is nothing), and the real data is fetched behind it for X-Plane's next read.
/// <para>
/// The file always has its full size, header and every level: a smaller texture, a short read or an error would
/// make X-Plane treat the texture as final. Level 0 complete is saved as a normal cached DDS (served as a plain
/// file from then on); coarser real chains are saved under .xpstream\mips; the low-zoom base holds the tiles.
/// </para>
/// <para>
/// With the photos skipped at night (<see cref="PhotosPaused"/>), nothing finer than the low-zoom base is fetched
/// while the sun in X-Plane is below <see cref="Daylight.DarkBelow"/> (skip-photos-sun-below): X-Plane's reads and load-ahead alike get the
/// base, stretched, and what is already cached is served as it is. In twilight the photos stream as usual.
/// </para>
/// </summary>
internal sealed class TextureStreamer : IDisposable
{
    /// <summary>X-Plane wanting level 0 or 1 means the texture is next to the camera.</summary>
    public const int NearMips = 1;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(15);
    /// <summary>The colour of a level when nothing of the place is at hand: a muted green-brown, like most land from afar.</summary>
    private static readonly (byte R, byte G, byte B) FlatColor = (96, 102, 80);

    private readonly SceneryRoots _roots;
    private readonly TextureBuilder _builder;
    private readonly ViewTracker _view;
    private readonly PagingModel _model;
    private readonly StreamingOptions _options;
    private readonly int _baseZoom;
    private readonly ConcurrentDictionary<string, StreamedTexture> _textures = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _shutdown = new();
    /// <summary>Builds of more than the base tile, by urgency and within the build memory: X-Plane's reads next to the aircraft first.</summary>
    private readonly WeightedPriorityGate _detailed;
    /// <summary>Builds done ahead of X-Plane: half of the tiles and half of the places at most, so its reads always find room.</summary>
    private readonly WeightedPriorityGate _ahead;
    private readonly PagingMeasurements _paging = new();
    private readonly ReadWaitStats _waitStats = new();
    /// <summary>X-Plane's sun when the photos are skipped at night; null streams them whatever the time.</summary>
    private readonly Daylight? _nightPause;
    private long _ramBytes;
    private int _trimming;
    private long _reads, _real, _standIn, _flat, _headerChunks, _readBuilds, _aheadBuilds, _fullBuilds, _failedBuilds, _nightReads;

    /// <param name="nightPause">X-Plane's sun, to skip the photos at night (only the low-zoom base); null streams them always.</param>
    public TextureStreamer(SceneryRoots roots, TextureBuilder builder, ViewTracker view, PagingModel model, StreamingOptions options,
        Daylight? nightPause = null)
    {
        _nightPause = nightPause;
        _roots = roots;
        _builder = builder;
        _view = view;
        _model = model;
        _options = options;
        _baseZoom = builder.Source.Store?.MaxZoom ?? TileStore.DefaultMaxZoom;
        _detailed = new WeightedPriorityGate(options.BuildMemoryBytes, options.MaxBuilds, secondBudget: options.BuildTiles);
        // Load-ahead runs dozens of small builds at once: without its own limit they could take every place.
        _ahead = new WeightedPriorityGate(AheadTiles(options.BuildTiles), Math.Max(1, options.MaxBuilds / 2));
    }

    public const int DefaultMaxBuilds = 64;
    public const int DefaultBuildTiles = 32768;

    /// <summary>The share of the tiles builds done ahead of X-Plane may use.</summary>
    public static long AheadTiles(long buildTiles) => Math.Max(1, buildTiles / 2);

    /// <summary>The tiles a build of <paramref name="name"/> downloads at <paramref name="zoom"/>: 16 x 16 at its own zoom, four times as many per zoom finer.</summary>
    public static long BuildTiles(TextureName name, int zoom)
    {
        int shift = zoom - name.Zoom;
        long span = shift >= 0 ? (long)Tiles.TextureSpan << shift : Math.Max(1, Tiles.TextureSpan >> -shift);
        return span * span;
    }

    /// <summary>
    /// The memory a build of a <paramref name="sizePx"/> level holds while its tiles arrive: the picture and its resize
    /// and encode buffers, about 1.5 x size² x 3 bytes (1.2 MB at 512 px, 19 MB at 2048 px, 75 MB at 4096).
    /// </summary>
    public static long BuildWeight(int sizePx) => (long)sizePx * sizePx * 9 / 2;

    public PagingModel Model => _model;

    /// <summary>Night in X-Plane with the photos skipped at night: nothing finer than the low-zoom base is fetched now.</summary>
    public bool PhotosPaused => _nightPause?.Dark == true;

    /// <summary>Reads of texture data answered (not counting header-only reads).</summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>Of those, the reads answered with real imagery at every level read.</summary>
    public long RealReads => Interlocked.Read(ref _real);

    /// <summary>Reads answered with a stand-in or a flat colour somewhere: what the flight report marks on the route.</summary>
    public long StandInReads => Interlocked.Read(ref _standIn) + Interlocked.Read(ref _flat);

    /// <summary>Builds finished this session, for X-Plane's reads and ahead of them.</summary>
    public long BuildsDone => Interlocked.Read(ref _readBuilds) + Interlocked.Read(ref _aheadBuilds);

    /// <summary>Builds queued or running.</summary>
    public int Building => _textures.Values.Sum(t =>
    {
        lock (t.Gate) return t.Builds.Count;
    });

    /// <summary>The texture behind a path, or null when it is not a photo texture (a tile name under textures\).</summary>
    public StreamedTexture? Get(VirtualPath path)
    {
        if (!path.IsDds || path.Parts.Length != 2 || !TextureName.TryParseFile(path.Name, out TextureName name)) return null;
        return _textures.GetOrAdd(path.Key, _ => new StreamedTexture(path, name, _roots.Textures, _baseZoom));
    }

    /// <summary>The time of the texture's latest real data, as the file's write time (WinFsp drops cached pages when it changes).</summary>
    public static DateTime VersionOf(StreamedTexture texture)
    {
        lock (texture.Gate) return texture.Version;
    }

    // ---------------------------------------------------------------- reads

    /// <summary>Answers one read: never an error and never short, whatever happens behind it.</summary>
    public ReadAnswer Read(StreamedTexture t, ReadCursor cursor, long offset, Span<byte> buffer)
    {
        DdsLayout layout = t.Layout;
        if (offset >= layout.Length) return new ReadAnswer(0);
        int count = (int)Math.Min(buffer.Length, layout.Length - offset);
        Span<byte> output = buffer[..count];
        long end = offset + count;
        if (offset < Dds.HeaderSize) layout.Header[(int)offset..(int)Math.Min(end, Dds.HeaderSize)].CopyTo(output);
        if (end <= Dds.HeaderSize)
        {
            cursor.LastEnd = end;
            return new ReadAnswer(count);
        }

        long dataStart = Math.Max(offset, Dds.HeaderSize);
        int first = layout.MipAt(dataStart), last = layout.MipAt(end - 1);
        try
        {
            return ReadLevels(t, cursor, offset, end, first, last, output);
        }
        catch (Exception ex)
        {
            Log.Error($"Streaming {t.Path.Display} failed; X-Plane gets one flat colour for this read", ex);
            FillFlat(output[(int)(dataStart - offset)..], dataStart);
            return new ReadAnswer(count, first, last, Served: Served.Flat);
        }
    }

    private ReadAnswer ReadLevels(StreamedTexture t, ReadCursor cursor, long offset, long end, int first, int last, Span<byte> output)
    {
        DdsLayout layout = t.Layout;
        // The header chunk gets the best data at hand without waiting, and does not fix level 0's version: X-Plane
        // wants level 0 only if it reads on from there, and then that read waits for it.
        bool headerChunk = ReadPattern.IsHeaderChunk(layout, offset, end);
        long dataStart = Math.Max(offset, Dds.HeaderSize);
        var (wanted, counted) = ReadPattern.WantedLevel(layout, cursor, dataStart, end, headerChunk);
        ReadPattern.NoteRead(cursor, end, headerChunk);

        Band? band = null;
        double? distance = null;
        double waitedMs = 0;
        bool keptToBase = false;
        if (wanted is int mip)
        {
            // Full detail wanted: the texture is next to the aircraft, which places it before X-Plane sends a position.
            if (mip == 0) _view.NoteSharpRead(t.Lat, t.Lon);
            // Before the wait is chosen: a read next to the aircraft without a recent position means X-Plane is loading.
            if (mip <= NearMips) _view.NoteNearRead();
            (band, distance) = Classify(t, mip);
            // A distance from the estimated position helps the band, but it is no measurement: not for k, not for the log.
            if (_view.Current is { Inferred: true }) distance = null;
            _paging.Note(t, mip, distance);
            int realFrom;
            lock (t.Gate)
            {
                Probe(t);
                realFrom = t.RealFrom;
            }
            // At night with the photos skipped, the low-zoom base is the finest level fetched: finer ones are stretched from it.
            keptToBase = cursor.KeptToBase = mip < t.BaseMip && PhotosPaused;
            int target = keptToBase ? t.BaseMip : mip;
            if (target < realFrom)
            {
                Task<bool> build = StartBuild(t, Math.Min(target, t.BaseMip), band switch
                {
                    Band.Near => FetchPriority.NearRead,
                    Band.Middle => FetchPriority.ForRead(mip),
                    _ => FetchPriority.Background,
                }, byRead: true);
                // While a flight starts the aircraft is parked and X-Plane reads none of these textures again for a
                // while: a stand-in then stays blurry, so near and middle reads alike may wait near-wait.
                bool starting = band is Band.Near or Band.Middle && _view.Starting;
                double wait = band switch
                {
                    Band.Near or Band.Middle => starting ? _options.NearWaitSeconds : _options.MaxWaitSeconds,
                    _ => 0,
                };
                if (wait > 0 && !build.IsCompleted)
                {
                    long waitStarted = Stopwatch.GetTimestamp();
                    bool done = build.Wait(TimeSpan.FromSeconds(wait));
                    TimeSpan waited = Stopwatch.GetElapsedTime(waitStarted);
                    waitedMs = waited.TotalMilliseconds;
                    // A wait for the base at night says nothing about how long the photos take: the tuning never sees it.
                    if (!keptToBase) NoteWait(band!.Value, starting, waitStarted, waited, done, build);
                }
            }
        }

        // The next piece of a read kept to the base at night is counted with it.
        else if (!headerChunk) keptToBase = cursor.KeptToBase;

        // A level X-Plane is not reading (the header chunk's start of level 0, or the tail before the level it wants)
        // is filled from the best data at hand; it neither fixes the level's version nor counts in the statistics.
        Served served = Served.Real, any = Served.Real;
        for (int m = first; m <= last; m++)
        {
            long levelStart = layout.Offset(m), levelEnd = levelStart + layout.LengthOf(m);
            long from = Math.Max(dataStart, levelStart), to = Math.Min(end, levelEnd);
            ReadOnlyMemory<byte> bytes;
            Served kind;
            if (from != levelStart && cursor.TryGet(m, out ReadOnlyMemory<byte> earlier, out Served earlierKind))
            {
                (bytes, kind) = (earlier, earlierKind);
            }
            else
            {
                (bytes, kind) = headerChunk ? LevelStart(t, m, (int)(to - levelStart)) : Level(t, m);
                if (m >= counted) cursor.Set(m, bytes, kind);
            }
            if ((m >= counted || cursor.Has(m)) && kind > served) served = kind;
            if (kind > any) any = kind;
            bytes.Span.Slice((int)(from - levelStart), (int)(to - from)).CopyTo(output[(int)(from - offset)..]);
        }
        if (headerChunk)
        {
            Interlocked.Increment(ref _headerChunks);
            return new ReadAnswer(output.Length, first, last, Served: any);
        }
        // A read kept to the base at night is blurry by choice: counted apart, so the Home page's share of real reads,
        // the flight report and the tuning see only the reads that wanted the photo.
        if (keptToBase && served != Served.Real) Interlocked.Increment(ref _nightReads);
        else
        {
            Interlocked.Increment(ref _reads);
            if (served == Served.Real) Interlocked.Increment(ref _real);
            else if (served == Served.StandIn) Interlocked.Increment(ref _standIn);
            else Interlocked.Increment(ref _flat);
        }
        TrimIfNeeded();
        return new ReadAnswer(output.Length, first, last, band, served, waitedMs, distance, wanted);
    }

    /// <summary>How long a read waited for its level, and how long its picture took when the wait ran out first.</summary>
    private void NoteWait(Band band, bool starting, long waitStarted, TimeSpan waited, bool done, Task<bool> build)
    {
        _waitStats.Record(band, starting, waited, done);
        if (done) _waitStats.NoteNeeded(starting, waited);
        else _ = build.ContinueWith(b =>
        {
            if (b.IsCompletedSuccessfully && b.Result) _waitStats.NoteNeeded(starting, Stopwatch.GetElapsedTime(waitStarted));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>X-Plane's reads at night that got the low-zoom base, stretched, because the photos were skipped.</summary>
    public long NightReads => Interlocked.Read(ref _nightReads);

    /// <summary>
    /// The band of a read of <paramref name="mip"/>. The level X-Plane asks for is its own verdict on the distance;
    /// the camera's distance can only make a read nearer, never farther, so a stale position (X-Plane still sending
    /// the previous flight's while it loads the next) cannot turn a texture beside the aircraft into a stand-in.
    /// </summary>
    internal (Band Band, double? DistanceM) Classify(StreamedTexture t, int mip)
    {
        Band byRead = BandOf(t, mip);
        if (_view.Current is not { } camera) return (byRead, null);
        double distance = ViewTracker.DistanceM(camera, t.Lat, t.Lon);
        Band byDistance = BandOf(t, _model.MipFor(distance, t.SizeM, t.Layout.Size));
        return (byDistance < byRead ? byDistance : byRead, distance);
    }

    /// <summary>
    /// A read of a texture X-Plane gets as a finished file (complete in the cache, served as it is): nothing to
    /// answer, but the same paging evidence as a streamed read, so the level it asks for is noted like one (the
    /// measurement of k, the aircraft's place) and logged. Counted as a real read.
    /// </summary>
    public ReadAnswer NoteFileRead(StreamedTexture t, ReadCursor cursor, long offset, int length)
    {
        DdsLayout layout = t.Layout;
        long end = offset + length;
        if (end <= Dds.HeaderSize || offset >= layout.Length)
        {
            cursor.LastEnd = end;
            return new ReadAnswer(length);
        }
        bool headerChunk = ReadPattern.IsHeaderChunk(layout, offset, end);
        long dataStart = Math.Max(offset, Dds.HeaderSize);
        var (wanted, _) = ReadPattern.WantedLevel(layout, cursor, dataStart, end, headerChunk);
        ReadPattern.NoteRead(cursor, end, headerChunk);
        int first = layout.MipAt(dataStart), last = layout.MipAt(Math.Min(end, layout.Length) - 1);
        if (headerChunk)
        {
            Interlocked.Increment(ref _headerChunks);
            return new ReadAnswer(length, first, last, Served: Served.Real);
        }
        Band? band = null;
        double? distance = null;
        if (wanted is int mip)
        {
            if (mip == 0) _view.NoteSharpRead(t.Lat, t.Lon);
            if (mip <= NearMips) _view.NoteNearRead();
            (band, distance) = Classify(t, mip);
            if (_view.Current is { Inferred: true }) distance = null;
            _paging.Note(t, mip, distance);
        }
        Interlocked.Increment(ref _reads);
        Interlocked.Increment(ref _real);
        return new ReadAnswer(length, first, last, band, Served.Real, 0, distance, wanted);
    }

    /// <summary>The band of a texture whose level <paramref name="mip"/> is wanted.</summary>
    public static Band BandOf(StreamedTexture t, int mip) => mip <= NearMips ? Band.Near : mip >= t.BaseMip ? Band.Far : Band.Middle;

    /// <summary>A level's bytes: real when the texture has real data there, otherwise a stand-in.</summary>
    private (ReadOnlyMemory<byte> Bytes, Served Kind) Level(StreamedTexture t, int mip)
    {
        lock (t.Gate)
        {
            t.LastUsed = Stopwatch.GetTimestamp();
            Probe(t);
            if (mip >= t.RealFrom && EnsureChain(t) is { } chain)
            {
                DdsLayout layout = t.Layout;
                int start = (int)(layout.Offset(mip) - layout.Offset(t.ChainFrom));
                return (chain.AsMemory(start, layout.LengthOf(mip)), Served.Real);
            }
            var (bytes, kind) = StandIn(t, mip);
            return (bytes, kind);
        }
    }

    /// <summary>
    /// The first <paramref name="length"/> bytes of a level, for X-Plane's header chunk: real data or a stand-in already
    /// made as they are, otherwise only the top block rows of the stand-in, which are kept nowhere. X-Plane reads the
    /// header chunk of every texture it loads, but reads on into level 0 only beside the aircraft; a whole 4096 px
    /// stand-in for those 32 rows (48 MB of pixels, 280 ms of CPU) made a scenery load saturate every core.
    /// </summary>
    private (ReadOnlyMemory<byte> Bytes, Served Kind) LevelStart(StreamedTexture t, int mip, int length)
    {
        lock (t.Gate)
        {
            Probe(t);
            if (mip >= t.RealFrom || (t.StandInSource == t.RealFrom && t.StandIns.ContainsKey(mip))) return Level(t, mip);
            t.LastUsed = Stopwatch.GetTimestamp();
            int size = t.Layout.SizeOf(mip), blockRowBytes = Math.Max(1, (size + 3) / 4) * 8;
            int rows = Math.Min(size, (length + blockRowBytes - 1) / blockRowBytes * 4);
            if (StandInImage(t) is { } image)
                return (Dds.EncodeRows(_builder.ResizeTop(image.Rgb, image.Size, size, rows), size, rows, _builder.EncoderWorkers), Served.StandIn);
            return (Dds.FlatBlocks(length, FlatColor.R, FlatColor.G, FlatColor.B), Served.Flat);
        }
    }

    // ---------------------------------------------------------------- the mip index

    /// <summary>Looks in the cache once for the texture's real data: a whole DDS, a saved chain, or the low-zoom base.</summary>
    private void Probe(StreamedTexture t)
    {
        if (t.RealFrom != StreamedTexture.Unknown)
        {
            // Another texture's build may have stored the base tile this one needs meanwhile.
            if (t.RealFrom > t.BaseMip && _builder.StoreHasRegion(t.Name, t.ZoomFor(t.BaseMip))) SetReal(t, t.BaseMip, fromBase: true);
            return;
        }
        t.RealFrom = t.Layout.Levels;
        if (_roots.CachedFile(t.Path) is not null)
        {
            SetReal(t, 0, fromBase: false);
            return;
        }
        for (int k = 1; k < t.BaseMip; k++)
            if (File.Exists(ChainPath(t, k)))
            {
                SetReal(t, k, fromBase: false);
                return;
            }
        if (_builder.StoreHasRegion(t.Name, t.ZoomFor(t.BaseMip))) SetReal(t, t.BaseMip, fromBase: true);
    }

    private static void SetReal(StreamedTexture t, int k, bool fromBase)
    {
        t.RealFrom = k;
        t.RealFromBase = fromBase;
        t.Version = DateTime.UtcNow;
    }

    private string ChainPath(StreamedTexture t, int k) => _roots.ChainPath(t.Name, k);

    /// <summary>The real chain in RAM, loaded or made again if it was dropped; null if it is gone (the texture is probed again).</summary>
    private byte[]? EnsureChain(StreamedTexture t)
    {
        if (t.Chain is not null && t.ChainFrom == t.RealFrom) return t.Chain;
        int k = t.RealFrom;
        byte[]? chain = null;
        try
        {
            if (k == 0 && _roots.CachedFile(t.Path) is { } file)
                chain = ReadAfterHeader(file);
            else if (t.RealFromBase && _builder.TryStoredRegion(t.Name, t.ZoomFor(k)) is { } region)
                chain = Dds.EncodeChain(_builder.ResizeTo(region.Rgb, region.Size, t.Layout.SizeOf(k)), t.Layout, k, 1);
            else if (k > 0 && k < t.Layout.Levels && !t.RealFromBase)
                chain = File.ReadAllBytes(ChainPath(t, k));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not read the cached levels of {t.Path.Display}: {ex.Message}");
        }
        if (chain is null || chain.Length != t.Layout.ChainLength(k))
        {
            t.RealFrom = StreamedTexture.Unknown; // removed meanwhile (the cache page, another run): look again
            Probe(t);
            return null;
        }
        SetChain(t, k, chain);
        return chain;
    }

    /// <summary>A cached DDS file's levels, read straight into their own array (not the whole file, then copied).</summary>
    private static byte[] ReadAfterHeader(string file)
    {
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] data = new byte[Math.Max(0, RandomAccess.GetLength(handle) - Dds.HeaderSize)];
        for (int read = 0, n; read < data.Length; read += n)
            if ((n = RandomAccess.Read(handle, data.AsSpan(read), Dds.HeaderSize + read)) == 0) return data[..read];
        return data;
    }

    private void SetChain(StreamedTexture t, int k, byte[] chain)
    {
        long before = t.RamBytes;
        t.Chain = chain;
        t.ChainFrom = k;
        t.RamBytes = chain.Length + t.StandIns.Values.Sum(s => (long)s.Bytes.Length);
        Interlocked.Add(ref _ramBytes, t.RamBytes - before);
    }

    /// <summary>
    /// A level finer than the real data: stretched from the real data's first level, or else from a coarser tile
    /// of the low-zoom base, or else one flat colour. Made once per source and kept with the texture.
    /// </summary>
    private (byte[] Bytes, Served Kind) StandIn(StreamedTexture t, int mip)
    {
        int source = t.RealFrom;
        if (t.StandInSource != source)
        {
            Interlocked.Add(ref _ramBytes, -t.StandIns.Values.Sum(s => (long)s.Bytes.Length));
            t.RamBytes = t.Chain?.Length ?? 0;
            t.StandIns.Clear();
            t.StandInSource = source;
        }
        if (t.StandIns.TryGetValue(mip, out var made)) return made;

        int size = t.Layout.SizeOf(mip);
        (byte[] Bytes, Served Kind) level;
        if (StandInImage(t) is { } image)
            level = (Dds.EncodeLevel(_builder.ResizeTo(image.Rgb, image.Size, size), size, _builder.EncoderWorkers), Served.StandIn);
        else
            level = (Dds.FlatLevel(size, FlatColor.R, FlatColor.G, FlatColor.B), Served.Flat);
        t.StandIns[mip] = level;
        t.RamBytes += level.Bytes.Length;
        Interlocked.Add(ref _ramBytes, level.Bytes.Length);
        return level;
    }

    /// <summary>The picture stand-ins are stretched from: the real data's first level, or a coarser stored tile.</summary>
    private (byte[] Rgb, int Size)? StandInImage(StreamedTexture t)
    {
        if (t.RealFrom < t.Layout.Levels && EnsureChain(t) is { } chain)
        {
            int size = t.Layout.SizeOf(t.RealFrom);
            return (Dds.DecodeLevel(chain.AsSpan(0, t.Layout.LengthOf(t.RealFrom)), size, size), size);
        }
        for (int zoom = Math.Min(t.ZoomFor(t.BaseMip), _baseZoom) - 1; zoom >= Math.Max(1, t.Name.Zoom - 12); zoom--)
            if (_builder.TryStoredRegion(t.Name, zoom) is { } region)
                return region;
        return null;
    }

    private static void FillFlat(Span<byte> output, long fileOffset)
    {
        byte[] block = Dds.FlatLevel(4, FlatColor.R, FlatColor.G, FlatColor.B);
        for (int i = 0; i < output.Length; i++) output[i] = block[(int)((fileOffset + i - Dds.HeaderSize) & 7)];
    }

    // ---------------------------------------------------------------- builds

    /// <summary>
    /// Fetches real data for levels <paramref name="k"/> and smaller, unless the texture has it or a build that
    /// covers it is already running (that one is raised to <paramref name="priority"/> instead).
    /// </summary>
    private Task<bool> StartBuild(StreamedTexture t, int k, int priority, bool byRead, PrefetchMeter? meter = null)
    {
        lock (t.Gate)
        {
            Probe(t);
            if (t.RealFrom <= k) return Task.FromResult(true);
            foreach (var (from, build) in t.Builds)
                if (from <= k)
                {
                    build.Ticket.Raise(priority);
                    return build.Task;
                }
            if (_shutdown.IsCancellationRequested) return Task.FromResult(false);
            // Every attempt already retried each tile on every server: the next one waits a little (a connection down).
            if (t.FailedAt != 0 && Stopwatch.GetElapsedTime(t.FailedAt) < RetryAfter) return Task.FromResult(false);
            var ticket = new FetchTicket(priority);
            meter?.NoteStarted();
            Task<bool> task = Task.Run(() => BuildAsync(t, k, ticket, byRead, meter));
            t.Builds[k] = (task, ticket);
            return task;
        }
    }

    /// <param name="meter">The prefetch phase that started it, told what it cost; null for every other build.</param>
    private async Task<bool> BuildAsync(StreamedTexture t, int k, FetchTicket ticket, bool byRead, PrefetchMeter? meter)
    {
        long started = Stopwatch.GetTimestamp();
        bool detailed = k < t.BaseMip;
        var stats = new TileStats();
        bool built = false;
        try
        {
            long weight = BuildWeight(t.Layout.SizeOf(k)), tiles = BuildTiles(t.Name, t.ZoomFor(k));
            // Ahead builds first take their share (always in this order: no two builds wait for each other's gate).
            bool ahead = detailed && !byRead;
            if (ahead) await _ahead.WaitAsync(tiles, ticket).ConfigureAwait(false);
            try
            {
                if (detailed) await _detailed.WaitAsync(weight, tiles, ticket).ConfigureAwait(false);
            }
            catch
            {
                if (ahead) _ahead.Release(tiles);
                throw;
            }
            int zoom = t.ZoomFor(k);
            byte[] chain;
            try
            {
                chain = await _builder.BuildChainAsync(t.Name, t.Layout, k, zoom, ticket, stats).ConfigureAwait(false);
            }
            finally
            {
                if (detailed) _detailed.Release(weight, tiles);
                if (ahead) _ahead.Release(tiles);
            }
            Save(t, k, chain);
            lock (t.Gate)
            {
                t.FailedAt = 0;
                if (t.RealFrom == StreamedTexture.Unknown || k < t.RealFrom)
                {
                    SetReal(t, k, fromBase: k >= t.BaseMip && _builder.Source.Store is not null);
                    SetChain(t, k, chain);
                }
            }
            if (byRead) Interlocked.Increment(ref _readBuilds);
            else Interlocked.Increment(ref _aheadBuilds);
            if (k == 0) Interlocked.Increment(ref _fullBuilds);
            if (k < t.BaseMip)
                Log.Detail($"Streamed {t.Path.Name} levels {k}-{t.Layout.Levels - 1} ({t.Layout.SizeOf(k)} px) from zoom {zoom}: " +
                         $"{stats.Misses} tiles downloaded, {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s ({(byRead ? "X-Plane read" : "ahead")})");
            TrimIfNeeded();
            built = true;
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedBuilds);
            lock (t.Gate) t.FailedAt = Stopwatch.GetTimestamp();
            Log.Warning($"Could not stream {t.Path.Display} levels {k}+ from zoom {t.ZoomFor(k)}: {ex.Message}");
            return false;
        }
        finally
        {
            lock (t.Gate) t.Builds.Remove(k);
            meter?.NoteBuild(built, stats.Misses, Interlocked.Read(ref stats.Bytes), Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>Level 0 complete becomes a normal cached DDS; a coarser chain goes to .xpstream\mips; base levels are never saved.</summary>
    private void Save(StreamedTexture t, int k, byte[] chain)
    {
        if (k >= t.BaseMip && _builder.Source.Store is not null) return; // made again from the stored tile
        if (k == 0)
        {
            byte[] file = new byte[t.Layout.Length];
            t.Layout.Header.CopyTo(file);
            chain.CopyTo(file, Dds.HeaderSize);
            AtomicFile.Write(_roots.CachePath(t.Path), file);
        }
        else if (k < t.BaseMip)
        {
            AtomicFile.Write(ChainPath(t, k), chain);
        }
        for (int coarser = k + 1; coarser < t.BaseMip; coarser++)
        {
            try
            {
                File.Delete(ChainPath(t, coarser));
            }
            catch (IOException)
            {
                // Being read: left for later, it is never used once a finer chain exists.
            }
        }
    }

    /// <summary>
    /// Fetches levels <paramref name="k"/> and smaller ahead of X-Plane (no wait). Returns the build, finished at
    /// once when the texture already has them, or null when the path is not a photo texture.
    /// </summary>
    /// <param name="meter">The SimBrief prefetch phase asking, told what a build it starts costs.</param>
    public Task<bool>? Warm(VirtualPath path, int k, int priority, PrefetchMeter? meter = null)
    {
        if (Get(path) is not { } t) return null;
        // At night a texture with its base has all load-ahead may fetch: nothing to ask for (the warmer rechecks every
        // loaded texture at each position, which would count each of them again and again).
        if (k < t.BaseMip && PhotosPaused && RealFrom(t) <= t.BaseMip) return Task.FromResult(true);
        Interlocked.Increment(ref _warmRequests);
        return StartBuild(t, Math.Clamp(k, PhotosPaused ? t.BaseMip : 0, t.BaseMip), priority, byRead: false, meter);
    }

    private long _warmRequests;

    /// <summary>The first level the texture has real data for (its level count when none).</summary>
    public int RealFrom(StreamedTexture t)
    {
        lock (t.Gate)
        {
            Probe(t);
            return t.RealFrom;
        }
    }

    /// <summary>
    /// Where the texture's picture comes from now, and its version: the photo once it has real data that is not the
    /// low-zoom base's (its stand-ins are stretched from it too), else the base.
    /// </summary>
    public (PictureSource Source, DateTime Version) Picture(StreamedTexture t)
    {
        lock (t.Gate)
        {
            Probe(t);
            return (t.RealFrom < t.Layout.Levels && !t.RealFromBase ? PictureSource.Photo : PictureSource.Base, t.Version);
        }
    }

    /// <summary>
    /// The photo's level nearest <paramref name="size"/> pixels (its first real level when that is coarser), decoded to
    /// RGB, or null when the picture is not the photo.
    /// </summary>
    public (byte[] Rgb, int Size)? PhotoSample(StreamedTexture t, int size)
    {
        lock (t.Gate)
        {
            Probe(t);
            if (t.RealFrom >= t.Layout.Levels || t.RealFromBase || EnsureChain(t) is not { } chain) return null;
            int mip = Math.Max(t.RealFrom, Math.Clamp((int)Math.Round(Math.Log2((double)t.Layout.Size / size)), 0, t.Layout.Levels - 1));
            int levelSize = t.Layout.SizeOf(mip), start = (int)(t.Layout.Offset(mip) - t.Layout.Offset(t.ChainFrom));
            return (Dds.DecodeLevel(chain.AsSpan(start, t.Layout.LengthOf(mip)), levelSize, levelSize), levelSize);
        }
    }

    // ---------------------------------------------------------------- RAM

    /// <summary>Drops the RAM copies of the least recently read textures when over budget; they are on disk or made again.</summary>
    private void TrimIfNeeded()
    {
        if (Interlocked.Read(ref _ramBytes) <= _options.RamBudgetBytes || Interlocked.Exchange(ref _trimming, 1) == 1) return;
        try
        {
            long target = _options.RamBudgetBytes * 3 / 4;
            foreach (StreamedTexture t in _textures.Values.OrderBy(t => Volatile.Read(ref t.LastUsed)))
            {
                if (Interlocked.Read(ref _ramBytes) <= target) break;
                lock (t.Gate)
                {
                    // Saved chains and the base's are read or made again; one from a provider without a base exists only here.
                    if (t.RealFromBase || t.ChainFrom < t.BaseMip || t.ChainFrom != t.RealFrom)
                    {
                        Interlocked.Add(ref _ramBytes, -(t.Chain?.Length ?? 0));
                        t.Chain = null;
                    }
                    Interlocked.Add(ref _ramBytes, -t.StandIns.Values.Sum(s => (long)s.Bytes.Length));
                    t.StandIns.Clear();
                    t.StandInSource = StreamedTexture.Unknown;
                    t.RamBytes = t.Chain?.Length ?? 0;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _trimming, 0);
        }
    }

    // ---------------------------------------------------------------- reporting


    /// <summary>
    /// The detail X-Plane holds of each texture it wanted within <paramref name="within"/>: the pixels across of the
    /// finest level it read, which is what that texture takes of the graphics memory.
    /// </summary>
    public IEnumerable<int> HeldDetail(TimeSpan within)
    {
        foreach (StreamedTexture t in _textures.Values)
        {
            int finest;
            long at;
            lock (t.Gate) (finest, at) = (t.FinestWanted, t.WantedAt);
            if (finest != StreamedTexture.Unknown && Stopwatch.GetElapsedTime(at) < within) yield return Math.Max(1, t.Layout.Size >> finest);
        }
    }

    /// <summary>What keeps X-Plane's builds out now: the build memory, the build tiles, or nothing.</summary>
    public GateLimit BuildLimit => _detailed.Limit;

    /// <summary>Builds waiting for their turn, at either gate.</summary>
    internal int BuildsWaiting => _ahead.Waiting + _detailed.Waiting;

    /// <summary>The numbers the flight tuning reads: the answers, the waits and what they needed, and k.</summary>
    public StreamingFacts Facts()
    {
        var w = _waitStats.Snapshot();
        return new StreamingFacts(Reads, RealReads, Interlocked.Read(ref _standIn), Interlocked.Read(ref _flat),
            w.StartWaits, w.StartTimedOut, w.StartNeeded, w.FlightWaits, w.FlightTimedOut, w.FlightNeeded,
            _model.K, _paging.MeasuredK());
    }

    /// <summary>One log line: how X-Plane's reads were answered and how long they waited.</summary>
    public string Summary()
    {
        long reads = Reads;
        string Share(long n) => reads == 0 ? "" : $" ({100.0 * n / reads:F0}%)";
        PagingFit? fit = _paging.Fit();
        string fitted = fit is null ? "" : $", fits {fit.K:F0} ({fit.Agreement:P0} of {fit.Samples} reads agree)";
        string k = _paging.MeasuredK() is { } measured
            ? $"; paging k: model {_model.K:F0}, measured {measured.Median:F0} from {measured.Count} mip changes{fitted}"
            : $"; paging k: model {_model.K:F0}{(fit is null ? ", not measured yet" : fitted)}";
        return $"Streaming summary: {_textures.Count} textures, {Interlocked.Read(ref _headerChunks)} header chunks; X-Plane's reads of levels: {reads}, " +
               $"{RealReads} real{Share(RealReads)}, {Interlocked.Read(ref _standIn)} with stand-ins{Share(Interlocked.Read(ref _standIn))}, " +
               $"{Interlocked.Read(ref _flat)} flat{Share(Interlocked.Read(ref _flat))}; waited: {_waitStats.Describe(Band.Near)}, {_waitStats.Describe(Band.Middle)}; " +
               $"builds: {Interlocked.Read(ref _readBuilds)} for reads, {Interlocked.Read(ref _aheadBuilds)} ahead ({Interlocked.Read(ref _warmRequests)} asked), " +
               $"{Interlocked.Read(ref _fullBuilds)} at full detail, {Interlocked.Read(ref _failedBuilds)} failed" +
               (_nightPause is null ? "" : $", {Interlocked.Read(ref _nightReads)} reads at night kept to the low-zoom base") + $"; RAM {Interlocked.Read(ref _ramBytes) >> 20} MB{k}";
    }

    /// <summary>A number that changes whenever the summary would.</summary>
    public long SummaryVersion => Reads + Interlocked.Read(ref _readBuilds) + Interlocked.Read(ref _aheadBuilds) + Interlocked.Read(ref _failedBuilds);

    /// <summary>Stops new builds and waits for the running ones.</summary>
    public async Task ShutdownAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task[] running = [.. _textures.Values.SelectMany(t =>
        {
            lock (t.Gate) return t.Builds.Values.Select(b => (Task)b.Task).ToArray();
        })];
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Lets go of every RAM copy at once, whoever still holds the streamer (the window keeps running after a stop).</summary>
    public void Dispose()
    {
        _shutdown.Dispose();
        foreach (StreamedTexture t in _textures.Values)
            lock (t.Gate)
            {
                t.Chain = null;
                t.StandIns.Clear();
            }
        _textures.Clear();
        _paging.Clear();
        Interlocked.Exchange(ref _ramBytes, 0);
    }
}
