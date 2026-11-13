using System.Windows.Input;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Gui;

/// <summary>
/// "Test SimBrief connection": fetches the latest flight plan of the username entered and says which flight it found,
/// or why there is none, so a typo shows before the first flight.
/// </summary>
internal sealed class SimBriefTestViewModel : ObservableObject
{
    private readonly SimBriefClient _client;
    private readonly Func<string?> _user;
    private bool _isRunning;
    private string? _status;
    private bool _failed;
    private bool _succeeded;

    /// <param name="user">The username or Pilot ID entered now.</param>
    public SimBriefTestViewModel(HttpClient http, Func<string?> user)
    {
        _client = new SimBriefClient(http);
        _user = user;
        Run = new RelayCommand(() => _ = RunAsync(), () => !IsRunning && !string.IsNullOrWhiteSpace(_user()));
    }

    public ICommand Run { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            Raise(nameof(RunText));
            RelayCommand.Refresh();
        }
    }

    public string RunText => IsRunning ? "Testing…" : "Test SimBrief connection";

    /// <summary>The flight found, or why there is none; null before a test.</summary>
    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>The last test found no flight plan (shown in red).</summary>
    public bool Failed
    {
        get => _failed;
        private set => Set(ref _failed, value);
    }

    /// <summary>The last test found a flight plan (shown in green).</summary>
    public bool Succeeded
    {
        get => _succeeded;
        private set => Set(ref _succeeded, value);
    }

    /// <summary>The username changed: the last result no longer applies.</summary>
    public void Reset()
    {
        Status = null;
        Failed = false;
        Succeeded = false;
        RelayCommand.Refresh();
    }

    private async Task RunAsync()
    {
        IsRunning = true;
        Status = "Asking SimBrief for your latest flight plan…";
        Failed = false;
        Succeeded = false;
        try
        {
            SimBriefAnswer answer = await _client.FetchAsync(_user() ?? "", CancellationToken.None).ConfigureAwait(true);
            Failed = answer.Origin is null;
            Succeeded = !Failed;
            Status = Describe(answer);
        }
#pragma warning disable CA1031 // The click's task is not awaited: an unexpected failure must show and be logged, not vanish.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Warning($"SimBrief connection test failed unexpectedly ({ex.GetType().Name}: {ex.Message})");
            Failed = true;
            Status = "The test failed unexpectedly; see the log.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    internal static string Describe(SimBriefAnswer answer)
    {
        if (answer.Plan is not { } plan) return answer.NoPlan;
        return $"Connected. Your latest flight plan is {plan.Describe()}.";
    }
}
