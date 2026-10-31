using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// The open folder, start to finish: scanning it into the catalog, filtering and sorting it, reading every
// prompt, caching thumbnails in the background, and applying file changes while it is open. It touches no
// control; the window reacts to the events below. Everything here runs on the UI thread except the work it
// hands to the thread pool.
internal sealed class FolderSession
{
    private const int InitialMetadataScannerMaxPolls = 15;
    private static readonly TimeSpan InitialMetadataScannerPollInterval = TimeSpan.FromMilliseconds(100);
    // How long a metadata scan has to run before its progress is worth showing. Warm folders finish first.
    private const long ScanProgressVisibleDelayMs = 250;
    private static readonly TimeSpan MetadataCountUpdateInterval = TimeSpan.FromMilliseconds(150);

    private readonly UserPreferencesStore _preferences;
    private readonly ThumbnailService _thumbnailService;
    private readonly ThumbnailLoadCoordinator _thumbnailLoads;
    private readonly DecodedImageCache _decodedImageCache;
    private readonly MetadataRepository _metadataRepository;
    private readonly MetadataScanCoordinator _metadataScanner;
    private readonly ThumbnailPrewarmer _prewarmer;
    private readonly SessionGate _folderLoader = new();
    private readonly SessionGate _searchFilterGate = new();
    private readonly GenerationGate _sortGate = new();
    private readonly GalleryCatalog _catalog = new();
    private FolderChangeMonitor? _folderChangeMonitor;
    private DispatcherTimer? _metadataCountUpdateTimer;
    private string? _currentFolderPath;
    private ThumbnailFolderScope? _folderCacheScope;
    private long _metadataScanStartedAt;
    private string _searchQuery = "";
    private SearchScope _searchScope = SearchScope.All;
    // Read by the scanner from its own threads, far more often than the query is applied.
    private volatile bool _hasSearchQuery;
    private double _tileSize;
    private double _renderScaling = 1.0;

    public FolderSession(
        UserPreferencesStore preferences,
        ThumbnailService thumbnailService,
        ThumbnailLoadCoordinator thumbnailLoads,
        DecodedImageCache decodedImageCache,
        MetadataRepository metadataRepository,
        MetadataScanCoordinator metadataScanner)
    {
        _preferences = preferences;
        _thumbnailService = thumbnailService;
        _thumbnailLoads = thumbnailLoads;
        _decodedImageCache = decodedImageCache;
        _metadataRepository = metadataRepository;
        _metadataScanner = metadataScanner;
        _prewarmer = new ThumbnailPrewarmer(thumbnailService, _folderLoader, preferences.LoadPrewarmThumbnails());
        _prewarmer.ProgressChanged += () => StatusChanged?.Invoke();
        IncludeSubfolders = preferences.LoadIncludeSubfolders();
    }

    // The items are about to be cleared: the window drops its selection and its own copy of the list. Raised
    // before the catalog is emptied, both when a folder is opened and when the session is closed.
    public event Action? Clearing;
    public event Action<string>? FolderOpening;
    public event Action<int>? FolderScanned;
    // The folder has images: the window shows the gallery in place of the start screen.
    public event Action? FolderShown;
    // The catalog is filled and filtered. selectPath is SelectedPath as it was before the reload cleared it.
    public event Action<string?>? CatalogLoaded;
    public event Action? Closed;
    public event Action<string>? LoadFailed;
    // A filter finished; the window applies it to the grid.
    public event Action? Filtering;
    public event Action<IReadOnlyList<ImageItem>, bool>? FilterApplied;
    // Counts moved (scan or prewarm progress); the window recomposes its count text.
    public event Action? StatusChanged;
    public event Action<string>? Message;
    public event Action? IncludeSubfoldersChanged;
    public event Action? ViewportScheduleRequested;
    public event Action<ImageItem, IReadOnlySet<string>>? ItemRemoved;
    public event Action<ImageItem, ImageItem>? ItemReplaced;

    public GalleryCatalog Catalog => _catalog;
    public string? CurrentFolderPath => _currentFolderPath;
    public ThumbnailFolderScope? CacheScope => _folderCacheScope;
    public bool IncludeSubfolders { get; private set; }
    public SortMode SortMode { get; set; } = SortMode.NewestFirst;
    public CancellationToken? CurrentToken => _folderLoader.CurrentToken;

    // The window keeps this on the active image, so a reload can hand it back through CatalogLoaded.
    public string? SelectedPath { get; set; }

    // New items are created at this size, and the prewarm pass caches at it.
    public double TileSize
    {
        get => _tileSize;
        set => _prewarmer.TileSize = _tileSize = value;
    }

    public double RenderScaling
    {
        get => _renderScaling;
        set => _prewarmer.RenderScaling = _renderScaling = value;
    }

    // The grid reports where the viewport is so the prewarm pass can cache outward from it. Unfiltered, the
    // grid is the catalog; filtered, it is a subsequence and needs a lookup.
    public void FollowViewport(IReadOnlyList<ImageItem> shown, int firstVisibleIndex, int lastVisibleIndex, int direction)
    {
        var middle = Math.Clamp((firstVisibleIndex + lastVisibleIndex) / 2, 0, shown.Count - 1);
        var catalogIndex = shown.Count == _catalog.Count
            ? middle
            : _catalog.IndexOf(shown[middle], CompareItems);
        _prewarmer.FollowViewport(catalogIndex, direction);
    }

    // An edit to the query: a filter still running for the old text is dropped, and the next ApplyFilter uses
    // this one. Edits arrive per keystroke; the caller debounces the apply.
    public void SetSearch(string query, SearchScope scope)
    {
        _searchQuery = query;
        _searchScope = scope;
        _hasSearchQuery = !string.IsNullOrWhiteSpace(query);
        CancelSearchFilter();
    }

    // What the count text needs from the session. Reading it also stops the progress timer once a scan is over.
    public CountSnapshot GetCountSnapshot()
    {
        var total = _catalog.Count;
        var loaded = _catalog.LoadedMetadataCount;
        var isScanning = _metadataScanner.HasActiveSession && loaded < total;
        if (!isScanning)
        {
            _metadataCountUpdateTimer?.Stop();
        }

        // A folder whose metadata is already cached resolves its whole catalog well inside this delay, so
        // the progress suffix never appears for one. Painting it immediately meant a warm folder flashed
        // "Scanning prompts" for work that finished before it could be read.
        var showScanProgress = isScanning &&
                               Environment.TickCount64 - _metadataScanStartedAt >= ScanProgressVisibleDelayMs;
        return new CountSnapshot(total, loaded, showScanProgress, isScanning, _prewarmer.Remaining, _prewarmer.Total);
    }

    internal readonly record struct CountSnapshot(
        int Total,
        int LoadedMetadata,
        bool ShowScanProgress,
        bool IsScanning,
        int PrewarmRemaining,
        int PrewarmTotal);

    public async Task OpenFolderAsync(string folderPath)
    {
        StopFolderWatcher();
        // Read before the items are cleared, so a reload can select the same image again.
        var selectPath = SelectedPath;
        var includeSubfolders = IncludeSubfolders;
        var loadSession = _folderLoader.Restart();
        var token = loadSession.Token;

        StopFolderWork();
        FolderOpening?.Invoke(folderPath);
        _currentFolderPath = folderPath;

        // Retires and drains the previous folder's cache scope before opening this one, so no leftover work
        // is still holding that pack when its handles close, and none of it can reach this folder's pack.
        var cacheScope = await _thumbnailService.OpenFolderScopeAsync(folderPath, token);
        if (!loadSession.IsCurrent)
        {
            return;
        }

        _folderCacheScope = cacheScope;

        try
        {
            var sortMode = SortMode;
            var imageFiles = await FolderScanner.ReadFolderAsync(
                folderPath,
                includeSubfolders,
                (left, right) => GallerySort.Compare(left, right, sortMode),
                token);

            if (!loadSession.IsCurrent)
            {
                return;
            }

            while (sortMode != SortMode)
            {
                sortMode = SortMode;
                await Task.Run(
                    () => imageFiles.Sort(
                        (left, right) => GallerySort.Compare(left, right, sortMode)),
                    token);
                if (!loadSession.IsCurrent)
                {
                    return;
                }
            }

            _preferences.SaveLastFolderPath(folderPath);
            _preferences.AddRecentFolder(folderPath, imageFiles.Count);
            FolderScanned?.Invoke(imageFiles.Count);

            if (imageFiles.Count == 0)
            {
                var hasNestedImages = !includeSubfolders &&
                                      await FolderScanner.HasImagesAsync(folderPath, includeSubfolders: true);
                if (!loadSession.IsCurrent)
                {
                    return;
                }

                Close();
                LoadFailed?.Invoke(hasNestedImages
                    ? $"No top-level {MediaFiles.SupportedTypesText} files found. Enable Include subfolders to scan nested folders."
                    : $"No {MediaFiles.SupportedTypesText} files found in that folder.");
                return;
            }

            FolderShown?.Invoke();

            foreach (var imageFile in imageFiles)
            {
                _catalog.Add(CreateImageItem(imageFile));
            }

            ApplyFilter(resetScroll: true);
            CatalogLoaded?.Invoke(selectPath);

            StartFolderWatcher(folderPath, includeSubfolders);
            QueueInitialMetadataScanner(loadSession.Generation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"Failed to load folder '{folderPath}': {ex}");
            if (!loadSession.IsCurrent)
            {
                return;
            }

            Close();
            LoadFailed?.Invoke(ex.Message);
        }
    }

    // Back to no folder at all: everything running for it stops and its cache scope is retired.
    public void Close()
    {
        _folderLoader.Cancel();
        StopFolderWork();
        _currentFolderPath = null;
        // Detached synchronously; only the drain and the pack close finish in the background, so a folder
        // reopened immediately cannot adopt this scope.
        _folderCacheScope = null;
        DebugLog.Observe(_thumbnailService.RetireFolderScopeAsync(), "Thumbnail folder scope retirement");
        Closed?.Invoke();
    }

    // Stops everything for good when the window closes.
    public void Shutdown()
    {
        _folderLoader.Cancel();
        StopFolderWork();
        _thumbnailService.SetCacheWritePause(null);
    }

    // Everything running for the current folder stops and its items go. The folder gate is the caller's:
    // a reopen restarts it, a close cancels it.
    private void StopFolderWork()
    {
        StopFolderWatcher();
        _metadataScanner.Cancel();
        _thumbnailLoads.Clear();
        _thumbnailService.ClearDeferredWrites();
        _decodedImageCache.ClearAndReleaseAll();
        ClearItems();
    }

    // Returns false when it was refused: turning subfolders off for a folder with no top-level images would
    // empty it, so it stays on and says so.
    public async Task<bool> SetIncludeSubfoldersAsync(bool includeSubfolders)
    {
        if (includeSubfolders == IncludeSubfolders)
        {
            IncludeSubfoldersChanged?.Invoke();
            return true;
        }

        var currentFolderPath = _currentFolderPath;
        if (!includeSubfolders &&
            !string.IsNullOrEmpty(currentFolderPath) &&
            !await FolderScanner.HasImagesAsync(currentFolderPath, includeSubfolders))
        {
            IncludeSubfoldersChanged?.Invoke();
            Message?.Invoke("No top-level images; kept subfolders on");
            return false;
        }

        IncludeSubfolders = includeSubfolders;
        IncludeSubfoldersChanged?.Invoke();
        _preferences.SaveIncludeSubfolders(includeSubfolders);

        if (!string.IsNullOrEmpty(currentFolderPath))
        {
            await OpenFolderAsync(currentFolderPath);
        }

        return true;
    }

    public void SetPrewarmThumbnails(bool enabled)
    {
        _prewarmer.SetEnabled(enabled);
        if (enabled && _folderLoader.CurrentToken is { IsCancellationRequested: false } token)
        {
            _prewarmer.QueueFolder(_catalog.Items, _folderLoader.Generation, token);
        }
    }

    // Re-sorts the catalog and shows it from the top. The sort runs off the UI thread: a name sort compares
    // culture-aware strings, which froze the window on a large folder. Until it lands the catalog stays in
    // the order SortMode names, so file changes in the meantime still find their sorted positions.
    public void ApplySort(SortMode sortMode)
    {
        var sort = _sortGate.Begin();
        if (_catalog.Count == 0 || _folderLoader.CurrentToken is not { IsCancellationRequested: false } token)
        {
            // Nothing to reorder yet; a load in flight picks the new mode up before it fills the catalog.
            SortMode = sortMode;
            ApplyFilter(resetScroll: true);
            return;
        }

        var loadGeneration = _folderLoader.Generation;
        var sorted = _catalog.Items.ToArray();
        DebugLog.Observe(Task.Run(() =>
        {
            Array.Sort(sorted, (left, right) => GallerySort.Compare(left, right, sortMode));
            Dispatcher.UIThread.Post(() =>
            {
                if (sort.IsStale || !_folderLoader.IsCurrent(loadGeneration))
                {
                    return;
                }

                SortMode = sortMode;
                // A file change landed while sorting, so the snapshot is out of date; sort the real catalog.
                if (!_catalog.TryReorder(sorted))
                {
                    _catalog.Sort(CompareItems);
                }

                ApplyFilter(resetScroll: true);
            });
        }, token), "Gallery sort");
    }

    private int CompareItems(ImageItem left, ImageItem right)
    {
        return GallerySort.Compare(left, right, SortMode);
    }

    private ImageItem CreateImageItem(ImageFileEntry file)
    {
        var item = new ImageItem(file, TileSize, _folderCacheScope);
        item.MetadataLoaded += OnMetadataLoaded;
        return item;
    }

    public void CancelSearchFilter()
    {
        _searchFilterGate.Cancel();
    }

    public void ApplyFilter(bool resetScroll)
    {
        Filtering?.Invoke();

        var query = _searchQuery.Trim();
        var searchScope = _searchScope;

        if (string.IsNullOrWhiteSpace(query))
        {
            CancelSearchFilter();
            FilterApplied?.Invoke(_catalog.Items, resetScroll);
            return;
        }

        SearchEngine.ParseQuery(query, out var positiveTerms, out var negativeTerms);
        var compiledQuery = new CompiledQuery(positiveTerms, negativeTerms);
        if (compiledQuery.IsEmpty)
        {
            CancelSearchFilter();
            FilterApplied?.Invoke(_catalog.Items, resetScroll);
            return;
        }

        // One columnar snapshot per query, so the scan never touches an ImageItem and partitions by range.
        var snapshot = _catalog.CreateSearchSnapshot();

        var session = _searchFilterGate.Restart();
        DebugLog.Observe(Task.Run(() =>
        {
            var matches = snapshot.Filter(compiledQuery, searchScope, session.Token);

            Dispatcher.UIThread.Post(() =>
            {
                // Only a newer query invalidates this. Metadata landing mid-pass does not: it can only drop
                // items, so an older result is a superset and the scanner's later passes reconcile.
                if (session.IsStale)
                {
                    return;
                }

                FilterApplied?.Invoke(matches, resetScroll);
            }, DispatcherPriority.Background);
        }, session.Token), "Gallery search filter");
    }

    private void ClearItems()
    {
        CancelSearchFilter();
        _prewarmer.Reset();
        _metadataCountUpdateTimer?.Stop();
        Clearing?.Invoke();

        foreach (var item in _catalog.Items)
        {
            item.MetadataLoaded -= OnMetadataLoaded;
        }

        _catalog.Clear();
    }

    private void StartFolderWatcher(string folderPath, bool includeSubfolders)
    {
        StopFolderWatcher();
        var loadGeneration = _folderLoader.Generation;
        FolderChangeMonitor? monitor = null;
        try
        {
            monitor = new FolderChangeMonitor(
                folderPath,
                includeSubfolders,
                batch => Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(monitor, _folderChangeMonitor) &&
                        _folderLoader.IsCurrent(loadGeneration))
                    {
                        ApplyChanges(
                            batch.AddedFiles,
                            batch.ChangedFiles,
                            batch.DeletedPaths);
                    }
                }),
                exception => Dispatcher.UIThread.Post(() =>
                {
                    DebugLog.Write($"Folder watcher failed: {exception}");
                    if (ReferenceEquals(monitor, _folderChangeMonitor) &&
                        _folderLoader.IsCurrent(loadGeneration) &&
                        string.Equals(folderPath, _currentFolderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Message?.Invoke("Folder changed rapidly; refreshing...");
                        DebugLog.Observe(OpenFolderAsync(folderPath), "Folder watcher recovery reload");
                    }
                }, DispatcherPriority.Background));
            _folderChangeMonitor = monitor;
        }
        catch (Exception ex)
        {
            monitor?.Dispose();
            DebugLog.Write($"Failed to start folder watcher for {folderPath}: {ex.Message}");
        }
    }

    private void StopFolderWatcher()
    {
        var monitor = _folderChangeMonitor;
        _folderChangeMonitor = null;
        monitor?.Dispose();
    }

    // Applies files that appeared, changed or went away while the folder was open.
    public void ApplyChanges(
        IReadOnlyList<ImageFileEntry> addedFiles,
        IReadOnlyList<ImageFileEntry> changedFiles,
        IReadOnlyList<string> deletedPaths)
    {
        bool changed = false;
        bool needsSort = false;
        var itemsToScan = new List<ImageItem>();

        if (deletedPaths.Count > 0)
        {
            DebugLog.Observe(
                Task.Run(() => _metadataRepository.DeletePaths(deletedPaths)),
                "MetadataRepository.DeletePaths");
            var deletedSet = new HashSet<string>(deletedPaths, StringComparer.OrdinalIgnoreCase);
            var removedEntries = _catalog.RemovePaths(deletedSet);
            if (removedEntries.Count > 0)
            {
                changed = true;
            }

            foreach (var item in removedEntries)
            {
                item.MetadataLoaded -= OnMetadataLoaded;
                ItemRemoved?.Invoke(item, deletedSet);
            }
        }

        if (addedFiles.Count > 0 || changedFiles.Count > 0)
        {
            var newFiles = new List<ImageFileEntry>(addedFiles.Count + changedFiles.Count);
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Small batches keep the catalog ordered as they go, so adds and replacements both avoid a
            // full re-sort. Larger batches swap items in one pass and pay for a single bulk sort.
            var useSortedPlacement = addedFiles.Count + changedFiles.Count <= GalleryListSync.MaxIncrementalChanges;
            var bulkReplacements = useSortedPlacement
                ? null
                : new Dictionary<string, ImageItem>(StringComparer.OrdinalIgnoreCase);

            void ProcessChangedFile(ImageFileEntry imageFile)
            {
                if (!processedPaths.Add(imageFile.Path))
                {
                    return;
                }

                if (!_catalog.TryGet(imageFile.Path, out var previousItem))
                {
                    newFiles.Add(imageFile);
                    return;
                }

                var replacementItem = CreateImageItem(imageFile);
                if (useSortedPlacement)
                {
                    if (!_catalog.TryReplaceSorted(replacementItem, CompareItems, out _))
                    {
                        // The item vanished between lookup and replacement; drop the orphan rather than
                        // leaving it subscribed to metadata events while outside the catalog.
                        replacementItem.MetadataLoaded -= OnMetadataLoaded;
                        return;
                    }
                }
                else
                {
                    bulkReplacements![imageFile.Path] = replacementItem;
                    needsSort = true;
                }

                previousItem.MetadataLoaded -= OnMetadataLoaded;
                _decodedImageCache.Release(previousItem);
                ItemReplaced?.Invoke(previousItem, replacementItem);

                itemsToScan.Add(replacementItem);
                changed = true;
            }

            foreach (var imageFile in changedFiles)
            {
                ProcessChangedFile(imageFile);
            }

            foreach (var imageFile in addedFiles)
            {
                ProcessChangedFile(imageFile);
            }

            foreach (var addedFile in newFiles)
            {
                var item = CreateImageItem(addedFile);
                if (useSortedPlacement)
                {
                    _catalog.Insert(_catalog.FindSortedInsertIndex(item, CompareItems), item);
                }
                else
                {
                    _catalog.Add(item);
                    needsSort = true;
                }

                itemsToScan.Add(item);
                changed = true;
            }

            if (bulkReplacements is { Count: > 0 })
            {
                _catalog.ReplaceMany(bulkReplacements);
            }

            if (needsSort)
            {
                _catalog.Sort(CompareItems);
            }
        }

        if (changed)
        {
            if (itemsToScan.Count > 0)
            {
                _metadataScanner.ScanAdded(
                    itemsToScan,
                    () => _hasSearchQuery,
                    () => ApplyFilter(resetScroll: false));

                // Files that arrive after the initial scan miss its completion hook, so warm them here.
                if (_folderLoader.CurrentToken is { IsCancellationRequested: false } prewarmToken)
                {
                    _prewarmer.QueueAdded(itemsToScan, _folderLoader.Generation, prewarmToken);
                }
            }

            ApplyFilter(resetScroll: false);
        }
    }

    private void QueueInitialMetadataScanner(int loadGeneration)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_folderLoader.CurrentToken is not { IsCancellationRequested: false } token ||
                !_folderLoader.IsCurrent(loadGeneration) ||
                _catalog.Count == 0)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested && _folderLoader.IsCurrent(loadGeneration))
                {
                    DebugLog.Observe(
                        StartInitialMetadataScannerWhenReadyAsync(loadGeneration, token),
                        "Initial metadata scanner startup");
                }
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Render);
    }

    private async Task StartInitialMetadataScannerWhenReadyAsync(int loadGeneration, CancellationToken token)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && _folderLoader.IsCurrent(loadGeneration))
                {
                    ViewportScheduleRequested?.Invoke();
                }
            }, DispatcherPriority.Background);

            for (var poll = 0; poll < InitialMetadataScannerMaxPolls; poll++)
            {
                if (token.IsCancellationRequested || !_folderLoader.IsCurrent(loadGeneration))
                {
                    return;
                }

                if (!_thumbnailLoads.HasVisibleWork)
                {
                    break;
                }

                await Task.Delay(InitialMetadataScannerPollInterval, token);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !_folderLoader.IsCurrent(loadGeneration) || _catalog.Count == 0)
                {
                    return;
                }

                _metadataScanStartedAt = Environment.TickCount64;
                // Alongside the scan rather than after it: a new folder used to stay cold for the whole first
                // browse. Cache writes still pause while visible thumbnails load, so the viewport keeps priority.
                _prewarmer.QueueFolder(_catalog.Items, loadGeneration, token);
                _metadataScanner.Start(
                    _catalog.Items,
                    () => _hasSearchQuery,
                    () => ApplyFilter(resetScroll: false),
                    () =>
                    {
                        var paths = _catalog.Items.Select(item => item.Path).ToArray();
                        var includeSubfolders = IncludeSubfolders;
                        DebugLog.Observe(Task.Run(() => _metadataRepository.PruneMissing(paths, includeSubfolders, token), token),
                            "MetadataRepository.PruneMissing");
                    });
                StatusChanged?.Invoke();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // One call updates both the scan-progress counter and the search row, so neither can be forgotten.
    private void OnMetadataLoaded(ImageItem item)
    {
        _catalog.MarkMetadataLoaded(item);
        QueueMetadataCountUpdate();
    }

    private void QueueMetadataCountUpdate()
    {
        if (_metadataCountUpdateTimer is null)
        {
            _metadataCountUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = MetadataCountUpdateInterval
            };
            _metadataCountUpdateTimer.Tick += (_, _) =>
            {
                _metadataCountUpdateTimer.Stop();
                StatusChanged?.Invoke();
            };
        }

        if (!_metadataCountUpdateTimer.IsEnabled)
        {
            _metadataCountUpdateTimer.Start();
        }
    }
}
