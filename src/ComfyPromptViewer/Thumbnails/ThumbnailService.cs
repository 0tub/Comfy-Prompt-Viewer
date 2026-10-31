using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace ComfyPromptViewer;

internal sealed class ThumbnailService : IDisposable
{
    // Bounds retained encoded pixels to at most 16 MiB; when full, a later prewarm pass retries.
    private const int MaxDeferredWrites = 128;
    private const int MaxDeferredPayloadBytes = 128 * 1024;
    // Every folder cache lives under this one subdirectory of the cache root, which is also why the
    // pre-pack cleanup below has to skip it: it is the only subdirectory there that is not legacy.
    private const string FolderCacheDirectoryName = "folders";
    private const string PackFileName = "thumbnails.pack";
    private const string IndexFileName = "thumbnails.idx";
    private readonly SemaphoreSlim _cacheWriteLimiter = new(1, 1);
    // Serializes scope swaps against clearing, so a pack handle is never disposed while a maintenance pass
    // is deleting the directory it lives in.
    private readonly SemaphoreSlim _maintenanceLock = new(1, 1);
    private readonly object _pendingWritesLock = new();
    private readonly HashSet<ScopedThumbnailKey> _pendingWrites = [];
    // Writes that already threw. The queue refills from the viewport on every scroll pass, so without this a
    // single unencodable file re-runs a full decode and writes an identical log line forever.
    private readonly HashSet<ScopedThumbnailKey> _failedWrites = [];
    private readonly Queue<ThumbnailCacheWrite> _deferredWrites = new();
    private volatile ThumbnailFolderScope? _activeScope;
    private Func<bool>? _writesPaused;
    private bool _maintenancePaused;

    public ThumbnailService(string cacheRootDirectory)
    {
        CacheRootDirectory = cacheRootDirectory;
        FolderCacheRootDirectory = Path.Combine(CacheRootDirectory, FolderCacheDirectoryName);

        try
        {
            Directory.CreateDirectory(FolderCacheRootDirectory);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to create thumbnail cache root {FolderCacheRootDirectory}: {ex.Message}");
        }

        DebugLog.Observe(Task.Run(RemoveLegacyCacheDirectories), "Legacy thumbnail cache cleanup");
    }

    public string CacheRootDirectory { get; private set; }
    public string FolderCacheRootDirectory { get; private set; }
    public ThumbnailFolderScope? ActiveFolderScope => _activeScope;

    // Lets bulk producers throttle themselves instead of queueing a whole folder at once.
    public int PendingWriteCount
    {
        get
        {
            lock (_pendingWritesLock)
            {
                return _deferredWrites.Count;
            }
        }
    }

    // Opens (or reuses) the cache scope for a folder and retires the previous one. Reopening the same
    // folder - a reload, or an Include subfolders toggle - keeps its pack rather than churning the handles.
    public async Task<ThumbnailFolderScope> OpenFolderScopeAsync(string folderPath, CancellationToken token = default)
    {
        var folderKey = FolderNames.Key(folderPath);
        await _maintenanceLock.WaitAsync();
        try
        {
            if (_activeScope is { IsRetired: false } current &&
                string.Equals(current.FolderKey, folderKey, StringComparison.Ordinal))
            {
                return current;
            }

            await DrainAndDisposeAsync(DetachActiveScope());
            var scope = await Task.Run(() => new ThumbnailFolderScope(FolderCacheRootDirectory, folderPath));
            if (token.IsCancellationRequested)
            {
                scope.Retire();
                scope.Dispose();
                return scope;
            }
            _activeScope = scope;
            return scope;
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    // Only offered on the start screen, where no folder scope is open, so the next folder opens its pack under
    // the new root. The old root's caches stay where they are.
    public async Task SetCacheRootAsync(string cacheRootDirectory)
    {
        await _maintenanceLock.WaitAsync();
        try
        {
            var folderCacheRoot = Path.Combine(cacheRootDirectory, FolderCacheDirectoryName);
            await Task.Run(() => Directory.CreateDirectory(folderCacheRoot));
            CacheRootDirectory = cacheRootDirectory;
            FolderCacheRootDirectory = folderCacheRoot;
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    // Detaching is synchronous on purpose. A caller that only observes the returned task - going back to the
    // main menu - must still have given up the scope by the time it returns, or a folder reopened right
    // afterwards could adopt a scope that is about to be disposed.
    public Task RetireFolderScopeAsync()
    {
        var previous = DetachActiveScope();
        return previous is null ? Task.CompletedTask : RetireDetachedScopeAsync(previous);
    }

    private async Task RetireDetachedScopeAsync(ThumbnailFolderScope scope)
    {
        await _maintenanceLock.WaitAsync();
        try
        {
            await DrainAndDisposeAsync(scope);
        }
        finally
        {
            _maintenanceLock.Release();
        }
    }

    // Marking the scope retired is what stops stale work: a queued write or a running decode from the
    // previous folder sees a retired scope and gives up instead of finding a live handle.
    private ThumbnailFolderScope? DetachActiveScope()
    {
        var previous = _activeScope;
        if (previous is null)
        {
            return null;
        }

        _activeScope = null;
        previous.Retire();
        lock (_pendingWritesLock)
        {
            RemoveDeferredWritesForScopeLocked(previous);
        }

        return previous;
    }

    // Only after the in-flight write finishes is it safe to close the pack underneath it.
    private async Task DrainAndDisposeAsync(ThumbnailFolderScope? scope)
    {
        if (scope is null)
        {
            return;
        }

        await WaitForInFlightWriteAsync();
        scope.Dispose();
    }

    public bool HasCachedThumbnail(ThumbnailFolderScope? scope, in ThumbnailKey key)
    {
        return scope is { IsRetired: false } && scope.Pack.Contains(key);
    }

    public Bitmap? TryLoadCachedThumbnail(ThumbnailFolderScope? scope, in ThumbnailKey key)
    {
        if (scope is not { IsRetired: false } || !scope.Pack.TryRead(key, out var data))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            // A payload that will not decode is worse than a miss; drop it so the next pass re-encodes.
            scope.Pack.Remove(key);
            throw;
        }
    }

    public bool TryQueueCacheWrite(ThumbnailSource source, in ThumbnailKey key, byte[]? payload = null)
    {
        // The source's own scope, not the active one: a folder swap must not be able to redirect leftover
        // work into the newly opened folder's pack.
        if (!IsWritable(source.Scope, key, out var scope))
        {
            return false;
        }

        var scopedKey = new ScopedThumbnailKey(scope, key);
        lock (_pendingWritesLock)
        {
            if ((payload is not null && (payload.Length > MaxDeferredPayloadBytes || _deferredWrites.Count >= MaxDeferredWrites)) ||
                _maintenancePaused || _failedWrites.Contains(scopedKey) || !_pendingWrites.Add(scopedKey))
            {
                return false;
            }

            _deferredWrites.Enqueue(new ThumbnailCacheWrite(source.Path, scope, key, source.Width, payload));
        }

        StartWriter();
        return true;
    }

    private static bool IsWritable(
        ThumbnailFolderScope? candidate,
        in ThumbnailKey key,
        [NotNullWhen(true)] out ThumbnailFolderScope? scope)
    {
        scope = candidate is { IsRetired: false } live && live.Pack.CanWrite && !live.Pack.Contains(key) ? live : null;
        return scope is not null;
    }

    public void SetCacheWritePause(Func<bool>? isPaused)
    {
        _writesPaused = isPaused;
    }

    internal bool TryBeginCacheWrite(ThumbnailFolderScope? scope, in ThumbnailKey key)
    {
        if (!_cacheWriteLimiter.Wait(0))
        {
            return false;
        }

        lock (_pendingWritesLock)
        {
            if (_pendingWrites.Add(new ScopedThumbnailKey(scope, key)))
            {
                return true;
            }
        }

        _cacheWriteLimiter.Release();
        return false;
    }

    internal void EndCacheWrite(ThumbnailFolderScope? scope, in ThumbnailKey key)
    {
        lock (_pendingWritesLock)
        {
            _pendingWrites.Remove(new ScopedThumbnailKey(scope, key));
        }

        _cacheWriteLimiter.Release();
        StartWriter();
    }

    public void ClearDeferredWrites()
    {
        lock (_pendingWritesLock)
        {
            DrainDeferredWritesLocked();
        }
    }

    // Clearing one folder is still two truncations, and it cannot touch any other folder's pack.
    public async Task ClearFolderCacheAsync(ThumbnailFolderScope scope)
    {
        await _maintenanceLock.WaitAsync();
        try
        {
            lock (_pendingWritesLock)
            {
                _maintenancePaused = true;
                RemoveDeferredWritesForScopeLocked(scope);
            }

            await WaitForInFlightWriteAsync();
            scope.Pack.Clear();
        }
        finally
        {
            ResumeWrites();
            _maintenanceLock.Release();
        }
    }

    // The active folder's pack is truncated in place because its handles are open; every other folder cache
    // and the pre-pack global cache are deleted outright.
    public async Task ClearAllCachesAsync()
    {
        await _maintenanceLock.WaitAsync();
        try
        {
            await PauseAndDrainWritesAsync();
            lock (_pendingWritesLock)
            {
                _failedWrites.Clear();
            }

            var active = _activeScope;
            active?.Pack.Clear();
            await Task.Run(() =>
            {
                RemoveFolderCaches(active?.Directory);
                RemoveLegacyGlobalPack();
                RemoveLegacyCacheDirectories();
            });
        }
        finally
        {
            ResumeWrites();
            _maintenanceLock.Release();
        }
    }

    private void RemoveFolderCaches(string? keepDirectory)
    {
        try
        {
            if (!Directory.Exists(FolderCacheRootDirectory))
            {
                return;
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            foreach (var directory in Directory.EnumerateDirectories(FolderCacheRootDirectory))
            {
                if (keepDirectory is not null && string.Equals(directory, keepDirectory, comparison))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"Failed to remove folder thumbnail cache {directory}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to enumerate folder thumbnail caches: {ex.Message}");
        }
    }

    // The pre-folder-scope global pack. It is left in place through the upgrade and only removed here, so a
    // user who never clears keeps whatever disk it occupies rather than losing it silently mid-session.
    private void RemoveLegacyGlobalPack()
    {
        foreach (var fileName in new[] { PackFileName, IndexFileName })
        {
            var path = Path.Combine(CacheRootDirectory, fileName);
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to remove legacy thumbnail cache file {path}: {ex.Message}");
            }
        }
    }

    // Any subdirectory of the cache root other than the folder-cache root is left over from the pre-pack
    // per-folder layout of loose JPEGs.
    private void RemoveLegacyCacheDirectories()
    {
        try
        {
            if (!Directory.Exists(CacheRootDirectory))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(CacheRootDirectory))
            {
                if (string.Equals(
                        Path.GetFileName(directory),
                        FolderCacheDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"Failed to remove legacy thumbnail cache directory {directory}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to enumerate legacy thumbnail cache directories: {ex.Message}");
        }
    }

    public async Task PauseAndDrainWritesAsync()
    {
        lock (_pendingWritesLock)
        {
            _maintenancePaused = true;
            DrainDeferredWritesLocked();
        }

        await WaitForInFlightWriteAsync();
    }

    // The writer holds the limiter for exactly one write, so taking and returning it waits that write out.
    private async Task WaitForInFlightWriteAsync()
    {
        await _cacheWriteLimiter.WaitAsync();
        _cacheWriteLimiter.Release();
    }

    private void DrainDeferredWritesLocked()
    {
        while (_deferredWrites.TryDequeue(out var write))
        {
            _pendingWrites.Remove(new ScopedThumbnailKey(write.Scope, write.Key));
        }
    }

    // Order is preserved for the scopes that survive, so a folder clear does not reshuffle the queue behind
    // the prewarm pass that filled it.
    private void RemoveDeferredWritesForScopeLocked(ThumbnailFolderScope scope)
    {
        // Retiring or clearing a folder resets what is known about it, so its failures are forgotten too and
        // reopening the folder attempts them again.
        _failedWrites.RemoveWhere(failed => failed.Scope == scope);

        var pending = _deferredWrites.Count;
        for (var index = 0; index < pending; index++)
        {
            if (!_deferredWrites.TryDequeue(out var write))
            {
                break;
            }

            if (write.Scope == scope)
            {
                _pendingWrites.Remove(new ScopedThumbnailKey(write.Scope, write.Key));
                continue;
            }

            _deferredWrites.Enqueue(write);
        }
    }

    public void ResumeWrites()
    {
        lock (_pendingWritesLock)
        {
            _maintenancePaused = false;
        }

        StartWriter();
    }

    public void Dispose()
    {
        var scope = _activeScope;
        _activeScope = null;
        scope?.Dispose();
    }

    // Picks up the next deferred write unless writes are paused or one is already running.
    public void StartWriter()
    {
        lock (_pendingWritesLock)
        {
            if (_maintenancePaused)
            {
                return;
            }
        }

        if (ShouldPauseWrites() || !_cacheWriteLimiter.Wait(0))
        {
            return;
        }

        if (!TryDequeue(out var write))
        {
            _cacheWriteLimiter.Release();
            return;
        }

        DebugLog.Observe(Task.Run(() =>
        {
            try
            {
                // Re-checked here rather than at dequeue: the folder can swap while this write waits its
                // turn, and a retired scope's pack is closing or already closed.
                if (write.Scope.IsRetired || !write.Scope.Pack.CanWrite || write.Scope.Pack.Contains(write.Key))
                {
                    return;
                }

                if (!write.Scope.Pack.Write(write.Key, write.Payload ?? ThumbnailDecoder.EncodeJpegThumbnail(write.SourcePath, write.ThumbnailWidth)))
                {
                    RecordWriteFailure(write.Scope, write.Key);
                }
            }
            catch (Exception ex)
            {
                if (RecordWriteFailure(write.Scope, write.Key))
                {
                    DebugLog.Write($"Failed to write deferred thumbnail cache for {write.SourcePath}: {ex.Message}");
                }
            }
            finally
            {
                EndCacheWrite(write.Scope, write.Key);
            }
        }), "Deferred thumbnail cache writer");
    }

    // Returns true only for the first failure of a given attempt, which is what keeps one broken file to one
    // log line. The key covers the source's write time and the decode width, so a file that is replaced on
    // disk - or a tile that changes size - produces a different key and gets a real retry; a transient failure
    // on an unchanged file is the one case this deliberately does not retry, because it cannot tell that case
    // from a permanently unreadable source and retrying forever is what caused the flood.
    private bool RecordWriteFailure(ThumbnailFolderScope scope, in ThumbnailKey key)
    {
        lock (_pendingWritesLock)
        {
            return _failedWrites.Add(new ScopedThumbnailKey(scope, key));
        }
    }

    private bool ShouldPauseWrites()
    {
        try
        {
            return _writesPaused?.Invoke() == true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Deferred thumbnail cache pause callback failed: {ex.Message}");
            return false;
        }
    }

    // Display keeps the original resized pixels; only the disk payload uses lossy JPEG encoding.
    public Bitmap DecodeThumbnailForCache(ThumbnailSource source, in ThumbnailKey key, Func<bool> canCache)
    {
        using var resized = ThumbnailDecoder.DecodeResized(source.Path, source.Width);
        var bitmap = ThumbnailDecoder.ToBitmap(resized);
        try
        {
            if (canCache() && IsWritable(source.Scope, key, out var scope))
            {
                var scopedKey = new ScopedThumbnailKey(scope, key);
                lock (_pendingWritesLock)
                {
                    if (_maintenancePaused || _deferredWrites.Count >= MaxDeferredWrites ||
                        _failedWrites.Contains(scopedKey) || _pendingWrites.Contains(scopedKey))
                    {
                        return bitmap;
                    }
                }

                if (ThumbnailDecoder.EncodeJpeg(resized) is { } payload && canCache())
                {
                    TryQueueCacheWrite(source, key, payload);
                }
            }
        }
        catch (Exception ex)
        {
            // Cache failure must not discard a successfully decoded display thumbnail.
            DebugLog.Write($"Failed to encode displayed thumbnail for {source.Path}: {ex.Message}");
        }
        return bitmap;
    }

    private bool TryDequeue(out ThumbnailCacheWrite write)
    {
        lock (_pendingWritesLock)
        {
            return _deferredWrites.TryDequeue(out write);
        }
    }

    private readonly record struct ThumbnailCacheWrite(
        string SourcePath,
        ThumbnailFolderScope Scope,
        ThumbnailKey Key,
        int ThumbnailWidth,
        byte[]? Payload);

    // Deduplication has to be per folder now: the same source path can appear under two folder roots, and
    // the key deliberately does not know which cache it belongs to.
    private readonly record struct ScopedThumbnailKey(ThumbnailFolderScope? Scope, ThumbnailKey Key);
}

// The file a thumbnail comes from, the folder cache it belongs to, and the width to decode it at.
internal readonly record struct ThumbnailSource(string Path, ThumbnailFolderScope? Scope, int Width);
