using System.Globalization;
using OrthoGlobalXP.Regions;

namespace OrthoStreamXP.Gui;

/// <summary>An area in the list of areas tiles can be picked by.</summary>
internal sealed record RegionItem(Region Region)
{
    public string Title => Region.Name;

    public string Detail => string.Create(CultureInfo.InvariantCulture,
        $"{KindName(Region.Kind)}{(Region.Parent.Length > 0 ? " · " + Region.Parent : "")} · {Region.Tiles.Count:N0} tiles");

    private static string KindName(RegionKind kind) => kind switch
    {
        RegionKind.Continent => "Continent",
        RegionKind.Subregion => "Subregion",
        RegionKind.Country => "Country",
        RegionKind.Region => "Region",
        _ => "State or province",
    };
}

/// <summary>
/// Finds the areas tiles can be picked by: continents, subregions, countries, regions and states (Natural Earth's
/// outlines, in OrthoGlobalXP's region catalogue), by name. Without a search it lists the continents, subregions and
/// countries.
/// </summary>
internal sealed class RegionSearch : ObservableObject
{
    private const int MaxMatches = 200;

    private readonly IReadOnlyList<Region> _regions;
    private string _text = "";
    private IReadOnlyList<RegionItem> _matches = [];
    private RegionItem? _selected;

    public RegionSearch(IReadOnlyList<Region> regions)
    {
        _regions = regions;
        Update();
    }

    /// <summary>A continent, subregion, country, region or state: "Germany", "Bavaria", "Western Europe".</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value ?? "")) Update();
        }
    }

    public IReadOnlyList<RegionItem> Matches
    {
        get => _matches;
        private set => Set(ref _matches, value);
    }

    public RegionItem? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) RelayCommand.Refresh();
        }
    }

    /// <summary>
    /// Takes an area from the list: its name goes into the search without searching again, so it stays the chosen one
    /// even when another area has the same name ("Georgia" the country and the state).
    /// </summary>
    public void Choose(RegionItem item)
    {
        _text = item.Title;
        Raise(nameof(Text));
        Selected = item;
    }

    /// <summary>
    /// The areas whose name (or, after them, whose parent) holds the search, accents and case aside: an exact name first,
    /// then names starting with it, names holding it and areas inside one named so; the larger kind first within each.
    /// </summary>
    public static IEnumerable<Region> Find(IEnumerable<Region> regions, string text)
    {
        string query = text.Trim();
        CompareInfo compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return regions.Select(r =>
            {
                if (query.Length == 0) return (Region: r, Rank: r.Kind <= RegionKind.Country ? 0 : -1);
                int at = compare.IndexOf(r.Name, query, Loose);
                int rank = at < 0 ? (compare.IndexOf(r.Parent, query, Loose) >= 0 ? 3 : -1)
                    : compare.Compare(r.Name, query, Loose) == 0 ? 0 : at == 0 ? 1 : 2;
                return (Region: r, Rank: rank);
            })
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank).ThenBy(x => x.Region.Kind).ThenBy(x => x.Region.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Region);
    }

    private void Update()
    {
        Matches = [.. Find(_regions, Text).Take(MaxMatches).Select(r => new RegionItem(r))];
        if (Text.Trim().Length > 0) Selected = Matches.Count > 0 ? Matches[0] : null;
    }
}
