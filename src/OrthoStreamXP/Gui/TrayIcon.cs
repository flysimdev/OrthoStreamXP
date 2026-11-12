using System.Windows;
using Forms = System.Windows.Forms;

namespace OrthoStreamXP.Gui;

/// <summary>
/// The window's icon in the notification area, shown while the window is hidden (such as after it started X-Plane, so it
/// does not stay in front of it). A click on it shows the window again. It goes when the window closes.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Window _window;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly System.Drawing.Icon _picture = LoadIcon();
    private bool _disposed;

    public TrayIcon(Window window)
    {
        _window = window;
        _menu.Items.Add("Open OrthoStreamXP", null, (_, _) => ShowWindow());
        // Through the window, so closing while the scenery is mounted still asks first.
        _menu.Items.Add("Exit", null, (_, _) =>
        {
            ShowWindow();
            _window.Close();
        });
        _icon = new Forms.NotifyIcon { Text = "OrthoStreamXP", Icon = _picture, ContextMenuStrip = _menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowWindow();
        };
        // Shown again by any means (the icon, or a second start of the program): the icon goes.
        _window.IsVisibleChanged += (_, _) =>
        {
            if (_window.IsVisible) _icon.Visible = false;
        };
        _window.Closed += (_, _) => Dispose();
    }

    /// <summary>Whether the icon is in the notification area now.</summary>
    public bool IsShown => _icon.Visible;

    /// <summary>Hides the window from the screen and the taskbar, leaving the icon in the notification area.</summary>
    public void HideWindow()
    {
        _icon.Visible = true;
        _window.Hide();
    }

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        // Shown on its own (X-Plane quit), this program may not take the front from the window that got it, such as
        // Explorer's: on top of all for a moment, it still comes above that window.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Taken off first: otherwise it stays in the notification area until the mouse passes over it.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _picture.Dispose();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        using Stream stream = Application.GetResourceStream(new Uri("pack://application:,,,/orthostreamxp;component/orthostreamxp.ico")).Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
