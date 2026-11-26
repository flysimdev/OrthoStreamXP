using OrthoGlobalXP.Airports;
using OrthoGlobalXP.Building;
using OrthoGlobalXP.Imagery;

namespace OrthoStreamXP.Setup;

/// <summary>
/// The Build Scenery page's choices: where the packages go, the map's layers and the picked tiles. How the tiles are made
/// is fixed, not a choice: ZL16 (AutoOrtho's layout), ZL18 within 2 km of a runway, X-Plane's roads, forests and buildings
/// kept, the photo DSFs packed, and no .ter files (OrthoStreamXP makes them from the names).
/// </summary>
internal sealed record SceneryBuildPreferences
{
    public const int Zoom = 16, AirportZoom = 18;
    public const double AirportRadiusKm = 2;

    /// <summary>The finer photos near airports as the engine takes them: <see cref="AirportZoom"/> within <see cref="AirportRadiusKm"/> of any land runway.</summary>
    public static AirportZoomOptions AirportZoomOptions { get; } = new() { Rules = [new ZoomRule(AirportZoom, AirportRadiusKm)] };

    /// <summary>The folder the packages are made in; null for OrthoStreamXP's packages folder (base-packages).</summary>
    public string? TargetFolder { get; init; }

    /// <summary>OpenStreetMap under the map's tiles.</summary>
    public bool ShowStreetMap { get; init; } = true;

    /// <summary>City names on the map.</summary>
    public bool ShowCities { get; init; } = true;

    /// <summary>The tiles picked on the map, by name (+50+014).</summary>
    public IReadOnlyList<string> PickedTiles { get; init; } = [];

    /// <summary>
    /// The engine's options for a build into <paramref name="outputFolder"/>. The provider code only names the textures
    /// (OrthoStreamXP downloads from its own provider setting), so it stays Bing's, as AutoOrtho's meshes have it.
    /// The tiles already made from the same Global Scenery with the same options are skipped.
    /// </summary>
    public static PackageOptions ToPackageOptions(string outputFolder) => new()
    {
        OutputFolder = outputFolder,
        Imagery = ImageryOptions.Default with { Zoom = Zoom },
        AirportZoom = AirportZoomOptions,
        KeepOverlays = true,
        PackDsf = true,
        WriteTerrainFiles = false,
        Rebuild = false,
    };
}
