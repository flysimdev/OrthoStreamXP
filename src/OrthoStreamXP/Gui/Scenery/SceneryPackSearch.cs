using System.Globalization;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Gui;

/// <summary>
/// What the Scenery page's search box keeps: the packages whose name or path contains the text, ignoring case and whether
/// the path is written with / or \. An empty search keeps every package.
/// </summary>
internal sealed class SceneryPackSearch(string? text)
{
    private readonly string _text = Normalize(text?.Trim() ?? "");

    public bool IsEmpty => _text.Length == 0;

    public bool Matches(SceneryPackEntry entry) =>
        IsEmpty
        || entry.Name.Contains(_text, StringComparison.OrdinalIgnoreCase)
        || Normalize(entry.Path).Contains(_text, StringComparison.OrdinalIgnoreCase);

    /// <summary>"1,630 packages", or "3 of 1,630 packages" while searching; null for an empty list.</summary>
    public string? CountText(int shown, int total)
    {
        if (total == 0) return null;
        // Invariant, as the program runs (Program.cs): the same thousands separator on every machine.
        string packages = string.Create(CultureInfo.InvariantCulture, $"{total:N0} {(total == 1 ? "package" : "packages")}");
        return IsEmpty ? packages : string.Create(CultureInfo.InvariantCulture, $"{shown:N0} of {packages}");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
