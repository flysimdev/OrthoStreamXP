using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>The Home page: Start/Stop, what is happening now, and what to do next.</summary>
internal sealed class HomeViewModel : ObservableObject, IPage
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private (DateTime At, long Bytes, long Tiles, long Failed)? _previous;
    private double _mbits, _tilesPerSecond;
    private readonly Queue<(DateTime At, long Failed, long Tiles)> _failureWindow = new();
    private MountStatus? _status;
    private bool _hasRun;
    private IReadOnlyList<Notice> _notices = [];
    private IReadOnlyList<PackageRow> _packages = [];
    private string _qualityTitle = "", _qualityText = "";
    private FlightReview? _review;
    private FlightReport? _report;
    private System.Windows.Media.ImageSource? _reportBackdrop;
    private string? _reviewPath;
    private Notice? _tuningNotice;
    private bool _reloading;
    /// <summary>The running session's flight plan, as fetched at the start or by the last reload; null until known.</summary>
    private string? _planStatus;
    private readonly AppUpdates _updates = new();
    /// <summary>Looks for a new version 10 seconds after the window opens, then every 6 hours.</summary>
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        StartStop = new RelayCommand(() => _ = ToggleAsync(), () => Mount.State is MountState.Stopped or MountState.Running);
        Fly = new RelayCommand(() => _ = FlyAsync(), () => ShowFly);
        ChangeQuality = new RelayCommand(() => _main.Show(_main.Settings));
        OpenLog = new RelayCommand(() => _main.Show(_main.LogPage));
        RestartMount = new RelayCommand(() => _ = Mount.RestartAsync(), () => Mount.IsRunning);
        ReloadFlightPlan = new RelayCommand(() => _ = ReloadFlightPlanAsync(), () => Mount.UsesSimBrief && !_reloading);
        ApplyReview = new RelayCommand(Apply);
        DismissReview = new RelayCommand(() =>
        {
            if (_reviewPath is not null) FlightReview.Delete(_reviewPath);
            ShowReviewOf(null);
        });
        _timer.Tick += (_, _) => Update();
        // Installed only when chosen (after the release notes), and only while the scenery is not streaming: OrthoStreamXP restarts to do it.
        InstallUpdate = new RelayCommand(OfferUpdate, () => _updates.Ready is not null && Mount.State == MountState.Stopped);
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            if (await _updates.CheckAsync().ConfigureAwait(true)) Notices = BuildNotices();
        };
        _updateTimer.Start();
        Mount.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(MountController.State) or nameof(MountController.Problem))) return;
            if (Mount.State == MountState.Stopped)
            {
                LoadReview();
                LoadReport();
                PlanStatus = null; // the next session starts with its own plan
            }
            if (Mount.State == MountState.Starting) _tuningNotice = null;
            if (Mount.State == MountState.Running)
            {
                _hasRun = true;
                _ = ShowFirstFlightPlanAsync();
                _previous = null;
                _mbits = _tilesPerSecond = 0;
                _failureWindow.Clear();
                _timer.Start();
            }
            Update();
        };
        LoadSettingsSummary();
        LoadReview();
        LoadReport();
        Update();
    }

    private MountController Mount => _main.Mount;

    public ICommand StartStop { get; }

    /// <summary>Stopped: start OrthoStreamXP, then X-Plane. Running: start X-Plane.</summary>
    public ICommand Fly { get; }

    /// <summary>The Fly button started X-Plane: the window gets out of its way.</summary>
    public event EventHandler? XPlaneStarted;

    /// <summary>X-Plane was running and has quit (checked every few seconds while OrthoStreamXP runs).</summary>
    public event EventHandler? XPlaneQuit;

    /// <summary>X-Plane is installed where the settings say, and not running: one button starts it (and OrthoStreamXP first).</summary>
    public bool ShowFly => Mount.State is MountState.Stopped or MountState.Running && _xplaneProgram is not null && !_xplaneRunning;

    public string FlyText => Mount.State == MountState.Running ? "Start X-Plane" : "Start and fly";

    public string FlyToolTip => Mount.State == MountState.Running
        ? "Start X-Plane: the photo scenery is ready for it"
        : "Start OrthoStreamXP, then X-Plane once the photo scenery is ready";
    public ICommand ChangeQuality { get; }
    public ICommand OpenLog { get; }
    public ICommand RestartMount { get; }

    /// <summary>Shows the downloaded version's release notes, to install it from there (only while the streaming is stopped).</summary>
    public ICommand InstallUpdate { get; }

    /// <summary>The person asked to install the downloaded version: the window shows its notes and asks to confirm.</summary>
    public event EventHandler<UpdateOffer>? UpdateOffered;

    private void OfferUpdate()
    {
        if (_updates.ReadyVersion is not { } version) return;
        UpdateOffered?.Invoke(this, new UpdateOffer(version, _updates.CurrentVersion, ReleaseNotes.Parse(_updates.ReadyNotes), AppUpdates.ReleasePage(version)));
    }

    /// <summary>Installs the downloaded version and restarts, once confirmed; not while the scenery streams.</summary>
    public void InstallConfirmedUpdate()
    {
        if (Mount.State == MountState.Stopped) _updates.ApplyAndRestart();
    }
    public ICommand ApplyReview { get; }
    public ICommand DismissReview { get; }

    /// <summary>The flight tuning's suggestions, shown while OrthoStreamXP is stopped until applied or dismissed.</summary>
    public bool ShowReview => _review is { Suggestions.Count: > 0 } && Mount.State == MountState.Stopped;

    public bool CanApplyReview => _review?.HasChanges == true;

    public string ReviewTitle => _review is null ? "" : $"Tuning for {PresetName(_review.Level)} from your last flight";

    public string ReviewText => _review is null ? ""
        : $"OrthoStreamXP watched {_review.MinutesInFlight:0} minutes of flying ({_review.When:g}). " +
          (_review.HasChanges ? "These changes should make the scenery load better on your PC and connection:" : "It found:");

    public IReadOnlyList<ReviewRow> ReviewRows => _review is null ? []
        : [.. _review.Suggestions.Select(s => new ReviewRow(s.Setting,
            s.Key is null ? "" : $"{TunedSettings.Find(s.Key)?.Show(s.From) ?? s.From}  →  {TunedSettings.Find(s.Key)?.Show(s.To) ?? s.To}", s.Why))];

    public bool AutoStart
    {
        get => _main.Preferences.AutoStart;
        set
        {
            _main.Preferences.AutoStart = value;
            _main.Preferences.Save();
            Raise();
        }
    }

    /// <summary>The SimBrief departure prefetch for the next start (this window only, not saved).</summary>
    public bool DeparturePrefetch
    {
        get => Mount.DeparturePrefetch;
        set
        {
            Mount.DeparturePrefetch = value;
            Raise();
            Raise(nameof(ShowFlightPlanStatus));
        }
    }

    /// <summary>
    /// "Reload SimBrief flight plan": asks SimBrief again whenever the pilot wants (generated the OFP after starting, or
    /// planned another flight). Never automatic; disabled while a reload runs, so a double click sends one request.
    /// </summary>
    public ICommand ReloadFlightPlan { get; }

    /// <summary>Streaming runs with a SimBrief flight plan to reload.</summary>
    public bool ShowReloadFlightPlan => Mount.UsesSimBrief;

    /// <summary>
    /// Home's SimBrief line, shown whenever the SimBrief switch is on: before the start that the plan is read then,
    /// while starting that SimBrief is asked, then the flight found ("Flight plan: EDDV → ENGM, generated 12 min ago.")
    /// or what the last reload found.
    /// </summary>
    public string FlightPlanStatus => Mount.State switch
    {
        MountState.Stopped => "Your latest SimBrief flight plan is read when streaming starts.",
        MountState.Running when !Mount.UsesSimBrief => "SimBrief is off for this run; the log says why.",
        MountState.Running or MountState.Starting => _planStatus ?? "Asking SimBrief for your latest flight plan…",
        _ => _planStatus ?? "",
    };

    public bool ShowFlightPlanStatus => ShowDeparturePrefetch && DeparturePrefetch && FlightPlanStatus.Length > 0;

    private string? PlanStatus
    {
        set
        {
            _planStatus = value;
            Raise(nameof(FlightPlanStatus));
            Raise(nameof(ShowFlightPlanStatus));
        }
    }

    /// <summary>Streaming started: shows the plan it fetched, unless a reload already said more.</summary>
    private async Task ShowFirstFlightPlanAsync()
    {
        if (Mount.FirstFlightPlanAsync() is not { } first)
        {
            PlanStatus = null;
            return;
        }
        string text = await first.ConfigureAwait(true);
        if (Mount.State == MountState.Running && !_reloading && _planStatus is null) PlanStatus = text;
    }

    private async Task ReloadFlightPlanAsync()
    {
        if (Mount.ReloadFlightPlanAsync() is not { } reload) return;
        _reloading = true;
        RelayCommand.Refresh();
        PlanStatus = "Asking SimBrief for your latest flight plan…";
        try
        {
            PlanStatus = await reload.ConfigureAwait(true);
        }
        finally
        {
            _reloading = false;
            RelayCommand.Refresh();
        }
    }

    /// <summary>A SimBrief user is set, so there is a departure prefetch to turn off.</summary>
    public bool ShowDeparturePrefetch { get; private set; }

    /// <summary>The switch applies when streaming starts: it can be changed only while stopped.</summary>
    public bool CanChangeDeparturePrefetch => Mount.State == MountState.Stopped;

    public string StartStopText => Mount.State switch
    {
        MountState.Running => "Stop",
        MountState.Starting => "Starting…",
        MountState.Stopping => "Stopping…",
        _ => "Start",
    };

    public string StartStopGlyph => Mount.State == MountState.Running ? "" : "";

    public bool IsBusy => Mount.State is MountState.Starting or MountState.Stopping;

    /// <summary>Idle, Busy, Good, Warning or Error: the colour of the status icon.</summary>
    public string StatusKind => Mount.State switch
    {
        MountState.Running => Connected ? "Good" : "Ready",
        MountState.Starting or MountState.Stopping => "Busy",
        _ => Mount.Problem is not null ? "Error" : "Idle",
    };

    public string StatusGlyph => StatusKind switch
    {
        "Good" => "",
        "Ready" => "",
        "Error" => "",
        "Busy" => "",
        _ => "",
    };

    public string StatusTitle => Mount.State switch
    {
        MountState.Running when Connected => "Flying with photo scenery",
        MountState.Running => "Photo scenery is ready",
        MountState.Starting => "Starting…",
        MountState.Stopping => "Stopping…",
        _ when Mount.Problem is not null => "OrthoStreamXP could not start",
        _ => "OrthoStreamXP is stopped",
    };

    public string StatusText => Mount.State switch
    {
        MountState.Running when Connected => $"X-Plane is connected. OrthoStreamXP builds the scenery around and ahead of the aircraft. Running for {RunningFor}.",
        MountState.Running => "Now start X-Plane and load a flight. The photo scenery appears as soon as X-Plane loads it.",
        MountState.Starting => "Showing the scenery packages to X-Plane…",
        MountState.Stopping => "Finishing the textures being built and removing the scenery from X-Plane…",
        _ when Mount.Problem is not null => "See below for what happened and what to do.",
        _ => "Start OrthoStreamXP before X-Plane, so X-Plane finds the photo scenery when it starts.",
    };

    private string RunningFor
    {
        get
        {
            TimeSpan running = DateTime.Now - Mount.StartedAt;
            return running.TotalHours >= 1 ? $"{(int)running.TotalHours} h {running.Minutes} min" : $"{Math.Max(1, running.Minutes)} min";
        }
    }

    private bool Connected => _status?.Aircraft is { } aircraft && DateTime.UtcNow - aircraft.ReceivedUtc < TimeSpan.FromSeconds(10);

    /// <summary>The three steps, shown until the first start.</summary>
    public bool ShowGettingStarted => !_hasRun && Mount.State == MountState.Stopped && Mount.Problem is null && Report is null;

    /// <summary>The live numbers, shown once the mount has run.</summary>
    public bool ShowStats => _hasRun;

    public string StatsTitle => Mount.State == MountState.Stopped ? "Last session" : "This session";

    public IReadOnlyList<Notice> Notices
    {
        get => _notices;
        private set => Set(ref _notices, value);
    }

    public string TexturesValue => Count(_status?.Requested);

    public string ReadyAheadValue => _status switch
    {
        { StreamedReads: > 0 } s => $"{100.0 * s.RealReads / s.StreamedReads:0}%",
        { Requested: > 0 } s => $"{100.0 * (s.ReadyByPrefetch + s.ReadyInCache) / s.Requested:0}%",
        _ => "–",
    };

    public string ReadyAheadCaption => _status switch
    {
        // Streaming: every read is answered at once; what counts is how often it was the real picture.
        { StreamedReads: > 0 } s => $"of X-Plane's reads got the real picture ({Count(s.ReadyInCache)} textures complete in the cache)",
        { Requested: > 0 } s => $"ready before X-Plane asked ({Count(s.ReadyInCache)} from the cache)",
        _ => "ready before X-Plane asked",
    };

    public string BuildingValue => Mount.IsRunning ? Count(_status?.Building) : "–";

    public string SpeedValue => Mount.IsRunning ? $"{_mbits:0} Mbit/s" : "–";

    public string SpeedCaption => Mount.IsRunning
        ? $"downloading, {_tilesPerSecond:0} picture tiles a second"
        : $"{Count(_status?.TilesDownloaded)} picture tiles downloaded";

    public string XPlaneTitle => Mount.State != MountState.Running ? "Not connected"
        : Connected ? "Connected"
        : _status?.Aircraft is not null ? "Paused or in the menu"
        : "Waiting for X-Plane";

    public string XPlaneGlyph => Connected ? "" : "";

    public string XPlaneText
    {
        get
        {
            if (Mount.State != MountState.Running)
                return "OrthoStreamXP finds X-Plane on this PC by itself while it runs; there is nothing to set up in X-Plane.";
            if (_status?.Aircraft is not { } a)
                return "Start X-Plane and load a flight. OrthoStreamXP follows the aircraft to build the scenery ahead of it.";
            string where = string.Create(CultureInfo.InvariantCulture,
                $"{Math.Abs(a.Lat):0.000}° {(a.Lat >= 0 ? "N" : "S")}, {Math.Abs(a.Lon):0.000}° {(a.Lon >= 0 ? "E" : "W")}");
            string speed = a.GroundSpeedKt is { } kt && kt >= 1 ? $" · {kt:0} kt" : "";
            return Connected ? $"Aircraft at {where}{speed}" : $"Last position {where}. X-Plane is not sending it now.";
        }
    }

    public string QualityTitle
    {
        get => _qualityTitle;
        private set => Set(ref _qualityTitle, value);
    }

    public string QualityText
    {
        get => _qualityText;
        private set => Set(ref _qualityText, value);
    }

    public IReadOnlyList<PackageRow> Packages
    {
        get => _packages;
        private set => Set(ref _packages, value);
    }

    public string PackagesCaption => Mount.IsRunning ? "Shown in X-Plane now" : "Shown in X-Plane while OrthoStreamXP runs";

    public void OnShown()
    {
        _xplaneCheckedAt = null;
        LoadSettingsSummary();
        LoadReview();
        Update();
    }

    private string? _xplaneProgram;
    private bool _xplaneRunning;
    private readonly XPlaneWatch _xplaneWatch = new();

    /// <summary>Notes where X-Plane is installed and whether it runs (on start-up and every few seconds).</summary>
    private void CheckXPlane()
    {
        _xplaneProgram = SystemCheck.XPlaneProgram(SettingsDocument.Load(SettingsDocument.DefaultPath).Get("xplane"));
        _xplaneRunning = SystemCheck.IsXPlaneRunning();
        if (_xplaneWatch.QuitSince(_xplaneRunning)) XPlaneQuit?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>OrthoStreamXP first (X-Plane looks for scenery only when it starts), then X-Plane.</summary>
    private async Task FlyAsync()
    {
        if (_xplaneProgram is not { } program) return;
        if (Mount.State == MountState.Stopped)
        {
            await Mount.StartAsync().ConfigureAwait(true);
            LoadSettingsSummary();
            if (Mount.State != MountState.Running) return; // the start failed: its problem is shown
        }
        if (SystemCheck.IsXPlaneRunning())
        {
            // Started meanwhile by hand: never a second X-Plane.
            _xplaneRunning = true;
            Update();
            return;
        }
        try
        {
            string? arguments = SettingsDocument.Load(SettingsDocument.DefaultPath).Get("xplane-arguments");
            Log.Step(string.IsNullOrWhiteSpace(arguments) ? "Starting X-Plane" : $"Starting X-Plane with {arguments.Trim()}");
            SystemCheck.StartXPlane(program, arguments);
            _xplaneRunning = true;
            _xplaneProblem = null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warning($"X-Plane could not be started from {program}: {ex.Message}");
            _xplaneProblem = new Notice(Severity.Error, "X-Plane could not be started", $"{ex.Message} Start it yourself; the photo scenery is ready for it.");
        }
        Update();
        if (_xplaneProblem is null) XPlaneStarted?.Invoke(this, EventArgs.Empty);
    }

    private Notice? _xplaneProblem;
    private DateTime? _xplaneCheckedAt;

    private async Task ToggleAsync()
    {
        if (Mount.State == MountState.Running) await Mount.StopAsync().ConfigureAwait(true);
        else
        {
            await Mount.StartAsync().ConfigureAwait(true);
            LoadSettingsSummary();
        }
    }

    /// <summary>The quality and packages as the settings file has them (for when OrthoStreamXP is stopped).</summary>
    private void LoadSettingsSummary()
    {
        SettingsDocument settings = SettingsDocument.Load(SettingsDocument.DefaultPath);
        int? level = settings.GetInt("level-change");
        QualityPreset? preset = QualityPreset.ForLevel(level ?? 0);
        QualityTitle = preset?.Name ?? $"Custom (level {level ?? 0})";
        var plan = new TexturePlan(level ?? 0);
        string zooms = $"{plan.SizeFor(16)} px textures from zoom {plan.FetchZoom(16)} in most areas and zoom {plan.FetchZoom(18)} near airports";
        QualityText = preset is null
            ? $"{zooms}."
            : $"{zooms}, about {preset.MetresPerPixel:0.#} m per pixel. A new area loads in {preset.FirstVisit}.";

        if (Mount.Running is { } running)
            Packages = [.. running.Packages.Select(p => new PackageRow(p.Name, p.Mountpoint))];
        else
        {
            string? xplane = settings.Get("xplane");
            Packages = [.. SystemCheck.PackagesIn(settings.Get("base-packages"))
                .Select(name => new PackageRow(name, xplane is null ? "" : Path.Join(xplane, "Custom Scenery", name)))];
        }
        Raise(nameof(PackagesCaption));
        ShowDeparturePrefetch = !string.IsNullOrWhiteSpace(settings.Get("simbrief-user"));
        Raise(nameof(ShowDeparturePrefetch));
        Raise(nameof(ShowFlightPlanStatus));
    }

    // ---------------------------------------------------------------- the last flight's report

    /// <summary>The report of the last flight: from the session just stopped, or the one saved in the log folder.</summary>
    public FlightReport? Report
    {
        get => _report;
        private set
        {
            Set(ref _report, value);
            ReportBackdrop = null;
            if (value is not null) _ = PaintBackdropAsync(value);
            foreach (string name in new[] { nameof(ShowReport), nameof(ShowGettingStarted), nameof(ReportTitle), nameof(ReportVerdict), nameof(ReportStats) })
                Raise(name);
        }
    }

    public bool ShowReport => Report is not null && Mount.State == MountState.Stopped;

    public string ReportTitle => Report is not { } r ? ""
        : $"Last flight · {r.StartedUtc.ToLocalTime():d MMM, HH:mm} · {(int)r.Duration.TotalHours} h {r.Duration.Minutes:00} min";

    /// <summary>The place under the route, from the cache's low-zoom base.</summary>
    public System.Windows.Media.ImageSource? ReportBackdrop
    {
        get => _reportBackdrop;
        private set => Set(ref _reportBackdrop, value);
    }

    private async Task PaintBackdropAsync(FlightReport report)
    {
        string? cache = SettingsDocument.Load(SettingsDocument.DefaultPath).Get("cache");
        if (cache is null || RouteView.For(report) is not { } view) return;
        // Twice the map's size, for a sharp picture on a high-resolution screen.
        const int width = (int)(RouteView.Width * 2), height = (int)(RouteView.Height * 2);
        System.Windows.Media.ImageSource? picture = await Task.Run(() =>
        {
            if (CacheAreas.BasePicture(cache, view, width, height) is not { } bgra) return null;
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, width * 4);
            bitmap.Freeze();
            return bitmap;
        }).ConfigureAwait(true);
        if (ReferenceEquals(report, Report)) ReportBackdrop = picture;
    }

    /// <summary>The report in one line: smooth, a few blurry moments, or often blurry, and why.</summary>
    public ReportVerdict? ReportVerdict => Report is { } r ? ReportSummary.Verdict(r) : null;

    /// <summary>The report's figures, each with what it means.</summary>
    public IReadOnlyList<ReportStat> ReportStats => Report is { } r ? ReportSummary.Stats(r) : [];

    private void LoadReport()
    {
        if (Mount.State != MountState.Stopped) return;
        if (Mount.LastReport is { } fresh)
        {
            Report = fresh;
            return;
        }
        string? cache = SettingsDocument.Load(SettingsDocument.DefaultPath).Get("cache");
        Report = FlightReport.Load(FlightReport.PathFor(AppPaths.History));
    }

    /// <summary>The saved review of the last flight, if it is for the preset the settings are at now.</summary>
    private void LoadReview()
    {
        if (Mount.State != MountState.Stopped) return;
        SettingsDocument settings = SettingsDocument.Load(SettingsDocument.DefaultPath);
        if (settings.Get("cache") is not { } cache)
        {
            ShowReviewOf(null);
            return;
        }
        _reviewPath = FlightReview.PathFor(AppPaths.History);
        FlightReview? review = FlightReview.Load(_reviewPath);
        if (review is not null && review.Level != (settings.GetInt("level-change") ?? 0))
        {
            // Another quality was chosen since that flight: its suggestions are not for this one.
            FlightReview.Delete(_reviewPath);
            review = null;
        }
        ShowReviewOf(review);
    }

    private void ShowReviewOf(FlightReview? review)
    {
        _review = review;
        foreach (string name in new[] { nameof(ShowReview), nameof(CanApplyReview), nameof(ReviewTitle), nameof(ReviewText), nameof(ReviewRows) })
            Raise(name);
        Notices = BuildNotices();
    }

    private void Apply()
    {
        if (_review is not { } review) return;
        try
        {
            SettingsDocument settings = SettingsDocument.Load(SettingsDocument.DefaultPath);
            TunedStore store = TunedStore.Load(settings.Path);
            if (TunedSettings.Apply(review, settings, store))
            {
                settings.Save();
                store.Save();
                _tuningNotice = new Notice(Severity.Success, "Tuning applied",
                    $"The {PresetName(review.Level)} settings are tuned for your next flight. Settings › Tuning shows what changed and can reset it.");
            }
            if (_reviewPath is not null) FlightReview.Delete(_reviewPath);
            ShowReviewOf(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _tuningNotice = new Notice(Severity.Error, "The tuning could not be saved", ex.Message);
            Notices = BuildNotices();
        }
    }

    private static string PresetName(int level) => QualityPreset.ForLevel(level)?.Name ?? $"level {level}";

    private void Update()
    {
        if (Mount.State == MountState.Running)
        {
            _status = Mount.Status();
            MeasureSpeed();
        }
        // X-Plane's install and whether it runs: every 3 s is plenty (a process list is not free).
        if (_xplaneCheckedAt is null || DateTime.UtcNow - _xplaneCheckedAt > TimeSpan.FromSeconds(3))
        {
            CheckXPlane();
            _xplaneCheckedAt = DateTime.UtcNow;
        }
        else if (Mount.State == MountState.Stopped)
        {
            _timer.Stop();
        }
        Notices = BuildNotices();
        foreach (string name in new[]
                 {
                     nameof(ShowFly), nameof(FlyText), nameof(FlyToolTip), nameof(CanChangeDeparturePrefetch), nameof(ShowReloadFlightPlan),
                     nameof(FlightPlanStatus), nameof(ShowFlightPlanStatus),
                     nameof(StartStopText), nameof(StartStopGlyph), nameof(IsBusy), nameof(StatusKind), nameof(StatusGlyph), nameof(StatusTitle),
                     nameof(StatusText), nameof(ShowGettingStarted), nameof(ShowStats), nameof(StatsTitle), nameof(TexturesValue), nameof(ReadyAheadValue),
                     nameof(ReadyAheadCaption), nameof(BuildingValue), nameof(SpeedValue), nameof(SpeedCaption), nameof(XPlaneTitle),
                     nameof(XPlaneGlyph), nameof(XPlaneText), nameof(PackagesCaption), nameof(ShowReport),
                 })
            Raise(name);
    }

    private void MeasureSpeed()
    {
        if (_status is not { } s) return;
        DateTime now = DateTime.UtcNow;
        if (_previous is { } p && (now - p.At).TotalSeconds > 0.2)
        {
            double seconds = (now - p.At).TotalSeconds;
            // Smoothed, so the number does not jump with every second's burst.
            _mbits = 0.7 * _mbits + 0.3 * ((s.BytesDownloaded - p.Bytes) * 8 / seconds / 1e6);
            _tilesPerSecond = 0.7 * _tilesPerSecond + 0.3 * ((s.TilesDownloaded - p.Tiles) / seconds);
        }
        _previous = (now, s.BytesDownloaded, s.TilesDownloaded, s.FailedTiles);
        _failureWindow.Enqueue((now, s.FailedTiles, s.TilesDownloaded));
        while (_failureWindow.Count > 0 && now - _failureWindow.Peek().At > TimeSpan.FromMinutes(1)) _failureWindow.Dequeue();
    }

    private List<Notice> BuildNotices()
    {
        var notices = new List<Notice>();
        if (Mount.State == MountState.Stopped && !ShowReview)
        {
            if (_tuningNotice is not null) notices.Add(_tuningNotice);
            else if (Mount.LastReview is { TooShort: true } tooShort)
                notices.Add(new Notice(Severity.Info, "Fly a little longer for tuning",
                    $"After {Tuner.MinMinutesInFlight:0} minutes of flying or more, OrthoStreamXP suggests how to tune {PresetName(tooShort.Level)} for your PC and connection. This session had {tooShort.MinutesInFlight:0}."));
            else if (Mount.LastReview is { Suggestions.Count: 0 } suited)
                notices.Add(new Notice(Severity.Success, $"{PresetName(suited.Level)} suits your PC and connection",
                    $"OrthoStreamXP watched {suited.MinutesInFlight:0} minutes of flying and found nothing to tune."));
        }
        if (_updates.ReadyVersion is { } version)
            notices.Add(Mount.State == MountState.Stopped
                ? new Notice(Severity.Info, $"OrthoStreamXP {version} is ready to install",
                    $"A new version was downloaded (you have {_updates.CurrentVersion}). See what's new and install it when you choose; your settings, cache and flight history stay.",
                    "Update…", InstallUpdate)
                : new Notice(Severity.Info, $"OrthoStreamXP {version} is ready to install",
                    $"A new version was downloaded (you have {_updates.CurrentVersion}). Stop the streaming when you have finished flying, and install it from here."));
        if (_xplaneProblem is not null) notices.Add(_xplaneProblem);
        if (Mount.Problem is { } problem)
            notices.Add(new Notice(Severity.Error, problem.Title, problem.Message, problem.ActionLabel,
                problem.Action == ProblemAction.None ? null : _main.HandleProblem, problem.Action));
        if (Mount.ChangedSinceStart() is { Count: > 0 } changed)
            notices.Add(new Notice(Severity.Warning, "Restart OrthoStreamXP to use your new settings",
                $"{SettingNames.List(changed)} changed since OrthoStreamXP started. They are saved, and used from the next start.",
                "Restart now", RestartMount));
        if (Mount.State == MountState.Running && Mount.XPlaneWasRunningAtStart && !XPlaneRestartedSinceStart())
            notices.Add(new Notice(Severity.Warning, "Restart X-Plane",
                "X-Plane was already running when OrthoStreamXP started. It looks for scenery only when it starts, so quit X-Plane and start it again to see the photo scenery."));
        if (Mount.State == MountState.Running && _failureWindow.Count > 1)
        {
            var (_, failedBefore, tilesBefore) = _failureWindow.Peek();
            long failed = _status!.FailedTiles - failedBefore, fetched = _status.TilesDownloaded - tilesBefore;
            if (failed >= 20 && failed > fetched / 10)
                notices.Add(new Notice(Severity.Warning, "Downloads are failing",
                    $"{Count(failed)} picture tiles could not be downloaded in the last minute. Check your internet connection; OrthoStreamXP keeps trying.",
                    "Open log", OpenLog));
        }
        return notices;
    }

    private bool XPlaneRestartedSinceStart()
    {
        Process[] running = Process.GetProcessesByName("X-Plane");
        try
        {
            if (running.Length == 0) return false;
            return running.Any(p =>
            {
                try
                {
                    return p.StartTime > Mount.StartedAt;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            });
        }
        finally
        {
            foreach (Process p in running) p.Dispose();
        }
    }

    private static string Count(long? value) => value is { } v ? v.ToString("N0", CultureInfo.CurrentCulture) : "–";
}
