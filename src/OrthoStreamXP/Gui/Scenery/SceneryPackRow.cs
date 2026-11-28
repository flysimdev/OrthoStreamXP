using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Gui;

/// <summary>An entry of scenery_packs.ini on the Scenery page, switched on or off in place.</summary>
internal sealed class SceneryPackRow : ObservableObject
{
    private readonly Func<SceneryPackRow, bool, bool> _switch;
    private bool _enabled, _highlighted;

    /// <param name="streamed">One of the packages the running mount shows to X-Plane.</param>
    /// <param name="switchTo">Writes the change; false when it could not, and the switch goes back.</param>
    public SceneryPackRow(SceneryPackEntry entry, bool streamed, Func<SceneryPackRow, bool, bool> switchTo)
    {
        Entry = entry;
        Streamed = streamed;
        _switch = switchTo;
        _enabled = entry.Enabled;
    }

    public SceneryPackEntry Entry { get; }
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool Streamed { get; }

    /// <summary>The package last found with "Show in list": marked so it stands out in the whole list.</summary>
    public bool IsHighlighted
    {
        get => _highlighted;
        set => Set(ref _highlighted, value);
    }

    /// <summary>Whether X-Plane loads it: written to the file as soon as it is switched.</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            if (_switch(this, value)) _enabled = value;
            Raise();
        }
    }
}
