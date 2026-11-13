using System.Collections.ObjectModel;
using System.Windows.Input;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>
/// The regions of AutoOrtho's scenery mesh, to download into the scenery packages folder: the Packages page, and the same
/// list in the setup's Scenery step for someone who has no packages yet. One install runs at a time; a region is updated
/// or deleted only while OrthoStreamXP is stopped (X-Plane has its files open while it runs).
/// </summary>
internal sealed class PackagesViewModel : ObservableObject, IPage
{
    private readonly MountController _mount;
    private readonly WindowPreferences _preferences;
    private readonly HttpClient _http;
    private readonly Func<string?> _folder;
    private readonly Action<string>? _installed;
    private readonly InstallSlot _slot;
    private CancellationTokenSource? _install;
    private bool _loading;
    private string? _message;

    /// <param name="folder">The scenery packages folder regions go into; null while there is none.</param>
    /// <param name="installed">Called with the folder once a region is installed.</param>
    /// <param name="slot">Shared with the window's other list of regions: one install or delete at a time.</param>
    public PackagesViewModel(MountController mount, WindowPreferences preferences, HttpClient http, InstallSlot slot, Func<string?> folder,
        Action<string>? installed = null)
    {
        _mount = mount;
        _preferences = preferences;
        _http = http;
        _folder = folder;
        _installed = installed;
        _slot = slot;
        Refresh = new RelayCommand(() => _ = LoadAsync(refresh: true), () => !Loading);
        OpenFolder = new RelayCommand(() => Shell.Open(Folder!), () => Folder is { } f && Directory.Exists(f));
        OpenProject = new RelayCommand(() => Shell.Open(MeshCatalog.ProjectPage));
        _mount.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MountController.State)) return;
            Raise(nameof(RunningNote));
            RelayCommand.Refresh();
        };
    }

    public ObservableCollection<RegionRow> Rows { get; } = [];
    public ICommand Refresh { get; }
    public ICommand OpenFolder { get; }
    public ICommand OpenProject { get; }

    public string? Folder => _folder();

    public string FolderText => Folder is { } folder
        ? $"Regions are installed in your scenery packages folder, {folder}, beside your own packages. Your own packages stay above them in X-Plane, so where they overlap yours are shown."
        : "Choose your scenery packages folder first (in the setup): the regions are installed there.";

    public bool DownloadOverlays
    {
        get => _preferences.DownloadOverlays;
        set
        {
            if (_preferences.DownloadOverlays == value) return;
            _preferences.DownloadOverlays = value;
            _preferences.Save();
            Raise();
        }
    }

    public bool Loading
    {
        get => _loading;
        private set
        {
            Set(ref _loading, value);
            RelayCommand.Refresh();
        }
    }

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

    public string? RunningNote => _mount.IsStopped ? null : "OrthoStreamXP is running: a region installed now is used from its next start. Stop it to update or delete a region.";

    /// <summary>The folder regions go into may have changed (the setup's choices): what is installed there is read again.</summary>
    public void FolderChanged()
    {
        Raise(nameof(FolderText));
        UpdateInstalled();
        RelayCommand.Refresh();
    }

    public void OnShown()
    {
        Raise(nameof(FolderText));
        _ = LoadAsync(refresh: false);
    }

    /// <summary>The list from GitHub (or the one kept), and what is installed; rows of a running install are kept.</summary>
    public async Task LoadAsync(bool refresh)
    {
        if (Loading) return;
        Loading = true;
        try
        {
            var catalog = new MeshCatalog(_http, Path.Join(AppPaths.Root, "mesh-regions.json"));
            IReadOnlyList<MeshRegion> regions = await catalog.LoadAsync(refresh, CancellationToken.None).ConfigureAwait(true);
            if (_install is null)
            {
                Rows.Clear();
                foreach (MeshRegion region in regions) Rows.Add(new RegionRow(this, region));
            }
            Message = regions.Count == 0 ? "GitHub lists no mesh regions at the moment." : null;
            UpdateInstalled();
        }
        catch (IOException ex)
        {
            Message = $"{ex.Message} Check the internet connection, then press Refresh.";
        }
        finally
        {
            Loading = false;
        }
    }

    public bool CanInstall(RegionRow row) =>
        !_slot.Busy && Folder is not null && !row.UpToDate && (!row.IsInstalled || _mount.IsStopped);

    public bool CanDelete(RegionRow row) => !_slot.Busy && row.IsInstalled && !row.Deleting && _mount.IsStopped;

    public void StopInstall() => _install?.Cancel();

    public async Task InstallAsync(RegionRow row)
    {
        if (!CanInstall(row) || Folder is not { } folder || !_slot.TryTake()) return;
        using var cancel = new CancellationTokenSource();
        _install = cancel;
        row.Error = null;
        row.Busy = true;
        Message = null;
        RelayCommand.Refresh();
        try
        {
            var progress = new Progress<InstallProgress>(row.Report);
            await Installer(folder).InstallAsync(row.Region, DownloadOverlays, progress, cancel.Token).ConfigureAwait(true);
            _installed?.Invoke(folder);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"The install of the {row.Name} mesh was stopped; what was downloaded is kept for the next time");
        }
        catch (Exception ex) when (ex is InstallException or IOException or UnauthorizedAccessException)
        {
            row.Error = ex.Message;
            Log.Warning(ex.Message);
        }
        catch (Exception ex)
        {
            // The install runs unawaited from a button: nothing that goes wrong may vanish.
            row.Error = $"The {row.Name} mesh could not be installed: {ex.Message}";
            Log.Error(row.Error, ex);
        }
        finally
        {
            _install = null;
            _slot.Release();
            RelayCommand.Refresh();
            row.Busy = false;
            UpdateInstalled();
        }
    }

    public async Task DeleteAsync(RegionRow row)
    {
        if (!CanDelete(row) || Folder is not { } folder || !_slot.TryTake()) return;
        row.Deleting = true;
        row.Error = null;
        try
        {
            await Task.Run(() => Installer(folder).Delete(row.Region.Id)).ConfigureAwait(true);
        }
        catch (InstallException ex)
        {
            row.Error = ex.Message;
            Log.Warning(row.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            row.Error = $"The {row.Name} mesh could not be deleted: {ex.Message}";
            Log.Warning(row.Error);
        }
        catch (Exception ex)
        {
            row.Error = $"The {row.Name} mesh could not be deleted: {ex.Message}";
            Log.Error(row.Error, ex);
        }
        finally
        {
            row.Deleting = false;
            _slot.Release();
            RelayCommand.Refresh();
            UpdateInstalled();
        }
    }

    private RegionInstaller Installer(string folder) => new(new AssetDownload(_http), folder);

    private void UpdateInstalled()
    {
        if (Folder is not { } folder)
        {
            foreach (RegionRow row in Rows) (row.Installed, row.Partial) = (null, 0);
            return;
        }
        RegionInstaller installer = Installer(folder);
        IReadOnlyDictionary<string, InstalledRegion> installed = installer.Installed();
        foreach (RegionRow row in Rows)
        {
            row.Installed = installed.GetValueOrDefault(row.Region.Id);
            row.Partial = row.Busy || row.UpToDate ? 0 : installer.AlreadyDone(row.Region, DownloadOverlays);
        }
    }

    /// <summary>The client every mesh download shares: GitHub asks for a user agent, and a download may take hours.</summary>
    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan, // each download watches for silence itself
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpIdentity.UserAgent);
        return http;
    }
}
