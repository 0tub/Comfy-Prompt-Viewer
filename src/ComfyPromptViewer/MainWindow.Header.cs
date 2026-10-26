using System;
using System.IO;
using System.Linq;
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
        _startMenu.IncludeSubfoldersRequested += enabled => DebugLog.Observe(_session.SetIncludeSubfoldersAsync(enabled), "Include subfolders");
        _startMenu.PrewarmThumbnailsChanged += OnPrewarmThumbnailsChanged;
        StartMenu.Initialize(_startMenu);
    }

    private void InitializeHeader()
    {
        _header = new HeaderViewModel(_cacheMaintenance, _targetTileSize);
        _header.BackRequested += ShowMainMenu;
        _header.CopyPathRequested += () => DebugLog.Observe(CopyFolderPathAsync(), "Copy folder path");
        _header.ClearFolderCacheRequested += () => DebugLog.Observe(ClearFolderCacheAsync(), "Clear folder thumbnail cache");
        _header.SortChanged += mode =>
        {
            _session.SortMode = mode;
            _session.ApplySort();
            ApplyFilter(resetScroll: true);
        };
        _header.IncludeSubfoldersRequested += enabled => DebugLog.Observe(_session.SetIncludeSubfoldersAsync(enabled), "Include subfolders");
        _header.SearchEdited += () =>
        {
            _session.HasSearchQueryActive = !string.IsNullOrWhiteSpace(_header.SearchText);
            _session.CancelSearchFilter();
        };
        _header.FilterRequested += () => ApplyFilter(resetScroll: true);
        _header.TileSizeChanged += value => SetTileSize(RoundToTileStep(value), persist: true);
        _header.SyncIncludeSubfolders(_session.IncludeSubfolders);
        Header.Initialize(_header);
    }

    private void OnPrewarmThumbnailsChanged(bool enabled)
    {
        _session.SetPrewarmThumbnails(enabled);
    }

    private async Task CopyFolderPathAsync()
    {
        if (_session.CurrentFolderPath is { Length: > 0 } folderPath)
        {
            await CopyTextAsync(folderPath);
        }
    }

    private static string TruncatePath(string path, int maxSegments = 3)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "Open an image folder to start scrolling.";
        }

        var separator = Path.DirectorySeparatorChar;
        var parts = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= maxSegments)
        {
            return path;
        }

        return "..." + separator + string.Join(separator, parts.Skip(parts.Length - maxSegments));
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
