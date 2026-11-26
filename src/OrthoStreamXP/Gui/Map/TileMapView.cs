using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OrthoGlobalXP.Airports;
using OrthoGlobalXP.Geo;
using GlobalCity = OrthoGlobalXP.Regions.City;

namespace OrthoStreamXP.Gui;

/// <summary>
/// The Build Scenery map: the world in Web Mercator as a grid of 1° tiles, over OpenStreetMap or plain. Tiles X-Plane's
/// Global Scenery has are shaded (or, over the street map, those it lacks are veiled). Zoomed in, the runways show, with the
/// finer textures the airports get. Cities are named, more as the map zooms in. Click picks or drops a tile, drag pans,
/// Shift+drag picks a rectangle, Ctrl+drag drops one, the wheel zooms. Its colours come from the brushes set on it (the
/// window's theme), so it follows light and dark.
/// </summary>
internal sealed class TileMapView : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(TileMapModel), typeof(TileMapView), new PropertyMetadata(null, OnModelChanged));

    public static readonly DependencyProperty ShowStreetMapProperty = DependencyProperty.Register(
        nameof(ShowStreetMap), typeof(bool), typeof(TileMapView), new PropertyMetadata(false, OnLookChanged));

    public static readonly DependencyProperty RunwaysProperty = DependencyProperty.Register(
        nameof(Runways), typeof(IReadOnlyList<Runway>), typeof(TileMapView), new PropertyMetadata(null, OnRunwaysChanged));

    public static readonly DependencyProperty AirportPreviewProperty = DependencyProperty.Register(
        nameof(AirportPreview), typeof(AirportZoomPreview), typeof(TileMapView), new PropertyMetadata(null, OnAirportPreviewChanged));

    public static readonly DependencyProperty CitiesProperty = DependencyProperty.Register(
        nameof(Cities), typeof(IReadOnlyList<GlobalCity>), typeof(TileMapView), new PropertyMetadata(null, OnLookChanged));

    public static readonly DependencyProperty ShowCitiesProperty = DependencyProperty.Register(
        nameof(ShowCities), typeof(bool), typeof(TileMapView), new PropertyMetadata(true, OnLookChanged));

    public static readonly DependencyProperty BackgroundProperty = BrushProperty(nameof(Background), Color.FromRgb(0x20, 0x20, 0x20));
    public static readonly DependencyProperty ForegroundProperty = BrushProperty(nameof(Foreground), Colors.White);
    public static readonly DependencyProperty PickedBrushProperty = BrushProperty(nameof(PickedBrush), Color.FromRgb(0x3D, 0x8B, 0xFD));
    public static readonly DependencyProperty BuiltBrushProperty = BrushProperty(nameof(BuiltBrush), Color.FromRgb(0x2F, 0xA3, 0x6B));
    public static readonly DependencyProperty BuildingBrushProperty = BrushProperty(nameof(BuildingBrush), Color.FromRgb(0xF0, 0xA0, 0x30));
    public static readonly DependencyProperty FailedBrushProperty = BrushProperty(nameof(FailedBrush), Color.FromRgb(0xE0, 0x52, 0x52));

    private const double ClickSlop = 4, MaxLatitude = 85.0511287798;
    // Cities are named from a zoom this much below the one Natural Earth gives (overlapping names are left out anyway).
    private const double CityZoomBias = 1;
    private const int MaxCityLabels = 400;
    // Scales are pixels across the whole world; pixels per degree of longitude are a 360th of that.
    private const double MinScale = 360 * 1.5, MaxScale = 256.0 * (1 << 16);
    // Pixels per degree from which the grid shows every degree, the tiles are named, the finer textures and the runways show.
    private const double GridFrom = 6, LabelsFrom = 56, FinerTexturesFrom = 40, RunwaysFrom = 90;
    private const int PreviewTilesPerFrame = 64;

    private static readonly Pen RunwayPen = Frozen(new Pen(Brushes.White, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen RunwayCasing = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), 4.5)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Typeface LabelFace = new("Segoe UI");
    private static readonly Typeface CityFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface MajorCityFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    // OpenStreetMap is light whatever the window's theme: dark text and lines with a white halo over it.
    private static readonly Color StreetInk = Color.FromRgb(0x1A, 0x22, 0x2C);
    private static readonly Brush StreetCityText = Frozen(new SolidColorBrush(StreetInk));
    private static readonly Pen StreetCityHalo = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 3) { LineJoin = PenLineJoin.Round });

    /// <summary>The finer textures: one zoom above the package's, two, three and beyond (the legend's colours).</summary>
    private static readonly Brush[] FinerFills =
        [Solid(Color.FromRgb(0xFF, 0xC8, 0x2E)), Solid(Color.FromRgb(0xE8, 0x4F, 0xD8)), Solid(Color.FromRgb(0xFF, 0x40, 0x40))];

    private readonly List<Run>[] _rows = [.. Enumerable.Range(0, 180).Select(_ => new List<Run>())];
    private readonly List<Edge> _sceneryEdges = [];
    private readonly Dictionary<TileCoordinate, IReadOnlyList<FinerTexture>> _preview = [];
    private readonly Dictionary<TileCoordinate, List<Runway>> _runwaysByTile = [];
    private readonly Dictionary<GlobalCity, Geometry> _cityLabels = [];
    private Palette? _palette;
    private StreetMapTiles? _streetMap;
    private bool _redrawQueued;

    private double _centerX = 0.5, _centerY = 0.5, _scale;
    private Point _pressedAt, _lastMouse;
    private DragMode _drag;
    private bool _moved;

    public TileMapView()
    {
        Focusable = true;
        ClipToBounds = true;
        Unloaded += (_, _) =>
        {
            _streetMap?.Dispose();
            _streetMap = null;
        };
    }

    private enum DragMode
    {
        None,
        Pan,
        Pick,
        Drop,
    }

    private enum Shade
    {
        NoScenery,
        Land,
        Picked,
        Built,
        PickedBuilt,
        Building,
        Failed,
    }

    /// <summary>The tile under the mouse, or null; for the line under the map.</summary>
    public event EventHandler<TileCoordinate?>? HoverChanged;

    public TileMapModel? Model
    {
        get => (TileMapModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public bool ShowStreetMap
    {
        get => (bool)GetValue(ShowStreetMapProperty);
        set => SetValue(ShowStreetMapProperty, value);
    }

    public IReadOnlyList<Runway>? Runways
    {
        get => (IReadOnlyList<Runway>?)GetValue(RunwaysProperty);
        set => SetValue(RunwaysProperty, value);
    }

    public AirportZoomPreview? AirportPreview
    {
        get => (AirportZoomPreview?)GetValue(AirportPreviewProperty);
        set => SetValue(AirportPreviewProperty, value);
    }

    /// <summary>The cities to name, most important first.</summary>
    public IReadOnlyList<GlobalCity>? Cities
    {
        get => (IReadOnlyList<GlobalCity>?)GetValue(CitiesProperty);
        set => SetValue(CitiesProperty, value);
    }

    public bool ShowCities
    {
        get => (bool)GetValue(ShowCitiesProperty);
        set => SetValue(ShowCitiesProperty, value);
    }

    /// <summary>The sea, and the map around the world (the plain map).</summary>
    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>The land, lines and names of the plain map.</summary>
    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush PickedBrush
    {
        get => (Brush)GetValue(PickedBrushProperty);
        set => SetValue(PickedBrushProperty, value);
    }

    public Brush BuiltBrush
    {
        get => (Brush)GetValue(BuiltBrushProperty);
        set => SetValue(BuiltBrushProperty, value);
    }

    public Brush BuildingBrush
    {
        get => (Brush)GetValue(BuildingBrushProperty);
        set => SetValue(BuildingBrushProperty, value);
    }

    public Brush FailedBrush
    {
        get => (Brush)GetValue(FailedBrushProperty);
        set => SetValue(FailedBrushProperty, value);
    }

    private double PixelsPerDegree => _scale / 360;

    private Palette Colours => _palette ??= new Palette(ColorOf(Background), ColorOf(Foreground), ColorOf(PickedBrush), ColorOf(BuiltBrush),
        ColorOf(BuildingBrush), ColorOf(FailedBrush));

    /// <summary>Centres the map on a point at the given zoom, or keeps the zoom when it is null.</summary>
    public void CenterOn(double lat, double lon, double? pixelsPerDegree = null)
    {
        _centerX = WorldX(lon);
        _centerY = WorldY(lat);
        if (pixelsPerDegree is { } ppd) _scale = Math.Clamp(ppd * 360, MinScale, MaxScale);
        InvalidateVisual();
    }

    /// <summary>Shows the whole box, with a margin, zoomed in no further than <paramref name="maxPixelsPerDegree"/>.</summary>
    public void Frame(double west, double south, double east, double north, double maxPixelsPerDegree = MaxScale / 360)
    {
        double x0 = WorldX(west), x1 = WorldX(east), y0 = WorldY(north), y1 = WorldY(south);
        _centerX = (x0 + x1) / 2;
        _centerY = (y0 + y1) / 2;
        double width = Math.Max(ActualWidth, 100), height = Math.Max(ActualHeight, 100);
        double fit = 0.85 * Math.Min(width / Math.Max(x1 - x0, 1e-9), height / Math.Max(y1 - y0, 1e-9));
        _scale = Math.Clamp(Math.Min(fit, maxPixelsPerDegree * 360), MinScale, MaxScale);
        InvalidateVisual();
    }

    /// <summary>Zooms around the middle of the map, for the zoom buttons.</summary>
    public void ZoomBy(double factor)
    {
        if (_scale <= 0) return;
        _scale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth, height = ActualHeight;
        Palette colours = Colours;
        drawingContext.DrawRectangle(colours.Outside, null, new Rect(0, 0, width, height));
        if (width <= 0 || height <= 0) return;
        if (_scale <= 0) _scale = Math.Max(MinScale, Math.Min(width, height));

        Rect world = new(ToScreen(-180, MaxLatitude), ToScreen(180, -MaxLatitude));
        if (ShowStreetMap) DrawStreetMap(drawingContext, width, height);
        else drawingContext.DrawRectangle(colours.Ocean, null, world);
        DrawTiles(drawingContext, width, height);
        DrawGrid(drawingContext, width, height);
        DrawSceneryEdges(drawingContext, width, height);
        if (PixelsPerDegree >= FinerTexturesFrom) DrawAirports(drawingContext, width, height);
        if (PixelsPerDegree >= LabelsFrom) DrawLabels(drawingContext, width, height);
        if (ShowCities) DrawCities(drawingContext, width, height);
        if (_drag is DragMode.Pick or DragMode.Drop && _moved)
            drawingContext.DrawRectangle(colours.RectangleFill, colours.RectanglePen, new Rect(_pressedAt, _lastMouse));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _pressedAt = _lastMouse = e.GetPosition(this);
        _moved = false;
        _drag = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? DragMode.Pick
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? DragMode.Drop
            : DragMode.Pan;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        _pressedAt = _lastMouse = e.GetPosition(this);
        _moved = true;
        _drag = DragMode.Pan;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point at = e.GetPosition(this);
        HoverChanged?.Invoke(this, TileAt(at));
        if (_drag == DragMode.None) return;
        if (!_moved && (at - _pressedAt).Length > ClickSlop) _moved = true;
        if (_drag == DragMode.Pan && _moved)
        {
            _centerX = Math.Clamp(_centerX - (at.X - _lastMouse.X) / _scale, 0, 1);
            _centerY = Math.Clamp(_centerY - (at.Y - _lastMouse.Y) / _scale, 0, 1);
        }
        _lastMouse = at;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag(e.GetPosition(this));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        EndDrag(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverChanged?.Invoke(this, null);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = DragMode.None;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Point at = e.GetPosition(this);
        double x = ScreenToWorldX(at.X), y = ScreenToWorldY(at.Y);
        _scale = Math.Clamp(_scale * Math.Pow(1.25, e.Delta / 120.0), MinScale, MaxScale);
        // Keep the point under the mouse where it was.
        _centerX = Math.Clamp(x - (at.X - ActualWidth / 2) / _scale, 0, 1);
        _centerY = Math.Clamp(y - (at.Y - ActualHeight / 2) / _scale, 0, 1);
        InvalidateVisual();
        // Over the map the wheel zooms, never scrolls the page.
        e.Handled = true;
    }

    private void EndDrag(Point at)
    {
        if (_drag == DragMode.None) return;
        DragMode drag = _drag;
        _drag = DragMode.None;
        ReleaseMouseCapture();
        if (Model is { } model)
        {
            if (drag == DragMode.Pan && !_moved && TileAt(at) is { } tile) model.Toggle(tile);
            else if (drag is DragMode.Pick or DragMode.Drop) model.Select(TilesIn(_pressedAt, at), drag == DragMode.Pick);
        }
        InvalidateVisual();
    }

    private static DependencyProperty BrushProperty(string name, Color fallback) => DependencyProperty.Register(
        name, typeof(Brush), typeof(TileMapView), new PropertyMetadata(Solid(fallback), OnPaletteChanged));

    private static void OnPaletteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TileMapView)d;
        view._palette = null;
        view._cityLabels.Clear();
        view.InvalidateVisual();
    }

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TileMapView)d;
        if (e.OldValue is TileMapModel old) old.Changed -= view.OnTilesChanged;
        if (e.NewValue is TileMapModel model) model.Changed += view.OnTilesChanged;
        view.RebuildRows();
    }

    private static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TileMapView)d).InvalidateVisual();

    private static void OnRunwaysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TileMapView)d;
        view._runwaysByTile.Clear();
        if (e.NewValue is IReadOnlyList<Runway> runways)
        {
            foreach (Runway runway in runways)
            {
                double lat = (runway.Lat1 + runway.Lat2) / 2, lon = (runway.Lon1 + runway.Lon2) / 2;
                if (Math.Abs(lat) >= 90 || Math.Abs(lon) >= 180) continue;
                var tile = new TileCoordinate((int)Math.Floor(lat), (int)Math.Floor(lon));
                if (!view._runwaysByTile.TryGetValue(tile, out List<Runway>? list)) view._runwaysByTile[tile] = list = [];
                list.Add(runway);
            }
        }
        view.InvalidateVisual();
    }

    private static void OnAirportPreviewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TileMapView)d;
        view._preview.Clear();
        view.InvalidateVisual();
    }

    private void OnTilesChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) RebuildRows();
        else Dispatcher.BeginInvoke(RebuildRows);
    }

    /// <summary>Redraws once, soon, however many tiles arrive meanwhile.</summary>
    private void QueueRedraw()
    {
        if (_redrawQueued) return;
        _redrawQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _redrawQueued = false;
            InvalidateVisual();
        });
    }

    /// <summary>Each row of tiles as runs of one shade, so a frame draws a few rectangles per row.</summary>
    private void RebuildRows()
    {
        TileMapModel? model = Model;
        for (int lat = -90; lat < 90; lat++)
        {
            List<Run> row = _rows[lat + 90];
            row.Clear();
            for (int lon = -180; lon < 180; lon++)
            {
                Shade shade = model is null ? Shade.NoScenery : ShadeOf(model, new TileCoordinate(lat, lon));
                if (row.Count > 0 && row[^1].Shade == shade && row[^1].East == lon - 1) row[^1] = row[^1] with { East = lon };
                else row.Add(new Run(lon, lon, shade));
            }
        }
        RebuildSceneryEdges(model);
        InvalidateVisual();
    }

    /// <summary>The outline of the tiles X-Plane has: every side between a tile it has and one it lacks (or the world's edge).</summary>
    private void RebuildSceneryEdges(TileMapModel? model)
    {
        _sceneryEdges.Clear();
        if (model is null || model.AvailableCount == 0) return;
        bool Has(int lat, int lon) => lat is >= -90 and < 90 && lon is >= -180 and < 180 && model.IsAvailable(new TileCoordinate(lat, lon));
        for (int lat = -90; lat < 90; lat++)
        {
            for (int lon = -180; lon < 180; lon++)
            {
                if (!Has(lat, lon)) continue;
                if (!Has(lat, lon - 1)) _sceneryEdges.Add(new Edge(lon, lat, lon, lat + 1));
                if (!Has(lat, lon + 1)) _sceneryEdges.Add(new Edge(lon + 1, lat, lon + 1, lat + 1));
                if (!Has(lat - 1, lon)) _sceneryEdges.Add(new Edge(lon, lat, lon + 1, lat));
                if (!Has(lat + 1, lon)) _sceneryEdges.Add(new Edge(lon, lat + 1, lon + 1, lat + 1));
            }
        }
    }

    private static Shade ShadeOf(TileMapModel model, TileCoordinate tile)
    {
        bool picked = model.IsSelected(tile);
        return model.StateOf(tile) switch
        {
            MapTileState.Building => Shade.Building,
            MapTileState.Failed => Shade.Failed,
            MapTileState.Built => picked ? Shade.PickedBuilt : Shade.Built,
            _ => picked ? Shade.Picked : model.IsAvailable(tile) ? Shade.Land : Shade.NoScenery,
        };
    }

    private void DrawStreetMap(DrawingContext dc, double width, double height)
    {
        _streetMap ??= CreateStreetMap();
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int zoom = Math.Clamp((int)Math.Round(Math.Log2(_scale * pixelsPerDip / 256)), 0, StreetMapTiles.MaxZoom);
        int n = 1 << zoom;
        int x0 = Math.Clamp((int)Math.Floor(ScreenToWorldX(0) * n), 0, n - 1), x1 = Math.Clamp((int)Math.Floor(ScreenToWorldX(width) * n), 0, n - 1);
        int y0 = Math.Clamp((int)Math.Floor(ScreenToWorldY(0) * n), 0, n - 1), y1 = Math.Clamp((int)Math.Floor(ScreenToWorldY(height) * n), 0, n - 1);
        var missing = new List<StreetMapTile>();
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var tile = new StreetMapTile(zoom, x, y);
                Rect rect = WorldRect(x, y, n, pixelsPerDip);
                if (_streetMap.TryGet(tile) is { } image)
                {
                    dc.DrawImage(image, rect);
                    continue;
                }
                missing.Add(tile);
                // Meanwhile, the part of a coarser tile in memory.
                for (int up = 1; up <= Math.Min(zoom, 8); up++)
                {
                    if (_streetMap.TryGet(new StreetMapTile(zoom - up, x >> up, y >> up)) is not { } coarser) continue;
                    dc.PushClip(new RectangleGeometry(rect));
                    dc.DrawImage(coarser, WorldRect(x >> up, y >> up, n >> up, pixelsPerDip));
                    dc.Pop();
                    break;
                }
            }
        }
        double cx = _centerX * n, cy = _centerY * n;
        _streetMap.Want([.. missing.OrderBy(t => (t.X + 0.5 - cx) * (t.X + 0.5 - cx) + (t.Y + 0.5 - cy) * (t.Y + 0.5 - cy))]);
    }

    private StreetMapTiles CreateStreetMap()
    {
        var tiles = new StreetMapTiles();
        tiles.TileLoaded += (_, _) => Dispatcher.BeginInvoke(QueueRedraw);
        return tiles;
    }

    private void DrawTiles(DrawingContext dc, double width, double height)
    {
        Brush?[] fills = ShowStreetMap ? Colours.StreetFill : Colours.PlainFill;
        var (west, north) = ToGeo(new Point(0, 0));
        var (east, south) = ToGeo(new Point(width, height));
        int firstLat = Math.Max((int)Math.Floor(south), -86), lastLat = Math.Min((int)Math.Floor(north), 85);
        for (int lat = firstLat; lat <= lastLat; lat++)
        {
            double top = ToScreen(0, Math.Min(lat + 1, MaxLatitude)).Y, bottom = ToScreen(0, Math.Max(lat, -MaxLatitude)).Y;
            foreach (Run run in _rows[lat + 90])
            {
                if (run.East + 1 < west || run.West > east || fills[(int)run.Shade] is not { } fill) continue;
                double left = ToScreen(Math.Max(run.West, west - 1), 0).X, right = ToScreen(Math.Min(run.East + 1, east + 1), 0).X;
                dc.DrawRectangle(fill, null, new Rect(left, top, right - left, bottom - top));
            }
        }
    }

    private void DrawGrid(DrawingContext dc, double width, double height)
    {
        var (west, north) = ToGeo(new Point(0, 0));
        var (east, south) = ToGeo(new Point(width, height));
        int step = PixelsPerDegree >= GridFrom ? 1 : 10;
        Palette colours = Colours;
        Pen degree = ShowStreetMap ? colours.StreetDegreePen : colours.DegreePen, tenDegrees = ShowStreetMap ? colours.StreetTenDegreePen : colours.TenDegreePen;
        double top = ToScreen(0, MaxLatitude).Y, bottom = ToScreen(0, -MaxLatitude).Y;
        for (int lon = (int)Math.Ceiling(Math.Max(west, -180) / step) * step; lon <= Math.Min(east, 180); lon += step)
        {
            double x = ToScreen(lon, 0).X;
            dc.DrawLine(lon % 10 == 0 ? tenDegrees : degree, new Point(x, top), new Point(x, bottom));
        }
        double left = ToScreen(-180, 0).X, right = ToScreen(180, 0).X;
        for (int lat = (int)Math.Ceiling(Math.Max(south, -85) / step) * step; lat <= Math.Min(north, 85); lat += step)
        {
            double y = ToScreen(0, lat).Y;
            dc.DrawLine(lat % 10 == 0 ? tenDegrees : degree, new Point(left, y), new Point(right, y));
        }
    }

    private void DrawSceneryEdges(DrawingContext dc, double width, double height)
    {
        if (_sceneryEdges.Count == 0) return;
        var (west, north) = ToGeo(new Point(0, 0));
        var (east, south) = ToGeo(new Point(width, height));
        Pen pen = ShowStreetMap ? Colours.StreetSceneryPen : Colours.SceneryPen;
        foreach (Edge edge in _sceneryEdges)
        {
            if (edge.East < west || edge.West > east || edge.North < south || edge.South > north) continue;
            dc.DrawLine(pen,
                ToScreen(edge.West, Math.Clamp(edge.South, -MaxLatitude, MaxLatitude)),
                ToScreen(edge.East, Math.Clamp(edge.North, -MaxLatitude, MaxLatitude)));
        }
    }

    /// <summary>The finer textures near airports (when the rules are on), then the runways over them.</summary>
    private void DrawAirports(DrawingContext dc, double width, double height)
    {
        var (west, north) = ToGeo(new Point(0, 0));
        var (east, south) = ToGeo(new Point(width, height));
        int firstLat = Math.Max((int)Math.Floor(south), -85), lastLat = Math.Min((int)Math.Floor(north), 84);
        int firstLon = Math.Max((int)Math.Floor(west), -180), lastLon = Math.Min((int)Math.Floor(east), 179);

        if (AirportPreview is { } preview && PixelsPerDegree >= FinerTexturesFrom)
        {
            int computed = 0;
            bool more = false;
            // Opaque inside one layer made translucent as a whole: overlapping textures do not darken each other.
            dc.PushOpacity(0.45);
            for (int lat = firstLat; lat <= lastLat; lat++)
            {
                for (int lon = firstLon; lon <= lastLon; lon++)
                {
                    var tile = new TileCoordinate(lat, lon);
                    if (!_preview.TryGetValue(tile, out IReadOnlyList<FinerTexture>? textures))
                    {
                        if (computed >= PreviewTilesPerFrame)
                        {
                            more = true;
                            continue;
                        }
                        _preview[tile] = textures = preview.TexturesOf(tile);
                        computed++;
                    }
                    foreach (FinerTexture texture in textures)
                    {
                        Brush fill = FinerFills[Math.Clamp(texture.Zoom - preview.BaseZoom - 1, 0, FinerFills.Length - 1)];
                        dc.DrawRectangle(fill, null, new Rect(ToScreen(texture.West, texture.North), ToScreen(texture.East, texture.South)));
                    }
                }
            }
            dc.Pop();
            if (more) QueueRedraw();
        }

        // Runways only once they are long enough to read as lines, not dots.
        if (PixelsPerDegree < RunwaysFrom) return;
        for (int lat = firstLat; lat <= lastLat; lat++)
        {
            for (int lon = firstLon; lon <= lastLon; lon++)
            {
                if (!_runwaysByTile.TryGetValue(new TileCoordinate(lat, lon), out List<Runway>? runways)) continue;
                foreach (Runway runway in runways)
                {
                    Point a = ToScreen(runway.Lon1, runway.Lat1), b = ToScreen(runway.Lon2, runway.Lat2);
                    dc.DrawLine(RunwayCasing, a, b);
                    dc.DrawLine(RunwayPen, a, b);
                }
            }
        }
    }

    private void DrawLabels(DrawingContext dc, double width, double height)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Brush brush = ShowStreetMap ? Colours.StreetLabel : Colours.Label;
        var (west, north) = ToGeo(new Point(0, 0));
        var (east, south) = ToGeo(new Point(width, height));
        for (int lat = Math.Max((int)Math.Floor(south), -85); lat <= Math.Min((int)Math.Floor(north), 84); lat++)
        {
            for (int lon = Math.Max((int)Math.Floor(west), -180); lon <= Math.Min((int)Math.Floor(east), 179); lon++)
            {
                var text = new FormattedText(new TileCoordinate(lat, lon).Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    LabelFace, Math.Min(14, PixelsPerDegree / 6), brush, pixelsPerDip);
                Point corner = ToScreen(lon, lat + 1);
                dc.DrawText(text, new Point(corner.X + 4, corner.Y + 3));
            }
        }
    }

    /// <summary>
    /// Names the cities big enough for the zoom, most important first, each beside its dot (right, else left); a name that
    /// would overlap one already placed is left out.
    /// </summary>
    private void DrawCities(DrawingContext dc, double width, double height)
    {
        if (Cities is not { } cities) return;
        double zoom = Math.Log2(_scale / 256) + CityZoomBias;
        Brush text = ShowStreetMap ? StreetCityText : Colours.CityText;
        Pen halo = ShowStreetMap ? StreetCityHalo : Colours.CityHalo;
        var placed = new List<Rect>();
        foreach (GlobalCity city in cities)
        {
            if (city.MinZoom > zoom || placed.Count >= MaxCityLabels) break;
            Point at = ToScreen(city.Lon, city.Lat);
            if (at.X < -100 || at.X > width + 100 || at.Y < -20 || at.Y > height + 20 || Math.Abs(city.Lat) > MaxLatitude) continue;
            Geometry label = CityLabel(city);
            Rect bounds = label.Bounds;
            double radius = city.IsCapital ? 3.2 : 2.4;
            Rect right = new(at.X + radius + 3, at.Y - bounds.Height / 2 - 1, bounds.Width + 2, bounds.Height + 2);
            Rect left = new(at.X - radius - 3 - bounds.Width - 2, right.Y, right.Width, right.Height);
            Rect box = Overlaps(placed, right) ? left : right;
            if (Overlaps(placed, box)) continue;
            placed.Add(box);
            placed.Add(new Rect(at.X - radius - 1, at.Y - radius - 1, 2 * radius + 2, 2 * radius + 2));
            dc.DrawEllipse(text, halo, at, radius, radius);
            dc.PushTransform(new TranslateTransform(box.X + 1 - bounds.X, box.Y + 1 - bounds.Y));
            dc.DrawGeometry(null, halo, label);
            dc.DrawGeometry(text, null, label);
            dc.Pop();
        }

        static bool Overlaps(List<Rect> placed, Rect box)
        {
            foreach (Rect other in placed)
            {
                if (other.IntersectsWith(box)) return true;
            }
            return false;
        }
    }

    /// <summary>A city's name as outlines (so it can have a halo), made once: capitals and the largest cities stand out.</summary>
    private Geometry CityLabel(GlobalCity city)
    {
        if (_cityLabels.TryGetValue(city, out Geometry? label)) return label;
        bool major = city.IsCapital || city.MinZoom < 4;
        var text = new FormattedText(city.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, major ? MajorCityFace : CityFace,
            city.IsCapital ? 13.5 : major ? 12.5 : 11.5, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        label = text.BuildGeometry(new Point(0, 0));
        label.Freeze();
        _cityLabels[city] = label;
        return label;
    }

    private IEnumerable<TileCoordinate> TilesIn(Point a, Point b)
    {
        var (lon1, lat1) = ToGeo(a);
        var (lon2, lat2) = ToGeo(b);
        int west = Math.Max((int)Math.Floor(Math.Min(lon1, lon2)), -180), east = Math.Min((int)Math.Floor(Math.Max(lon1, lon2)), 179);
        int south = Math.Max((int)Math.Floor(Math.Min(lat1, lat2)), -90), north = Math.Min((int)Math.Floor(Math.Max(lat1, lat2)), 89);
        for (int lat = south; lat <= north; lat++)
        {
            for (int lon = west; lon <= east; lon++) yield return new TileCoordinate(lat, lon);
        }
    }

    private TileCoordinate? TileAt(Point at)
    {
        var (lon, lat) = ToGeo(at);
        int tileLat = (int)Math.Floor(lat), tileLon = (int)Math.Floor(lon);
        return tileLat is >= -90 and <= 89 && tileLon is >= -180 and <= 179 && Math.Abs(lat) < MaxLatitude ? new TileCoordinate(tileLat, tileLon) : null;
    }

    /// <summary>Web Mercator from 0 at 180°W to 1 at 180°E.</summary>
    private static double WorldX(double lon) => (lon + 180) / 360;

    /// <summary>Web Mercator from 0 at the top (85.05°N) to 1 at the bottom.</summary>
    private static double WorldY(double lat)
    {
        double phi = Math.Clamp(lat, -MaxLatitude, MaxLatitude) * Math.PI / 180;
        return (1 - Math.Log(Math.Tan(Math.PI / 4 + phi / 2)) / Math.PI) / 2;
    }

    private double ScreenToWorldX(double x) => _centerX + (x - ActualWidth / 2) / _scale;

    private double ScreenToWorldY(double y) => _centerY + (y - ActualHeight / 2) / _scale;

    private Point ToScreen(double lon, double lat) =>
        new((WorldX(lon) - _centerX) * _scale + ActualWidth / 2, (WorldY(lat) - _centerY) * _scale + ActualHeight / 2);

    private (double Lon, double Lat) ToGeo(Point p)
    {
        double y = ScreenToWorldY(p.Y);
        return (ScreenToWorldX(p.X) * 360 - 180, Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y))) * 180 / Math.PI);
    }

    /// <summary>A street-map tile's place on screen, on whole device pixels so neighbours meet without a seam.</summary>
    private Rect WorldRect(int x, int y, int n, double pixelsPerDip)
    {
        double Snap(double value) => Math.Round(value * pixelsPerDip) / pixelsPerDip;
        double left = Snap(((double)x / n - _centerX) * _scale + ActualWidth / 2), right = Snap(((double)(x + 1) / n - _centerX) * _scale + ActualWidth / 2);
        double top = Snap(((double)y / n - _centerY) * _scale + ActualHeight / 2), bottom = Snap(((double)(y + 1) / n - _centerY) * _scale + ActualHeight / 2);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Color ColorOf(Brush? brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Gray;

    private static SolidColorBrush Solid(Color color) => Frozen(new SolidColorBrush(color));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Tiles <see cref="West"/> to <see cref="East"/> (inclusive) of one row, all of one shade.</summary>
    private readonly record struct Run(int West, int East, Shade Shade);

    /// <summary>One side of a tile, in degrees: west to east along a parallel, or south to north along a meridian.</summary>
    private readonly record struct Edge(int West, int South, int East, int North);

    /// <summary>
    /// The map's brushes and pens, made from the theme's colours: the plain map draws the land in a wash of the text colour
    /// over the background, and the picked, built, building and failed tiles in the accent and status colours; over the
    /// street map the tiles X-Plane has are left clear, the others greyed out and the states translucent. Either way an
    /// outline goes round the tiles X-Plane has.
    /// </summary>
    private sealed class Palette
    {
        public Palette(Color background, Color ink, Color picked, Color built, Color building, Color failed)
        {
            Color pickedBuilt = Mix(picked, built);
            Outside = Solid(background);
            Ocean = Solid(Mix(background, ink, 0.04));
            PlainFill = [null, Solid(Mix(background, ink, 0.22)), Solid(picked), Solid(built), Solid(pickedBuilt), Solid(building), Solid(failed)];
            // The tiles X-Plane lacks are greyed out (a light veil alone vanishes over OpenStreetMap's light colours).
            StreetFill = [Solid(WithAlpha(Mix(background, ink, 0.15), 0xB0)), null, Solid(WithAlpha(picked, 0x88)), Solid(WithAlpha(built, 0x80)),
                Solid(WithAlpha(pickedBuilt, 0x88)), Solid(WithAlpha(building, 0x90)), Solid(WithAlpha(failed, 0x90))];
            DegreePen = Frozen(new Pen(Solid(WithAlpha(ink, 0x22)), 1));
            TenDegreePen = Frozen(new Pen(Solid(WithAlpha(ink, 0x55)), 1));
            StreetDegreePen = Frozen(new Pen(Solid(WithAlpha(StreetInk, 0x50)), 1));
            StreetTenDegreePen = Frozen(new Pen(Solid(WithAlpha(StreetInk, 0x90)), 1));
            SceneryPen = Frozen(new Pen(Solid(WithAlpha(ink, 0xB0)), 1.5));
            StreetSceneryPen = Frozen(new Pen(Solid(WithAlpha(StreetInk, 0xD0)), 1.5));
            Label = Solid(WithAlpha(ink, 0xC0));
            StreetLabel = Solid(WithAlpha(StreetInk, 0xD0));
            CityText = Solid(ink);
            CityHalo = Frozen(new Pen(Solid(WithAlpha(background, 0xD0)), 3) { LineJoin = PenLineJoin.Round });
            RectangleFill = Solid(WithAlpha(picked, 0x30));
            RectanglePen = Frozen(new Pen(Solid(picked), 1.5) { DashStyle = DashStyles.Dash });
        }

        public Brush Outside { get; }
        public Brush Ocean { get; }

        /// <summary>A fill per <see cref="Shade"/>, null for none.</summary>
        public Brush?[] PlainFill { get; }
        public Brush?[] StreetFill { get; }
        public Pen DegreePen { get; }
        public Pen TenDegreePen { get; }
        public Pen StreetDegreePen { get; }
        public Pen StreetTenDegreePen { get; }

        /// <summary>The outline of the tiles X-Plane has.</summary>
        public Pen SceneryPen { get; }
        public Pen StreetSceneryPen { get; }
        public Brush Label { get; }
        public Brush StreetLabel { get; }
        public Brush CityText { get; }
        public Pen CityHalo { get; }
        public Brush RectangleFill { get; }
        public Pen RectanglePen { get; }

        private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

        private static Color Mix(Color a, Color b, double share = 0.5) => Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * share), (byte)Math.Round(a.G + (b.G - a.G) * share), (byte)Math.Round(a.B + (b.B - a.B) * share));
    }
}
