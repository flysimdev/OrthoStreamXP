namespace OrthoStreamXP.Gui;

/// <summary>
/// Tells from the process list's answers, one check at a time, when X-Plane quits. Only what a check saw counts: an
/// X-Plane just started may not be in the process list at the next check yet, and that is not a quit.
/// </summary>
internal sealed class XPlaneWatch
{
    private bool _seenRunning;

    /// <summary>Notes whether X-Plane runs now; true when it ran at the last check and no longer does.</summary>
    public bool QuitSince(bool runningNow)
    {
        bool quit = _seenRunning && !runningNow;
        _seenRunning = runningNow;
        return quit;
    }
}
