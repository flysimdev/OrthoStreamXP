using OrthoStreamXP.Gui;

namespace OrthoStreamXP.Tests;

public sealed class XPlaneWatchTests
{
    [Fact]
    public void Quit_is_when_a_check_no_longer_sees_X_Plane_that_the_last_one_saw()
    {
        var watch = new XPlaneWatch();
        Assert.False(watch.QuitSince(true));
        Assert.False(watch.QuitSince(true));
        Assert.True(watch.QuitSince(false));
        // Reported once, not at every check after.
        Assert.False(watch.QuitSince(false));
    }

    [Fact]
    public void Not_running_yet_is_not_a_quit()
    {
        // Just started from the Fly button: the first checks may not see it in the process list yet.
        var watch = new XPlaneWatch();
        Assert.False(watch.QuitSince(false));
        Assert.False(watch.QuitSince(false));
        Assert.False(watch.QuitSince(true));
    }

    [Fact]
    public void A_second_flight_is_watched_too()
    {
        var watch = new XPlaneWatch();
        watch.QuitSince(true);
        Assert.True(watch.QuitSince(false));
        Assert.False(watch.QuitSince(true));
        Assert.True(watch.QuitSince(false));
    }
}
