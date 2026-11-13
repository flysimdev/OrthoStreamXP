using System.Globalization;
using System.Windows.Input;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>The Settings page: everything in orthostreamxp.ini, in plain words, saved as soon as it changes.</summary>
internal sealed class SettingsViewModel : ObservableObject, IPage
{
    private readonly MainViewModel _main;
    private SettingsDocument _document = SettingsDocument.Load(SettingsDocument.DefaultPath);
    private DateTime _loadedWrite;
    private bool _loading;
    private string _xplane = "", _packages = "", _cache = "";
    private string? _saveError;
    private string _toastText = "";
    private bool _showToast;
    private readonly System.Windows.Threading.DispatcherTimer _toastTimer = new();
    private string? _customLevelNote;
    private bool _confirmingReset;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Presets = [.. QualityPreset.All.Select(p => new PresetOption(p, ChoosePreset))];
        Providers =
        [
            new Choice("Bing, then Esri where Bing has no picture (recommended)", "bing,esri"),
            new Choice("Bing only", "bing"),
            new Choice("Geoportal in Poland (national orthophoto), Bing and Esri elsewhere", "geoportal,bing,esri"),
        ];
        BrowseXPlane = new RelayCommand(() =>
        {
            if (Shell.PickFolder("Choose the X-Plane folder", XPlaneFolder) is { } folder) XPlaneFolder = folder;
        });
        BrowsePackages = new RelayCommand(() =>
        {
            if (Shell.PickFolder("Choose the folder that holds your scenery packages", PackagesFolder) is { } folder) PackagesFolder = folder;
        });
        BrowseCache = new RelayCommand(() =>
        {
            if (Shell.PickFolder("Choose the cache folder", CacheFolder) is { } folder) CacheFolder = folder;
        });
        OpenFile = new RelayCommand(() => Shell.Reveal(_document.Path));
        RunSetup = new RelayCommand(_main.RunSetup, () => CanEdit);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ShowToast = false;
        };
        AskReset = new RelayCommand(() => ConfirmingReset = true, () => CanReset);
        CancelReset = new RelayCommand(() => ConfirmingReset = false);
        Reset = new RelayCommand(ResetToDefaults);
        StopStreaming = new RelayCommand(() => _ = _main.Mount.StopAsync(), () => _main.Mount.IsRunning);

        Other =
        [
            Field("scenery-packs", "Keep the photo scenery below airports and overlays",
                "Moves the packages to the end of X-Plane's scenery_packs.ini at every start. Leave it on unless you order the list yourself.", FieldKind.Toggle, on: "on", off: "off"),
            Field("xplane-address", "X-Plane address", "Only needed when X-Plane runs on another PC.", FieldKind.Text, placeholder: "127.0.0.1:49000"),
            Field("xplane-arguments", "X-Plane start options",
                "Added when OrthoStreamXP starts X-Plane (Start and fly), such as --allow_reshade. Takes effect the next time X-Plane is started.",
                FieldKind.Text, placeholder: "--allow_reshade"),
        ];
        SimBriefUser = Field("simbrief-user", "SimBrief username",
            "Your username or Pilot ID. Your latest flight plan's departure, arrival and climb are built ahead. Empty: off.",
            FieldKind.Text, placeholder: "username or Pilot ID");
        SimBriefTest = new SimBriefTestViewModel(_main.MeshHttp, () => SimBriefUser.Value);
        SimBriefUser.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingField.Value)) SimBriefTest.Reset();
        };
        SimBrief =
        [
            Field("simbrief-download-limit", "Download limit per session",
                SimBriefText.LimitHelp + " Enter the MB, from 0 (no limit) to 100000.",
                FieldKind.Number, min: 0, max: 100_000),
            Field("simbrief-alternate", "Also build the alternate airport", SimBriefText.AlternateHelp,
                FieldKind.Toggle, on: "on", off: "off"),
        ];
        ConnectionTest = new ConnectionTestViewModel(
            () => _document.Get("texture-provider") ?? "bing,esri",
            () => _main.Mount.State == MountState.Stopped,
            _ =>
            {
                // The recommended preset follows the measured speed until a flight says more.
                PresetMemory.Load(_document.Get("cache"));
                foreach (PresetOption option in Presets) option.RefreshMemory();
            },
            workers => DownloadWorkers = workers,
            () => (int)DownloadWorkers);
        _main.Mount.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MountController.State)) return;
            RefreshStatus();
            ConnectionTest.Refresh();
        };
        ColourPreview = new ColourPreviewViewModel(() => _document.Get("cache"), () => ColourSettings);
        Reload();
    }

    /// <summary>The connection test beside "Downloads at once".</summary>
    public ConnectionTestViewModel ConnectionTest { get; }

    /// <summary>The Colours tab's before and after pictures.</summary>
    public ColourPreviewViewModel ColourPreview { get; }

    /// <summary>The colour settings as the page shows them, for the preview.</summary>
    private ColourAdjustments ColourSettings => new(MatchColoursOn, MatchStrength / 100,
        new Scenery.SeasonOptions(SeasonsOn, SeasonStrength / 100, SeasonModel.Names.GetValueOrDefault(Season?.Value ?? "auto", SeasonChoice.Auto)), Exposure);

    public IReadOnlyList<PresetOption> Presets { get; }
    public IReadOnlyList<Choice> Providers { get; }
    public IReadOnlyList<SettingField> Other { get; }

    /// <summary>The SimBrief tab's username; the test below it asks SimBrief with what is typed there.</summary>
    public SettingField SimBriefUser { get; }

    /// <summary>The SimBrief tab's "Test SimBrief connection".</summary>
    public SimBriefTestViewModel SimBriefTest { get; }

    /// <summary>The SimBrief tab's download limit and alternate airport.</summary>
    public IReadOnlyList<SettingField> SimBrief { get; }
    public ICommand BrowseXPlane { get; }
    public ICommand BrowsePackages { get; }
    public ICommand BrowseCache { get; }
    public ICommand OpenFile { get; }
    public ICommand RunSetup { get; }
    /// <summary>Stops the streaming, so the settings can be changed.</summary>
    public ICommand StopStreaming { get; }
    public ICommand AskReset { get; }
    public ICommand CancelReset { get; }
    public ICommand Reset { get; }

    public string FilePath => _document.Path;

    /// <summary>The index of the Folders tab (SettingsView.xaml).</summary>
    public const int FoldersTab = 1;

    /// <summary>The index of the Colours tab (SettingsView.xaml).</summary>
    public const int ColoursTab = 3;

    /// <summary>The tab shown: kept while the window is open, so coming back to Settings shows the same one.</summary>
    public int Tab
    {
        get => _tab;
        set
        {
            if (Set(ref _tab, value) && value == ColoursTab) ColourPreview.Show();
        }
    }

    private int _tab;

    /// <summary>The preset the settings are at, or null for a level set by hand.</summary>
    private QualityPreset? CurrentPreset => QualityPreset.ForLevel(_document.GetInt("level-change") ?? 0);

    /// <summary>What the flight tuning changed from the preset's own values.</summary>
    public IReadOnlyList<TunedRow> TunedRows => CurrentPreset is { } preset
        ? [.. TunedSettings.Changed(_document, preset).Select(c => new TunedRow(c.Setting.Label, c.Setting.Show(c.Value), $"{preset.Name}: {c.Setting.Show(c.Default)}"))]
        : [];

    public string TuningText => CurrentPreset is not { } preset
        ? "Choose a quality above: tuning starts from its settings."
        : TunedRows.Count == 0
            ? $"{preset.Name} uses its own settings. After a flight of {Tuner.MinMinutesInFlight:0} minutes or more, Home suggests changes when they would help; nothing changes without your OK."
            : $"Tuned for this PC and connection from your flights. {preset.Name}'s own values are shown beside them.";

    /// <summary>Reset would change something: a tuned value, or a setting edited in the file.</summary>
    public bool CanReset => CurrentPreset is { } preset && TunedSettings.DiffersFromDefaults(_document, preset);

    public string ResetQuestion => $"Put every setting back to the {CurrentPreset?.Name} defaults and forget what was tuned for it? The folders, the picture source and Downloads at once stay as they are.";

    public bool ConfirmingReset
    {
        get => _confirmingReset;
        set => Set(ref _confirmingReset, value);
    }

    /// <summary>The level change is not one of the presets (set by hand in the file).</summary>
    public string? CustomLevelNote
    {
        get => _customLevelNote;
        private set => Set(ref _customLevelNote, value);
    }

    public Choice? Provider
    {
        get => Providers.FirstOrDefault(c => c.Value == _document.Get("texture-provider")?.Replace(" ", "", StringComparison.Ordinal));
        set
        {
            if (value is null) return;
            Save("texture-provider", value.Value);
            Raise();
        }
    }

    /// <summary>A double for the slider; saved as a whole number.</summary>
    public double DownloadWorkers
    {
        get => _document.GetInt("download-workers") ?? 8;
        set
        {
            int workers = Math.Clamp((int)Math.Round(value), 1, 256);
            // Only a real change is a decision: the slider also reports a value it merely snapped (8 shown as 16).
            bool changed = _document.GetInt("download-workers") is { } before && before != workers;
            Save("download-workers", workers.ToString(CultureInfo.InvariantCulture));
            Raise();
            Raise(nameof(DownloadWorkersText));
            if (changed) ConnectionTest?.Decide(); // set by hand or by "Use": the test's suggestion has had its answer
            else ConnectionTest?.Refresh();
        }
    }

    public string DownloadWorkersText => $"{DownloadWorkers} at once";

    /// <summary>The seasons by hand, or automatic.</summary>
    public IReadOnlyList<Choice> SeasonChoices { get; } =
    [
        new Choice("Automatic: X-Plane's date and the place", "auto"),
        new Choice("Early spring", "early-spring"),
        new Choice("Spring", "spring"),
        new Choice("Summer (the pictures as they are)", "summer"),
        new Choice("Early autumn", "early-autumn"),
        new Choice("Autumn", "autumn"),
        new Choice("Late autumn", "late-autumn"),
        new Choice("Winter, without snow", "winter"),
    ];

    public bool MatchColoursOn
    {
        get => _document.GetBool("match-colours") == true;
        set
        {
            Save("match-colours", value ? "on" : "off");
            Raise();
            Raise(nameof(MatchColoursOnText));
            ColourPreview.Refresh();
        }
    }

    public string MatchColoursOnText => MatchColoursOn ? "On" : "Off";

    /// <summary>The colour matching's strength in percent, for the slider; saved as a whole number.</summary>
    public double MatchStrength
    {
        get => _document.GetDouble("match-strength") ?? 100;
        set
        {
            Save("match-strength", Math.Clamp((int)Math.Round(value), 0, 100).ToString(CultureInfo.InvariantCulture));
            Raise();
            Raise(nameof(MatchStrengthText));
            ColourPreview.Refresh();
        }
    }

    public string MatchStrengthText => $"{MatchStrength:0} %";

    public bool SeasonsOn
    {
        get => _document.GetBool("seasons") == true;
        set
        {
            Save("seasons", value ? "on" : "off");
            Raise();
            Raise(nameof(SeasonsOnText));
            ColourPreview.Refresh();
        }
    }

    public string SeasonsOnText => SeasonsOn ? "On" : "Off";

    public Choice? Season
    {
        get => SeasonChoices.FirstOrDefault(c => c.Value == (_document.Get("season") ?? "auto")) ?? SeasonChoices[0];
        set
        {
            if (value is null) return;
            Save("season", value.Value);
            Raise();
            ColourPreview.Refresh();
        }
    }

    /// <summary>The strength in percent, for the slider; saved as a whole number.</summary>
    public double SeasonStrength
    {
        get => _document.GetDouble("season-strength") ?? Scenery.SeasonOptions.DefaultStrength;
        set
        {
            Save("season-strength", Math.Clamp((int)Math.Round(value), 0, 100).ToString(CultureInfo.InvariantCulture));
            Raise();
            Raise(nameof(SeasonStrengthText));
            ColourPreview.Refresh();
        }
    }

    public string SeasonStrengthText => $"{SeasonStrength:0} %";

    /// <summary>The photos' exposure in percent, for the slider; saved as a whole number.</summary>
    public double Exposure
    {
        get => _document.GetDouble("exposure") ?? Textures.Exposure.Full;
        set
        {
            Save("exposure", Math.Clamp((int)Math.Round(value), Textures.Exposure.Min, Textures.Exposure.Full).ToString(CultureInfo.InvariantCulture));
            Raise();
            Raise(nameof(ExposureText));
            ColourPreview.Refresh();
        }
    }

    public string ExposureText => $"{Exposure:0} %";

    /// <summary>How strong X-Plane's grass and dirt decals are over the photos, in percent, for the slider.</summary>
    public double GroundDetail
    {
        get => _document.GetDouble("ground-detail") ?? 0;
        set
        {
            Save("ground-detail", Math.Clamp((int)Math.Round(value), 0, Scenery.TerrainDecals.Full).ToString(CultureInfo.InvariantCulture));
            Raise();
            Raise(nameof(GroundDetailText));
        }
    }

    public string GroundDetailText => GroundDetail switch
    {
        <= 0 => "Off",
        _ => $"{GroundDetail:0} %",
    };

    /// <summary>Street lamps and lit windows on the photo scenery at night.</summary>
    public bool NightLightsOn
    {
        get => _document.GetBool("night-lights") == true;
        set
        {
            Save("night-lights", value ? "on" : "off");
            Raise();
            Raise(nameof(NightLightsOnText));
        }
    }

    public string NightLightsOnText => NightLightsOn ? "On" : "Off";

    /// <summary>No detailed photos downloaded at night in X-Plane: the ground under the lights is blurry.</summary>
    public bool SkipPhotosAtNight
    {
        get => _document.GetBool("skip-photos-at-night") == true;
        set
        {
            Save("skip-photos-at-night", value ? "on" : "off");
            Raise();
            Raise(nameof(SkipPhotosAtNightText));
        }
    }

    public string SkipPhotosAtNightText => SkipPhotosAtNight ? "On" : "Off";

    public string XPlaneFolder
    {
        get => _xplane;
        set
        {
            if (!Set(ref _xplane, value.Trim())) return;
            if (!_loading)
            {
                Save("xplane", _xplane.Length == 0 ? null : _xplane);
                SavePackages(); // a packages folder held back until X-Plane was known
            }
            Raise(nameof(XPlaneOk));
            Raise(nameof(XPlaneStatus));
            Raise(nameof(PackagesOk));
            Raise(nameof(PackagesStatus));
        }
    }

    public bool XPlaneOk => SystemCheck.IsXPlaneFolder(XPlaneFolder);

    public string XPlaneStatus => XPlaneOk
        ? "X-Plane found. The packages appear in its Custom Scenery folder while OrthoStreamXP runs."
        : "This is not an X-Plane folder: it has no Custom Scenery folder.";

    public string PackagesFolder
    {
        get => _packages;
        set
        {
            if (!Set(ref _packages, value.Trim())) return;
            if (!_loading) SavePackages();
            Raise(nameof(PackagesOk));
            Raise(nameof(PackagesStatus));
        }
    }

    /// <summary>
    /// The packages folder can be saved: X-Plane is known and the folder is not in its Custom Scenery. Otherwise the
    /// settings keep the previous folder (an empty one, which clears the setting, is always saved).
    /// </summary>
    private bool PackagesAllowed => _packages.Length == 0 || (XPlaneOk && !SystemCheck.IsInCustomScenery(_packages, _xplane));

    private void SavePackages()
    {
        string? value = _packages.Length == 0 ? null : _packages;
        if (PackagesAllowed && _document.Get("base-packages") != value) Save("base-packages", value);
    }

    public bool PackagesOk => XPlaneOk && !SystemCheck.IsInCustomScenery(PackagesFolder, XPlaneFolder) && SystemCheck.PackagesIn(PackagesFolder).Count > 0;

    public string PackagesStatus
    {
        get
        {
            if (!XPlaneOk) return SystemCheck.XPlaneFirstExplanation;
            if (SystemCheck.IsInCustomScenery(PackagesFolder, XPlaneFolder)) return SystemCheck.InCustomSceneryExplanation;
            IReadOnlyList<string> found = SystemCheck.PackagesIn(PackagesFolder);
            if (found.Count == 0) return "No scenery packages here. Choose the folder that holds them: each package is a folder with an Earth nav data folder in it.";
            IReadOnlyList<string> taken = _main.Mount.IsRunning ? [] : SystemCheck.TakenInCustomScenery(XPlaneFolder, found);
            return $"Found {string.Join(", ", found)}." + (taken.Count == 0 ? ""
                : $" Custom Scenery already has {string.Join(", ", taken)}: close any other program that shows these packages to X-Plane before you start OrthoStreamXP.");
        }
    }

    public string CacheFolder
    {
        get => _cache;
        set
        {
            if (!Set(ref _cache, value.Trim())) return;
            if (!_loading) Save("cache", _cache.Length == 0 ? null : _cache);
            Raise(nameof(CacheStatus));
            if (!_loading) ColourPreview.Refresh();
        }
    }

    public string CacheStatus => SystemCheck.FreeBytes(CacheFolder) is { } free
        ? $"{CacheInventory.FormatBytes(free)} free on this drive. {CacheEstimate}"
        : "Choose a folder on a fast drive with plenty of free space.";

    /// <summary>The disk a newly flown area takes at the chosen texture size (about 3000 textures).</summary>
    public string CacheEstimate
    {
        get
        {
            int size = new TexturePlan(_document.GetInt("level-change") ?? 0).SizeFor(16);
            return $"Each new area takes up to about {CacheInventory.FormatBytes(3000L * size * size * 2 / 3)} at this texture size.";
        }
    }

    public string? SaveError
    {
        get => _saveError;
        private set => Set(ref _saveError, value);
    }

    // ---------------------------------------------------------------- locked while streaming

    /// <summary>
    /// The settings can be changed only while the streaming is stopped: what runs is then always what the page shows,
    /// and the flight tuning judges the settings a whole session used. While it runs (or starts or stops) they are
    /// shown, not editable, and a banner offers to stop it.
    /// </summary>
    public bool CanEdit => _main.Mount.State == MountState.Stopped;

    public bool IsLocked => !CanEdit;

    public string LockedText => _main.Mount.State == MountState.Running
        ? "Streaming is running. Stop it to change settings."
        : "Streaming is starting or stopping. Settings can be changed once it has stopped.";

    /// <summary>The note that a change was saved, shown for a moment after each change.</summary>
    public string ToastText
    {
        get => _toastText;
        private set => Set(ref _toastText, value);
    }

    public bool ShowToast
    {
        get => _showToast;
        private set => Set(ref _showToast, value);
    }

    /// <summary>A change was saved: a short note says so (the next start uses it).</summary>
    private void Saved()
    {
        ToastText = "Saved";
        ShowToast = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(2);
        _toastTimer.Start();
    }

    private void RefreshStatus()
    {
        foreach (string name in new[] { nameof(CanEdit), nameof(IsLocked), nameof(LockedText) })
            Raise(name);
        RelayCommand.Refresh();
    }

    public void OnShown()
    {
        // Picks up changes made to the file outside the window.
        if (File.Exists(_document.Path) && File.GetLastWriteTimeUtc(_document.Path) != _loadedWrite) Reload();
        Raise(nameof(PackagesStatus));
        Raise(nameof(CacheStatus));
        RefreshStatus();
        if (Tab == ColoursTab) ColourPreview.Show();
    }

    /// <summary>Reads the settings file again and shows its values.</summary>
    public void Reload()
    {
        _document = SettingsDocument.Load(SettingsDocument.DefaultPath);
        PresetMemory.Load(_document.Get("cache"));
        foreach (PresetOption option in Presets) option.RefreshMemory();
        _loadedWrite = File.Exists(_document.Path) ? File.GetLastWriteTimeUtc(_document.Path) : default;
        _loading = true;
        XPlaneFolder = _document.Get("xplane") ?? "";
        PackagesFolder = _document.Get("base-packages") ?? _document.Get("source") ?? "";
        CacheFolder = _document.Get("cache") ?? "";
        _loading = false;
        int level = _document.GetInt("level-change") ?? 0;
        foreach (PresetOption option in Presets) option.Show(option.Preset.Level == level);
        CustomLevelNote = QualityPreset.ForLevel(level) is null
            ? $"Your settings file uses level-change {level}, which is not one of these. Choosing one replaces it."
            : null;
        foreach (SettingField field in Other.Append(SimBriefUser).Concat(SimBrief))
            field.Load(_document.Get(field.Key), Default(field.Key));
        ConfirmingReset = false;
        foreach (string name in new[]
                 {
                     nameof(Provider), nameof(DownloadWorkers), nameof(DownloadWorkersText), nameof(FilePath), nameof(CacheEstimate),
                     nameof(MatchColoursOn), nameof(MatchColoursOnText), nameof(MatchStrength), nameof(MatchStrengthText), nameof(SeasonsOn), nameof(SeasonsOnText), nameof(Season), nameof(SeasonStrength), nameof(SeasonStrengthText), nameof(Exposure), nameof(ExposureText),
                     nameof(GroundDetail), nameof(GroundDetailText), nameof(NightLightsOn), nameof(NightLightsOnText), nameof(SkipPhotosAtNight), nameof(SkipPhotosAtNightText),
                     nameof(TunedRows), nameof(TuningText), nameof(CanReset), nameof(ResetQuestion),
                 })
            Raise(name);
        RelayCommand.Refresh();
        ColourPreview.Refresh();
    }

    private void ChoosePreset(QualityPreset preset)
    {
        TunedSettings.ChoosePreset(_document, preset, TunedStore.Load(_document.Path));
        SaveDocument();
        foreach (PresetOption option in Presets) option.Show(option.Preset == preset);
        CustomLevelNote = null;
        Reload();
    }

    private void ResetToDefaults()
    {
        if (CurrentPreset is not { } preset) return;
        TunedStore store = TunedStore.Load(_document.Path);
        TunedSettings.Reset(_document, preset, store);
        SaveDocument();
        try
        {
            store.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"What was tuned could not be forgotten: {ex.Message}";
        }
        Reload();
    }

    private void Save(string key, string? value)
    {
        _document.Set(key, value);
        SaveDocument();
    }

    private void SaveDocument()
    {
        try
        {
            _document.Save();
            _loadedWrite = File.GetLastWriteTimeUtc(_document.Path);
            SaveError = null;
            Saved();
            RefreshStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"The settings could not be saved: {ex.Message}";
        }
    }

    private SettingField Field(string key, string label, string help, FieldKind kind, double min = 0, double max = 0, bool decimals = false,
        IReadOnlyList<Choice>? choices = null, string on = "true", string off = "false", string? placeholder = null) =>
        new(key, label, help, kind, Save, min, max, decimals, choices, on, off, placeholder);

    /// <summary>What the program uses when the file does not set a value.</summary>
    private static string Default(string key) => key switch
    {
        "scenery-packs" => "on",
        "simbrief-download-limit" => MountSettings.DefaultSimBriefDownloadLimitMb.ToString(CultureInfo.InvariantCulture),
        "simbrief-alternate" => "off",
        _ => "",
    };
}
