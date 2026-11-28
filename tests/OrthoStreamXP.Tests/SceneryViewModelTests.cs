using System.Windows.Data;
using OrthoStreamXP.Gui;

namespace OrthoStreamXP.Tests;

public sealed class SceneryViewModelTests : IDisposable
{
    private const string Packs =
        "I\r\n1000 Version\r\nSCENERY\r\n\r\n"
        + "SCENERY_PACK Custom Scenery/KDEN - Denver International Airport/\r\n"
        + "SCENERY_PACK *GLOBAL_AIRPORTS*\r\n"
        + "SCENERY_PACK_DISABLED Custom Scenery/SFD_KLAX_Los_Angeles_HD_2_Mesh/\r\n"
        + "SCENERY_PACK Custom Scenery/Z KDEN Mesh/\r\n";

    private readonly TempTree _tree = new();

    public SceneryViewModelTests() => _tree.WriteText("X-Plane 12/Custom Scenery/scenery_packs.ini", Packs);

    public void Dispose() => _tree.Dispose();

    private string FileText => File.ReadAllText(_tree.PathOf("X-Plane 12/Custom Scenery/scenery_packs.ini"));

    private SceneryViewModel Page()
    {
        var page = new SceneryViewModel(new MountController(), () => _tree.PathOf("X-Plane 12"));
        page.OnShown();
        return page;
    }

    /// <summary>The rows the page's list shows: the view of Rows the search filters.</summary>
    private static List<string> Shown(SceneryViewModel page) =>
        [.. CollectionViewSource.GetDefaultView(page.Rows).Cast<SceneryPackRow>().Select(r => r.Name)];

    private static SceneryPackRow Row(SceneryViewModel page, string name) => page.Rows.Single(r => r.Name == name);

    [Fact]
    public void Without_a_search_every_package_is_shown_and_can_be_dragged()
    {
        SceneryViewModel page = Page();

        Assert.Equal(["KDEN - Denver International Airport", "*GLOBAL_AIRPORTS*", "SFD_KLAX_Los_Angeles_HD_2_Mesh", "Z KDEN Mesh"], Shown(page));
        Assert.Equal("4 packages", page.CountText);
        Assert.False(page.IsSearching);
        Assert.True(page.CanDrag);
        Assert.Null(page.SearchNote);
    }

    [Fact]
    public void A_search_shows_only_the_matching_packages_in_the_files_order()
    {
        SceneryViewModel page = Page();

        page.Search = "kden";

        Assert.Equal(["KDEN - Denver International Airport", "Z KDEN Mesh"], Shown(page));
        Assert.Equal(4, page.Rows.Count); // nothing is taken out of the list itself
        Assert.Equal("2 of 4 packages", page.CountText);
        Assert.True(page.IsSearching);
    }

    [Fact]
    public void While_searching_rows_cannot_be_dragged_and_a_drop_changes_nothing()
    {
        SceneryViewModel page = Page();
        page.Search = "kden";

        page.Drop(Row(page, "Z KDEN Mesh"), 0);

        Assert.False(page.CanDrag);
        Assert.Equal("Clear the search to drag packages into a new order.", page.SearchNote);
        Assert.Equal(Packs, FileText);
        Assert.Equal("Z KDEN Mesh", page.Rows[^1].Name);
    }

    [Fact]
    public void Once_the_search_is_cleared_a_drop_moves_the_package()
    {
        SceneryViewModel page = Page();
        page.Search = "kden";
        page.Search = "";

        page.Drop(Row(page, "Z KDEN Mesh"), 0);

        Assert.True(page.CanDrag);
        Assert.Equal("Z KDEN Mesh", page.Rows[0].Name);
        Assert.StartsWith("I\r\n1000 Version\r\nSCENERY\r\n\r\nSCENERY_PACK Custom Scenery/Z KDEN Mesh/", FileText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_that_matches_nothing_says_so()
    {
        SceneryViewModel page = Page();

        page.Search = "  KSEZ ";

        Assert.Empty(Shown(page));
        Assert.Equal("0 of 4 packages", page.CountText);
        Assert.Equal("No package matches \"KSEZ\".", page.SearchNote);
    }

    [Fact]
    public void The_search_stays_when_the_list_is_read_again()
    {
        SceneryViewModel page = Page();
        page.Search = "mesh";

        page.Refresh.Execute(null);

        Assert.Equal(["SFD_KLAX_Los_Angeles_HD_2_Mesh", "Z KDEN Mesh"], Shown(page));
        Assert.Equal("2 of 4 packages", page.CountText);
    }

    [Fact]
    public void A_switch_flipped_while_searching_changes_that_package_alone()
    {
        SceneryViewModel page = Page();
        page.Search = "kden";

        // Second of the rows shown, fourth in the file: the switch must find it by the package, not by where it is shown.
        Assert.Equal(["KDEN - Denver International Airport", "Z KDEN Mesh"], Shown(page));
        Row(page, "Z KDEN Mesh").IsEnabled = false;

        Assert.Equal(Packs.Replace("SCENERY_PACK Custom Scenery/Z KDEN Mesh/", "SCENERY_PACK_DISABLED Custom Scenery/Z KDEN Mesh/", StringComparison.Ordinal), FileText);
        Assert.False(Row(page, "Z KDEN Mesh").IsEnabled);
        Assert.True(Row(page, "KDEN - Denver International Airport").IsEnabled);
    }

    [Fact]
    public void Reading_the_list_again_removes_the_highlight()
    {
        SceneryViewModel page = Page();
        page.ShowInList(Row(page, "Z KDEN Mesh"));

        page.Refresh.Execute(null);

        Assert.DoesNotContain(page.Rows, r => r.IsHighlighted);
    }

    [Fact]
    public void The_search_keeps_packages_added_to_the_file_since_it_was_read()
    {
        SceneryViewModel page = Page();
        page.Search = "kden";
        _tree.WriteText("X-Plane 12/Custom Scenery/scenery_packs.ini", Packs + "SCENERY_PACK Custom Scenery/Y KDEN Overlay/\r\n");

        page.Refresh.Execute(null);

        Assert.Equal(["KDEN - Denver International Airport", "Z KDEN Mesh", "Y KDEN Overlay"], Shown(page));
        Assert.Equal("3 of 5 packages", page.CountText);
    }

    [Fact]
    public void Without_an_x_plane_folder_a_search_shows_nothing_and_no_count()
    {
        var page = new SceneryViewModel(new MountController(), () => null);
        page.OnShown();

        page.Search = "kden";

        Assert.Empty(Shown(page));
        Assert.Null(page.CountText);
        Assert.Null(page.SearchNote);
    }

    [Fact]
    public void Show_in_list_clears_the_search_and_highlights_only_that_package()
    {
        SceneryViewModel page = Page();
        page.Search = "kden";
        page.ShowInList(Row(page, "KDEN - Denver International Airport"));
        page.Search = "kden";

        page.ShowInList(Row(page, "Z KDEN Mesh"));

        Assert.Equal("", page.Search);
        Assert.Equal(4, Shown(page).Count);
        Assert.True(page.CanDrag);
        Assert.Equal(["Z KDEN Mesh"], page.Rows.Where(r => r.IsHighlighted).Select(r => r.Name));
    }
}
