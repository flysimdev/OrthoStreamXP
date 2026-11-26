using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>A page that wants to refresh when it is shown.</summary>
internal interface IPage
{
    void OnShown();
}

/// <summary>A sidebar entry.</summary>
internal sealed record NavItem(string Title, string Glyph, IPage Page);

/// <summary>The window's own choices, kept in %LOCALAPPDATA%\OrthoStreamXP\window.json (not in the mount settings).</summary>
internal sealed class WindowPreferences
{
    private static readonly string FilePath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrthoStreamXP", "window.json");

    /// <summary>Start the mount as soon as the window opens.</summary>
    public bool AutoStart { get; set; }

    /// <summary>Download a mesh region's roads, forests and buildings (its overlays) with it.</summary>
    public bool DownloadOverlays { get; set; } = true;

    /// <summary>Light, dark, or the same as Windows.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; set; }

    public static WindowPreferences Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save the window preferences: {ex.Message}");
        }
    }
}

/// <summary>The window: the sidebar pages, the first-run setup, and the actions problems offer.</summary>
internal sealed class MainViewModel : ObservableObject
{
    private NavItem _selected;
    private SetupViewModel? _setup;

    public MainViewModel()
    {
        Preferences = WindowPreferences.Load();
        LogPage = new LogViewModel();
        Home = new HomeViewModel(this);
        Settings = new SettingsViewModel(this);
        Cache = new CacheViewModel(this);
        Packages = new PackagesViewModel(Mount, Preferences, MeshHttp, MeshInstalls, PackagesFolder);
        // The Build tab sets the packages folder through the Settings page, so that page shows it and checks it as its own box does.
        BuildScenery = new BuildSceneryViewModel(Mount, () => SettingsDocument.Load(SettingsDocument.DefaultPath).Get("xplane"), PackagesFolder,
            folder => Settings.PackagesFolder = folder, new SceneryBuildPreferencesStore(SceneryBuildPreferencesStore.DefaultPath));
        PackagesPage = new PackagesPageViewModel(Packages, BuildScenery);
        Scenery = new SceneryViewModel(Mount, () => SettingsDocument.Load(SettingsDocument.DefaultPath).Get("xplane"));
        Airports = new AirportsViewModel(Mount, () => SettingsDocument.Load(SettingsDocument.DefaultPath).Get("xplane"));
        About = new AboutViewModel();
        Navigation =
        [
            new NavItem("Home", "", Home),
            new NavItem("Settings", "", Settings),
            new NavItem("Cache", "", Cache),
            new NavItem("Packages", "", PackagesPage),
            new NavItem("X-Plane Custom Scenery", "", Scenery),
            new NavItem("Airports", "", Airports),
            new NavItem("Log", "", LogPage),
            new NavItem("About", "", About),
        ];
        _selected = Navigation[0];
        HandleProblem = new RelayCommand<ProblemAction>(Handle);
        Donate = new RelayCommand(() => Shell.Open(AboutViewModel.DonationPage));
        ChangeTheme = new RelayCommand(() => Theme = Theme.Next(WindowsTheme.AppsUseLight));
        Mount.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MountController.State)) Raise(nameof(SidebarStatus));
        };
    }

    public MountController Mount { get; } = new();
    public WindowPreferences Preferences { get; }
    public HomeViewModel Home { get; }
    public SettingsViewModel Settings { get; }
    public CacheViewModel Cache { get; }
    /// <summary>The regions of the mesh to download into the scenery packages folder.</summary>
    public PackagesViewModel Packages { get; }
    /// <summary>The client mesh downloads share, the setup's and the Packages page's.</summary>
    public HttpClient MeshHttp { get; } = PackagesViewModel.CreateHttpClient();
    /// <summary>One region install or delete at a time, from the Packages page or the setup.</summary>
    public InstallSlot MeshInstalls { get; } = new();
    /// <summary>Photo-terrain packages made from X-Plane's own Global Scenery, for the tiles picked on a map: the Packages page's Build tab.</summary>
    public BuildSceneryViewModel BuildScenery { get; }
    /// <summary>The Packages page: its Download and Build tabs.</summary>
    public PackagesPageViewModel PackagesPage { get; }
    /// <summary>X-Plane's scenery_packs.ini, to switch packages on or off.</summary>
    public SceneryViewModel Scenery { get; }
    /// <summary>Flattens the terrain under an airport, one at a time, and undoes it.</summary>
    public AirportsViewModel Airports { get; }
    public LogViewModel LogPage { get; }
    public AboutViewModel About { get; }
    public IReadOnlyList<NavItem> Navigation { get; }

    public NavItem SelectedNav
    {
        get => _selected;
        set
        {
            if (value is null || !Set(ref _selected, value)) return;
            Raise(nameof(CurrentPage));
            value.Page.OnShown();
        }
    }

    public IPage CurrentPage => _selected.Page;

    /// <summary>The first-run setup, shown instead of the pages while it is open.</summary>
    public SetupViewModel? Setup
    {
        get => _setup;
        private set
        {
            Set(ref _setup, value);
            Raise(nameof(InSetup));
        }
    }

    public bool InSetup => Setup is not null;

    /// <summary>"Running" or "Stopped", under the sidebar.</summary>
    public string SidebarStatus => Mount.State switch
    {
        MountState.Running => "Running",
        MountState.Starting => "Starting…",
        MountState.Stopping => "Stopping…",
        _ => "Stopped",
    };

    public ICommand HandleProblem { get; }

    /// <summary>Opens the donation page, from the sidebar.</summary>
    public ICommand Donate { get; }

    /// <summary>The sidebar's theme button: Windows, the other look than Windows', Windows' look kept fixed, back to Windows.</summary>
    public ICommand ChangeTheme { get; }

    /// <summary>The window's theme (the Windows one follows Windows): applied at once and remembered for the next start.</summary>
    public ThemeChoice Theme
    {
        get => ThemeChoice.Of(Preferences.Theme);
        private set
        {
            if (value.Theme == Preferences.Theme) return;
            Preferences.Theme = value.Theme;
            Preferences.Save();
            value.Theme.Apply();
            Raise();
        }
    }

    public void Show(IPage page) => SelectedNav = Navigation.First(n => n.Page == page);

    /// <summary>
    /// Called when the window has opened: setup on the first run, otherwise the auto-start, with the tiles AutoOrtho lacks
    /// added meanwhile to the regions installed without them (they only add files, so the streaming need not wait).
    /// </summary>
    public async Task OnOpenedAsync()
    {
        if (NeedsSetup())
        {
            RunSetup();
            return;
        }
        _ = AddLackingTilesAsync();
        if (Preferences.AutoStart) await Mount.StartAsync().ConfigureAwait(true);
    }

    private static string? PackagesFolder() => SettingsDocument.Load(SettingsDocument.DefaultPath).Get("base-packages");

    /// <summary>The tiles AutoOrtho lacks, added to the installed regions without them; Home shows it while it runs.</summary>
    private async Task AddLackingTilesAsync()
    {
        var progress = new Progress<InstallProgress>(Home.ShowTilesProgress);
        IReadOnlyList<string> added = [];
        try
        {
            var updater = new SupplementUpdater(MeshHttp, PackagesViewModel.Supplements(MeshHttp), MeshInstalls, PackagesFolder);
            added = await updater.RunAsync(progress, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Runs unawaited as the window opens: nothing that goes wrong may vanish, or stop the window.
            Log.Error("The tiles AutoOrtho lacks could not be added; they are added at the next start", ex);
        }
        finally
        {
            Home.ShowTilesAdded(added);
            Packages.FolderChanged();
        }
    }

    /// <summary>No settings file yet, or one without the folders a mount needs.</summary>
    public static bool NeedsSetup()
    {
        SettingsDocument settings = SettingsDocument.Load(SettingsDocument.DefaultPath);
        return settings.Get("cache") is null
               || (settings.Get("base-packages") ?? settings.Get("source")) is null
               || (settings.Get("xplane") ?? settings.Get("mountpoint")) is null;
    }

    public void RunSetup() => Setup = new SetupViewModel(this);

    public void CloseSetup() => Setup = null;

    /// <summary>The setup saved the settings: show them, and start when asked to.</summary>
    public async Task FinishSetupAsync(bool startNow)
    {
        Setup = null;
        Settings.Reload();
        SelectedNav = Navigation[0];
        Home.OnShown();
        if (startNow) await Mount.StartAsync().ConfigureAwait(true);
    }

    private void Handle(ProblemAction action)
    {
        switch (action)
        {
            case ProblemAction.OpenSettings:
                Show(Settings);
                break;
            case ProblemAction.RunSetup:
                RunSetup();
                break;
            case ProblemAction.DownloadWinFsp:
                Shell.Open(SystemCheck.WinFspDownload);
                break;
            case ProblemAction.OpenCustomScenery:
                if (SettingsDocument.Load(SettingsDocument.DefaultPath).Get("xplane") is { } xplane)
                    Shell.Open(Path.Join(xplane, "Custom Scenery"));
                break;
        }
    }
}
