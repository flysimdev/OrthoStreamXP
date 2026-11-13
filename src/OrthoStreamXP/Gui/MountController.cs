using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

internal enum MountState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>What the person can do about a problem, shown as a button next to it.</summary>
internal enum ProblemAction
{
    None,
    OpenSettings,
    RunSetup,
    OpenCustomScenery,
    DownloadWinFsp,
}

/// <summary>A problem in plain words: what happened, and what to do about it.</summary>
internal sealed record UserProblem(string Title, string Message, ProblemAction Action = ProblemAction.None, string? ActionLabel = null);

/// <summary>Starts and stops the mount for the window, and turns failures into <see cref="UserProblem"/>s.</summary>
internal sealed class MountController : ObservableObject
{
    private MountState _state;
    private UserProblem? _problem;
    private MountSession? _session;
    private Tuning.FlightReview? _lastReview;

    public MountState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsRunning));
            Raise(nameof(IsStopped));
            RelayCommand.Refresh();
        }
    }

    public bool IsRunning => State == MountState.Running;

    public bool IsStopped => State == MountState.Stopped;

    /// <summary>Why the last start failed, until the next start.</summary>
    public UserProblem? Problem
    {
        get => _problem;
        private set => Set(ref _problem, value);
    }

    /// <summary>The settings the running mount started with.</summary>
    public MountSettings? Running => _session?.Settings;

    /// <summary>The settings file's values when the running mount started (null when stopped).</summary>
    public IReadOnlyDictionary<string, string>? StartedWith { get; private set; }

    /// <summary>
    /// The settings changed in the file since the running mount started, by the names the window gives them; empty
    /// when stopped or nothing changed. They take effect at the next start.
    /// </summary>
    public IReadOnlyList<string> ChangedSinceStart()
    {
        if (StartedWith is not { } started || State != MountState.Running) return [];
        string path = SettingsDocument.DefaultPath;
        DateTime written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        if (_changes is { } known && known.Written == written) return known.Names;
        IReadOnlyDictionary<string, string> now = SettingsDocument.Load(path).Values();
        IReadOnlyList<string> names = SettingNames.Of(started.Keys.Union(now.Keys)
            .Where(key => !SettingNames.NotForStreaming.Contains(key))
            .Where(key => !(started.TryGetValue(key, out string? a) && now.TryGetValue(key, out string? b) && a == b)));
        _changes = (written, names);
        return names;
    }

    private (DateTime Written, IReadOnlyList<string> Names)? _changes;

    /// <summary>Stops and starts again, with the settings as the file has them now.</summary>
    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(true);
        await StartAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Builds the SimBrief flight plan's departure ahead at the next start (when a SimBrief user is set). For this
    /// window only, not saved: each time OrthoStreamXP opens it is on again.
    /// </summary>
    public bool DeparturePrefetch { get; set; } = true;

    /// <summary>X-Plane was already running when the mount started, so it has not seen the photo scenery.</summary>
    public bool XPlaneWasRunningAtStart { get; private set; }

    /// <summary>When the running mount started.</summary>
    public DateTime StartedAt { get; private set; }

    public MountStatus? Status() => _session?.Status();

    /// <summary>The running session uses SimBrief (a SimBrief user is set and the switch is on).</summary>
    public bool UsesSimBrief => State == MountState.Running && _session is { UsesSimBrief: true };

    /// <summary>Asks SimBrief for the latest flight plan again (the Home page's button); what came of it, or null without one.</summary>
    public Task<string>? ReloadFlightPlanAsync() => State == MountState.Running ? _session?.ReloadFlightPlanAsync() : null;

    /// <summary>The flight plan the running session fetched as it started (Home's SimBrief line), or null without one.</summary>
    public Task<string>? FirstFlightPlanAsync() => State == MountState.Running ? _session?.FirstFlightPlanAsync() : null;

    /// <summary>The flight report of the session stopped last in this window (null before one stops, or without a flight).</summary>
    public Tuning.FlightReport? LastReport { get; private set; }

    /// <summary>The flight tuning's review of the session stopped last in this window (null before one stops).</summary>
    public Tuning.FlightReview? LastReview
    {
        get => _lastReview;
        private set => Set(ref _lastReview, value);
    }

    public async Task StartAsync()
    {
        if (State != MountState.Stopped) return;
        Problem = null;
        LastReview = null;
        State = MountState.Starting;
        Log.Step("Starting OrthoStreamXP");
        try
        {
            if (!SystemCheck.WinFspInstalled())
            {
                Problem = new UserProblem("WinFsp is needed",
                    "OrthoStreamXP shows the scenery to X-Plane through WinFsp, a free file-system driver. Install it, restart your computer, then press Start again.",
                    ProblemAction.DownloadWinFsp, "Get WinFsp");
                State = MountState.Stopped;
                return;
            }
            string path = SettingsDocument.DefaultPath;
            _session = await Task.Run(() =>
            {
                SettingsDocument document = SettingsDocument.Load(path);
                StartedWith = document.Values();
                _changes = null;
                MountSettings settings = MountSettings.FromValues(document.Parse(), console: false);
                Log.Configure(settings.LogOutput, settings.LogPath, settings.LogDetail);
                if (settings.LogOutput.HasFlag(LogOutput.File)) Log.Info($"Log file: {settings.LogPath}{(settings.LogDetail ? " (detailed)" : "")}");
                Log.Info($"Settings file: {path}");
                if (!DeparturePrefetch && settings.SimBriefUser is not null)
                {
                    settings = settings with { SimBriefUser = null };
                    Log.Info("SimBrief: the departure prefetch is off for this session");
                }
                return MountSession.Start(settings);
            }).ConfigureAwait(true);
            XPlaneWasRunningAtStart = SystemCheck.IsXPlaneRunning();
            StartedAt = DateTime.Now;
            State = MountState.Running;
        }
        catch (Exception ex) when (ex is SettingsException or IOException or UnauthorizedAccessException or DllNotFoundException
                                       or TypeInitializationException or BadImageFormatException or InvalidOperationException)
        {
            Log.Error("OrthoStreamXP could not start", ex);
            Problem = Explain(ex);
            _session = null;
            State = MountState.Stopped;
        }
    }

    /// <param name="hurry">Windows is ending the session: do not wait long for builds still running.</param>
    public async Task StopAsync(bool hurry = false)
    {
        if (State != MountState.Running || _session is not { } session) return;
        State = MountState.Stopping;
        try
        {
            await Task.Run(async () =>
            {
                await session.StopAsync(hurry).ConfigureAwait(false);
                session.Dispose();
                // The streamed textures' RAM copies were large arrays. An ordinary collection frees them but keeps the
                // memory for reuse (3.9 GB stayed with an idle window); an aggressive one gives it back to Windows.
                PrewarmMemory.GiveBack();
            }).ConfigureAwait(true);
            Log.Step("OrthoStreamXP stopped: the photo scenery is no longer shown to X-Plane");
        }
        finally
        {
            LastReview = session.Review;
            LastReport = session.Report;
            _session = null;
            StartedWith = null;
            State = MountState.Stopped;
        }
    }

    /// <summary>Turns a start failure into words and a way out.</summary>
    internal static UserProblem Explain(Exception ex)
    {
        string message = ex.Message;
        if (ex is SettingsException)
        {
            if (message.StartsWith(MountSettings.InCustomScenery, StringComparison.Ordinal))
                return new UserProblem("Your scenery packages are in Custom Scenery", SystemCheck.InCustomSceneryExplanation,
                    ProblemAction.OpenSettings, "Open settings");
            if (message.Contains("must not already exist", StringComparison.Ordinal))
            {
                string folder = message[(message.IndexOf(": ", StringComparison.Ordinal) + 2)..];
                int end = folder.IndexOf(" (", StringComparison.Ordinal);
                string name = Path.GetFileName(end > 0 ? folder[..end] : folder);
                return new UserProblem("A scenery folder is in the way",
                    $"X-Plane's Custom Scenery folder already has a folder called {name}. Usually another program still shows that package " +
                    "there: close it and press Start again. If it is a real folder, move or rename it.",
                    ProblemAction.OpenCustomScenery, "Open Custom Scenery");
            }
            if (message.Contains("Setting required", StringComparison.Ordinal) || message.Contains("give either", StringComparison.Ordinal))
                return new UserProblem("Setup is not finished",
                    "OrthoStreamXP needs to know where X-Plane, your scenery packages and the cache are.", ProblemAction.RunSetup, "Run setup");
            if (message.Contains("Base packages folder", StringComparison.Ordinal) || message.Contains("No scenery packages", StringComparison.Ordinal))
                return new UserProblem("Your scenery packages were not found",
                    "The scenery packages folder is missing or has no packages in it. Choose the folder that holds them.",
                    ProblemAction.OpenSettings, "Open settings");
            if (message.Contains("Not an X-Plane folder", StringComparison.Ordinal))
                return new UserProblem("X-Plane was not found",
                    "The X-Plane folder in the settings has no Custom Scenery folder. Choose the folder X-Plane is installed in.",
                    ProblemAction.OpenSettings, "Open settings");
            return new UserProblem("A setting needs attention", message, ProblemAction.OpenSettings, "Open settings");
        }
        if (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException
            || message.Contains("WinFsp", StringComparison.Ordinal) || message.Contains("winfsp", StringComparison.OrdinalIgnoreCase))
            return new UserProblem("WinFsp could not show the scenery",
                $"{message} If you installed WinFsp or OrthoStreamXP since Windows last started, restart your computer. Otherwise installing (or repairing) WinFsp usually fixes this.", ProblemAction.DownloadWinFsp, "Get WinFsp");
        if (ex is UnauthorizedAccessException)
            return new UserProblem("A folder cannot be written",
                $"{message} Choose a cache folder you can write to, such as one in your user folder.", ProblemAction.OpenSettings, "Open settings");
        return new UserProblem("OrthoStreamXP could not start", message);
    }
}
