using System.Globalization;
using System.Net;
using System.Reflection;
using System.Windows.Media.Imaging;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>One OpenStreetMap tile: zoom, column from 180°W, row from the top of the map.</summary>
internal readonly record struct StreetMapTile(int Zoom, int X, int Y);

/// <summary>
/// OpenStreetMap's standard tiles for the Build Scenery map's background, as its tile usage policy asks: an identifying
/// User-Agent, a few connections, tiles kept on disk (<c>%LOCALAPPDATA%\OrthoStreamXP\osm</c>) for two weeks and in memory,
/// and only the tiles on screen asked for, those nearest the middle first.
/// </summary>
internal sealed class StreetMapTiles : IDisposable
{
    public const int MaxZoom = 19;
    private const int MemoryTiles = 768, Workers = 4;
    private const string Server = "https://tile.openstreetmap.org";
    private static readonly TimeSpan KeepOnDisk = TimeSpan.FromDays(14), RetryAfter = TimeSpan.FromMinutes(2);

    private readonly string _folder = Path.Join(AppPaths.Root, "osm");
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<StreetMapTile, LinkedListNode<(StreetMapTile Tile, BitmapSource Image)>> _memory = [];
    private readonly LinkedList<(StreetMapTile Tile, BitmapSource Image)> _recent = new();
    private readonly Dictionary<StreetMapTile, DateTime> _failed = [];
    private readonly HashSet<StreetMapTile> _loading = [];
    private List<StreetMapTile> _wanted = [];
    private int _working;

    public StreetMapTiles()
    {
        string version = typeof(StreetMapTiles).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
        _http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = Workers, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"OrthoStreamXP/{version} (X-Plane scenery tool; tile picker map)");
    }

    /// <summary>Raised on a worker thread when a tile asked for is ready.</summary>
    public event EventHandler? TileLoaded;

    /// <summary>The tile, when it is in memory.</summary>
    public BitmapSource? TryGet(StreetMapTile tile)
    {
        lock (_lock)
        {
            if (!_memory.TryGetValue(tile, out var node)) return null;
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>The tiles to load now, most wanted first. Replaces the previous request: tiles scrolled away are not loaded.</summary>
    public void Want(IReadOnlyList<StreetMapTile> tiles)
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            _wanted = [.. tiles.Where(t => !_memory.ContainsKey(t) && !_loading.Contains(t) && !(_failed.TryGetValue(t, out DateTime at) && now - at < RetryAfter))];
            _wanted.Reverse(); // taken from the end
            while (_working < Workers && _working < _wanted.Count)
            {
                _working++;
                _ = Task.Run(WorkAsync);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
        _stop.Dispose();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            StreetMapTile tile;
            CancellationToken token;
            lock (_lock)
            {
                if (_wanted.Count == 0 || _stop.IsCancellationRequested)
                {
                    _working--;
                    return;
                }
                tile = _wanted[^1];
                _wanted.RemoveAt(_wanted.Count - 1);
                _loading.Add(tile);
                token = _stop.Token;
            }
            BitmapSource? image = null;
            try
            {
                image = await LoadAsync(tile, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException
                                           or NotSupportedException or InvalidOperationException or ArgumentException or ObjectDisposedException)
            {
                // Offline, refused, closed or a damaged file: the map shows the coarser tile and tries again later.
            }
            lock (_lock)
            {
                _loading.Remove(tile);
                if (image is null) _failed[tile] = DateTime.UtcNow;
                else Remember(tile, image);
            }
            if (image is not null) TileLoaded?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<BitmapSource> LoadAsync(StreetMapTile tile, CancellationToken cancellationToken)
    {
        string path = Path.Join(_folder, tile.Zoom.ToString(CultureInfo.InvariantCulture), tile.X.ToString(CultureInfo.InvariantCulture),
            tile.Y.ToString(CultureInfo.InvariantCulture) + ".png");
        var file = new FileInfo(path);
        if (file.Exists && DateTime.UtcNow - file.LastWriteTimeUtc < KeepOnDisk)
            return Decode(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        byte[] bytes;
        try
        {
            bytes = await _http.GetByteArrayAsync(new Uri($"{Server}/{tile.Zoom}/{tile.X}/{tile.Y}.png"), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (file.Exists)
        {
            // Offline: an old tile is better than none.
            return Decode(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }
        BitmapSource image = Decode(bytes);
        AtomicFile.Write(path, bytes);
        return image;
    }

    private static BitmapImage Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Remember(StreetMapTile tile, BitmapSource image)
    {
        if (_memory.ContainsKey(tile)) return;
        _memory[tile] = _recent.AddFirst((tile, image));
        while (_recent.Count > MemoryTiles)
        {
            _memory.Remove(_recent.Last!.Value.Tile);
            _recent.RemoveLast();
        }
    }
}
