using System.Collections.Generic;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public static class ImageCache
{
    private static readonly LinkedList<ImageItem> _lruList = new();
    private static readonly object _lock = new();
    private static long _estimatedBytes;
    internal const int MaxCapacity = 512;
    internal const long MaxEstimatedBytes = 128L * 1024 * 1024;

    public static void Touch(ImageItem item)
    {
        lock (_lock)
        {
            RemoveFromCacheLocked(item);

            item.CachedPreviewBytes = item.EstimatedPreviewBytes;
            _estimatedBytes += item.CachedPreviewBytes;
            item.CacheNode = _lruList.AddLast(item);

            var attemptsRemaining = _lruList.Count;
            while (ExceedsBudget(_lruList.Count, _estimatedBytes) && attemptsRemaining > 0)
            {
                attemptsRemaining--;
                var firstNode = _lruList.First;
                if (firstNode is null)
                {
                    break;
                }

                _lruList.Remove(firstNode);
                var evicted = firstNode.Value;
                _estimatedBytes -= evicted.CachedPreviewBytes;
                evicted.CachedPreviewBytes = 0;
                evicted.CacheNode = null;

                if (evicted.IsSelected || evicted.IsRealized)
                {
                    evicted.CachedPreviewBytes = evicted.EstimatedPreviewBytes;
                    _estimatedBytes += evicted.CachedPreviewBytes;
                    evicted.CacheNode = _lruList.AddLast(evicted);
                    continue;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    evicted.ReleasePreview(skipIfCached: true);
                });
            }
        }
    }

    internal static bool ExceedsBudget(int count, long estimatedBytes)
    {
        return count > MaxCapacity || estimatedBytes > MaxEstimatedBytes;
    }

    private static void RemoveFromCacheLocked(ImageItem item)
    {
        if (item.CacheNode != null)
        {
            if (item.CacheNode.List == _lruList)
            {
                _lruList.Remove(item.CacheNode);
                _estimatedBytes -= item.CachedPreviewBytes;
            }

            item.CacheNode = null;
        }

        item.CachedPreviewBytes = 0;
    }

    public static void Remove(ImageItem item)
    {
        lock (_lock)
        {
            RemoveFromCacheLocked(item);
        }
    }

    public static void ClearAndReleaseAll()
    {
        List<ImageItem> itemsToRelease;
        lock (_lock)
        {
            itemsToRelease = new List<ImageItem>(_lruList.Count);
            foreach (var item in _lruList)
            {
                itemsToRelease.Add(item);
                item.CacheNode = null;
                item.CachedPreviewBytes = 0;
            }
            _lruList.Clear();
            _estimatedBytes = 0;
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in itemsToRelease)
            {
                item.ReleasePreview();
            }
        });
    }
}
