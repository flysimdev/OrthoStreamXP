using System.Windows;
using OrthoStreamXP.Gui;

namespace OrthoStreamXP.Tests;

/// <summary>The notification-area icon is there exactly while the window is hidden.</summary>
public sealed class TrayIconTests
{
    [Fact]
    public void Icon_shows_while_the_window_is_hidden_and_goes_when_it_is_shown_again()
    {
        var seen = new List<(bool WindowVisible, bool IconShown)>();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window { Width = 200, Height = 100, ShowActivated = false };
                using var tray = new TrayIcon(window);
                window.Show();
                seen.Add((window.IsVisible, tray.IsShown));
                tray.HideWindow();
                seen.Add((window.IsVisible, tray.IsShown));
                tray.ShowWindow();
                seen.Add((window.IsVisible, tray.IsShown));
                // Shown by other means (a second start of the program): the icon goes too.
                tray.HideWindow();
                window.Show();
                seen.Add((window.IsVisible, tray.IsShown));
                window.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
        Assert.Equal([(true, false), (false, true), (true, false), (true, false)], seen);
    }
}
