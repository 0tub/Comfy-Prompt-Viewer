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
    // Shallow on purpose: the queue is ordered by distance from the viewport when items enter it, so a
    // deep queue would keep caching where the user was rather than where they are. One writer drains it.
    private const int PrewarmQueueHighWaterMark = 32;
    private const int PrewarmProgressBatch = 32;
    private static readonly TimeSpan PrewarmQueuePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly UserPreferencesStore _preferences;
    private readonly ThumbnailService _thumbnailService;
    private readonly ThumbnailLoadCoordinator _thumbnailLoads;
    private readonly DecodedImageCache _decodedImageCache;
    private readonly MetadataRepository _metadataRepository;
    private readonly ImageMetadataService _metadataService;
    private readonly MetadataScanCoordinator _metadataScanner;
    private readonly Func<(string Query, SearchScope Scope)> _getSearch;
    private readonly Func<string?> _getSelectedPath;
    private readonly SessionGate _folderLoader = new();
    private readonly SessionGate _searchFilterGate = new();
    private readonly GalleryCatalog _catalog = new();
    private FolderChangeMonitor? _folderChangeMonitor;
    private DispatcherTimer? _metadataCountUpdateTimer;
    private string? _currentFolderPath;
    private ThumbnailFolderScope? _folderCacheScope;
    private int _prewarmRemaining;
    private int _prewarmTotal;
    private long _metadataScanStartedAt;
    private volatile bool _hasSearchQueryActive;
    private volatile int _prewarmFocusIndex;
    private volatile int _prewarmDirection = 1;
    private volatile bool _prewarmThumbnails;

    public FolderSession(
        UserPreferencesStore preferences,
        ThumbnailService thumbnailService,
        ThumbnailLoadCoordinator thumbnailLoads,
        DecodedImageCache decodedImageCache,
        MetadataRepository metadataRepository,
        ImageMetadataService metadataService,
        MetadataScanCoordinator metadataScanner,
        Func<(string Query, SearchScope Scope)> getSearch,
        Func<string?> getSelectedPath)
    {
        _preferences = preferences;
        _thumbnailService = thumbnailService;
        _thumbnailLoads = thumbnailLoads;
        _decodedImageCache = decodedImageCache;
        _metadataRepository = metadataRepository;
        _metadataService = metadataService;
        _metadataScanner = metadataScanner;
        _getSearch = getSearch;
        _getSelectedPath = getSelectedPath;
        _prewarmThumbnails = preferences.LoadPrewarmThumbnails();
        IncludeSubfolders = preferences.LoadIncludeSubfolders();
    }

    // The items are about to be cleared: the window drops its selection and its own copy of the list. Raised
    // before the catalog is emptied, both when a folder is opened and when the session is closed.
    public event Action? Clearing;
    public event Action<string>? FolderOpening;
    public event Action<int>? FolderScanned;
    // The folder has images: the window shows the gallery in place of the start screen.
    public event Action? FolderShown;
    // The catalog is filled and filtered. selectPath is the image that was selected before a reload.
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
    public double TileSize { get; set; }
    public double RenderScaling { get; set; } = 1.0;
    public CancellationToken? CurrentToken => _folderLoader.CurrentToken;
    public int Generation => _folderLoader.Generation;
    public bool IsCurrent(int generation) => _folderLoader.IsCurrent(generation);

    // Where the viewport is, in catalog order, for the background prewarm pass to cache around.
    public int PrewarmFocusIndex { get => _prewarmFocusIndex; set => _prewarmFocusIndex = value; }
    public int PrewarmDirection { get => _prewarmDirection; set => _prewarmDirection = value; }

    // The grid reports where the viewport is so the prewarm pass can cache outward from it. Unfiltered, the
    // grid is the catalog; filtered, it is a subsequence and needs a lookup.
    public void FollowViewport(IReadOnlyList<ImageItem> shown, int firstVisibleIndex, int lastVisibleIndex, int direction)
    {
        var middle = Math.Clamp((firstVisibleIndex + lastVisibleIndex) / 2, 0, shown.Count - 1);
        var catalogIndex = shown.Count == _catalog.Count
            ? middle
            : _catalog.IndexOf(shown[middle], CompareEntries);
        if (catalogIndex >= 0)
        {
            _prewarmFocusIndex = catalogIndex;
        }

        _prewarmDirection = direction;
    }

    // The prewarm setting itself belongs to the start screen; the session only needs to know it.
    public bool PrewarmThumbnails => _prewarmThumbnails;

    // The query is edited far more often than it is applied, so the scanner asks this from its own thread.
    public bool HasSearchQueryActive
    {
        get => _hasSearchQueryActive;
        set => _hasSearchQueryActive = value;
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
        return new CountSnapshot(total, loaded, showScanProgress, isScanning, _prewarmRemaining, _prewarmTotal);
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
        var selectPath = _getSelectedPath();
        var includeSubfolders = IncludeSubfolders;
        var loadSession = _folderLoader.Restart();
        var token = loadSession.Token;

        _metadataScanner.Cancel();
        _thumbnailLoads.Clear();
        _thumbnailService.ClearDeferredWrites();

        _decodedImageCache.ClearAndReleaseAll();
        ClearItems();
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
                    ? "No top-level PNG, JPG, or WebP images found. Enable Include subfolders to scan nested folders."
                    : "No PNG, JPG, or WebP images found in that folder.");
                return;
            }

            FolderShown?.Invoke();

            foreach (var imageFile in imageFiles)
            {
                _catalog.Add(new GalleryEntry(
                    imageFile.Path,
                    imageFile.Fingerprint,
                    CreateImageItem(imageFile.Path, imageFile.Fingerprint)));
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
        StopFolderWatcher();
        _metadataScanner.Cancel();
        _folderLoader.Cancel();
        _thumbnailLoads.Clear();
        _thumbnailService.ClearDeferredWrites();
        _decodedImageCache.ClearAndReleaseAll();
        ClearItems();
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
        StopFolderWatcher();
        CancelSearchFilter();
        _metadataCountUpdateTimer?.Stop();
        _metadataScanner.Cancel();
        _folderLoader.Cancel();
        _thumbnailLoads.Clear();
        _thumbnailService.ClearDeferredWrites();
        _thumbnailService.SetCacheWritePause(null);
        ClearItems();
        _decodedImageCache.ClearAndReleaseAll();
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
        _prewarmThumbnails = enabled;
        if (!enabled)
        {
            _thumbnailService.ClearDeferredWrites();
            _prewarmRemaining = 0;
            _prewarmTotal = 0;
            StatusChanged?.Invoke();
            return;
        }

        if (_catalog.Count > 0 &&
            _folderLoader.CurrentToken is { IsCancellationRequested: false } token)
        {
            QueueThumbnailCachePrewarm(_folderLoader.Generation, token);
        }
    }

    public void ApplySort()
    {
        _catalog.Sort(CompareEntries);
    }

    public int CompareEntries(GalleryEntry left, GalleryEntry right)
    {
        return GallerySort.Compare(left, right, SortMode);
    }

    public ImageItem CreateImageItem(string path, SourceFingerprint fingerprint)
    {
        var item = new ImageItem(
            path,
            fingerprint,
            TileSize,
            _metadataService,
            _decodedImageCache,
            _thumbnailService,
            _folderCacheScope);
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

        var (rawQuery, searchScope) = _getSearch();
        var query = rawQuery.Trim();

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
        _prewarmRemaining = 0;
        _prewarmTotal = 0;
        _metadataCountUpdateTimer?.Stop();
        Clearing?.Invoke();

        foreach (var item in _catalog.Items)
        {
            item.MetadataLoaded -= OnMetadataLoaded;
        }

        _catalog.Clear();
        _prewarmFocusIndex = 0;
        _prewarmDirection = 1;
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

            foreach (var entry in removedEntries)
            {
                var item = entry.Item;
                item.MetadataLoaded -= OnMetadataLoaded;
                ItemRemoved?.Invoke(item, deletedSet);
            }
        }

        if (addedFiles.Count > 0 || changedFiles.Count > 0)
        {
            var newFiles = new List<ImageFileEntry>(addedFiles.Count + changedFiles.Count);
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Small batches keep the catalog ordered as they go, so adds and replacements both avoid a
            // full re-sort. Larger batches swap entries in one pass and pay for a single bulk sort.
            var useSortedPlacement = addedFiles.Count + changedFiles.Count <= GalleryListSync.MaxIncrementalChanges;
            var bulkReplacements = useSortedPlacement
                ? null
                : new Dictionary<string, GalleryEntry>(StringComparer.OrdinalIgnoreCase);

            void ProcessChangedFile(ImageFileEntry imageFile)
            {
                if (!processedPaths.Add(imageFile.Path))
                {
                    return;
                }

                if (!_catalog.TryGet(imageFile.Path, out var existingEntry))
                {
                    newFiles.Add(imageFile);
                    return;
                }

                var previousItem = existingEntry.Item;
                var replacementItem = CreateImageItem(imageFile.Path, imageFile.Fingerprint);
                var replacementEntry = new GalleryEntry(imageFile.Path, imageFile.Fingerprint, replacementItem);
                if (useSortedPlacement)
                {
                    if (!_catalog.TryReplaceSorted(replacementEntry, CompareEntries, out _))
                    {
                        // The entry vanished between lookup and replacement; drop the orphan rather than
                        // leaving it subscribed to metadata events while outside the catalog.
                        replacementItem.MetadataLoaded -= OnMetadataLoaded;
                        return;
                    }
                }
                else
                {
                    bulkReplacements![imageFile.Path] = replacementEntry;
                    needsSort = true;
                }

                previousItem.MetadataLoaded -= OnMetadataLoaded;
                previousItem.ReleasePreview();
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
                var path = addedFile.Path;
                var item = CreateImageItem(path, addedFile.Fingerprint);
                var entry = new GalleryEntry(path, addedFile.Fingerprint, item);
                if (useSortedPlacement)
                {
                    var insertIndex = _catalog.FindSortedInsertIndex(entry, CompareEntries);
                    _catalog.Insert(insertIndex, entry);
                }
                else
                {
                    _catalog.Add(entry);
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
                ApplySort();
            }
        }

        if (changed)
        {
            if (itemsToScan.Count > 0)
            {
                _metadataScanner.ScanAdded(
                    itemsToScan,
                    () => HasSearchQueryActive,
                    () => ApplyFilter(resetScroll: false));

                // Files that arrive after the initial scan miss its completion hook, so warm them here.
                if (_folderLoader.CurrentToken is { IsCancellationRequested: false } prewarmToken)
                {
                    QueueThumbnailCachePrewarm(itemsToScan, _folderLoader.Generation, prewarmToken);
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
                QueueThumbnailCachePrewarm(loadGeneration, token);
                _metadataScanner.Start(
                    _catalog.Items,
                    () => HasSearchQueryActive,
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

    // A whole-folder pass, cached outward from the viewport. Writes pause for visible work and a folder swap
    // drops the queue.
    private void QueueThumbnailCachePrewarm(int loadGeneration, CancellationToken token)
    {
        if (_catalog.Count == 0 || !_folderLoader.IsCurrent(loadGeneration))
        {
            return;
        }

        if (!_prewarmThumbnails)
        {
            return;
        }

        var items = new ImageItem[_catalog.Count];
        for (var index = 0; index < items.Length; index++)
        {
            items[index] = _catalog.Items[index];
        }

        QueueThumbnailCachePrewarm(items, loadGeneration, token, followViewport: true);
    }

    // followViewport requires items to be the catalog in catalog order, so an item's index is its position.
    private void QueueThumbnailCachePrewarm(
        IReadOnlyList<ImageItem> items,
        int loadGeneration,
        CancellationToken token,
        bool followViewport = false)
    {
        if (items.Count == 0 || !_prewarmThumbnails)
        {
            return;
        }

        DebugLog.Observe(
            PrewarmThumbnailCacheAsync(items, loadGeneration, token, followViewport),
            "Thumbnail cache prewarm");
    }

    // Two passes. The first is one in-memory pack lookup per image and decides whether there is any work at
    // all, so reopening a folder that is already cached queues nothing, wakes no writer, and shows no
    // progress. Only the second pass counts toward the label, which is why it now tracks thumbnails that are
    // genuinely missing instead of images walked.
    private async Task PrewarmThumbnailCacheAsync(
        IReadOnlyList<ImageItem> items,
        int loadGeneration,
        CancellationToken token,
        bool followViewport)
    {
        var announced = 0;
        var completed = 0;
        try
        {
            await Task.Run(async () =>
            {
                var (pending, positions) = CollectPrewarmCandidates(items, loadGeneration);
                if (pending.Count == 0)
                {
                    return;
                }

                announced = pending.Count;
                AddPrewarmWork(announced, loadGeneration);
                var order = followViewport ? new PrewarmOrder(positions) : null;

                for (var queued = 0; queued < pending.Count; queued++)
                {
                    // Enqueue against a high-water mark so a large folder does not sit in the queue whole.
                    while (_thumbnailService.PendingWriteCount >= PrewarmQueueHighWaterMark)
                    {
                        await Task.Delay(PrewarmQueuePollInterval, token);
                        if (!_folderLoader.IsCurrent(loadGeneration) || !_prewarmThumbnails)
                        {
                            return;
                        }
                    }

                    if (!_folderLoader.IsCurrent(loadGeneration) || !_prewarmThumbnails)
                    {
                        return;
                    }

                    // Picked at enqueue time, not up front, so the pass follows the user as they scroll.
                    var item = order is null
                        ? pending[queued]
                        : pending[order.Take(_prewarmFocusIndex, _prewarmDirection)];
                    _thumbnailService.TryQueueCacheWrite(item, item.GetThumbnailKey());

                    if (Interlocked.Increment(ref completed) % PrewarmProgressBatch == 0)
                    {
                        ReportPrewarmProgress(PrewarmProgressBatch, loadGeneration);
                    }
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Whatever the last partial batch did not report, so an early return still clears the label.
            ReportPrewarmProgress(
                announced - (completed / PrewarmProgressBatch * PrewarmProgressBatch),
                loadGeneration);
        }
    }

    // An item's decode bucket is only refreshed for visible and ahead tiles, so an off-screen item can still
    // be carrying the tile size the folder was opened with. Aligning it here keeps the pass from caching a
    // width the gallery will never ask for and then leaving those images cold at the size actually shown.
    // Positions are each candidate's index in items, ascending, for PrewarmOrder.
    private (List<ImageItem> Pending, int[] Positions) CollectPrewarmCandidates(
        IReadOnlyList<ImageItem> items,
        int loadGeneration)
    {
        var pending = new List<ImageItem>();
        var positions = new List<int>();
        var tileSize = TileSize;
        var renderScaling = RenderScaling;
        for (var index = 0; index < items.Count; index++)
        {
            if (!_folderLoader.IsCurrent(loadGeneration) || !_prewarmThumbnails)
            {
                pending.Clear();
                positions.Clear();
                break;
            }

            var item = items[index];
            item.SetTileSize(tileSize, renderScaling);
            if (item.CacheScope is { IsRetired: false } scope && scope.Pack.CanWrite && !item.HasCachedThumbnail())
            {
                pending.Add(item);
                positions.Add(index);
            }
        }

        return (pending, positions.ToArray());
    }

    // Both counter posts carry the load generation: a pass that was still current when it queued work can
    // land after a folder swap, and a stale add would report caching for a folder that is gone while a
    // stale decrement would eat the new folder's progress.
    private void AddPrewarmWork(int count, int loadGeneration)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_folderLoader.IsCurrent(loadGeneration))
                {
                    return;
                }

                _prewarmTotal += count;
                _prewarmRemaining += count;
                StatusChanged?.Invoke();
            },
            DispatcherPriority.Background);
    }

    private void ReportPrewarmProgress(int completedCount, int loadGeneration)
    {
        if (completedCount <= 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_folderLoader.IsCurrent(loadGeneration))
                {
                    return;
                }

                _prewarmRemaining = Math.Max(0, _prewarmRemaining - completedCount);
                if (_prewarmRemaining == 0)
                {
                    _prewarmTotal = 0;
                }

                StatusChanged?.Invoke();
            },
            DispatcherPriority.Background);
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
