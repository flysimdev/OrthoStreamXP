using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace OrthoStreamXP.Textures;

/// <summary>Per-build counters; download time is summed across concurrent requests and retries.</summary>
internal sealed class TileStats
{
    public int Hits, Misses, Calls, Fallbacks;
    /// <summary>Bytes downloaded for the build (every answer, as the session's own count).</summary>
    public long Bytes;
    private long _downloadTicks;

    public double DownloadSeconds => TimeSpan.FromTicks(Interlocked.Read(ref _downloadTicks)).TotalSeconds;

    public void AddDownload(TimeSpan elapsed) => Interlocked.Add(ref _downloadTicks, elapsed.Ticks);
}

/// <summary>A source of 256x256 XYZ imagery tiles: its name and URL template.</summary>
internal sealed record ImageryProvider(string Name, string Template)
{
    public const string Synthetic = "synthetic";

    /// <summary>The providers known by name; any other name takes its template from --texture-url (--url).</summary>
    public static readonly IReadOnlyDictionary<string, string> BuiltIn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["bing"] = "https://ecn.t{s}.tiles.virtualearth.net/tiles/a{q}.jpeg?g=1",
        ["esri"] = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
        // Poland's national orthophoto (GUGiK, free to use), on the web-map grid up to zoom 19.
        ["geoportal"] = "https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMTS/StandardResolution?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
                        "&LAYER=ORTOFOTOMAPA&STYLE=default&TILEMATRIXSET=EPSG:3857&TILEMATRIX=EPSG:3857:{z}&TILEROW={y}&TILECOL={x}&FORMAT=image/jpeg",
    };

    /// <summary>
    /// Where a national provider has imagery (south, west, north, east in degrees): it is not asked for a tile wholly
    /// outside, so the next provider answers at once. Inside the box but outside the country it answers a transparent
    /// tile, which counts as no picture.
    /// </summary>
    private static readonly Dictionary<string, (double South, double West, double North, double East)> Areas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["geoportal"] = (49.0, 14.1, 54.9, 24.2),
    };

    /// <summary>Whether the provider may have the tile: always, except a national provider for a tile outside its country's box.</summary>
    public bool Covers(int x, int y, int zoom)
    {
        if (!Areas.TryGetValue(Name, out var area)) return true;
        var (north, west) = Tiles.TileToLatLon(x, y, zoom);
        var (south, east) = Tiles.TileToLatLon(x + 1, y + 1, zoom);
        return south < area.North && north > area.South && west < area.East && east > area.West;
    }

    /// <summary>
    /// Reads a comma-separated list of providers in the order they are tried, e.g. "esri,bing". Built-in names,
    /// plus at most one other name, which uses <paramref name="customTemplate"/> (so does a lone name, built in or
    /// not). "synthetic" (generated test pattern, no internet) stands alone.
    /// </summary>
    public static IReadOnlyList<ImageryProvider> ParseList(string list, string? customTemplate)
    {
        string[] names = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) throw new ArgumentException("no texture provider named");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() < names.Length)
            throw new ArgumentException($"a texture provider is named twice in '{list}'");
        if (names.Contains(Synthetic, StringComparer.OrdinalIgnoreCase))
            return names.Length == 1 ? [new ImageryProvider(Synthetic, "")]
                : throw new ArgumentException("the synthetic texture provider cannot be combined with others");
        if (names.Length == 1 && customTemplate is not null) return [new ImageryProvider(names[0], customTemplate)];

        string[] custom = names.Where(name => !BuiltIn.ContainsKey(name)).ToArray();
        if (custom.Length > 1)
            throw new ArgumentException($"only one provider can use the URL template; {string.Join(" and ", custom)} are not built in ({string.Join(", ", BuiltIn.Keys)})");
        if (custom.Length == 1 && customTemplate is null)
            throw new ArgumentException($"the texture provider '{custom[0]}' is not built in ({string.Join(", ", BuiltIn.Keys)}), so it needs a URL template");
        if (custom.Length == 0 && customTemplate is not null)
            throw new ArgumentException("a URL template is given, but every provider in the list is built in; add a name for it to the list");
        return names.Select(name => new ImageryProvider(name, BuiltIn.TryGetValue(name, out string? template) ? template : customTemplate!)).ToArray();
    }
}

/// <summary>
/// Downloads and decodes 256x256 source tiles. Decoded tiles stay in a bounded RAM cache only while
/// builds are active, and concurrent requests for the same tile share one download. A tile the first
/// provider cannot supply is asked from the next one in the list. Downloads wait in one queue ordered by
/// <see cref="FetchPriority"/>; tiles of the low-zoom base (<see cref="Store"/>) are kept on disk for good.
/// </summary>
internal sealed class TileSource : IDisposable
{
    private const long MaxCacheBytes = 256L * 1024 * 1024;
    public const double DefaultTimeoutSeconds = 5;
    public const int DefaultAttempts = 4;

    /// <summary>A tile being fetched: everyone who wants it, so the download goes at the most urgent one's priority.</summary>
    private sealed class Pending(FetchTicket first)
    {
        private readonly List<FetchTicket> _tickets = [first];

        public required Task<byte[]> Task { get; init; }

        public void Join(FetchTicket ticket)
        {
            lock (_tickets) _tickets.Add(ticket);
        }

        public int Priority()
        {
            lock (_tickets) return _tickets.Min(t => t.Priority);
        }
    }

    private readonly IReadOnlyList<ImageryProvider> _providers;
    private readonly int _attempts;
    private readonly HttpClient _http;
    private readonly PriorityGate _gate;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, byte[]> _cache = [];
    private readonly Queue<string> _cacheOrder = new();
    private readonly Dictionary<string, Pending> _inFlight = [];
    private long _cacheBytes;
    private int _activeBuilds;
    private long _tilesDownloaded, _bytesDownloaded, _failedTiles, _attemptsMade, _troubledAttempts, _tileTicks;

    /// <param name="provider">A provider name, or several separated by commas (see <see cref="ImageryProvider.ParseList"/>).</param>
    public TileSource(string provider, string? urlTemplate, int downloadWorkers,
        double timeoutSeconds = DefaultTimeoutSeconds, int attempts = DefaultAttempts, HttpMessageHandler? handler = null)
        : this(ImageryProvider.ParseList(provider, urlTemplate), downloadWorkers, timeoutSeconds, attempts, handler)
    {
    }

    /// <param name="providers">Tried in order for each tile.</param>
    /// <param name="timeoutSeconds">Limit for one download attempt; a healthy tile takes well under a second.</param>
    /// <param name="attempts">Tries per tile and provider; each retry goes to the next {s} server.</param>
    /// <param name="handler">Replaces the network stack (tests).</param>
    public TileSource(IReadOnlyList<ImageryProvider> providers, int downloadWorkers,
        double timeoutSeconds = DefaultTimeoutSeconds, int attempts = DefaultAttempts, HttpMessageHandler? handler = null)
    {
        if (providers.Count == 0) throw new ArgumentException("At least one imagery provider is required");
        _providers = providers;
        _attempts = Math.Clamp(attempts, 1, 10);
        Provider = string.Join(",", providers.Select(p => p.Name));
        IsSynthetic = providers[0].Name == ImageryProvider.Synthetic;
        DownloadWorkers = Math.Clamp(downloadWorkers, 1, 256);
        _gate = new PriorityGate(DownloadWorkers);
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1024,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Connections idle this long are closed rather than reused; a silently dropped one would hang a request.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(8, timeoutSeconds)),
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
        })
        { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpIdentity.UserAgent);
    }

    /// <summary>The provider names in the order they are tried, e.g. "esri,bing".</summary>
    public string Provider { get; }
    public bool IsSynthetic { get; }
    public int DownloadWorkers { get; }

    /// <summary>Tiles downloaded this session (each successful answer of a server).</summary>
    public long TilesDownloaded => Interlocked.Read(ref _tilesDownloaded);

    /// <summary>Bytes of those tiles, for the download speed.</summary>
    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);

    /// <summary>Tiles no provider could supply after every attempt.</summary>
    public long FailedTiles => Interlocked.Read(ref _failedTiles);

    /// <summary>Download attempts made: every request to a server, a retry being another one.</summary>
    public long AttemptsMade => Interlocked.Read(ref _attemptsMade);

    /// <summary>Attempts that timed out or met a server, connection or data error (not "no such tile"): tried again.</summary>
    public long TroubledAttempts => Interlocked.Read(ref _troubledAttempts);

    /// <summary>How long a successful tile download took on average, in seconds.</summary>
    public double MeanTileSeconds
    {
        get
        {
            long tiles = TilesDownloaded;
            return tiles == 0 ? 0 : TimeSpan.FromTicks(Interlocked.Read(ref _tileTicks) / tiles).TotalSeconds;
        }
    }

    /// <summary>Downloads waiting for a free download worker.</summary>
    public int QueuedDownloads => _gate.Waiting;

    /// <summary>The low-zoom base on disk: tiles it covers are read from it and saved to it. None by default.</summary>
    public TileStore? Store { get; init; }

    /// <summary>Marks a build as active; the RAM tile cache is released when the last active build ends.</summary>
    public IDisposable BeginBuild()
    {
        Interlocked.Increment(ref _activeBuilds);
        return new BuildScope(this);
    }

    private sealed class BuildScope(TileSource owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && Interlocked.Decrement(ref owner._activeBuilds) == 0)
                owner.ClearCache();
        }
    }

    /// <summary>
    /// Opens TLS connections to every shard host of the first provider so a cold run does not pay handshakes on its
    /// first tiles; the fallbacks get one connection each.
    /// </summary>
    public async Task WarmUpAsync()
    {
        if (IsSynthetic) return;
        var urls = new List<string>();
        for (int p = 0; p < _providers.Count; p++)
        {
            string template = _providers[p].Template;
            int hosts = template.Contains("{s}", StringComparison.Ordinal) ? 8 : 1;
            int perHost = p == 0 ? Math.Clamp(DownloadWorkers / 8, 1, 8) : 1;
            for (int i = 0; i < hosts * perHost; i++) urls.Add(Url(template, 0, 0, 1, i % hosts));
        }
        await Parallel.ForEachAsync(urls, new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (url, ct) =>
        {
            try
            {
                using HttpResponseMessage response = await _http.GetAsync(url, ct);
                await response.Content.CopyToAsync(Stream.Null, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Warm-up is best effort.
            }
        });
        Log.Info("Connection warm-up finished");
    }

    /// <summary>A decoded 256x256 RGB tile; <paramref name="priority"/> puts it ahead of ordinary downloads.</summary>
    public Task<byte[]> GetTileAsync(int x, int y, int zoom, bool priority, TileStats stats) =>
        GetTileAsync(x, y, zoom, new FetchTicket(priority ? FetchPriority.Read : FetchPriority.Warm), stats);

    /// <summary>A decoded 256x256 RGB tile, downloaded at the urgency of <paramref name="ticket"/> if it must be.</summary>
    public async Task<byte[]> GetTileAsync(int x, int y, int zoom, FetchTicket ticket, TileStats stats)
    {
        string key = $"{zoom}/{x}/{y}";
        Pending? shared;
        var mine = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Pending(ticket) { Task = mine.Task };
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out byte[]? cached))
            {
                Interlocked.Increment(ref stats.Hits);
                return cached;
            }
            if (!_inFlight.TryGetValue(key, out shared)) _inFlight[key] = pending;
        }
        if (shared is not null)
        {
            shared.Join(ticket);
            Interlocked.Increment(ref stats.Hits);
            return await shared.Task.ConfigureAwait(false);
        }

        try
        {
            byte[]? stored = Stored(x, y, zoom);
            if (stored is not null) Interlocked.Increment(ref stats.Hits);
            else Interlocked.Increment(ref stats.Misses);
            byte[] tile = stored ?? await DownloadAsync(x, y, zoom, pending, stats).ConfigureAwait(false);
            lock (_cacheLock)
            {
                _inFlight.Remove(key);
                AddToCache(key, tile);
            }
            mine.SetResult(tile);
            return tile;
        }
        catch (Exception ex)
        {
            lock (_cacheLock) _inFlight.Remove(key);
            mine.SetException(ex);
            _ = mine.Task.Exception; // observed here; waiters get it from their own await
            throw;
        }
    }

    /// <summary>
    /// A tile that needs no download: in the RAM cache or the low-zoom base. Null otherwise; this never waits
    /// for the network, so far scenery can be drawn from whatever is at hand.
    /// </summary>
    public byte[]? TryGetStored(int x, int y, int zoom)
    {
        lock (_cacheLock)
            if (_cache.TryGetValue($"{zoom}/{x}/{y}", out byte[]? cached)) return cached;
        return Stored(x, y, zoom);
    }

    /// <summary>The tile decoded from the low-zoom base, or null (a damaged file is downloaded again).</summary>
    private byte[]? Stored(int x, int y, int zoom)
    {
        if (IsSynthetic || Store?.TryRead(x, y, zoom) is not { } encoded) return null;
        try
        {
            return Images.DecodeTile(encoded);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private async Task<byte[]> DownloadAsync(int x, int y, int zoom, Pending pending, TileStats stats)
    {
        if (IsSynthetic) return Images.SyntheticTile(x, y, zoom);
        await _gate.WaitAsync(pending.Priority).ConfigureAwait(false);
        try
        {
            Exception? last = null;
            byte[]? placeholder = null; // a "no imagery here" picture, used when no provider has the tile
            var failures = new List<string>();
            for (int p = 0; p < _providers.Count; p++)
            {
                ImageryProvider provider = _providers[p];
                if (!provider.Covers(x, y, zoom))
                {
                    failures.Add($"{provider.Name}: outside its area");
                    continue;
                }
                for (int attempt = 0; attempt < _attempts; attempt++)
                {
                    // Each retry asks the next server, so one stuck host or connection is not retried.
                    string url = Url(provider.Template, x, y, zoom, (x + y + attempt) % 8);
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        Interlocked.Increment(ref stats.Calls);
                        Interlocked.Increment(ref _attemptsMade);
                        using HttpResponseMessage response = await _http.GetAsync(url).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        Interlocked.Increment(ref _tilesDownloaded);
                        Interlocked.Add(ref _bytesDownloaded, bytes.Length);
                        Interlocked.Add(ref stats.Bytes, bytes.Length);
                        Interlocked.Add(ref _tileTicks, Stopwatch.GetElapsedTime(started).Ticks);
                        byte[] decoded = Images.DecodeTile(bytes); // rejects error pages and truncated data
                        stats.AddDownload(Stopwatch.GetElapsedTime(started));
                        // Bing answers a place without imagery with 200 and a placeholder picture, marked only by this header.
                        if (response.Headers.TryGetValues("X-VE-Tile-Info", out var info) && info.Contains("no-tile", StringComparer.OrdinalIgnoreCase))
                        {
                            placeholder ??= decoded;
                            last = new HttpRequestException("no imagery here (X-VE-Tile-Info: no-tile)", null, HttpStatusCode.NotFound);
                            break;
                        }
                        // Geoportal answers a place outside the country with a transparent PNG (as image/jpeg); at the border, a
                        // partly transparent one. Either has no whole picture: the next provider's tile is used instead.
                        if (Images.HasTransparentPixels(bytes))
                        {
                            last = new HttpRequestException("no imagery here (a transparent tile)", null, HttpStatusCode.NotFound);
                            break;
                        }
                        if (p > 0) Interlocked.Increment(ref stats.Fallbacks);
                        if (Store?.Covers(zoom) == true) Store.Save(x, y, zoom, bytes); // never a placeholder
                        return decoded;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
                    {
                        stats.AddDownload(Stopwatch.GetElapsedTime(started));
                        last = ex;
                        if (IsDefinitive(ex)) break;
                        Interlocked.Increment(ref _troubledAttempts);
                    }
                    if (attempt < _attempts - 1) await Task.Delay(Math.Min(2000, 250 << attempt)).ConfigureAwait(false);
                }
                failures.Add(_providers.Count > 1 ? $"{provider.Name}: {last?.Message}" : last?.Message ?? "");
            }
            if (placeholder is not null) return placeholder;
            Interlocked.Increment(ref _failedTiles);
            throw new IOException($"Failed downloading tile {zoom}/{x}/{y}: {string.Join("; ", failures)}", last);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A client error (404 no such tile, 403 refused, ...) will not change on another server, so it is not
    /// retried; 408 (request timeout) and 429 (too many requests) are.
    /// </summary>
    private static bool IsDefinitive(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } status } && (int)status is >= 400 and < 500 and not 408 and not 429;

    internal static string Url(string template, int x, int y, int zoom, int server) => template
        .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
        .Replace("{y}", y.ToString(CultureInfo.InvariantCulture))
        .Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture))
        .Replace("{q}", Tiles.QuadKey(x, y, zoom))
        .Replace("{s}", server.ToString(CultureInfo.InvariantCulture));

    private void AddToCache(string key, byte[] tile)
    {
        if (!_cache.TryAdd(key, tile)) return;
        _cacheOrder.Enqueue(key);
        _cacheBytes += tile.Length;
        while (_cacheBytes > MaxCacheBytes && _cacheOrder.TryDequeue(out string? old))
            if (_cache.Remove(old, out byte[]? removed)) _cacheBytes -= removed.Length;
    }

    private void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
            _cacheBytes = 0;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        ClearCache();
    }
}
