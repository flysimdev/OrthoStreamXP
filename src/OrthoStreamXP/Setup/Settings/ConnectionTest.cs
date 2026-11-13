using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Setup;

/// <summary>
/// One round of the connection test: so many tiles downloaded with so many at once, in so many seconds, and the speed
/// of its steady middle (<see cref="Mbits"/>).
/// </summary>
internal sealed record ConnectionRound(int AtOnce, int Tiles, int Failed, long Bytes, double Seconds, double MeanTileSeconds, double Mbits)
{
    public double FailedShare => Tiles == 0 ? 0 : (double)Failed / Tiles;
}

/// <summary>
/// What the connection test measured: each round, the downloads at once it suggests, the best download speed and how
/// long a tile took. Kept beside the settings file (orthostreamxp.connection.json): it describes the connection, not a cache.
/// </summary>
internal sealed record ConnectionResult(DateTime WhenUtc, string Provider, IReadOnlyList<ConnectionRound> Rounds, int Suggested)
{
    /// <summary>The person has decided on downloads at once since this test (used its suggestion or set their own): it is not offered again.</summary>
    public bool Decided { get; init; }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The best speed of a round with few failures, in Mbit/s.</summary>
    public double Mbits => ConnectionTest.Usable(Rounds).Select(r => r.Mbits).DefaultIfEmpty(0).Max();

    /// <summary>How long a tile took in the suggested round, in seconds.</summary>
    public double TileSeconds => Rounds.FirstOrDefault(r => r.AtOnce == Suggested)?.MeanTileSeconds ?? 0;

    public static string PathFor(string settingsPath) => System.IO.Path.ChangeExtension(settingsPath, ".connection.json");

    /// <summary>"About 95 Mbit/s from bing; a tile takes 0.12 s. 64 downloads at once reach that."</summary>
    public string Summary
    {
        get
        {
            if (Suggested == 0) return $"The test could not download from {Provider}: check the internet connection, then try again.";
            ConnectionRound chosen = Rounds.First(r => r.AtOnce == Suggested);
            double share = Mbits > 0 ? chosen.Mbits / Mbits : 1;
            return string.Create(CultureInfo.CurrentCulture,
                $"About {Mbits:0} Mbit/s from {Provider}; a picture tile takes {TileSeconds:0.00} s. {Suggested} downloads at once reach {share:P0} of that.");
        }
    }

    public void Save(string path)
    {
        try
        {
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save the connection test to {path}: {ex.Message}");
        }
    }

    public static ConnectionResult? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ConnectionResult>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Measures how many downloads at once the connection needs. A picture tile is small (15-25 KB), so one download
/// spends most of its time waiting for the server, and the line is full only with many at once: about the tiles a
/// second it can take times the time one takes. Real tiles are downloaded from the provider chosen, in rounds of 16,
/// 32, 64, 128 and 256 at once (each round new tiles, in a big city near a random point so that no cache answers
/// them), until more at once no longer gets faster or the server starts refusing. The suggestion is the fewest at
/// once that reach 90 % of the best speed with under 1 % failed. A round's speed is that of its steady middle, from
/// its 10th to its 90th percent of tiles arrived: neither the start nor a few slow last tiles count. Nothing is kept;
/// it downloads about 30 MB.
/// </summary>
internal static class ConnectionTest
{
    public static readonly int[] Steps = [16, 32, 64, 128, 256];
    private const int Zoom = 17;

    /// <summary>Places with imagery from every provider: big cities over the world, and Poland's for its national one.</summary>
    private static readonly (double Lat, double Lon)[] Places =
    [
        (51.11, 17.03), (52.23, 21.01), (50.06, 19.94), (54.35, 18.65), (52.52, 13.40), (48.86, 2.35), (40.42, -3.70),
        (41.90, 12.50), (51.50, -0.12), (41.88, -87.63), (32.78, -96.80), (-33.87, 151.20), (35.68, 139.69),
    ];

    /// <summary>Rounds that count: under 5 % of their tiles failed.</summary>
    internal static IEnumerable<ConnectionRound> Usable(IEnumerable<ConnectionRound> rounds) => rounds.Where(r => r.Tiles > 0 && r.FailedShare < 0.05);

    /// <summary>
    /// Whether the last round says more at once would not help: the server refused (5 % or more failed), or it was not
    /// at least 5 % faster than the best round with fewer (the line is full).
    /// </summary>
    internal static bool Enough(IReadOnlyList<ConnectionRound> rounds)
    {
        if (rounds.Count == 0) return false;
        ConnectionRound last = rounds[^1];
        if (last.FailedShare >= 0.05) return true;
        double before = Usable(rounds.Take(rounds.Count - 1)).Select(r => r.Mbits).DefaultIfEmpty(0).Max();
        return before > 0 && last.Mbits < before * 1.05;
    }

    /// <summary>The fewest downloads at once that reach 90 % of the best speed with under 1 % failed; 0 when no round downloaded.</summary>
    public static int Suggest(IReadOnlyList<ConnectionRound> rounds)
    {
        double best = Usable(rounds).Select(r => r.Mbits).DefaultIfEmpty(0).Max();
        if (best <= 0) return 0;
        return rounds.Where(r => r.FailedShare < 0.01 && r.Mbits >= best * 0.9).Select(r => r.AtOnce).DefaultIfEmpty(0).Min() is > 0 and var fewest
            ? fewest
            : Usable(rounds).MaxBy(r => r.Mbits)!.AtOnce;
    }

    /// <param name="provider">The provider X-Plane's pictures come from first (its tiles, its servers).</param>
    /// <param name="progress">A line for the window after each round.</param>
    /// <param name="handler">Replaces the network stack (tests).</param>
    public static async Task<ConnectionResult> RunAsync(ImageryProvider provider, IProgress<string>? progress = null,
        HttpMessageHandler? handler = null, CancellationToken cancel = default)
    {
        using var http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 1024,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
        })
        { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpIdentity.UserAgent);

        using IEnumerator<(int X, int Y)> tiles = TestTiles(provider).GetEnumerator();
        // Connections are opened first, so the first round measures downloads and not their set-up.
        await RoundAsync(http, provider, tiles, 16, 16, cancel).ConfigureAwait(false);
        var rounds = new List<ConnectionRound>();
        foreach (int atOnce in Steps)
        {
            progress?.Report($"Downloading with {atOnce} at once…");
            ConnectionRound round = await RoundAsync(http, provider, tiles, atOnce, Math.Max(64, 4 * atOnce), cancel).ConfigureAwait(false);
            rounds.Add(round);
            Log.Info(string.Create(CultureInfo.InvariantCulture,
                $"Connection test, {atOnce} at once: {round.Mbits:0.0} Mbit/s, {round.Tiles / Math.Max(0.001, round.Seconds):0} tiles/s, a tile {round.MeanTileSeconds:0.00} s, {round.Failed} of {round.Tiles} failed"));
            if (Enough(rounds)) break;
        }
        var result = new ConnectionResult(DateTime.UtcNow, provider.Name, rounds, Suggest(rounds));
        Log.Step($"Connection test: {result.Summary}");
        return result;
    }

    private static async Task<ConnectionRound> RoundAsync(HttpClient http, ImageryProvider provider, IEnumerator<(int X, int Y)> tiles,
        int atOnce, int count, CancellationToken cancel)
    {
        var batch = new List<(int X, int Y)>(count);
        while (batch.Count < count && tiles.MoveNext()) batch.Add(tiles.Current);
        using var gate = new SemaphoreSlim(atOnce);
        long bytes = 0, tileTicks = 0;
        int failed = 0;
        var arrived = new List<(long At, int Bytes)>(count);
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(batch.Select(async tile =>
        {
            await gate.WaitAsync(cancel).ConfigureAwait(false);
            long started = Stopwatch.GetTimestamp();
            try
            {
                string url = TileSource.Url(provider.Template, tile.X, tile.Y, Zoom, (tile.X + tile.Y) % 8);
                using HttpResponseMessage response = await http.GetAsync(url, cancel).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                byte[] body = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                Interlocked.Add(ref bytes, body.Length);
                lock (arrived) arrived.Add((Stopwatch.GetTimestamp(), body.Length));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !cancel.IsCancellationRequested)
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                Interlocked.Add(ref tileTicks, Stopwatch.GetTimestamp() - started);
                gate.Release();
            }
        })).ConfigureAwait(false);
        double seconds = clock.Elapsed.TotalSeconds;
        double meanTile = batch.Count == 0 ? 0 : (double)tileTicks / Stopwatch.Frequency / batch.Count;
        return new ConnectionRound(atOnce, batch.Count, failed, bytes, seconds, meanTile, SteadyMbits(arrived));
    }

    /// <summary>The speed between the arrival of the 10th and the 90th percent of the tiles, in Mbit/s (0 for too few).</summary>
    internal static double SteadyMbits(List<(long At, int Bytes)> arrived)
    {
        if (arrived.Count < 10) return 0;
        arrived.Sort((a, b) => a.At.CompareTo(b.At));
        int first = arrived.Count / 10, last = arrived.Count - 1 - arrived.Count / 10;
        double seconds = (double)(arrived[last].At - arrived[first].At) / Stopwatch.Frequency;
        long bytes = 0;
        for (int i = first + 1; i <= last; i++) bytes += arrived[i].Bytes;
        return seconds > 0 ? bytes * 8 / seconds / 1e6 : 0;
    }

    /// <summary>Tiles never asked for twice: 16 x 16 blocks near random points around the places the provider covers.</summary>
    private static IEnumerable<(int X, int Y)> TestTiles(ImageryProvider provider)
    {
        var places = Places.Where(p => Covers(provider, p)).DefaultIfEmpty(Places[0]).ToArray();
        for (int block = 0; ; block++)
        {
            var (lat, lon) = places[block % places.Length];
            var (x0, y0) = Tiles.LatLonToTile(lat + (Random.Shared.NextDouble() - 0.5) * 0.2, lon + (Random.Shared.NextDouble() - 0.5) * 0.3, Zoom);
            for (int y = 0; y < Tiles.TextureSpan; y++)
                for (int x = 0; x < Tiles.TextureSpan; x++)
                    yield return (x0 + x, y0 + y);
        }
    }

    private static bool Covers(ImageryProvider provider, (double Lat, double Lon) place)
    {
        var (x, y) = Tiles.LatLonToTile(place.Lat, place.Lon, Zoom);
        return provider.Covers(x, y, Zoom);
    }
}
