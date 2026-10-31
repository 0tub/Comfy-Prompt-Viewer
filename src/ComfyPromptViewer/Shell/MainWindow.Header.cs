using System.Threading.Tasks;

namespace ComfyPromptViewer;

// The folder header (back, path, clear this folder's thumbnails) and the way back to the start screen. The
// start screen itself is StartMenuView; clearing caches is CacheMaintenance.
public partial class MainWindow
{
    private HeaderViewModel _header = null!;
    private CacheMaintenance _cacheMaintenance = null!;
    private StartMenuViewModel _startMenu = null!;

    private void InitializeStartMenu()
    {
        _cacheMaintenance = new CacheMaintenance(
            _thumbnailService,
            _thumbnailLoads,
            _decodedImageCache,
            _metadataRepository,
            () => Gallery.QueueViewportSchedule(force: true));

        _startMenu = new StartMenuViewModel(_preferences, _cacheMaintenance, ThemeManager.Apply);
        _startMenu.FolderOpenRequested += OpenFolder;
        _startMenu.IncludeSubfoldersRequested += SetIncludeSubfolders;
        _startMenu.PrewarmThumbnailsChanged += _session.SetPrewarmThumbnails;
        StartMenu.Initialize(_startMenu);
    }

    private void InitializeHeader()
    {
        _header = new HeaderViewModel(_cacheMaintenance, _targetTileSize);
        _header.BackRequested += _session.Close;
        _header.CopyPathRequested += () => DebugLog.Observe(CopyFolderPathAsync(), "Copy folder path");
        _header.ClearFolderCacheRequested += () => DebugLog.Observe(ClearFolderCacheAsync(), "Clear folder thumbnail cache");
        _header.SortChanged += _session.ApplySort;
        _header.IncludeSubfoldersRequested += SetIncludeSubfolders;
        _header.SearchEdited += () => _session.SetSearch(_header.SearchText, _header.SearchScope);
        _header.FilterRequested += () =>
        {
            _session.SetSearch(_header.SearchText, _header.SearchScope);
            _session.ApplyFilter(resetScroll: true);
        };
        _header.TileSizeChanged += value => SetTileSize(value, persist: true);
        _header.SyncIncludeSubfolders(_session.IncludeSubfolders);
        Header.Initialize(_header);
    }

    // Both the start screen and the header offer the toggle; the session may refuse, and syncs both back.
    private void SetIncludeSubfolders(bool enabled)
    {
        DebugLog.Observe(_session.SetIncludeSubfoldersAsync(enabled), "Include subfolders");
    }

    private async Task CopyFolderPathAsync()
    {
        if (_session.CurrentFolderPath is { Length: > 0 } folderPath)
        {
            await CopyTextAsync(folderPath);
        }
    }

    private async Task ClearFolderCacheAsync()
    {
        if (_cacheMaintenance.IsBusy)
        {
            return;
        }

        if (_session.CacheScope is not { IsRetired: false } scope)
        {
            _header.FolderCacheStatus.Show("No folder cache to clear.");
            return;
        }

        var failure = await _cacheMaintenance.ClearFolderAsync(scope);
        _header.FolderCacheStatus.Show(failure is null
            ? "Folder thumbnail cache cleared."
            : $"Could not clear folder cache: {failure.Message}");
    }
}
