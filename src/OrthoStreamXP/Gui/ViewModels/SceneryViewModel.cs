using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Gui;

/// <summary>
/// The X-Plane Custom Scenery page: every entry of X-Plane's Custom Scenery\scenery_packs.ini, to switch on or off and reorder.
/// Read again when the page is shown and when OrthoStreamXP starts or stops, as both change the file; changed only while
/// OrthoStreamXP is stopped, so the person's changes and the mount's never meet.
/// </summary>
internal sealed class SceneryViewModel : ObservableObject, IPage
{
    private readonly MountController _mount;
    private readonly Func<string?> _xplane;
    private readonly ICollectionView _shown;
    private string? _message;
    private string _searchText = "";
    private SceneryPackSearch _search = new(null);

    /// <param name="xplane">The X-Plane folder of the settings; null while there is none.</param>
    public SceneryViewModel(MountController mount, Func<string?> xplane)
    {
        _mount = mount;
        _xplane = xplane;
        // The list on the page shows this view of Rows: the rows the search keeps, in the file's order.
        _shown = CollectionViewSource.GetDefaultView(Rows);
        _shown.Filter = row => _search.Matches(((SceneryPackRow)row).Entry);
        Refresh = new RelayCommand(Load);
        OpenFolder = new RelayCommand(() => Shell.Open(CustomScenery!), () => CustomScenery is { } folder && Directory.Exists(folder));
        _mount.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MountController.State)) return;
            Raise(nameof(CanEdit));
            Raise(nameof(CanDrag));
            Raise(nameof(RunningNote));
            RaiseCount();
            RelayCommand.Refresh();
            if (_mount.IsRunning || _mount.IsStopped) Load();
        };
    }

    public ObservableCollection<SceneryPackRow> Rows { get; } = [];
    public ICommand Refresh { get; }
    public ICommand OpenFolder { get; }

    /// <summary>The list can be changed only while OrthoStreamXP is stopped: starting and stopping change the file too.</summary>
    public bool CanEdit => _mount.IsStopped;

    /// <summary>
    /// Rows are dragged only while every row is shown: between two rows a search keeps, hundreds may be hidden, so where a
    /// drop lands would be a guess.
    /// </summary>
    public bool CanDrag => CanEdit && _search.IsEmpty;

    /// <summary>Part of a package's name or path; kept while the list is read again.</summary>
    public string Search
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? "")) return;
            _search = new SceneryPackSearch(_searchText);
            _shown.Refresh();
            Raise(nameof(IsSearching));
            Raise(nameof(CanDrag));
            RaiseCount();
        }
    }

    /// <summary>Whether the search hides some rows; then each row offers "Show in list".</summary>
    public bool IsSearching => !_search.IsEmpty;

    /// <summary>
    /// Clears the search and highlights <paramref name="row"/>, so it can be found in the whole list and dragged to its new
    /// place (the page scrolls to it).
    /// </summary>
    public void ShowInList(SceneryPackRow row)
    {
        foreach (SceneryPackRow other in Rows) other.IsHighlighted = false;
        row.IsHighlighted = true;
        Search = "";
    }

    /// <summary>How many packages the list has, and how many of them the search keeps.</summary>
    public string? CountText => _search.CountText(Rows.Count(r => _search.Matches(r.Entry)), Rows.Count);

    /// <summary>While searching: why the rows cannot be dragged, or that nothing matches.</summary>
    public string? SearchNote =>
        _search.IsEmpty || Rows.Count == 0 ? null
        : Rows.Any(r => _search.Matches(r.Entry)) ? (CanEdit ? "Clear the search to drag packages into a new order." : null)
        : $"No package matches \"{_searchText.Trim()}\".";

    public bool HasSearchNote => SearchNote is not null;

    public string? RunningNote => CanEdit ? null : "OrthoStreamXP is running: stop it to change the list.";

    public string? CustomScenery => _xplane() is { } xplane ? Path.Join(xplane, "Custom Scenery") : null;

    public string FileText => CustomScenery is { } folder
        ? $"From {Path.Join(folder, SceneryPacks.FileName)}, highest priority first."
        : "Choose your X-Plane folder first (in Settings): its scenery list is shown here.";

    public string? Message
    {
        get => _message;
        private set
        {
            Set(ref _message, value);
            Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message is not null;

    public void OnShown() => Load();

    private void Load()
    {
        Rows.Clear();
        Message = null;
        Raise(nameof(FileText));
        RelayCommand.Refresh();
        if (CustomScenery is not { } folder)
        {
            RaiseCount();
            return;
        }
        var list = new SceneryPackList(folder);
        try
        {
            var streamed = new HashSet<string>(_mount.Running?.Packages.Select(p => p.Name) ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (SceneryPackEntry entry in list.Read())
                Rows.Add(new SceneryPackRow(entry, streamed.Contains(entry.Name), Switch));
            if (Rows.Count == 0) Message = $"{list.FilePath} lists no scenery yet: X-Plane writes it when it starts.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not read {list.FilePath}: {ex.Message}";
        }
        finally
        {
            RaiseCount();
        }
    }

    private void RaiseCount()
    {
        Raise(nameof(CountText));
        Raise(nameof(SearchNote));
        Raise(nameof(HasSearchNote));
    }

    private bool Switch(SceneryPackRow row, bool enabled)
    {
        if (!CanEdit || CustomScenery is not { } folder) return false;
        try
        {
            new SceneryPackList(folder).SetEnabled(row.Path, enabled);
            Message = null;
            Log.Info($"{(enabled ? "Enabled" : "Disabled")} {row.Path} in {SceneryPacks.FileName}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not change {row.Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Drops <paramref name="row"/> before the row at <paramref name="insertAt"/> (the row count puts it last), as dragged on the page.
    /// </summary>
    public void Drop(SceneryPackRow row, int insertAt)
    {
        int from = Rows.IndexOf(row);
        if (!CanDrag || CustomScenery is not { } folder || from < 0 || insertAt < 0 || insertAt > Rows.Count) return;
        int to = insertAt > from ? insertAt - 1 : insertAt;
        if (to == from) return;
        try
        {
            if (new SceneryPackList(folder).MoveTo(row.Path, to) && Rows.Count == new SceneryPackList(folder).Read().Count)
            {
                Rows.Move(from, to);
                Message = null;
                Log.Info($"Moved {row.Path} to place {to + 1} in {SceneryPacks.FileName}");
            }
            else
            {
                Load(); // The file changed meanwhile: show it as it is now.
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"Could not move {row.Name}: {ex.Message}";
        }
    }
}
