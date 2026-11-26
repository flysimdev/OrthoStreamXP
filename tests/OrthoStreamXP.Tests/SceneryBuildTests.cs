using OrthoGlobalXP.Airports;
using OrthoGlobalXP.Building;
using OrthoGlobalXP.Geo;
using OrthoGlobalXP.Regions;
using OrthoGlobalXP.Scenery;
using OrthoStreamXP.Gui;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tests;

/// <summary>
/// The Build Scenery page around OrthoGlobalXP's engine: its default choices and how they become the engine's options, the
/// choices file, picking tiles on the map and by region, and a build run through the engine from here.
/// </summary>
public sealed class SceneryBuildTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void The_defaults_are_ZL16_with_ZL18_within_2_km_of_runways_roads_kept_packed_and_no_ter_files()
    {
        PackageOptions options = SceneryBuildPreferences.ToPackageOptions(@"D:\Packages");

        Assert.Equal(@"D:\Packages", options.OutputFolder);
        Assert.Equal(16, options.Imagery.Zoom);
        Assert.Equal("BI", options.Imagery.ProviderCode);
        ZoomRule rule = Assert.Single(options.AirportZoom!.Rules);
        Assert.Equal(18, rule.Zoom);
        Assert.Equal(2, rule.RadiusKm);
        Assert.True(options.KeepOverlays);
        Assert.True(options.PackDsf);
        Assert.False(options.WriteTerrainFiles);
        Assert.False(options.Rebuild);
        Assert.Equal(PackageOptions.DefaultPackagePrefix, options.PackagePrefix);
        options.Validate();
    }

    [Fact]
    public void The_choices_are_kept_between_sessions()
    {
        var store = new SceneryBuildPreferencesStore(_tree.PathOf("scenery-build.json"));
        var chosen = new SceneryBuildPreferences
        {
            TargetFolder = @"E:\Scenery", ShowStreetMap = false, ShowCities = false, PickedTiles = ["+50+014", "+51+014"],
        };

        store.Save(chosen);
        SceneryBuildPreferences read = store.Load();

        Assert.Equal(chosen with { PickedTiles = [] }, read with { PickedTiles = [] });
        Assert.Equal(chosen.PickedTiles, read.PickedTiles);
    }

    [Fact]
    public void A_choices_file_from_when_the_build_options_were_choices_still_loads()
    {
        string old = _tree.WriteText("old.json", """
            { "TargetFolder": "E:\\Scenery", "Zoom": 17, "AirportZoomEnabled": false, "AirportZoom": 19, "AirportRadiusKm": 1.5,
              "KeepOverlays": false, "PackDsf": false, "WriteTerrainFiles": true, "ShowStreetMap": false, "PickedTiles": ["+50+014"] }
            """);

        SceneryBuildPreferences read = new SceneryBuildPreferencesStore(old).Load();

        Assert.Equal(@"E:\Scenery", read.TargetFolder);
        Assert.False(read.ShowStreetMap);
        Assert.Equal(["+50+014"], read.PickedTiles);
    }

    [Fact]
    public void A_missing_or_damaged_choices_file_gives_the_defaults()
    {
        Assert.Null(new SceneryBuildPreferencesStore(_tree.PathOf("none.json")).Load().TargetFolder);
        string damaged = _tree.WriteText("damaged.json", "{ not json");
        SceneryBuildPreferences read = new SceneryBuildPreferencesStore(damaged).Load();
        Assert.Null(read.TargetFolder);
        Assert.True(read.ShowStreetMap);
        Assert.Empty(read.PickedTiles);
    }

    [Fact]
    public void Only_tiles_X_Plane_has_can_be_picked_on_the_map()
    {
        var map = new TileMapModel();
        TileCoordinate land = new(50, 14), sea = new(40, -40);
        map.SetAvailable([land, new TileCoordinate(51, 14)]);

        map.Toggle(sea);
        map.Select([land, sea], true);

        Assert.Equal([land], map.Selected);
        map.Toggle(land);
        Assert.Empty(map.Selected);
        map.Select([land, new TileCoordinate(51, 14)], true);
        map.SetAvailable([land]);
        Assert.Equal([land], map.Selected);
    }

    [Fact]
    public void A_region_is_found_by_its_name_first_then_by_names_holding_it()
    {
        TileCoordinate tile = new(50, 10);
        Region[] regions =
        [
            new(RegionKind.State, "Bavaria", "Germany", [tile]),
            new(RegionKind.Country, "Germany", "Western Europe", [tile]),
            new(RegionKind.Subregion, "Western Europe", "Europe", [tile]),
            new(RegionKind.Continent, "Europe", "", [tile]),
            new(RegionKind.Country, "Réunion", "Eastern Africa", [tile]),
        ];

        Assert.Equal(["Germany", "Bavaria"], RegionSearch.Find(regions, "germany").Select(r => r.Name));
        Assert.Equal(["Europe", "Western Europe", "Germany"], RegionSearch.Find(regions, "europe").Select(r => r.Name));
        Assert.Equal(["Réunion"], RegionSearch.Find(regions, "reunion").Select(r => r.Name)); // accents aside
        // Without a search: continents, subregions and countries, larger first.
        Assert.Equal(["Europe", "Western Europe", "Germany", "Réunion"], RegionSearch.Find(regions, " ").Select(r => r.Name));
    }

    [Fact]
    public void An_area_chosen_from_the_list_stays_chosen_when_another_has_its_name()
    {
        TileCoordinate tile = new(33, -84);
        var search = new RegionSearch(
        [
            new(RegionKind.Country, "Georgia", "Western Asia", [new TileCoordinate(42, 43)]),
            new(RegionKind.State, "Georgia", "United States of America", [tile]),
        ]);
        search.Text = "georg";
        RegionItem state = search.Matches.Single(m => m.Region.Kind == RegionKind.State);

        search.Choose(state);

        Assert.Equal("Georgia", search.Text);
        Assert.Same(state, search.Selected);
        Assert.Equal(2, search.Matches.Count);
    }

    [Fact]
    public void The_region_catalogue_and_cities_load_from_the_engine()
    {
        Region germany = Assert.Single(RegionCatalog.All, r => r.Kind == RegionKind.Country && r.Name == "Germany");
        Assert.Contains(new TileCoordinate(50, 10), germany.Tiles); // Thuringia and Bavaria
        Assert.NotEmpty(CityCatalog.All);
    }

    [Fact]
    public async Task A_build_through_the_engine_reports_the_tiles_X_Plane_lacks_and_those_it_cannot_read()
    {
        string xplane = _tree.PathOf("X-Plane 12");
        string earthNavData = GlobalScenery.EarthNavDataOf(xplane);
        Directory.CreateDirectory(earthNavData);
        _tree.WriteBytes(Path.GetRelativePath(_tree.Root, Path.Join(earthNavData, "+50+010", "+50+014.dsf")), "not a DSF"u8.ToArray());
        string packages = _tree.PathOf("packages");
        PackageOptions options = SceneryBuildPreferences.ToPackageOptions(packages) with { AirportZoom = null, Workers = 1 };
        var progress = new List<TileProgress>();

        BuildSummary summary = await new PackageBuilder(new GlobalScenery(xplane), options)
            .BuildAsync([new TileCoordinate(50, 14), new TileCoordinate(40, -40)], new SyncProgress<TileProgress>(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal((0, 1, 1, false), (summary.Built, summary.NoSource, summary.Failed, summary.Cancelled));
        Assert.Contains(progress, p => p.Tile == new TileCoordinate(50, 14) && p.State == TileState.Failed);
        Assert.False(File.Exists(Path.Join(options.PackageFolderOf(Continent.Of(new TileCoordinate(50, 14))), "Earth nav data", "+50+010", "+50+014.dsf")));
    }

    [Fact]
    public async Task A_tile_only_in_the_Demo_Areas_is_listed_and_built_from_there()
    {
        string xplane = _tree.PathOf("X-Plane 12");
        Directory.CreateDirectory(GlobalScenery.EarthNavDataOf(xplane));
        string demoTile = Path.Join(GlobalScenery.DemoAreasEarthNavDataOf(xplane), "+20-160", "+21-158.dsf");
        _tree.WriteBytes(Path.GetRelativePath(_tree.Root, demoTile), "not a DSF"u8.ToArray());
        var scenery = new GlobalScenery(xplane);
        PackageOptions options = SceneryBuildPreferences.ToPackageOptions(_tree.PathOf("packages")) with { AirportZoom = null, Workers = 1 };

        BuildSummary summary = await new PackageBuilder(scenery, options)
            .BuildAsync([new TileCoordinate(21, -158)], null, TestContext.Current.CancellationToken);

        Assert.Equal([new TileCoordinate(21, -158)], scenery.Tiles());
        Assert.Equal(demoTile, scenery.PathOf(new TileCoordinate(21, -158)));
        // Read (and refused as no DSF) rather than reported as missing: the build found it in the Demo Areas.
        Assert.Equal((0, 1), (summary.NoSource, summary.Failed));
    }

    [Fact]
    public void Stream_from_here_makes_the_build_folder_OrthoStreamXPs_packages_folder()
    {
        string? packages = _tree.PathOf("streamed");
        string built = _tree.PathOf("built");
        var store = new SceneryBuildPreferencesStore(_tree.PathOf("scenery-build.json"));
        store.Save(new SceneryBuildPreferences { TargetFolder = built });
        var model = new BuildSceneryViewModel(new MountController(), () => null, () => packages, folder => packages = folder, store);
        Assert.False(model.IsPackagesFolder);
        Assert.True(model.StreamFromTarget.CanExecute(null));

        model.StreamFromTarget.Execute(null);

        Assert.Equal(built, packages);
        Assert.True(model.IsPackagesFolder);
        Assert.Equal(built, model.TargetFolder);
        Assert.Equal("", model.TargetNote);
        Assert.False(model.StreamFromTarget.CanExecute(null));
    }

    [Fact]
    public void Stream_from_here_keeps_the_build_folder_when_Settings_refuses_it()
    {
        string packages = _tree.PathOf("streamed");
        string built = _tree.PathOf("built");
        var store = new SceneryBuildPreferencesStore(_tree.PathOf("scenery-build.json"));
        store.Save(new SceneryBuildPreferences { TargetFolder = built });
        var model = new BuildSceneryViewModel(new MountController(), () => null, () => packages, _ => { }, store);

        model.StreamFromTarget.Execute(null);

        Assert.Equal(built, model.TargetFolder);
        Assert.False(model.IsPackagesFolder);
        Assert.Contains("cannot be the scenery packages folder", model.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\X\Packages", @"c:\x\packages\", true)]
    [InlineData(@"C:\X\Packages", @"C:\X\Other", false)]
    [InlineData("", @"C:\X", false)]
    public void The_packages_folder_is_recognised_whatever_its_case_or_last_slash(string a, string b, bool same) =>
        Assert.Equal(same, BuildSceneryViewModel.SamePath(a, b));

    /// <summary>Reports at once, on the reporting thread (Progress posts to a thread pool, after the build may have returned).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
