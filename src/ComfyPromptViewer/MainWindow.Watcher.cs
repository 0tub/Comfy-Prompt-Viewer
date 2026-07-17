using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private static readonly TimeSpan WatcherDebounceInterval = TimeSpan.FromMilliseconds(300);
    private FileSystemWatcher? _folderWatcher;
    private readonly object _watcherLock = new();
    private readonly HashSet<string> _pendingAddedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingChangedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDeletedFiles = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _watcherDebounceTimer;
    private FileSystemWatcher? _watcherRecoverySource;
    private bool _watcherBatchProcessing;
    private bool _watcherBatchRerunRequested;

    private void StartFolderWatcher(string folderPath, bool includeSubfolders)
    {
        StopFolderWatcher();
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(folderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = includeSubfolders
            };

            watcher.Created += OnFileCreated;
            watcher.Changed += OnFileChanged;
            watcher.Deleted += OnFileDeleted;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;
            lock (_watcherLock)
            {
                _folderWatcher = watcher;
            }
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            lock (_watcherLock)
            {
                if (ReferenceEquals(watcher, _folderWatcher))
                {
                    _folderWatcher = null;
                }
            }
            watcher?.Dispose();
            DebugLog.Write($"Failed to start folder watcher for {folderPath}: {ex.Message}");
        }
    }

    private void StopFolderWatcher()
    {
        FileSystemWatcher? watcher;
        lock (_watcherLock)
        {
            watcher = _folderWatcher;
            _folderWatcher = null;
            _pendingAddedFiles.Clear();
            _pendingChangedFiles.Clear();
            _pendingDeletedFiles.Clear();
            _watcherRecoverySource = null;
            _watcherBatchRerunRequested = false;
        }

        if (watcher != null)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnFileCreated;
                watcher.Changed -= OnFileChanged;
                watcher.Deleted -= OnFileDeleted;
                watcher.Renamed -= OnFileRenamed;
                watcher.Error -= OnWatcherError;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to stop folder watcher: {ex.Message}");
            }
        }

        if (_watcherDebounceTimer != null)
        {
            _watcherDebounceTimer.Stop();
            _watcherDebounceTimer = null;
        }

    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        DebugLog.Write($"Folder watcher failed: {e.GetException()}");
        if (sender is not FileSystemWatcher watcher)
        {
            return;
        }

        int loadGeneration;
        string? folderPath;
        lock (_watcherLock)
        {
            if (!ReferenceEquals(watcher, _folderWatcher) ||
                ReferenceEquals(watcher, _watcherRecoverySource))
            {
                return;
            }

            _watcherRecoverySource = watcher;
            loadGeneration = _folderLoader.Generation;
            folderPath = _currentFolderPath;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_watcherLock)
            {
                if (ReferenceEquals(watcher, _watcherRecoverySource))
                {
                    _watcherRecoverySource = null;
                }
            }

            if (_folderLoader.IsCurrent(loadGeneration) &&
                ReferenceEquals(watcher, _folderWatcher) &&
                !string.IsNullOrEmpty(folderPath) &&
                string.Equals(folderPath, _currentFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                CountText.Text = "Folder changed rapidly; refreshing...";
                DebugLog.Observe(LoadFolderAsync(folderPath), "Folder watcher recovery reload");
            }
        }, DispatcherPriority.Background);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (!ImageFileReader.IsSupportedImage(e.FullPath)) return;

        lock (_watcherLock)
        {
            if (!ReferenceEquals(sender, _folderWatcher)) return;
            _pendingDeletedFiles.Remove(e.FullPath);
            _pendingAddedFiles.Add(e.FullPath);
        }

        Dispatcher.UIThread.Post(() => StartWatcherTimer());
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_watcherLock)
        {
            if (!ReferenceEquals(sender, _folderWatcher)) return;
            _pendingAddedFiles.Remove(e.FullPath);
            _pendingChangedFiles.Remove(e.FullPath);
            _pendingDeletedFiles.Add(e.FullPath);
        }

        Dispatcher.UIThread.Post(() => StartWatcherTimer());
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!ImageFileReader.IsSupportedImage(e.FullPath)) return;

        lock (_watcherLock)
        {
            if (!ReferenceEquals(sender, _folderWatcher)) return;
            _pendingDeletedFiles.Remove(e.FullPath);
            if (!_pendingAddedFiles.Contains(e.FullPath))
            {
                _pendingChangedFiles.Add(e.FullPath);
            }
        }

        Dispatcher.UIThread.Post(() => StartWatcherTimer());
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        lock (_watcherLock)
        {
            if (!ReferenceEquals(sender, _folderWatcher)) return;

            if (ImageFileReader.IsSupportedImage(e.OldFullPath))
            {
                _pendingAddedFiles.Remove(e.OldFullPath);
                _pendingChangedFiles.Remove(e.OldFullPath);
                _pendingDeletedFiles.Add(e.OldFullPath);
            }

            if (ImageFileReader.IsSupportedImage(e.FullPath))
            {
                _pendingDeletedFiles.Remove(e.FullPath);
                _pendingChangedFiles.Remove(e.FullPath);
                _pendingAddedFiles.Add(e.FullPath);
            }
        }

        Dispatcher.UIThread.Post(() => StartWatcherTimer());
    }

    private void StartWatcherTimer()
    {
        if (_watcherDebounceTimer == null)
        {
            _watcherDebounceTimer = new DispatcherTimer
            {
                Interval = WatcherDebounceInterval
            };
            _watcherDebounceTimer.Tick += OnWatcherTimerTick;
        }

        _watcherDebounceTimer.Stop();
        _watcherDebounceTimer.Start();
    }

    private async void OnWatcherTimerTick(object? sender, EventArgs e)
    {
        _watcherDebounceTimer?.Stop();
        if (_watcherBatchProcessing)
        {
            _watcherBatchRerunRequested = true;
            return;
        }

        _watcherBatchProcessing = true;

        try
        {
            List<string> added;
            List<string> modified;
            List<string> deleted;
            FileSystemWatcher? watcher;

            lock (_watcherLock)
            {
                watcher = _folderWatcher;
                added = new List<string>(_pendingAddedFiles);
                modified = new List<string>(_pendingChangedFiles);
                deleted = new List<string>(_pendingDeletedFiles);
                _pendingAddedFiles.Clear();
                _pendingChangedFiles.Clear();
                _pendingDeletedFiles.Clear();
            }

            if (watcher is null || (added.Count == 0 && modified.Count == 0 && deleted.Count == 0)) return;

            var loadGeneration = _folderLoader.Generation;
            var addedPaths = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
            addedPaths.UnionWith(modified);
            var imageFiles = await FolderLoadCoordinator.ReadEntriesAsync(addedPaths, CancellationToken.None);
            if (!_folderLoader.IsCurrent(loadGeneration) || !ReferenceEquals(watcher, _folderWatcher))
            {
                return;
            }

            var addedFiles = new List<ImageFileEntry>();
            var changedFiles = new List<ImageFileEntry>();
            var addedSet = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
            foreach (var imageFile in imageFiles)
            {
                if (addedSet.Contains(imageFile.Path))
                {
                    addedFiles.Add(imageFile);
                }
                else
                {
                    changedFiles.Add(imageFile);
                }
            }

            ProcessWatcherChanges(addedFiles, changedFiles, deleted);
        }
        catch (Exception ex)
        {
            DebugLog.WriteException("Folder watcher batch", ex);
        }
        finally
        {
            _watcherBatchProcessing = false;
            bool hasPendingChanges;
            lock (_watcherLock)
            {
                hasPendingChanges = _pendingAddedFiles.Count > 0 ||
                                    _pendingChangedFiles.Count > 0 ||
                                    _pendingDeletedFiles.Count > 0;
            }

            if (_watcherBatchRerunRequested || hasPendingChanges)
            {
                _watcherBatchRerunRequested = false;
                StartWatcherTimer();
            }
        }
    }

    private void ProcessWatcherChanges(
        List<ImageFileEntry> addedFiles,
        List<ImageFileEntry> changedFiles,
        List<string> deletedPaths)
    {
        bool changed = false;
        bool needsSort = false;
        var itemsToScan = new List<ImageItem>();

        if (deletedPaths.Count > 0)
        {
            DebugLog.Observe(
                Task.Run(() => MetadataIndex.DeletePaths(deletedPaths)),
                "MetadataIndex.DeletePaths");
            var deletedSet = new HashSet<string>(deletedPaths, StringComparer.OrdinalIgnoreCase);
            var pathCount = _allImagePaths.RemoveAll(path => deletedSet.Contains(path));
            foreach (var path in deletedSet)
            {
                _imageLastWriteTimes.Remove(path);
            }
            if (pathCount > 0)
            {
                changed = true;
            }

            for (var index = _allImageItems.Count - 1; index >= 0; index--)
            {
                var item = _allImageItems[index];
                if (!deletedSet.Contains(item.Path))
                {
                    continue;
                }

                _allImageItems.RemoveAt(index);
                item.MetadataLoaded -= ImageItem_MetadataLoaded;
                RemoveSelectedItem(item);
                if (_selectionAnchor == item)
                {
                    _selectionAnchor = null;
                }

                if (_selectedItem == item)
                {
                    SetActiveItem(_viewModel.Items.FirstOrDefault(candidate =>
                        _selectedItems.Contains(candidate) && !deletedSet.Contains(candidate.Path)));
                }
            }
        }

        if (addedFiles.Count > 0 || changedFiles.Count > 0)
        {
            var existingIndexes = new Dictionary<string, int>(_allImagePaths.Count, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < _allImagePaths.Count; index++)
            {
                existingIndexes[_allImagePaths[index]] = index;
            }

            var newFiles = new List<ImageFileEntry>(addedFiles.Count + changedFiles.Count);
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ProcessChangedFile(ImageFileEntry imageFile)
            {
                if (!processedPaths.Add(imageFile.Path))
                {
                    return;
                }

                if (!existingIndexes.TryGetValue(imageFile.Path, out var index))
                {
                    newFiles.Add(imageFile);
                    return;
                }

                var previousItem = _allImageItems[index];
                var replacementItem = CreateImageItem(imageFile.Path);
                _allImageItems[index] = replacementItem;
                _imageLastWriteTimes[imageFile.Path] = imageFile.LastWriteTimeUtc;
                previousItem.MetadataLoaded -= ImageItem_MetadataLoaded;
                previousItem.ReleasePreview();
                if (_selectedItems.Contains(previousItem))
                {
                    RemoveSelectedItem(previousItem);
                    AddSelectedItem(replacementItem);
                }
                if (_selectionAnchor == previousItem)
                {
                    _selectionAnchor = replacementItem;
                }
                if (_selectedItem == previousItem)
                {
                    SetActiveItem(replacementItem);
                }

                itemsToScan.Add(replacementItem);
                changed = true;
                needsSort = true;
            }

            foreach (var imageFile in changedFiles)
            {
                ProcessChangedFile(imageFile);
            }

            foreach (var imageFile in addedFiles)
            {
                ProcessChangedFile(imageFile);
            }

            var useSortedInsertion = !needsSort && newFiles.Count <= MaxIncrementalGalleryChanges;
            foreach (var addedFile in newFiles)
            {
                var path = addedFile.Path;

                _imageLastWriteTimes[path] = addedFile.LastWriteTimeUtc;
                var item = CreateImageItem(path);
                if (useSortedInsertion)
                {
                    var insertIndex = FindSortedInsertIndex(_allImagePaths, path, CompareImagePaths);
                    _allImagePaths.Insert(insertIndex, path);
                    _allImageItems.Insert(insertIndex, item);
                }
                else
                {
                    _allImagePaths.Add(path);
                    _allImageItems.Add(item);
                }

                itemsToScan.Add(item);
                changed = true;
            }

            if ((!useSortedInsertion && newFiles.Count > 0) || needsSort)
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
                    HasSearchQueryActive,
                    () => ApplyFilter(resetScroll: false));
            }

            ApplyFilter(resetScroll: false);
        }
    }

}
