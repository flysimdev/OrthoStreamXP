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
