using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows.Input;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>A folder offered as a choice card in the setup.</summary>
internal sealed class FolderOption(string path, string description, Action<FolderOption> choose) : ObservableObject
{
    private bool _isSelected;

    public string Path { get; } = path;
    public string Description { get; } = description;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value) choose(this);
        }
    }

    public void Show(bool selected)
    {
        _isSelected = selected;
        Raise(nameof(IsSelected));
    }
}

/// <summary>
/// The first-run setup: checks WinFsp, finds X-Plane, asks for the folder of the person's own scenery packages,
/// lets them pick a cache folder and a quality, and optionally their SimBrief account (departure prefetch); then writes
/// orthostreamxp.ini.
/// </summary>
internal sealed class SetupViewModel : ObservableObject
{
    public static readonly string[] StepTitles = ["Welcome", "X-Plane", "Scenery", "Cache", "Quality", "SimBrief", "Ready"];

    private readonly MainViewModel _main;
    private int _step;
    private bool _winFsp;
    private string? _xplane, _packages;
    private string _cache;
    private string _simBriefUser;
    private Choice _simBriefLimit;
    private bool _simBriefAlternate;
    /// <summary>The cache folder came from the settings or the person: it no longer follows the packages folder.</summary>
    private bool _cacheChosen;
    private QualityPreset _preset = PresetMemory.Recommended.Preset;
    private bool _startNow;
    private string? _error;
    /// <summary>Downloads at once the connection test suggested in this setup, saved when it finishes.</summary>
    private int? _testedWorkers;

    public SetupViewModel(MainViewModel main)
    {
        _main = main;
        SettingsDocument existing = SettingsDocument.Load(SettingsDocument.DefaultPath);
        _winFsp = SystemCheck.WinFspInstalled();
        // Before the folders are chosen below: each choice tells it where regions would go.
        Downloads = new PackagesViewModel(_main.Mount, _main.Preferences, _main.MeshHttp, _main.MeshInstalls, () => DownloadFolder, RegionInstalled);

        XPlaneOptions = [.. SystemCheck.FindXPlaneFolders().Select(f => new FolderOption(f, "X-Plane found here", ChooseXPlane))];
        // Which packages to use is the person's own choice: nothing is searched for, only the folder chosen before is offered.
        PackageOptions = [];
        Presets = [.. QualityPreset.All.Select(p => new PresetOption(p, preset => Preset = preset))];
        // Re-running the setup starts from the current settings; a first setup suggests the cache beside the packages.
        _cacheChosen = existing.Get("cache") is not null;
        _cache = existing.Get("cache") ?? SystemCheck.SuggestedCacheFolder(null);
        Select(XPlaneOptions, existing.Get("xplane") ?? XPlaneOptions.FirstOrDefault()?.Path, ChooseXPlane, "X-Plane found here");
        Select(PackageOptions, existing.Get("base-packages"), ChoosePackages, null);
        PresetMemory.Load(existing.Get("cache"));
        foreach (PresetOption option in Presets) option.RefreshMemory();
        QualityPreset recommended = PresetMemory.Recommended.Preset;
        Preset = QualityPreset.ForLevel(existing.GetInt("level-change") ?? recommended.Level) ?? recommended;
        IsRerun = existing.Get("cache") is not null;
        _simBriefUser = existing.Get("simbrief-user") ?? "";
        SimBriefTest = new SimBriefTestViewModel(_main.MeshHttp, () => SimBriefUser);
        int? limit = existing.GetInt("simbrief-download-limit");
        SimBriefLimits = SimBriefText.LimitChoices(limit);
        string chosen = (limit ?? MountSettings.DefaultSimBriefDownloadLimitMb).ToString(CultureInfo.InvariantCulture);
        _simBriefLimit = SimBriefLimits.First(c => c.Value == chosen);
        _simBriefAlternate = existing.GetBool("simbrief-alternate") == true;
        ConnectionTest = new ConnectionTestViewModel(
            () => existing.Get("texture-provider") ?? "bing,esri",
            () => _main.Mount.State == MountState.Stopped,
            result =>
            {
                if (result.Suggested > 0) _testedWorkers = result.Suggested;
                // The recommendation follows the measured speed, and is chosen for the person (they may pick another).
                PresetMemory.Load(_cache);
                foreach (PresetOption option in Presets) option.RefreshMemory();
                Preset = PresetMemory.Recommended.Preset;
            });

        Back = new RelayCommand(() => Step--, () => Step > 0);
        Next = new RelayCommand(() => Step++, () => Step < StepTitles.Length - 1 && CanLeave(Step));
        Finish = new RelayCommand(() => _ = FinishAsync(), () => Step == StepTitles.Length - 1);
        Cancel = new RelayCommand(_main.CloseSetup);
        CheckWinFsp = new RelayCommand(() =>
        {
            _winFsp = SystemCheck.WinFspInstalled();
            Raise(nameof(WinFspOk));
            Raise(nameof(WinFspText));
        });
        GetWinFsp = new RelayCommand(() => Shell.Open(SystemCheck.WinFspDownload));
        BrowseXPlane = new RelayCommand(() =>
        {
            if (Shell.PickFolder("Choose the folder X-Plane is installed in", _xplane) is { } folder)
                Select(XPlaneOptions, folder, ChooseXPlane, SystemCheck.IsXPlaneFolder(folder) ? "Chosen by you" : "Not an X-Plane folder");
        });
        BrowsePackages = new RelayCommand(() =>
        {
            if (Shell.PickFolder("Choose the folder that holds your scenery packages", _packages) is { } folder)
                Select(PackageOptions, folder, ChoosePackages, null);
        });
        BrowseCache = new RelayCommand(() =>
        {
            if (Shell.PickFolder("Choose a folder for the cache", CacheFolder) is { } folder) CacheFolder = folder;
        });
        ShowDownloads = new RelayCommand(() =>
        {
            DownloadsShown = true;
            Downloads.OnShown();
        }, () => !DownloadsShown);
    }

    /// <summary>The regions of the mesh, for someone without scenery packages: installed into the folder chosen, or a suggested one.</summary>
    public PackagesViewModel Downloads { get; }

    public ICommand ShowDownloads { get; }

    public bool DownloadsShown
    {
        get => _downloadsShown;
        private set
        {
            if (Set(ref _downloadsShown, value)) Raise(nameof(PackagesMissing));
        }
    }

    private bool _downloadsShown;

    /// <summary>The packages folder chosen, when it can hold packages; otherwise the one the setup suggests.</summary>
    private string DownloadFolder =>
        _packages is not null && XPlaneOk && !SystemCheck.IsInCustomScenery(_packages, _xplane) ? _packages : SystemCheck.SuggestedPackagesFolder();

    /// <summary>A region is installed: its folder becomes the packages folder, its card telling the packages now in it.</summary>
    private void RegionInstalled(string folder)
    {
        FolderOption? old = PackageOptions.FirstOrDefault(o => string.Equals(o.Path, folder, StringComparison.OrdinalIgnoreCase));
        if (old is not null) PackageOptions.Remove(old);
        Select(PackageOptions, folder, ChoosePackages, null);
    }

    public ObservableCollection<FolderOption> XPlaneOptions { get; }
    public ObservableCollection<FolderOption> PackageOptions { get; }
    public IReadOnlyList<PresetOption> Presets { get; }
    /// <summary>The SimBrief step's "Test SimBrief connection".</summary>
    public SimBriefTestViewModel SimBriefTest { get; }
    /// <summary>The connection test above the presets: the recommendation follows its speed.</summary>
    public ConnectionTestViewModel ConnectionTest { get; }
    public ICommand Back { get; }
    public ICommand Next { get; }
    public ICommand Finish { get; }
    public ICommand Cancel { get; }
    public ICommand CheckWinFsp { get; }
    public ICommand GetWinFsp { get; }
    public ICommand BrowseXPlane { get; }
    public ICommand BrowsePackages { get; }
    public ICommand BrowseCache { get; }

    /// <summary>Settings exist already: the setup can be closed without finishing.</summary>
    public bool IsRerun { get; }

    public int Step
    {
        get => _step;
        set
        {
            if (!Set(ref _step, Math.Clamp(value, 0, StepTitles.Length - 1))) return;
            Raise(nameof(IsLastStep));
            Raise(nameof(Steps));
            RelayCommand.Refresh();
        }
    }

    public bool IsLastStep => Step == StepTitles.Length - 1;

    /// <summary>The step indicator: each step's number, title and whether it is done, current or still to come.</summary>
    public IReadOnlyList<SetupStep> Steps => [.. StepTitles.Select((title, i) => new SetupStep(i + 1, title, i < Step, i == Step))];

    public bool WinFspOk => _winFsp;

    public string WinFspText => _winFsp
        ? "WinFsp is installed."
        : "WinFsp is not installed yet. It is a free driver OrthoStreamXP uses to show the scenery to X-Plane. Install it, then press Check again. Restart your computer before the first flight: WinFsp works only after a restart.";

    public bool XPlaneOk => SystemCheck.IsXPlaneFolder(_xplane);

    public string XPlaneHint => XPlaneOptions.Count == 0 && _xplane is null
        ? "X-Plane was not found automatically. Choose the folder it is installed in (the one with X-Plane.exe)."
        : XPlaneOk ? "OrthoStreamXP shows your scenery packages in this X-Plane's Custom Scenery folder." : "That folder has no Custom Scenery folder: choose the folder X-Plane is installed in.";

    public bool PackagesOk => XPlaneOk && !SystemCheck.IsInCustomScenery(_packages, _xplane) && SystemCheck.PackagesIn(_packages).Count > 0;

    /// <summary>A folder was chosen and it has no packages (shown in red; before a choice the hint is just a hint).</summary>
    public bool PackagesWrong => _packages is not null && !PackagesOk;

    /// <summary>
    /// The folder chosen can be used but holds no packages: the download is offered right under the message, until the
    /// list of regions is open.
    /// </summary>
    public bool PackagesMissing =>
        !DownloadsShown && _packages is not null && XPlaneOk && !SystemCheck.IsInCustomScenery(_packages, _xplane) && SystemCheck.PackagesIn(_packages).Count == 0;

    public string PackagesHint
    {
        get
        {
            if (_packages is null)
                return PackageOptions.Count == 0
                    ? "Choose the folder that holds your scenery packages."
                    : "Choose where your scenery packages are.";
            if (!XPlaneOk) return SystemCheck.XPlaneFirstExplanation;
            if (SystemCheck.IsInCustomScenery(_packages, _xplane)) return SystemCheck.InCustomSceneryExplanation;
            IReadOnlyList<string> found = SystemCheck.PackagesIn(_packages);
            if (found.Count == 0)
                return "That folder has no scenery packages: none of its folders has an Earth nav data folder. " +
                       "Choose the folder that holds your packages, not a package itself. No packages yet? Download a region below.";
            IReadOnlyList<string> taken = SystemCheck.TakenInCustomScenery(_xplane, found);
            return taken.Count == 0
                ? "OrthoStreamXP adds photos to these packages while it runs. They stay unchanged on disk."
                : $"X-Plane's Custom Scenery already has {string.Join(", ", taken)}. Close any other program that shows these packages to X-Plane before you start OrthoStreamXP.";
        }
    }

    public QualityPreset Preset
    {
        get => _preset;
        private set
        {
            _preset = value;
            foreach (PresetOption option in Presets) option.Show(option.Preset == value);
            Raise();
        }
    }

    /// <summary>The cache folder; set from the window (typed or browsed), it is the person's choice from then on.</summary>
    public string CacheFolder
    {
        get => _cache;
        set
        {
            _cacheChosen = true;
            SetCache(value);
        }
    }

    private void SetCache(string value)
    {
        if (!Set(ref _cache, value.Trim(), nameof(CacheFolder))) return;
        Raise(nameof(CacheOk));
        Raise(nameof(CacheWrong));
        Raise(nameof(CacheHint));
        Raise(nameof(Summary));
        RelayCommand.Refresh();
    }

    /// <summary>The cache folder is a full path (it is created when the setup finishes).</summary>
    public bool CacheOk => CacheFolder.Length > 0 && Path.IsPathFullyQualified(CacheFolder);

    /// <summary>Something was typed that is not a full path (shown in red).</summary>
    public bool CacheWrong => CacheFolder.Length > 0 && !CacheOk;

    public string CacheHint
    {
        get
        {
            if (!CacheOk)
                return CacheWrong
                    ? "Type a full path, such as D:\\OrthoStreamXP\\Cache, or press Browse."
                    : "Choose a folder on a drive with plenty of free space.";
            return SystemCheck.FreeBytes(CacheFolder) is { } free
                ? $"{CacheInventory.FormatBytes(free)} free on this drive. Each new area you fly takes up to about 8 GB; a fast SSD is best."
                : "Choose a folder on a drive with plenty of free space.";
        }
    }

    /// <summary>SimBrief username or Pilot ID (optional): the departure of the latest flight plan is built ahead.</summary>
    public string SimBriefUser
    {
        get => _simBriefUser;
        set
        {
            if (!Set(ref _simBriefUser, value?.Trim() ?? "")) return;
            SimBriefTest.Reset();
            Raise(nameof(HasSimBriefUser));
            Raise(nameof(Summary));
        }
    }

    /// <summary>The limit and the alternate apply only with a username.</summary>
    public bool HasSimBriefUser => SimBriefUser.Length > 0;

    /// <summary>The SimBrief step's download limits; a value set by hand in the file is one of them.</summary>
    public IReadOnlyList<Choice> SimBriefLimits { get; }

    /// <summary>The most the SimBrief prefetch may download per session (<c>simbrief-download-limit</c>).</summary>
    public Choice SimBriefLimit
    {
        get => _simBriefLimit;
        set
        {
            if (value is not null && Set(ref _simBriefLimit, value)) Raise(nameof(Summary));
        }
    }

    /// <summary>Build the alternate airport too (<c>simbrief-alternate</c>).</summary>
    public bool SimBriefAlternate
    {
        get => _simBriefAlternate;
        set
        {
            if (Set(ref _simBriefAlternate, value)) Raise(nameof(Summary));
        }
    }


    public bool StartNow
    {
        get => _startNow;
        set => Set(ref _startNow, value);
    }

    public string Summary =>
        $"X-Plane: {_xplane}\nScenery packages: {string.Join(", ", SystemCheck.PackagesIn(_packages))} in {_packages}\n" +
        $"Quality: {Preset.Name}\nCache: {CacheFolder}\n" +
        $"SimBrief: {(SimBriefUser.Length > 0 ? $"on (your flight's scenery is built ahead, {LimitShort}{(SimBriefAlternate ? ", with the alternate" : "")})" : "not used")}";

    private string LimitShort => SimBriefLimit.Value == "0" ? "no download limit" : $"up to {SimBriefLimit.Value} MB per session";

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    private bool CanLeave(int step) => step switch
    {
        0 => WinFspOk,
        1 => XPlaneOk,
        2 => PackagesOk,
        3 => CacheOk,
        _ => true,
    };

    private void ChooseXPlane(FolderOption option)
    {
        _xplane = option.Path;
        foreach (FolderOption other in XPlaneOptions) other.Show(other == option);
        Raise(nameof(XPlaneOk));
        Raise(nameof(XPlaneHint));
        Raise(nameof(PackagesOk));
        Raise(nameof(PackagesWrong));
        Raise(nameof(PackagesHint));
        Raise(nameof(PackagesMissing));
        Raise(nameof(Summary));
        Downloads.FolderChanged();
        RelayCommand.Refresh();
    }

    private void ChoosePackages(FolderOption option)
    {
        _packages = option.Path;
        if (!_cacheChosen) SetCache(SystemCheck.SuggestedCacheFolder(_packages));
        foreach (FolderOption other in PackageOptions) other.Show(other == option);
        Raise(nameof(PackagesOk));
        Raise(nameof(PackagesWrong));
        Raise(nameof(PackagesHint));
        Raise(nameof(PackagesMissing));
        Raise(nameof(Summary));
        Downloads.FolderChanged();
        RelayCommand.Refresh();
    }

    /// <summary>Selects a folder, adding it to the choices when it is not one of them.</summary>
    private static void Select(ObservableCollection<FolderOption> options, string? path, Action<FolderOption> choose, string? description)
    {
        if (path is null) return;
        FolderOption? option = options.FirstOrDefault(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            option = new FolderOption(path, description ?? Describe(path), choose);
            options.Add(option);
        }
        choose(option);
    }

    private static string Describe(string folder)
    {
        IReadOnlyList<string> found = SystemCheck.PackagesIn(folder);
        return found.Count == 0 ? "No scenery packages in this folder" : string.Join(", ", found);
    }

    private async Task FinishAsync()
    {
        try
        {
            string path = SettingsDocument.DefaultPath;
            SettingsDocument settings;
            if (File.Exists(path))
            {
                // Keep everything else the person has set; change only what the setup asked about.
                settings = SettingsDocument.Load(path);
                settings.Set("xplane", _xplane);
                settings.Set("base-packages", _packages);
                settings.Set("source", null);
                settings.Set("mountpoint", null);
                settings.Set("cache", CacheFolder);
                Tuning.TunedSettings.ChoosePreset(settings, Preset, Tuning.TunedStore.Load(path));
                if (settings.Get("texture-provider") is null) settings.Set("texture-provider", "bing,esri");
                if (settings.Get("track-aircraft") is null) settings.Set("track-aircraft", "true");
                if (settings.Get("download-workers") is null) settings.Set("download-workers", "128");
            }
            else
            {
                settings = SettingsDocument.CreateNew(path, _xplane!, _packages!, CacheFolder, Preset);
            }
            if (_testedWorkers is { } workers) settings.Set("download-workers", workers);
            settings.Set("simbrief-user", SimBriefUser.Length > 0 ? SimBriefUser : null);
            // Defaults stay out of the file, so a later default applies.
            bool defaultLimit = SimBriefLimit.Value == MountSettings.DefaultSimBriefDownloadLimitMb.ToString(CultureInfo.InvariantCulture);
            settings.Set("simbrief-download-limit", defaultLimit ? null : SimBriefLimit.Value);
            settings.Set("simbrief-alternate", SimBriefAlternate ? "on" : null);
            Directory.CreateDirectory(CacheFolder);
            settings.Save();
            Log.Info($"Setup saved the settings to {path}");
            await _main.FinishSetupAsync(StartNow).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"The settings could not be saved: {ex.Message}";
        }
    }
}

/// <summary>One step in the setup's step indicator.</summary>
internal sealed record SetupStep(int Number, string Title, bool Done, bool Current);
