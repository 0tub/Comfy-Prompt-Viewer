using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

// The window's reactions to the folder session: what each of its events means for the header, the grid, the
// selection and the start screen. What the session does is in FolderSession.
public partial class MainWindow
{
    private FolderSession _session = null!;

    // The catalog belongs to the session; the grid code reads it all the time.
    private GalleryCatalog _catalog => _session.Catalog;

    private void InitializeSession()
    {
        _session = new FolderSession(
            _preferences,
            _thumbnailService,
            _thumbnailLoads,
            _decodedImageCache,
            _metadataRepository,
            _metadataService,
            _metadataScanner,
            () => (_header.SearchText, _header.SearchScope),
            () => _viewModel.ActiveItem?.Path)
        {
            TileSize = _targetTileSize
        };
        _session.Clearing += OnSessionClearing;
        _session.FolderOpening += OnFolderOpening;
        _session.FolderScanned += count => _header.SetCountText($"{count:n0} images");
        _session.FolderShown += () =>
        {
            StartMenu.IsVisible = false;
            Header.IsVisible = true;
        };
        _session.CatalogLoaded += OnCatalogLoaded;
        _session.Closed += () =>
        {
            _header.FolderCacheStatus.Clear();
            Header.IsVisible = false;
            StartMenu.Open();
        };
        _session.LoadFailed += message => _startMenu.ShowError(message);
        _session.Filtering += RefreshPromptHighlightsIfQueryChanged;
        _session.FilterApplied += ApplyFilteredItems;
        _session.StatusChanged += UpdateCountText;
        _session.Message += text => _header.SetCountText(text);
        _session.IncludeSubfoldersChanged += SyncIncludeSubfoldersToggles;
        _session.ViewportScheduleRequested += () => Gallery.ScheduleViewportThumbnails();
        _session.ItemRemoved += OnItemRemoved;
        _session.ItemReplaced += OnItemReplaced;
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        var lastFolder = _preferences.LoadLastFolderPath();
        if (!string.IsNullOrWhiteSpace(lastFolder) && System.IO.Directory.Exists(lastFolder))
        {
            OpenFolder(lastFolder);
        }
        else
        {
            ShowMainMenu();
        }
    }

    private void ApplyFilter(bool resetScroll = false)
    {
        _session.ApplyFilter(resetScroll);
    }

    private void ApplyFilteredItems(IReadOnlyList<ImageItem> filtered, bool resetScroll)
    {
        Gallery.ShowItems(filtered, resetScroll, _catalog.Count);
    }

    private void OpenFolder(string folderPath)
    {
        DebugLog.Observe(_session.OpenFolderAsync(folderPath), "Open folder");
    }

    private void ShowMainMenu()
    {
        _session.Close();
    }

    // The items are about to go: nothing may stay selected, and the grid and its schedule start over.
    private void OnSessionClearing()
    {
        _viewModel.SelectItem(null);
        _viewModel.ClearSelection();
        _viewModel.Items.Clear();
        Gallery.Reset();
    }

    private void OnFolderOpening(string folderPath)
    {
        ResetSearchForNewFolder(folderPath);
        _header.SetFolder(GetFolderDisplayName(folderPath), TruncatePath(folderPath));
        _header.SetCountText("Scanning...");
        _header.FolderCacheStatus.Clear();
    }

    private void OnCatalogLoaded(string? selectPath)
    {
        Gallery.QueueViewportSchedule();

        if (!string.IsNullOrEmpty(selectPath) && _catalog.TryGet(selectPath, out var match))
        {
            _viewModel.SelectItem(match.Item);
        }
    }

    private void OnItemRemoved(ImageItem item, IReadOnlySet<string> deletedPaths)
    {
        _viewModel.Remove(item, deletedPaths);
    }

    private void OnItemReplaced(ImageItem previous, ImageItem replacement)
    {
        _viewModel.Replace(previous, replacement);
    }

    private void SyncIncludeSubfoldersToggles()
    {
        _header.SyncIncludeSubfolders(_session.IncludeSubfolders);
        _startMenu.SyncIncludeSubfolders(_session.IncludeSubfolders);
    }

    // A search typed for one folder rarely means anything in another. Reloads, the Subfolders toggle, and
    // reopening the same folder keep it.
    private void ResetSearchForNewFolder(string folderPath)
    {
        var folderKey = ThumbnailFolderScope.NormalizeFolderPath(folderPath);
        if (string.Equals(folderKey, _searchFolderKey, StringComparison.Ordinal))
        {
            return;
        }

        _searchFolderKey = folderKey;
        _header.ResetSearch();
    }

    private static string GetFolderDisplayName(string folderPath)
    {
        var name = System.IO.Path.GetFileName(folderPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? folderPath : name;
    }

    private void UpdateCountText()
    {
        var counts = _session.GetCountSnapshot();
        _header.SetCountText(HeaderViewModel.FormatCount(
            counts.Total,
            _viewModel.Items.Count,
            counts.LoadedMetadata,
            counts.ShowScanProgress,
            counts.IsScanning,
            counts.PrewarmRemaining,
            counts.PrewarmTotal,
            _viewModel.Selection.Count));
    }
}
