using OrthoGlobalXP.Geo;

namespace OrthoStreamXP.Gui;

/// <summary>Where a tile is in a build, as the Build Scenery map shows it.</summary>
internal enum MapTileState
{
    None,
    Building,
    Built,
    Failed,
}

/// <summary>
/// What the Build Scenery map shows of each 1° tile: whether X-Plane's Global Scenery has it (so it can be built), whether
/// it is picked, and where it is in a build. <see cref="Changed"/> fires after every change.
/// </summary>
internal sealed class TileMapModel
{
    private readonly HashSet<TileCoordinate> _available = [];
    private readonly HashSet<TileCoordinate> _selected = [];
    private readonly Dictionary<TileCoordinate, MapTileState> _states = [];

    public event EventHandler? Changed;

    public IReadOnlyCollection<TileCoordinate> Selected => _selected;

    public int AvailableCount => _available.Count;

    public bool IsAvailable(TileCoordinate tile) => _available.Contains(tile);

    public bool IsSelected(TileCoordinate tile) => _selected.Contains(tile);

    public MapTileState StateOf(TileCoordinate tile) => _states.GetValueOrDefault(tile);

    /// <summary>The tiles X-Plane has; picked tiles it lacks are dropped.</summary>
    public void SetAvailable(IEnumerable<TileCoordinate> tiles)
    {
        _available.Clear();
        _available.UnionWith(tiles);
        _selected.IntersectWith(_available);
        OnChanged();
    }

    /// <summary>Marks the tiles the packages have, clearing the build states of the others.</summary>
    public void SetBuilt(IEnumerable<TileCoordinate> tiles)
    {
        _states.Clear();
        foreach (TileCoordinate tile in tiles) _states[tile] = MapTileState.Built;
        OnChanged();
    }

    public void SetState(TileCoordinate tile, MapTileState state)
    {
        if (state == MapTileState.None) _states.Remove(tile);
        else _states[tile] = state;
        OnChanged();
    }

    /// <summary>Picks or drops one tile; only tiles X-Plane has can be picked.</summary>
    public void Toggle(TileCoordinate tile)
    {
        if (!_selected.Remove(tile) && _available.Contains(tile)) _selected.Add(tile);
        OnChanged();
    }

    /// <summary>Picks (only those X-Plane has) or drops the tiles.</summary>
    public void Select(IEnumerable<TileCoordinate> tiles, bool selected)
    {
        foreach (TileCoordinate tile in tiles)
        {
            if (!selected) _selected.Remove(tile);
            else if (_available.Contains(tile)) _selected.Add(tile);
        }
        OnChanged();
    }

    public void ClearSelection()
    {
        _selected.Clear();
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
