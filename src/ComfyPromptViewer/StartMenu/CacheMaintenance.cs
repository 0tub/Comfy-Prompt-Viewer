using System;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

// Clearing the thumbnail and metadata caches. Both the start menu and the folder header offer it, and all of
// it has to stop thumbnail work first and make the visible tiles reload afterwards, so it lives in one place.
internal sealed class CacheMaintenance(
    ThumbnailService thumbnailService,
    ThumbnailLoadCoordinator thumbnailLoads,
    DecodedImageCache decodedImageCache,
    MetadataRepository metadataRepository,
    Action refreshViewport)
{
    private bool _isBusy;

    public bool IsBusy => _isBusy;
    public string ThumbnailCacheRoot => thumbnailService.CacheRootDirectory;

    // Raised on the UI thread whenever a thumbnail clear starts or finishes, so buttons can disable themselves.
    public event Action? BusyChanged;

    // Only the open folder's pack. Its thumbnails go; every other folder's cache is untouched, which is the
    // whole point of the per-folder layout. Returns null on success.
    public Task<Exception?> ClearFolderAsync(ThumbnailFolderScope scope)
    {
        return RunAsync(() => thumbnailService.ClearFolderCacheAsync(scope), "folder thumbnail cache");
    }

    // Two file truncations for the open folder, plus a directory delete per other folder cache and the
    // pre-upgrade global pack. No per-item state to invalidate: every item resolves "is this cached" through
    // its folder's pack index.
    public Task<Exception?> ClearAllAsync()
    {
        return RunAsync(thumbnailService.ClearAllCachesAsync, "thumbnail caches");
    }

    public Task SetThumbnailCacheRootAsync(string cacheRootDirectory)
    {
        return thumbnailService.SetCacheRootAsync(cacheRootDirectory);
    }

    public Exception? ClearMetadata()
    {
        try
        {
            metadataRepository.Clear();
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to clear metadata cache: {ex}");
            return ex;
        }
    }

    private async Task<Exception?> RunAsync(Func<Task> clear, string what)
    {
        if (_isBusy)
        {
            return null;
        }

        SetBusy(true);
        thumbnailLoads.Clear();
        try
        {
            decodedImageCache.ClearAndReleaseAll();
            await clear();
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to clear {what}: {ex}");
            return ex;
        }
        finally
        {
            SetBusy(false);
            // The decoded bitmaps just went with the pack entries, so the visible tiles have to be reloaded.
            refreshViewport();
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BusyChanged?.Invoke();
    }
}
