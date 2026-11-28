using System.Globalization;
using OrthoStreamXP.Gui;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Tests;

public sealed class SceneryPackSearchTests
{
    private static readonly SceneryPackEntry Denver = new("Custom Scenery/KDEN - Denver International Airport/", true);
    private static readonly SceneryPackEntry OrthoTile = new(@"D:\Ortho4XP\Tiles\zOrtho4XP_+39-105/", false);
    private static readonly SceneryPackEntry GlobalAirports = new("*GLOBAL_AIRPORTS*", true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_search_keeps_every_package(string? text)
    {
        var search = new SceneryPackSearch(text);

        Assert.True(search.IsEmpty);
        Assert.True(search.Matches(Denver));
        Assert.True(search.Matches(OrthoTile));
    }

    [Theory]
    [InlineData("kden")]
    [InlineData("  KDEN  ")]
    [InlineData("denver international")]
    public void The_name_matches_ignoring_case_and_surrounding_spaces(string text)
    {
        var search = new SceneryPackSearch(text);

        Assert.True(search.Matches(Denver));
        Assert.False(search.Matches(OrthoTile));
    }

    [Theory]
    [InlineData(@"D:\Ortho4XP\Tiles")]
    [InlineData("d:/ortho4xp/tiles")]
    public void The_path_matches_whichever_slashes_it_is_written_with(string text)
    {
        var search = new SceneryPackSearch(text);

        Assert.True(search.Matches(OrthoTile));
        Assert.False(search.Matches(Denver));
    }

    [Fact]
    public void Custom_scenery_paths_match_with_a_backslash_too()
    {
        Assert.True(new SceneryPackSearch(@"Custom Scenery\KDEN").Matches(Denver));
    }

    [Fact]
    public void X_planes_own_entries_match_by_name()
    {
        Assert.True(new SceneryPackSearch("global_airports").Matches(GlobalAirports));
    }

    [Fact]
    public void A_search_that_matches_nothing_keeps_nothing()
    {
        var search = new SceneryPackSearch("KLAX");

        Assert.False(search.IsEmpty);
        Assert.False(search.Matches(Denver));
        Assert.False(search.Matches(OrthoTile));
        Assert.False(search.Matches(GlobalAirports));
    }

    [Fact]
    public void The_count_shows_every_package_without_a_search()
    {
        var search = new SceneryPackSearch("");

        Assert.Equal("1,630 packages", search.CountText(1630, 1630));
        Assert.Equal("1 package", search.CountText(1, 1));
    }

    [Fact]
    public void The_count_shows_the_matches_out_of_every_package_while_searching()
    {
        var search = new SceneryPackSearch("kden");

        Assert.Equal("3 of 1,630 packages", search.CountText(3, 1630));
        Assert.Equal("0 of 1,630 packages", search.CountText(0, 1630));
    }

    [Fact]
    public void The_count_is_written_the_same_in_every_windows_language()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); // 1.630 there

            Assert.Equal("3 of 1,630 packages", new SceneryPackSearch("kden").CountText(3, 1630));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void An_empty_list_has_no_count()
    {
        Assert.Null(new SceneryPackSearch("").CountText(0, 0));
        Assert.Null(new SceneryPackSearch("kden").CountText(0, 0));
    }
}
