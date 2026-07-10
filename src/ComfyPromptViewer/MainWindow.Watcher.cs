using System;
using System.Collections.Generic;
using System.IO;
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
    private readonly HashSet<string> _pendingDeletedFiles = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _watcherDebounceTimer;
    private bool _watcherRecoveryQueued;

    private void StartFolderWatcher(string folderPath, bool includeSubfolders)
    {
        StopFolderWatcher();
        try
        {
            _folderWatcher = new FileSystemWatcher(folderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = includeSubfolders
            };

            _folderWatcher.Created += OnFileCreated;
            _folderWatcher.Changed += OnFileCreated;
            _folderWatcher.Deleted += OnFileDeleted;
            _folderWatcher.Renamed += OnFileRenamed;
            _folderWatcher.Error += OnWatcherError;
            _folderWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to start folder watcher for {folderPath}: {ex.Message}");
        }
    }

    private void StopFolderWatcher()
    {
        if (_folderWatcher != null)
        {
            try
            {
                _folderWatcher.EnableRaisingEvents = false;
                _folderWatcher.Created -= OnFileCreated;
                _folderWatcher.Changed -= OnFileCreated;
                _folderWatcher.Deleted -= OnFileDeleted;
                _folderWatcher.Renamed -= OnFileRenamed;
                _folderWatcher.Error -= OnWatcherError;
                _folderWatcher.Dispose();
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to stop folder watcher: {ex.Message}");
            }
            _folderWatcher = null;
        }

        if (_watcherDebounceTimer != null)
        {
            _watcherDebounceTimer.Stop();
            _watcherDebounceTimer = null;
        }

        lock (_watcherLock)
        {
            _pendingAddedFiles.Clear();
            _pendingDeletedFiles.Clear();
            _watcherRecoveryQueued = false;
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        DebugLog.Write($"Folder watcher failed: {e.GetException()}");
        lock (_watcherLock)
        {
            if (_watcherRecoveryQueued)
            {
                return;
            }

            _watcherRecoveryQueued = true;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            lock (_watcherLock)
            {
                _watcherRecoveryQueued = false;
            }

            if (!string.IsNullOrEmpty(_currentFolderPath))
            {
                CountText.Text = "Folder changed rapidly; refreshing...";
                await LoadFolderAsync(_currentFolderPath);
            }
        }, DispatcherPriority.Background);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (!ImageFileReader.IsSupportedImage(e.FullPath)) return;

        lock (_watcherLock)
        {
            _pendingDeletedFiles.Remove(e.FullPath);
            _pendingAddedFiles.Add(e.FullPath);
        }

        Dispatcher.UIThread.Post(() => StartWatcherTimer());
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_watcherLock)
        {
            _pendingAddedFiles.Remove(e.FullPath);
            _pendingDeletedFiles.Add(e.FullPath);
        }

        Dispatcher.UIThread.Post(() => StartWatcherTimer());
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (ImageFileReader.IsSupportedImage(e.OldFullPath))
        {
            lock (_watcherLock)
            {
                _pendingAddedFiles.Remove(e.OldFullPath);
                _pendingDeletedFiles.Add(e.OldFullPath);
            }
        }

        if (ImageFileReader.IsSupportedImage(e.FullPath))
        {
            lock (_watcherLock)
            {
                _pendingDeletedFiles.Remove(e.FullPath);
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

        List<string> added;
        List<string> deleted;

        lock (_watcherLock)
        {
            added = new List<string>(_pendingAddedFiles);
            deleted = new List<string>(_pendingDeletedFiles);
            _pendingAddedFiles.Clear();
            _pendingDeletedFiles.Clear();
        }

        if (added.Count == 0 && deleted.Count == 0) return;

        var loadGeneration = _loadGeneration;
        var addedFiles = await Task.Run(() => ReadImageFileEntries(added, CancellationToken.None));
        if (loadGeneration != _loadGeneration)
        {
            return;
        }

        ProcessWatcherChanges(addedFiles, deleted);
    }

    private void ProcessWatcherChanges(List<ImageFileEntry> addedFiles, List<string> deletedPaths)
    {
        bool changed = false;

        if (deletedPaths.Count > 0)
        {
            _ = Task.Run(() => MetadataIndex.DeletePaths(deletedPaths));
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

                if (_selectedItem == item)
                {
                    SelectItem(null);
                }
            }
        }

        List<ImageItem>? newItems = null;
        if (addedFiles.Count > 0)
        {
            var existingPaths = new HashSet<string>(_allImagePaths, StringComparer.OrdinalIgnoreCase);
            var uniqueAddedFiles = new List<ImageFileEntry>(addedFiles.Count);
            foreach (var addedFile in addedFiles)
            {
                if (existingPaths.Add(addedFile.Path))
                {
                    uniqueAddedFiles.Add(addedFile);
                }
            }

            newItems = new List<ImageItem>(uniqueAddedFiles.Count);
            var useSortedInsertion = uniqueAddedFiles.Count <= MaxIncrementalGalleryChanges;
            foreach (var addedFile in uniqueAddedFiles)
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

                newItems.Add(item);
                changed = true;
            }

            if (!useSortedInsertion && newItems.Count > 0)
            {
                ApplySort();
            }
        }

        if (changed)
        {
            if (newItems is { Count: > 0 })
            {
                ScanNewItemsMetadata(newItems);
            }

            ApplyFilter(resetScroll: false);
        }
    }

    private void ScanNewItemsMetadata(List<ImageItem> newItems)
    {
        var token = _scannerCancellation?.Token ?? CancellationToken.None;
        var scannerGeneration = _scannerGeneration;
        _ = Task.Run(async () =>
        {
            try
            {
                var lastRefreshTime = DateTime.UtcNow;
                var refreshLock = new object();

                await Parallel.ForEachAsync(newItems, new ParallelOptions
                {
                    MaxDegreeOfParallelism = MetadataScannerMaxDegreeOfParallelism,
                    CancellationToken = token
                },
                async (item, cancellationToken) =>
                {
                    try
                    {
                        await item.EnsureMetadataLoadedAsync(cancellationToken);
                        if (HasSearchQueryActive())
                        {
                            var now = DateTime.UtcNow;
                            var shouldRefresh = false;
                            lock (refreshLock)
                            {
                                if (now - lastRefreshTime > MetadataScannerSearchRefreshInterval)
                                {
                                    lastRefreshTime = now;
                                    shouldRefresh = true;
                                }
                            }

                            if (shouldRefresh)
                            {
                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (!token.IsCancellationRequested && scannerGeneration == _scannerGeneration)
                                    {
                                        ApplyFilter(resetScroll: false);
                                    }
                                });
                            }
                        }
                    }
                    catch (OperationCanceledException) {}
                    catch (Exception ex)
                    {
                        DebugLog.Write($"Failed to scan metadata for watcher-added file {item.Path}: {ex}");
                    }
                });

                if (HasSearchQueryActive())
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested && scannerGeneration == _scannerGeneration)
                        {
                            ApplyFilter(resetScroll: false);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
