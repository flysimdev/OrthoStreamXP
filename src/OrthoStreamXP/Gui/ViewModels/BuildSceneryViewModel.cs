using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using OrthoGlobalXP.Airports;
using OrthoGlobalXP.Building;
using OrthoGlobalXP.Geo;
using OrthoGlobalXP.Imagery;
using OrthoGlobalXP.Regions;
using OrthoGlobalXP.Scenery;
using OrthoStreamXP.Setup;
using GlobalCity = OrthoGlobalXP.Regions.City;

namespace OrthoStreamXP.Gui;

/// <summary>A box of the map to show, in degrees.</summary>
internal sealed record MapFrame(double West, double South, double East, double North);

/// <summary>
/// The Build Scenery page: photo-terrain packages made on this PC from X-Plane's own Global Scenery (OrthoGlobalXP's
/// engine). Tiles are picked on the map, by country or region, by name or from a list; each goes into its continent's
/// package (OrthoGlobal_Eur, _NA…) in the chosen folder, its roads, forests and buildings into OrthoGlobal_Overlays beside
/// them. OrthoStreamXP streams them from its next start when the folder is its packages folder.
/// </summary>
internal sealed class BuildSceneryViewModel : ObservableObject, IPage
{
    private const int MaxLogLines = 2000;

    private readonly MountController _mount;
    private readonly Func<string?> _xplane;
    private readonly Func<string?> _packagesFolder;
    private readonly Action<string> _setPackagesFolder;
    private readonly SceneryBuildPreferencesStore _store;
    private SceneryBuildPreferences _choices;
    private string? _loadedXPlane;
    private bool _picksRestored, _loading, _isBuilding;
    private string _hoverText = "", _status = "", _sceneryText = "";
    private int _progressValue, _progressMaximum = 1;
    private IReadOnlyList<Runway>? _runways;
    private AirportZoomPreview? _airportPreview;
    private Action? _stop;

    /// <param name="xplane">The X-Plane folder of the settings; null while there is none.</param>
    /// <param name="packagesFolder">OrthoStreamXP's packages folder (base-packages); null while there is none.</param>
    /// <param name="setPackagesFolder">
    /// Sets OrthoStreamXP's packages folder, as Settings does (which may refuse it: <paramref name="packagesFolder"/> tells).
    /// </param>
    public BuildSceneryViewModel(MountController mount, Func<string?> xplane, Func<string?> packagesFolder, Action<string> setPackagesFolder,
        SceneryBuildPreferencesStore store)
    {
        _mount = mount;
        _xplane = xplane;
        _packagesFolder = packagesFolder;
        _setPackagesFolder = setPackagesFolder;
        _store = store;
        _choices = store.Load();
        Regions = new RegionSearch(RegionCatalog.All);
        Map.Changed += (_, _) =>
        {
            Raise(nameof(SelectionText));
            RelayCommand.Refresh();
        };
        BrowseTarget = new RelayCommand(BrowseTargetFolder, () => IsIdle);
        StreamFromTarget = new RelayCommand(MakeTargetThePackagesFolder, () => IsIdle && _mount.IsStopped && TargetFolder.Length > 0 && !IsPackagesFolder);
        OpenTarget = new RelayCommand(() => Shell.Open(TargetFolder), () => Directory.Exists(TargetFolder));
        PickRegion = new RelayCommand(() => SelectRegion(true), () => IsIdle && Regions.Selected is not null && Map.AvailableCount > 0);
        DropRegion = new RelayCommand(() => SelectRegion(false), () => IsIdle && Regions.Selected is not null && Map.Selected.Count > 0);
        ClearPicks = new RelayCommand(Map.ClearSelection, () => IsIdle && Map.Selected.Count > 0);
        Build = new RelayCommand(() => _ = BuildAsync(), () => IsIdle && !_loading && Map.Selected.Count > 0 && TargetFolder.Length > 0);
        Stop = new RelayCommand(() => _stop?.Invoke(), () => IsBuilding && _stop is not null);
        _mount.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MountController.State)) return;
            Raise(nameof(RunningNote));
            Raise(nameof(TargetNote));
            RelayCommand.Refresh();
        };
    }

    /// <summary>Asks the view to show a part of the map.</summary>
    public event EventHandler<MapFrame>? FrameRequested;

    /// <summary>A build ended with tiles of the packages in this folder (built now or up to date already).</summary>
    public event EventHandler<string>? TilesBuilt;

    public TileMapModel Map { get; } = new();

    public RegionSearch Regions { get; }

    /// <summary>The cities the map names (Natural Earth's, in the engine).</summary>
    public IReadOnlyList<GlobalCity> Cities { get; } = CityCatalog.All;

    /// <summary>What the page did, newest last; also written to OrthoStreamXP's log.</summary>
    public ObservableCollection<string> BuildLog { get; } = [];

    public ICommand BrowseTarget { get; }
    /// <summary>Makes the target folder OrthoStreamXP's packages folder (Settings), so it streams what is built there.</summary>
    public ICommand StreamFromTarget { get; }
    public ICommand OpenTarget { get; }
    public ICommand PickRegion { get; }
    public ICommand DropRegion { get; }
    public ICommand ClearPicks { get; }
    public ICommand Build { get; }
    public ICommand Stop { get; }

    /// <summary>Whether X-Plane's Global Scenery was found, and how many tiles it has.</summary>
    public string SceneryText { get => _sceneryText; private set => Set(ref _sceneryText, value); }

    /// <summary>The folder the packages are made in: the one chosen, else OrthoStreamXP's packages folder.</summary>
    public string TargetFolder => _choices.TargetFolder ?? _packagesFolder() ?? "";

    /// <summary>The target is OrthoStreamXP's packages folder, so it streams the packages made there.</summary>
    public bool IsPackagesFolder => _packagesFolder() is { } packages && SamePath(packages, TargetFolder);

    /// <summary>A warning when OrthoStreamXP would not stream what is made in the target.</summary>
    public string TargetNote => TargetFolder.Length == 0
        ? "Choose a folder to build your packages in."
        : IsPackagesFolder ? ""
        : "OrthoStreamXP will not stream the packages built here: its scenery packages folder is a different folder. "
          + "Press \"Stream from here\" to make it this one"
          + (_mount.IsStopped ? "." : " (stop streaming first).");

    /// <summary>What the target folder is for.</summary>
    public string TargetFolderHelp { get; } =
        "Your own packages are built in this folder. Keep it for them only, apart from scenery packages from other sources.";

    public bool ShowStreetMap { get => _choices.ShowStreetMap; set => Change(_choices with { ShowStreetMap = value }); }

    public bool ShowCities { get => _choices.ShowCities; set => Change(_choices with { ShowCities = value }); }

    /// <summary>X-Plane's runways, once read; the map draws them.</summary>
    public IReadOnlyList<Runway>? Runways { get => _runways; private set => Set(ref _runways, value); }

    /// <summary>The finer textures the airport rule gives, for the map; null when it is off.</summary>
    public AirportZoomPreview? AirportPreview { get => _airportPreview; private set => Set(ref _airportPreview, value); }


    /// <summary>The tile under the mouse.</summary>
    public string HoverText { get => _hoverText; private set => Set(ref _hoverText, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }

    public int ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    public int ProgressMaximum { get => _progressMaximum; private set => Set(ref _progressMaximum, value); }

    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (!Set(ref _isBuilding, value)) return;
            Raise(nameof(IsIdle));
            RelayCommand.Refresh();
        }
    }

    public bool IsIdle => !IsBuilding;

    public string SelectionText => Map.Selected.Count switch
    {
        0 => "No tiles picked",
        1 => "1 tile picked",
        int n => Invariant($"{n:N0} tiles picked"),
    };

    /// <summary>While OrthoStreamXP runs, the packages made are streamed from its next start.</summary>
    public string RunningNote => _mount.IsStopped ? "" : "OrthoStreamXP is running: what you build now is streamed from its next start.";

    /// <summary>Reads X-Plane's Global Scenery and runways the first time, and again when the X-Plane folder changed.</summary>
    public void OnShown()
    {
        Raise(nameof(TargetFolder));
        Raise(nameof(IsPackagesFolder));
        Raise(nameof(TargetNote));
        if (!IsBuilding && !_loading && _xplane() != _loadedXPlane) _ = LoadGlobalSceneryAsync();
        else RefreshBuiltTiles();
    }

    public void SetHover(TileCoordinate? tile)
    {
        if (tile is not { } t)
        {
            HoverText = "";
            return;
        }
        string what = Map.StateOf(t) == MapTileState.Built ? "built" : Map.IsAvailable(t) ? "X-Plane scenery" : "no X-Plane scenery (sea)";
        HoverText = $"{t.Name}: {what} · {PackageOptions.DefaultPackagePrefix}_{Continent.Of(t).Suffix}";
    }

    /// <summary>Remembers the choices and the picked tiles, for the next start.</summary>
    public void SaveChoices()
    {
        if (_picksRestored) _choices = _choices with { PickedTiles = [.. Map.Selected.Select(t => t.Name).Order(StringComparer.Ordinal)] };
        _store.Save(_choices);
    }

    /// <summary>Takes a changed choice and remembers it; false when nothing changed.</summary>
    private bool Change(SceneryBuildPreferences choices, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (choices == _choices) return false;
        _choices = choices;
        Raise(name);
        _store.Save(_choices);
        return true;
    }

    private void SetTarget(string? folder)
    {
        Change(_choices with { TargetFolder = folder }, nameof(TargetFolder));
        Raise(nameof(IsPackagesFolder));
        Raise(nameof(TargetNote));
        RelayCommand.Refresh();
        RefreshBuiltTiles();
    }

    private void MakeTargetThePackagesFolder()
    {
        string folder = TargetFolder;
        _setPackagesFolder(folder);
        if (_packagesFolder() is { } packages && SamePath(packages, folder))
        {
            // Now "the packages folder", so the target follows a later change of it in Settings (as a folder chosen here would).
            SetTarget(null);
            Status = $"OrthoStreamXP's scenery packages folder is now {folder}: it streams the packages built here.";
            AddLog(Status);
            return;
        }
        Status = $"{folder} cannot be the scenery packages folder: choose X-Plane's folder first, and keep this folder out of X-Plane's Custom Scenery.";
        AddLog(Status, warning: true);
        SetTarget(folder);
    }

    private void BrowseTargetFolder()
    {
        if (Shell.PickFolder("Folder the scenery packages are made in (OrthoStreamXP's packages folder)", TargetFolder) is not { } folder) return;
        // The packages folder itself is kept as "the packages folder", so it follows a change of it in Settings.
        SetTarget(_packagesFolder() is { } packages && SamePath(packages, folder) ? null : folder);
    }

    private void SelectRegion(bool pick)
    {
        if (Regions.Selected?.Region is not { } region) return;
        int before = Map.Selected.Count;
        Map.Select(region.Tiles, pick);
        int changed = Math.Abs(Map.Selected.Count - before), withScenery = region.Tiles.Count(Map.IsAvailable);
        AddLog(pick
            ? Invariant($"Picked {changed:N0} tiles of {region.Name}: it touches {region.Tiles.Count:N0}, {withScenery:N0} with X-Plane scenery")
            : Invariant($"Unpicked {changed:N0} tiles of {region.Name}"));
        if (pick) FrameTiles(region.Tiles);
    }

    /// <summary>Shows the tiles, leaving out the farthest few (Alaska's Aleutians, overseas islands) so the bulk fills the map.</summary>
    private void FrameTiles(IReadOnlyCollection<TileCoordinate> tiles)
    {
        if (tiles.Count == 0) return;
        int[] lats = [.. tiles.Select(t => t.Lat).Order()], lons = [.. tiles.Select(t => t.Lon).Order()];
        int low = tiles.Count >= 50 ? tiles.Count / 50 : 0, high = tiles.Count - 1 - low;
        FrameRequested?.Invoke(this, new MapFrame(lons[low], lats[low], lons[high] + 1, lats[high] + 1));
    }

    private void UpdateAirportPreview()
    {
        try
        {
            AirportPreview = Runways is { Count: > 0 } runways ? new AirportZoomPreview(runways, SceneryBuildPreferences.AirportZoomOptions, SceneryBuildPreferences.Zoom) : null;
        }
        catch (ArgumentException ex)
        {
            AirportPreview = null;
            Status = ex.Message;
        }
    }

    private async Task LoadGlobalSceneryAsync()
    {
        string? folder = _xplane();
        _loadedXPlane = folder;
        if (folder is null)
        {
            Map.SetAvailable([]);
            SceneryText = "Choose your X-Plane folder in Settings first: its Global Scenery is what the packages are made from.";
            return;
        }
        _loading = true;
        SceneryText = "Reading X-Plane's Global Scenery…";
        RelayCommand.Refresh();
        try
        {
            IReadOnlyList<TileCoordinate> tiles = await Task.Run(() => new GlobalScenery(folder).Tiles()).ConfigureAwait(true);
            Map.SetAvailable(tiles);
            SceneryText = Invariant($"X-Plane's Global Scenery and Demo Areas have {tiles.Count:N0} tiles ({folder}). They are read, never changed.");
            RestorePicks();
            RefreshBuiltTiles();
            _ = LoadRunwaysAsync(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Map.SetAvailable([]);
            SceneryText = ex.Message;
            Log.Warning($"Build Scenery: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Runs unawaited from the page: nothing that goes wrong may vanish.
            Map.SetAvailable([]);
            SceneryText = $"X-Plane's Global Scenery could not be read: {ex.Message}";
            Log.Error("Build Scenery: X-Plane's Global Scenery could not be read", ex);
        }
        finally
        {
            _loading = false;
            RelayCommand.Refresh();
        }
    }

    /// <summary>The tiles picked in the last session, once X-Plane's are known (only those can be picked).</summary>
    private void RestorePicks()
    {
        if (_picksRestored) return;
        _picksRestored = true;
        TileCoordinate[] picked = [.. _choices.PickedTiles.Select(n => TileCoordinate.TryParse(n, out TileCoordinate t) ? t : (TileCoordinate?)null).OfType<TileCoordinate>()];
        if (picked.Length == 0) return;
        Map.Select(picked, true);
        FrameTiles(picked);
    }

    /// <summary>Reads X-Plane's runways in the background, for the map and the finer photos near airports.</summary>
    private async Task LoadRunwaysAsync(string folder)
    {
        Runways = null;
        UpdateAirportPreview();
        try
        {
            IReadOnlyList<Runway> runways = await Task.Run(() => AirportData.LoadRunwaysAsync(folder)).ConfigureAwait(true);
            if (folder != _loadedXPlane) return;
            Runways = runways;
            UpdateAirportPreview();
            if (runways.Count == 0) AddLog("X-Plane's apt.dat was not found: no finer photos near airports.", warning: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog($"X-Plane's apt.dat could not be read: {ex.Message}", warning: true);
        }
        catch (Exception ex)
        {
            // Runs unawaited: nothing that goes wrong may vanish.
            Log.Error("Build Scenery: X-Plane's runways could not be read", ex);
        }
    }

    private void RefreshBuiltTiles()
    {
        if (IsBuilding) return;
        try
        {
            Map.SetBuilt(TargetFolder.Length > 0 ? PackageTiles.Find(new PackageOptions { OutputFolder = TargetFolder }) : []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog($"The packages' tiles could not be listed: {ex.Message}", warning: true);
        }
    }

    private async Task BuildAsync()
    {
        PackageOptions options;
        GlobalScenery scenery;
        try
        {
            options = SceneryBuildPreferences.ToPackageOptions(TargetFolder);
            options.Validate();
            scenery = new GlobalScenery(_xplane() ?? throw new DirectoryNotFoundException("Choose your X-Plane folder in Settings first."));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Status = ex.Message;
            AddLog(ex.Message, warning: true);
            return;
        }
        SaveChoices();

        TileCoordinate[] tiles = [.. Map.Selected.OrderByDescending(t => t.Lat).ThenBy(t => t.Lon)];
        ProgressMaximum = tiles.Length;
        ProgressValue = 0;
        IsBuilding = true;
        using var cancel = new CancellationTokenSource();
        _stop = cancel.Cancel;
        RelayCommand.Refresh();
        string continents = string.Join(", ", tiles.GroupBy(Continent.Of).OrderBy(g => g.Key.Name, StringComparer.Ordinal)
            .Select(g => Invariant($"{g.Count():N0} in {options.PackageNameOf(g.Key)}")));
        AddLog(Invariant($"Building {tiles.Length:N0} tiles, {options.Workers} at once, into {options.OutputFolder}: {continents} (ZL{options.Imagery.Zoom}")
               + (options.AirportZoom is { } airports ? $", {airports}" : "") + ", "
               + (options.KeepOverlays ? $"X-Plane's roads, forests and buildings into {options.OverlayPackageName}" : "without X-Plane's roads, forests and buildings") + ")");
        Status = "Starting…";
        var progress = new Progress<TileProgress>(OnProgress);
        try
        {
            var builder = new PackageBuilder(scenery, options, Runways is { Count: > 0 } runways ? runways : null);
            BuildSummary summary = await Task.Run(() => builder.BuildAsync(tiles, progress, cancel.Token)).ConfigureAwait(true);
            Status = Invariant($"{(summary.Cancelled ? "Stopped" : "Done")} in {summary.Elapsed:hh\\:mm\\:ss}: {summary.Built:N0} built, {summary.UpToDate:N0} up to date, ")
                     + Invariant($"{summary.Failed:N0} failed, {summary.NoSource:N0} without X-Plane scenery.");
            AddLog(Status, warning: summary.Failed > 0);
            if (summary.Built + summary.UpToDate > 0) TilesBuilt?.Invoke(this, options.OutputFolder);
            if (summary.Built > 0 && IsPackagesFolder)
                AddLog(_mount.IsStopped
                    ? "Start OrthoStreamXP (before X-Plane) to stream the new tiles."
                    : "Stop and start OrthoStreamXP (with X-Plane closed) so it mounts the new tiles.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"The build stopped: {ex.Message}";
            AddLog(Status, warning: true);
        }
        catch (Exception ex)
        {
            // Runs unawaited from a button: nothing that goes wrong may vanish.
            Status = $"The build stopped: {ex.Message}";
            Log.Error("Build Scenery: the build stopped", ex);
            BuildLog.Add($"{DateTime.Now:HH:mm:ss}  {Status}");
        }
        finally
        {
            _stop = null;
            IsBuilding = false;
            RefreshBuiltTiles();
        }
    }

    private void OnProgress(TileProgress p)
    {
        switch (p.State)
        {
            case TileState.Building:
                Map.SetState(p.Tile, MapTileState.Building);
                Status = Invariant($"Building {p.Tile.Name}… ({ProgressValue:N0} of {ProgressMaximum:N0} done)");
                return;
            case TileState.Built:
                Map.SetState(p.Tile, MapTileState.Built);
                AddLog(Invariant($"{p.Tile.Name} built in {p.Elapsed.TotalSeconds:F1} s: {p.Message}"));
                break;
            case TileState.Failed:
                Map.SetState(p.Tile, MapTileState.Failed);
                AddLog($"{p.Tile.Name} failed: {p.Message}", warning: true);
                break;
            default:
                AddLog($"{p.Tile.Name}: {p.Message}");
                break;
        }
        ProgressValue++;
    }

    private void AddLog(string line, bool warning = false)
    {
        if (warning) Log.Warning($"Build Scenery: {line}");
        else Log.Info($"Build Scenery: {line}");
        BuildLog.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (BuildLog.Count > MaxLogLines) BuildLog.RemoveAt(0);
    }

    internal static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
