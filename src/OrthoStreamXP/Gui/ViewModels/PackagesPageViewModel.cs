namespace OrthoStreamXP.Gui;

/// <summary>
/// The Packages page: its "Build own packages" tab (scenery made from X-Plane's Global Scenery), first and open at the start,
/// and its Download tab (AutoOrtho's mesh regions). Only the open tab is brought up to date, when the page is shown or the
/// tab opened.
/// </summary>
internal sealed class PackagesPageViewModel(PackagesViewModel download, BuildSceneryViewModel build) : ObservableObject, IPage
{
    public const int BuildTab = 0;
    public const int DownloadTab = 1;

    private int _tab = BuildTab;

    public PackagesViewModel Download { get; } = download;

    public BuildSceneryViewModel Build { get; } = build;

    /// <summary>The open tab, kept while the page is made again each time it is shown.</summary>
    public int Tab
    {
        get => _tab;
        set
        {
            if (Set(ref _tab, value)) OnShown();
        }
    }

    public void OnShown()
    {
        if (Tab == BuildTab) Build.OnShown();
        else Download.OnShown();
    }
}
