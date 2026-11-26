using System.ComponentModel;
using System.Windows;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

internal sealed partial class MainWindow
{
    private readonly MainViewModel _main;
    private bool _closing;

    public MainWindow(MainViewModel main)
    {
        _main = main;
        DataContext = main;
        InitializeComponent();
        // The Home page's Update button: the release notes first, installed only when confirmed.
        _main.Home.UpdateOffered += (_, offer) =>
        {
            if (new UpdateDialog(offer) { Owner = this }.ShowDialog() == true) _main.Home.InstallConfirmedUpdate();
        };
        // The Home page's Check for updates button found nothing to install.
        _main.Home.UpdateCheckReported += (_, message) =>
            MessageBox.Show(this, message, "OrthoStreamXP", MessageBoxButton.OK, MessageBoxImage.Information);
        var tray = new TrayIcon(this);
        _main.Home.XPlaneStarted += async (_, _) => await HandOverToXPlaneAsync(tray).ConfigureAwait(true);
        // The flight is over: back on the Home page, with Stop and (once stopped) the flight's report.
        _main.Home.XPlaneQuit += (_, _) =>
        {
            if (IsVisible) return;
            _main.Show(_main.Home);
            tray.ShowWindow();
        };
    }

    /// <summary>
    /// X-Plane was started: once its window has the front (given by this window, which has it), this window goes to the
    /// notification area, out of its way. Not before: hidden while it still has the front, the front would go to the
    /// window behind it instead of X-Plane's.
    /// </summary>
    private async Task HandOverToXPlaneAsync(TrayIcon tray)
    {
        await XPlaneWindow.HandOverAsync(settle: TimeSpan.FromSeconds(1), timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(true);
        // X-Plane quit meanwhile: the window stays, as it would come back for the quit anyway.
        if (IsVisible && SystemCheck.IsXPlaneRunning()) tray.HideWindow();
    }

    /// <summary>Closing while the scenery is mounted asks first, then unmounts before the window goes.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // A build stops before the window goes: the tiles under way are given up, the ones built kept.
        if (!_closing && _main.BuildScenery.IsBuilding)
        {
            e.Cancel = true;
            if (MessageBox.Show(this, "Scenery is being built. Stop the build and close?\n\nThe tiles already built are kept.",
                    "OrthoStreamXP", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;
            _main.BuildScenery.Stop.Execute(null);
            while (_main.BuildScenery.IsBuilding) await Task.Delay(200).ConfigureAwait(true);
            Close();
            return;
        }
        _main.BuildScenery.SaveChoices();
        if (_closing || _main.Mount.IsStopped) return;
        e.Cancel = true;
        if (_main.Mount.IsRunning && MessageBox.Show(this,
                "OrthoStreamXP is running. If you close it, X-Plane loses the photo scenery.\n\nStop OrthoStreamXP and close?",
                "OrthoStreamXP", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        _closing = true;
        while (_main.Mount.State is MountState.Starting or MountState.Stopping) await Task.Delay(200).ConfigureAwait(true);
        await _main.Mount.StopAsync().ConfigureAwait(true);
        Close();
    }
}
