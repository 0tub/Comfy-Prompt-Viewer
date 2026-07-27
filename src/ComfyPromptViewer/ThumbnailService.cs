using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ComfyPromptViewer;

internal sealed class ThumbnailService : IDisposable
{
    private const int ThumbnailJpegQuality = 82;
    private const int SelectedPreviewMaxWidth = 2048;
    private const long SelectedPreviewMaxPixels = 8_000_000;
    private const double PreviewDownscaleThreshold = 1.15;
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

    public ThumbnailService(string appDataDirectory)
    {
        CacheRootDirectory = Path.Combine(appDataDirectory, "thumbnails");
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

    public string CacheRootDirectory { get; }
    public string FolderCacheRootDirectory { get; }
    public ThumbnailFolderScope? ActiveFolderScope => _activeScope;
    public SemaphoreSlim SelectedPreviewLoadLimiter { get; } = new(1);

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
    public async Task<ThumbnailFolderScope> OpenFolderScopeAsync(string folderPath)
    {
        var folderKey = ThumbnailFolderScope.NormalizeFolderPath(folderPath);
        await _maintenanceLock.WaitAsync();
        try
        {
            if (_activeScope is { IsRetired: false } current &&
                string.Equals(current.FolderKey, folderKey, StringComparison.Ordinal))
            {
                return current;
            }

            await DrainAndDisposeAsync(DetachActiveScope());
            var scope = new ThumbnailFolderScope(FolderCacheRootDirectory, folderPath);
            _activeScope = scope;
            return scope;
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

        await _cacheWriteLimiter.WaitAsync();
        _cacheWriteLimiter.Release();
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

    public void RemoveCachedThumbnail(ThumbnailFolderScope? scope, in ThumbnailKey key)
    {
        scope?.Pack.Remove(key);
    }

    public Bitmap DecodeThumbnail(string sourcePath, int width)
    {
        using var stream = File.OpenRead(sourcePath);
        return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
    }

    public Bitmap DecodeSelectedPreview(string sourcePath, int knownSourceWidth = 0, int knownSourceHeight = 0)
    {
        var (sourceWidth, sourceHeight) = knownSourceWidth > 0 && knownSourceHeight > 0
            ? (knownSourceWidth, knownSourceHeight)
            : TryReadSourceSize(sourcePath);

        var decodeWidth = GetPreviewDecodeWidth(sourceWidth, sourceHeight);
        using var stream = File.OpenRead(sourcePath);
        return decodeWidth > 0
            ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality)
            : new Bitmap(stream);
    }

    // Returns 0 to decode at native size. PNG has no scaled decode, so the full image is decoded either
    // way and a resample is pure extra work; only real upscales are worth it. The pixel budget catches
    // aspect ratios that stay under the width cap but blow past it in total pixels.
    internal static int GetPreviewDecodeWidth(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return SelectedPreviewMaxWidth;
        }

        var targetWidth = Math.Min(sourceWidth, SelectedPreviewMaxWidth);
        var pixelBudgetWidth = (int)Math.Sqrt(
            SelectedPreviewMaxPixels * (double)sourceWidth / sourceHeight);
        targetWidth = Math.Min(targetWidth, Math.Max(1, pixelBudgetWidth));

        return sourceWidth > targetWidth * PreviewDownscaleThreshold ? targetWidth : 0;
    }

    private static (int Width, int Height) TryReadSourceSize(string sourcePath)
    {
        try
        {
            using var codec = CreateCodec(sourcePath);
            return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to read source dimensions for {sourcePath}: {ex.Message}");
            return (0, 0);
        }
    }

    public bool TryQueueCacheWrite(ImageItem item, in ThumbnailKey key)
    {
        // The item's own scope, not the active one: a folder swap must not be able to redirect leftover
        // work into the newly opened folder's pack.
        if (item.CacheScope is not { IsRetired: false } scope || scope.Pack.Contains(key))
        {
            return false;
        }

        var scopedKey = new ScopedThumbnailKey(scope, key);
        lock (_pendingWritesLock)
        {
            if (_maintenancePaused || _failedWrites.Contains(scopedKey) || !_pendingWrites.Add(scopedKey))
            {
                return false;
            }

            _deferredWrites.Enqueue(new ThumbnailCacheWrite(
                item,
                scope,
                key,
                item.GetThumbnailDecodeWidth()));
        }

        StartWriter();
        return true;
    }

    public void SetCacheWritePause(Func<bool>? isPaused)
    {
        _writesPaused = isPaused;
    }

    public void ResumeDeferredWrites()
    {
        StartWriter();
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

            await _cacheWriteLimiter.WaitAsync();
            _cacheWriteLimiter.Release();
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

    private void StartWriter()
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
                if (write.Scope.IsRetired || write.Scope.Pack.Contains(write.Key))
                {
                    return;
                }

                write.Scope.Pack.Write(write.Key, EncodeJpegThumbnail(write.Item.Path, write.ThumbnailWidth));
            }
            catch (Exception ex)
            {
                if (RecordWriteFailure(write.Scope, write.Key))
                {
                    DebugLog.Write($"Failed to write deferred thumbnail cache for {write.Item.Path}: {ex.Message}");
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

    // Bytes only; durability is the pack's problem, so no temp file or atomic move per thumbnail.
    internal static byte[] EncodeJpegThumbnail(string sourcePath, int thumbnailWidth)
    {
        using var codec = CreateCodec(sourcePath)
            ?? throw new InvalidDataException($"Could not open thumbnail source {sourcePath}.");
        var sourceInfo = codec.Info;
        var width = Math.Min(sourceInfo.Width, thumbnailWidth);
        var height = Math.Max(1, (int)Math.Round(sourceInfo.Height * (width / (double)sourceInfo.Width)));
        var decodedSize = codec.GetScaledDimensions(width / (float)sourceInfo.Width);
        var decodedInfo = new SKImageInfo(
            decodedSize.Width,
            decodedSize.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var decoded = SKBitmap.Decode(codec, decodedInfo)
            ?? throw new InvalidDataException($"Could not decode thumbnail source {sourcePath}.");
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var resized = new SKBitmap(info);
        if (!decoded.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
        {
            throw new InvalidDataException($"Could not resize thumbnail source {sourcePath}.");
        }

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, ThumbnailJpegQuality)
            ?? throw new InvalidDataException($"Could not encode JPEG thumbnail for {sourcePath}.");
        return data.ToArray();
    }

    // Never SKCodec.Create(path). That opens the file by name through Skia's native file stream, so it is
    // capped at MAX_PATH unless the process manifest is longPathAware and the machine has long paths enabled
    // - neither of which holds for the shipped exe - and it reports the cap as a plain null. Prompt-named
    // generator output clears 260 characters routinely, which is why those files decoded fine for display
    // (every other read here goes through a managed FileStream, and .NET applies the \\?\ prefix itself)
    // while the cache write failed on each pass. Handing Skia a managed stream puts this read on the same
    // footing. The codec adopts the stream, and disposes it itself if creation fails.
    private static SKCodec? CreateCodec(string sourcePath)
    {
        var stream = new SKManagedStream(File.OpenRead(sourcePath), disposeManagedStream: true);
        var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            stream.Dispose();
        }

        return codec;
    }

    private bool TryDequeue(out ThumbnailCacheWrite write)
    {
        lock (_pendingWritesLock)
        {
            return _deferredWrites.TryDequeue(out write);
        }
    }

    private readonly record struct ThumbnailCacheWrite(
        ImageItem Item,
        ThumbnailFolderScope Scope,
        ThumbnailKey Key,
        int ThumbnailWidth);

    // Deduplication has to be per folder now: the same source path can appear under two folder roots, and
    // the key deliberately does not know which cache it belongs to.
    private readonly record struct ScopedThumbnailKey(ThumbnailFolderScope? Scope, ThumbnailKey Key);
}
