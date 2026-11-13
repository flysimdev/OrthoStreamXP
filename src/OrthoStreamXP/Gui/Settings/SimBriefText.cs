namespace OrthoStreamXP.Gui;

/// <summary>
/// The SimBrief prefetch's explanations, shared by the setup's SimBrief step and the Settings page's SimBrief tab so
/// both say the same.
/// </summary>
internal static class SimBriefText
{
    /// <summary>What the download limit (<c>simbrief-download-limit</c>) does, in one line.</summary>
    public const string LimitHelp =
        "The most the prefetch may download per session. Beyond it, scenery loads as usual while you fly.";

    /// <summary>Why there is a limit at all, and how to choose it.</summary>
    public const string LimitWhy =
        "Why a limit? The free map services can block heavy downloaders, which would hit every user. 1000 MB fits a typical flight; " +
        "choose less on a metered connection, more with Extra quality or the alternate on.";

    /// <summary>What the alternate switch (<c>simbrief-alternate</c>) does.</summary>
    public const string AlternateHelp =
        "At the gate, build the alternate airport too, after the arrival. Rarely flown, so off by default.";

    /// <summary>The limits offered in the setup; a value set by hand in the file is added to them.</summary>
    public static IReadOnlyList<Choice> LimitChoices(int? current)
    {
        List<Choice> choices =
        [
            new("300 MB: a metered or mobile connection", "300"),
            new("500 MB: a slow connection", "500"),
            new($"{MountSettings.DefaultSimBriefDownloadLimitMb} MB: a typical flight (recommended)", MountSettings.DefaultSimBriefDownloadLimitMb.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("3000 MB: Extra quality, or the alternate airport on", "3000"),
            new("No limit (not recommended)", "0"),
        ];
        string? own = current?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (own is not null && choices.All(c => c.Value != own)) choices.Insert(0, new Choice($"{own} MB (set in the settings file)", own));
        return choices;
    }
}
