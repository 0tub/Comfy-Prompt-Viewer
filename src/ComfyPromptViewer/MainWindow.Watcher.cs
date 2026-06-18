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
    private FileSystemWatcher? _folderWatcher;
    private readonly object _watcherLock = new();
    private readonly HashSet<string> _pendingAddedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDeletedFiles = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _watcherDebounceTimer;

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
            _folderWatcher.Deleted += OnFileDeleted;
            _folderWatcher.Renamed += OnFileRenamed;
            _folderWatcher.EnableRaisingEvents = true;
        }
        catch
        {
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
                _folderWatcher.Deleted -= OnFileDeleted;
                _folderWatcher.Renamed -= OnFileRenamed;
                _folderWatcher.Dispose();
            }
            catch
            {
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
        }
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
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _watcherDebounceTimer.Tick += OnWatcherTimerTick;
        }

        _watcherDebounceTimer.Stop();
        _watcherDebounceTimer.Start();
    }

    private void OnWatcherTimerTick(object? sender, EventArgs e)
    {
        _watcherDebounceTimer?.Stop();

        List<string> added;
        List<string> deleted;

        lock (_watcherLock)
        {
            added = _pendingAddedFiles.ToList();
            deleted = _pendingDeletedFiles.ToList();
            _pendingAddedFiles.Clear();
            _pendingDeletedFiles.Clear();
        }

        if (added.Count == 0 && deleted.Count == 0) return;

        ProcessWatcherChanges(added, deleted);
    }

    private void ProcessWatcherChanges(List<string> addedPaths, List<string> deletedPaths)
    {
        bool changed = false;

        // 1. Process deletions
        if (deletedPaths.Count > 0)
        {
            var deletedSet = new HashSet<string>(deletedPaths, StringComparer.OrdinalIgnoreCase);
            var pathCount = _allImagePaths.RemoveAll(path => deletedSet.Contains(path));
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

        // 2. Process additions
        var newItems = new List<ImageItem>();
        var existingPaths = new HashSet<string>(_allImagePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in addedPaths)
        {
            if (!File.Exists(path)) continue;
            if (!existingPaths.Add(path)) continue;

            _allImagePaths.Add(path);
            var item = CreateImageItem(path);
            _allImageItems.Add(item);
            newItems.Add(item);
            changed = true;
        }

        if (changed)
        {
            ApplySort();

            if (newItems.Count > 0)
            {
                ScanNewItemsMetadata(newItems);
            }

            ApplyFilter(resetScroll: false);
            QueueViewportThumbnailSchedule();
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
                await Parallel.ForEachAsync(newItems, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 2,
                    CancellationToken = token
                },
                async (item, cancellationToken) =>
                {
                    try
                    {
                        await item.EnsureMetadataLoadedAsync(cancellationToken);
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
                    catch (OperationCanceledException) {}
                    catch (Exception ex)
                    {
                        DebugLog.Write($"Failed to scan metadata for watcher-added file {item.Path}: {ex}");
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
