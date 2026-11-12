using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OrthoStreamXP.Setup;

/// <summary>
/// Gives X-Plane's window the front while it starts. Started through Explorer, X-Plane does not bring itself to the front
/// (Windows lets only the program in front, here OrthoStreamXP, decide which window comes next). Windows hands the front
/// over a moment after it is asked: until X-Plane has it, OrthoStreamXP's window must stay, or the front goes to the
/// window behind it.
/// </summary>
internal static partial class XPlaneWindow
{
    private const int SwRestore = 9;
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Hands the front to X-Plane's window whenever this program has it, until X-Plane's window has stayed in front for
    /// <paramref name="settle"/>, someone chose another program, X-Plane quit, or <paramref name="timeout"/> passed.
    /// Call it while this program's window is in front, and hide that window only after it returns.
    /// </summary>
    /// <returns>True when X-Plane's window settled in front.</returns>
    public static async Task<bool> HandOverAsync(TimeSpan settle, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var waited = Stopwatch.StartNew();
        nint shown = 0;
        TimeSpan? inFrontSince = null;
        bool seen = false;
        while (waited.Elapsed < timeout)
        {
            if (Find() is not { } xplane)
            {
                if (seen)
                {
                    Log.Info("X-Plane quit while it started");
                    return false;
                }
            }
            else
            {
                seen = true;
                uint inFront = ProcessOf(GetForegroundWindow());
                if (inFront == 0)
                {
                    // No window in front for a moment, as while a window is replaced: look again.
                }
                else if (inFront == xplane.ProcessId)
                {
                    inFrontSince ??= waited.Elapsed;
                    if (waited.Elapsed - inFrontSince >= settle)
                    {
                        Log.Info($"X-Plane's window is in front (after {waited.Elapsed.TotalSeconds:0} s)");
                        return true;
                    }
                }
                else if (inFront == (uint)Environment.ProcessId)
                {
                    inFrontSince = null;
                    if (IsIconic(xplane.Window)) ShowWindow(xplane.Window, SwRestore);
                    bool given = SetForegroundWindow(xplane.Window);
                    if (xplane.Window != shown)
                        Log.Info($"X-Plane's window \"{xplane.Title}\" (0x{xplane.Window:X}) {(given ? "brought to the front" : "was not let to the front by Windows")} after {waited.Elapsed.TotalSeconds:0.0} s");
                    shown = xplane.Window;
                }
                else
                {
                    // Another program is in front: chosen by the person, so X-Plane is not pushed over it.
                    Log.Info($"{ProgramName(inFront)} came to the front while X-Plane started; X-Plane's window is left where it is");
                    return false;
                }
            }
            await Task.Delay(CheckEvery, cancellationToken).ConfigureAwait(true);
        }
        Log.Info($"X-Plane's window did not settle in front within {timeout.TotalSeconds:0} s");
        return false;
    }

    private static (uint ProcessId, nint Window, string Title)? Find()
    {
        Process[] running = Process.GetProcessesByName("X-Plane");
        try
        {
            foreach (Process process in running)
            {
                try
                {
                    if (process.MainWindowHandle != 0) return ((uint)process.Id, process.MainWindowHandle, process.MainWindowTitle);
                }
                catch (InvalidOperationException)
                {
                    // It quit meanwhile.
                }
            }
            return null;
        }
        finally
        {
            foreach (Process process in running) process.Dispose();
        }
    }

    private static string ProgramName(uint processId)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "Another program"; // it quit meanwhile
        }
    }

    private static uint ProcessOf(nint window)
    {
        if (window == 0) return 0;
        _ = GetWindowThreadProcessId(window, out uint processId);
        return processId;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);
}
