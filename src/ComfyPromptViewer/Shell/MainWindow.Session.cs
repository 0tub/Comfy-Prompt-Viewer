using System;

namespace ComfyPromptViewer;

// The window's reactions to the folder session: what each of its events means for the header, the grid, the
// selection and the start screen. What the session does is in FolderSession.
public partial class MainWindow
{
    private FolderSession _session = null!;

    private void InitializeSession()
    {
        _session = new FolderSession(
            _preferences,
            _thumbnailService,
            _thumbnailLoads,
            _decodedImageCache,
            _metadataRepository,
            _metadataScanner)
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
        _session.FilterApplied += (filtered, resetScroll) => Gallery.ShowItems(filtered, resetScroll, _session.Catalog.Count);
        _session.StatusChanged += UpdateCountText;
        _session.Message += text => _header.SetCountText(text);
        _session.IncludeSubfoldersChanged += SyncIncludeSubfoldersToggles;
        _session.ViewportScheduleRequested += () => Gallery.ScheduleViewportThumbnails();
        _session.ItemRemoved += _viewModel.Remove;
        _session.ItemReplaced += _viewModel.Replace;
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
            _session.Close();
        }
    }

    private void OpenFolder(string folderPath)
    {
        DebugLog.Observe(_session.OpenFolderAsync(folderPath), "Open folder");
    }

    // The items are about to go: nothing may stay selected, and the grid and its schedule start over.
    private void OnSessionClearing()
    {
        _viewModel.SelectItem(null);
        _viewModel.Items.Clear();
        Gallery.Reset();
    }

    private void OnFolderOpening(string folderPath)
    {
        ResetSearchForNewFolder(folderPath);
        _header.SetFolder(FolderNames.Leaf(folderPath), FolderNames.Truncate(folderPath));
        _header.SetCountText("Scanning...");
        _header.FolderCacheStatus.Clear();
    }

    private void OnCatalogLoaded(string? selectPath)
    {
        Gallery.QueueViewportSchedule();

        if (!string.IsNullOrEmpty(selectPath) && _session.Catalog.TryGet(selectPath, out var match))
        {
            _viewModel.SelectItem(match);
        }
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
        var folderKey = FolderNames.Key(folderPath);
        if (string.Equals(folderKey, _searchFolderKey, StringComparison.Ordinal))
        {
            return;
        }

        _searchFolderKey = folderKey;
        _header.ResetSearch();
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
