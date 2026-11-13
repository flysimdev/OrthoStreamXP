namespace OrthoStreamXP.Gui;

/// <summary>What the window calls the settings of orthostreamxp.ini, to say in plain words which ones changed.</summary>
internal static class SettingNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["level-change"] = "Quality",
        ["ground-detail"] = "Ground detail",
        ["xplane"] = "X-Plane folder",
        ["base-packages"] = "Scenery packages folder",
        ["source"] = "Scenery packages folder",
        ["cache"] = "Cache folder",
        ["texture-provider"] = "Where the pictures come from",
        ["download-workers"] = "Downloads at once",
        ["match-colours"] = "Colour matching",
        ["match-strength"] = "Colour matching",
        ["seasons"] = "Seasons",
        ["season"] = "Seasons",
        ["season-strength"] = "Seasons",
        ["exposure"] = "Exposure",
        ["night-lights"] = "Night lights",
        ["skip-photos-at-night"] = "Photos at night",
        ["skip-photos-sun-below"] = "Photos at night",
        ["scenery-packs"] = "Keep the photo scenery below airports",
        ["xplane-address"] = "X-Plane address",
        ["xplane-arguments"] = "X-Plane start options",
        ["simbrief-user"] = "SimBrief departure prefetch",
        ["simbrief-radius-km"] = "SimBrief departure prefetch",
        ["simbrief-download-limit"] = "SimBrief prefetch download limit",
        ["simbrief-alternate"] = "SimBrief alternate airport",
    };

    /// <summary>Settings the running streaming does not use: changing them never needs a restart.</summary>
    public static readonly IReadOnlySet<string> NotForStreaming = new HashSet<string>(StringComparer.Ordinal) { "xplane-arguments" };

    /// <summary>The names of the settings, each once and in the order they are on the Settings page; any other is "Tuned settings".</summary>
    public static IReadOnlyList<string> Of(IEnumerable<string> keys)
    {
        string[] order = [.. Names.Values.Distinct(), "Tuned settings"];
        return [.. keys.Select(k => Names.GetValueOrDefault(k, "Tuned settings")).Distinct().OrderBy(n => Array.IndexOf(order, n))];
    }

    /// <summary>"Quality", "Quality and Seasons", "Quality, Seasons and Cache folder".</summary>
    public static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };
}
