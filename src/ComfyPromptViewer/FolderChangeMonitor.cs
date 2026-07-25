using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

internal sealed class FolderChangeMonitor : IDisposable
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(300);
    private readonly object _lock = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly Action<FolderChangeBatch> _batchReady;
    private readonly Action<Exception> _recoveryRequested;
    private readonly HashSet<string> _pendingAdded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingChanged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDeleted = new(StringComparer.OrdinalIgnoreCase);
    private bool _batchProcessing;
    private bool _rerunRequested;
    private bool _recoveryQueued;
    private bool _disposed;

    public FolderChangeMonitor(
        string folderPath,
        bool includeSubfolders,
        Action<FolderChangeBatch> batchReady,
        Action<Exception> recoveryRequested)
    {
        _batchReady = batchReady;
        _recoveryRequested = recoveryRequested;
        _timer = new Timer(OnTimerElapsed);
        try
        {
            _watcher = new FileSystemWatcher(folderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = includeSubfolders
            };
            _watcher.Created += OnCreated;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _timer.Dispose();
            throw;
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (!ImageFileReader.IsSupportedImage(e.FullPath))
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed) return;
            _pendingDeleted.Remove(e.FullPath);
            _pendingAdded.Add(e.FullPath);
            RestartTimerLocked();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!ImageFileReader.IsSupportedImage(e.FullPath))
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed) return;
            _pendingDeleted.Remove(e.FullPath);
            if (!_pendingAdded.Contains(e.FullPath))
            {
                _pendingChanged.Add(e.FullPath);
            }
            RestartTimerLocked();
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _pendingAdded.Remove(e.FullPath);
            _pendingChanged.Remove(e.FullPath);
            _pendingDeleted.Add(e.FullPath);
            RestartTimerLocked();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (ImageFileReader.IsSupportedImage(e.OldFullPath))
            {
                _pendingAdded.Remove(e.OldFullPath);
                _pendingChanged.Remove(e.OldFullPath);
                _pendingDeleted.Add(e.OldFullPath);
            }

            if (ImageFileReader.IsSupportedImage(e.FullPath))
            {
                _pendingDeleted.Remove(e.FullPath);
                _pendingChanged.Remove(e.FullPath);
                _pendingAdded.Add(e.FullPath);
            }
            RestartTimerLocked();
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        var exception = e.GetException();
        lock (_lock)
        {
            if (_disposed || _recoveryQueued)
            {
                return;
            }
            _recoveryQueued = true;
        }

        _recoveryRequested(exception);
    }

    private void RestartTimerLocked()
    {
        _timer.Change(DebounceInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnTimerElapsed(object? state)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_batchProcessing)
            {
                _rerunRequested = true;
                return;
            }

            _batchProcessing = true;
        }

        DebugLog.Observe(ProcessPendingAsync(), "Folder watcher batch");
    }

    private async Task ProcessPendingAsync()
    {
        try
        {
            List<string> added;
            List<string> changed;
            List<string> deleted;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                added = [.. _pendingAdded];
                changed = [.. _pendingChanged];
                deleted = [.. _pendingDeleted];
                _pendingAdded.Clear();
                _pendingChanged.Clear();
                _pendingDeleted.Clear();
            }

            if (added.Count == 0 && changed.Count == 0 && deleted.Count == 0)
            {
                return;
            }

            var readablePaths = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
            readablePaths.UnionWith(changed);
            var entries = await FolderLoadCoordinator.ReadEntriesAsync(readablePaths, CancellationToken.None);
            var addedSet = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
            var addedFiles = new List<ImageFileEntry>();
            var changedFiles = new List<ImageFileEntry>();
            foreach (var entry in entries)
            {
                (addedSet.Contains(entry.Path) ? addedFiles : changedFiles).Add(entry);
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
            }

            _batchReady(new FolderChangeBatch(addedFiles, changedFiles, deleted));
        }
        catch (Exception ex)
        {
            DebugLog.WriteException("Folder watcher batch", ex);
        }
        finally
        {
            lock (_lock)
            {
                _batchProcessing = false;
                if (!_disposed &&
                    (_rerunRequested ||
                     _pendingAdded.Count > 0 ||
                     _pendingChanged.Count > 0 ||
                     _pendingDeleted.Count > 0))
                {
                    _rerunRequested = false;
                    RestartTimerLocked();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _pendingAdded.Clear();
            _pendingChanged.Clear();
            _pendingDeleted.Clear();
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreated;
        _watcher.Changed -= OnChanged;
        _watcher.Deleted -= OnDeleted;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _timer.Dispose();
    }
}

internal sealed record FolderChangeBatch(
    IReadOnlyList<ImageFileEntry> AddedFiles,
    IReadOnlyList<ImageFileEntry> ChangedFiles,
    IReadOnlyList<string> DeletedPaths);
