# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OrthoStreamXP: a Windows-only WPF app (.NET 10, `net10.0-windows`) that mounts a read-only WinFsp virtual file system over the user's existing X-Plane 12 scenery packages and builds photo-scenery DDS textures on demand from online aerial imagery (Bing, Esri, Geoportal PL) as X-Plane reads them. Users must start it before X-Plane. See `README.md` for the user-facing description.

## Commands

Requires the .NET 10 SDK and WinFsp **with its Developer part** (the build references `%ProgramFiles(x86)%\WinFsp\bin\winfsp-msil.dll`; override with `-p:WinFspDir=...`).

```powershell
dotnet build OrthoStreamXP.slnx
dotnet test --solution OrthoStreamXP.slnx                       # Microsoft.Testing.Platform runner (global.json), xUnit v3 style
dotnet test --solution OrthoStreamXP.slnx --filter-method "*Name*"   # single test (MTP filter syntax; verify against installed runner)
.\build-installer.ps1 [-Version x.y.z]                          # publish + Velopack pack -> artifacts\releases (MSI, portable zip)
```

- `TreatWarningsAsErrors` and `AnalysisMode=Recommended` are on (`Directory.Build.props`): warnings fail the build.
- `MountSmokeTests` performs a real WinFsp mount with synthetic imagery; it is skipped locally if WinFsp is missing, but always runs when the `CI` env var is set. CI (`.github/workflows/ci.yml`) runs on Windows and installs WinFsp.
- Tests use `FakeTileServer` / `TempTree` helpers instead of the network.
- Releases: pushing a `v*` tag runs `.github/workflows/release.yml`, which needs `release-notes/<tag>.md` (pre-releases may skip it) and puts it above GitHub's generated notes. See `release-notes/README.md`.

## Branching

Don't commit anything to the repo

## Architecture

The program is the WPF window only (`Program.cs` runs the Velopack install/update/uninstall hooks, forces the Invariant culture for logs/settings, then starts `GuiHost`); there is no command-line interface and arguments are ignored. Settings live in `orthostreamxp.ini`: `SettingsOptions.Schema` (a `SettingsSchema`) declares every known setting, reads the file's `name = value` lines into `SettingValues`, and `MountSettings.FromValues` validates them (a bad one is a `SettingsException`).

Source layout under `src/OrthoStreamXP/`. Sub-folders are for navigation only: **namespaces stay at the top-level folder** (`OrthoStreamXP.Scenery`, `OrthoStreamXP.Gui`, ...; the root-level folders `Configuration`, `Mounting` and `Infrastructure` use `OrthoStreamXP`), so moving a file never needs `using`/XAML changes.

- `Configuration/` — `SettingsOptions`/`SettingsSchema` (the known settings and the ini reader), `SettingValues` (typed access), `SettingsException`; `MountSettings` turns the values into a mount's settings.
- `Mounting/` — `MountSession`: each scenery package gets its own `PackageMount` (overlay + build scheduler + load-ahead), sharing one downloader/encoder and the aircraft-position listener. `MountStatus` is the live snapshot for the UI.
- `Infrastructure/` — `Log`, `AtomicFile`.
- `Gui/` — `App`, `MainWindow`, `GuiHost`, `MountController` (drives a `MountSession` from the window) at the root; `Views/` (XAML + code-behind: Home, Setup, Settings, Cache, Airports, Log, About), `ViewModels/`, `Settings/`, `Home/` and `Airports/` (the types those pages show), `Mvvm/` (`ObservableObject`, `RelayCommand`, converters), `Map/` (cities, flight charts).
- `Scenery/` — the runtime streaming pipeline:
  - `FileSystem/`: `SceneryFileSystem` (WinFsp `FileSystemBase`) → `SceneryOverlay` (resolves a path to a backing file, a streamed photo texture, or an in-memory terrain definition); `SceneryPacks`, `TerrainDefinitions`.
  - `Streaming/`: `TextureStreamer` and its read/stand-in/build types, `TextureScheduler`, `TextureCatalog`, `ReadLog`.
  - `Paging/`: `PagingModel`, `PagingMeasurements`, `PagingCalibration`.
  - `Prefetch/`: `LoadAheadPrefetcher`, `PrefetchPlanner`, `MipWarmer`.
  - `Aircraft/`: `AircraftPositionListener` (X-Plane UDP), `ViewTracker`, `SceneryLoads`.
  - `Colour/`: `SeasonClock` (seasonal tint by simulated date/location), `ObservedSeasons` (loads the satellite record per ~500 km tile for `season = satellite`).
- `Textures/` — imagery to DDS: `TextureBuilder` (fetch → stitch → resize → sharpen → mipmaps → BC1 encode → atomic write; SkiaSharp is decode-only), `TextureQuality`, `Images`, `Sharpening` (unsharp mask on brightness, not a setting: 100 % for the areas' textures below ZL18, 60 % for the airports' ZL18 and finer) at the root; `Tiles/` (`TileSource`/`TileStore`: download + cache; the low-zoom base is zoom 13 and coarser because Bing's zoom 12 is other, lighter imagery; a tile every provider definitively lacks throws `TileNotAvailableException`, and `TextureBuilder` then makes it from a parent at hand, never coarser than 13), `Dds/` (`Dds` layout/encode/decode; mip levels by `MipFilter` = 2x2 averages in linear light; BC1 by `IspcBc1` = Intel's ISPC `ispc_texcomp.dll` from `native/ispc_texcomp/` (checked in, rebuilt by its `build.ps1`), loaded from the exe's folder, with one-colour blocks and four-colour mode fixed after it and every other block's colours refined by least squares (`Bc1Refinement`); `Dds.EncodeRangeFit` is the fallback without the DLL), `Scheduling/` (priority gates), `Seasons/` (`SeasonModel` calendar, `ObservedSeason` + `GreennessYear`: grass and fields by a place's yearly NDVI against its greenest month, trees by the calendar), `Vegetation/` (`VegetationSource`: NASA GIBS monthly VIIRS NDVI, downloaded and kept under the low-zoom base; `VegetationTile`, `NdviPalette`), `Colour/` (`Exposure`, `Contrast` (fixed 60 % S-curve, always on, not a setting), `ColourTable` = the shared BC1 lookup-table recolouring, `PhotoColours` = season → contrast → exposure as one table (rounded to 565 once) applied as X-Plane reads, also used by the Settings preview).
- `Setup/` — `Settings/` (`SettingsDocument`, ini under `%LOCALAPPDATA%\OrthoStreamXP`, `QualityPreset`, `ConnectionTest`), `Cache/` (inventory, areas, eviction), `Packages/` (downloads AutoOrtho's mesh regions from GitHub into the packages folder as `OrthoStream_*` packages: `MeshCatalog`, `AssetDownload`, `ZipUnpacker`, `OverlayPackage`, `RegionInstaller`; the tiles AutoOrtho lacks come from `flysimdev/orthostreamxp-scenery`'s latest-release `supplements.json` (`SupplementCatalog`, `RegionSupplement`) and are merged into their region without replacing any file, at install or, for regions installed before, by `Gui/Packages/SupplementUpdater` at each window start), `Airports/` (the Airports page's flattening, one airport at a time: `AptDatReader` lists the airports of an apt.dat, `AirportSources` + `AirportCatalog` pick each ident from the highest enabled package in scenery_packs.ini (`*GLOBAL_AIRPORTS*` = Global Scenery\Global Airports), `FlattenedAirports` keeps a copy of each flattened airport with `1302 flatten 1` in its own `Custom Scenery\OrthoStreamXP Flattened Airports` package listed above the airport's source; the source files are never changed, Undo removes the copy and the last one removes the package), `Installation/` (`AppPaths`, `AppUpdates`, `LegacyNames` migrating older settings/cache locations, `SystemCheck`, Velopack hooks).
- `Tuning/` — flight reports, facts and the preset/setting recommendations.
- Packages page — two tabs: "Build own packages" (`BuildSceneryView`, first and open at the start) and "Download kubilus1 packages" (`PackagesViewModel`, AutoOrtho's mesh regions), held by `Gui/ViewModels/PackagesPageViewModel`. The Build tab makes photo-terrain packages made from X-Plane's own Global Scenery (`GlobalScenery` reads `X-Plane 12 Global Scenery` and `X-Plane 12 Demo Areas`, a tile in both from the former) by OrthoGlobalXP's engine, `lib/OrthoGlobalXP.Core/OrthoGlobalXP.Core.dll` (compiled and checked in by its `build.ps1` from the sibling OrthoGlobalXP repo; needs the `SharpCompress` package at the engine's version). `Gui/ViewModels/BuildSceneryViewModel` drives `PackageBuilder`; the map is `Gui/Map/TileMapView` (Web Mercator over OpenStreetMap, `StreetMapTiles` cached in `%LOCALAPPDATA%\OrthoStreamXP\osm`) with `TileMapModel`; `Gui/Building/RegionSearch` picks by region or country; how tiles are made is fixed, not a choice (ZL16, ZL18 within 2 km of runways, roads kept, DSFs packed, no .ter files, up-to-date tiles skipped: `SceneryBuildPreferences.ToPackageOptions`); the page's choices (target folder, map layers, picked tiles) are `Setup/Building/SceneryBuildPreferences`, kept in `scenery-build.json`.
- Logs go to `%LOCALAPPDATA%\OrthoStreamXP\logs`.


## C# / .NET Engineering Guidelines

### General principles

- Inspect the existing solution and conventions before making changes.
- Prefer object-oriented design, composition, and explicit dependencies.
- Leverage the rule one class - one responsibility. The classes should be small and easy to understand.
- Avoid static state and static service classes unless justified by the framework or an established project convention and performance.
- Follow SOLID where it improves maintainability; avoid unnecessary abstractions and over engineering.
- Make the smallest change that correctly solves the task.
- Do not introduce dependencies without explaining why they are needed.

### C# standards

- Follow the project's TargetFramework and LangVersion.
- Prefer modern, idiomatic C# supported by the project.
- Enable and respect nullable reference types where configured.
- Use async/await for asynchronous I/O.
- Propagate CancellationToken through cancellable async operations.
- Dispose resources correctly; use IDisposable or IAsyncDisposable
  when appropriate.
- Handle exceptions intentionally. Do not silently swallow exceptions.
- Avoid unnecessary allocations, reflection, and LINQ complexity in performance-sensitive code.

### Architecture

- Prefer constructor injection for external dependencies.
- Define clear interfaces at genuine architectural boundaries.
- Keep public APIs small, explicit, and backward-compatible where required.
- Follow existing naming, folder structure, and dependency direction.
- Do not refactor unrelated code as part of a feature or bug fix.

### Testing

- Add or update tests for new behavior and bug fixes.
- Cover edge cases and failure paths where relevant.
- Follow the test framework already used by the solution.
- Never change a test merely to hide a genuine regression.
- Do not claim tests passed unless they were actually executed.

### Workflow

1. Inspect the relevant code and existing tests.
2. Summarize the intended change and any important trade-offs.
3. Implement the smallest maintainable solution.
4. Build the affected projects.
5. Run relevant tests.
6. Review the diff for regressions, unnecessary changes, and security issues.
7. Report changed files, validation results, and any remaining risks.

### Tool usage

- Use the repository's existing SDK, NuGet configuration, and build tools.
- Prefer existing project scripts and documented commands.
- Do not modify generated files unless explicitly required.
- Ask before destructive operations, dependency upgrades, or broad
  architectural changes.
- Never expose secrets, tokens, or credentials in logs or source code.

