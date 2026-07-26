using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace ComfyPromptViewer;

internal sealed class DecodedImageCache
{
    private const int LinuxMallocTrimThreshold = -1;
    private const int LinuxMallocArenaMax = -8;
    private const int LinuxTrimThresholdBytes = 64 * 1024;
    private const int LinuxMaxMallocArenas = 4;
    private readonly LinkedList<ImageItem> _lruList = new();
    private readonly object _lock = new();
    private long _estimatedBytes;
    internal const int MaxCapacity = 512;
    internal const long MaxEstimatedBytes = 64L * 1024 * 1024;

    // Touch adds at most one item, so a small scan keeps eviction ahead of growth. Unbounded, a cache of
    // all-protected visible tiles walks the whole list, frees nothing, and repeats per tile per scroll.
    internal const int MaxEvictionScanPerTouch = 16;

    // A re-touch adds nothing, so it only has to carry its share of the trimming, not lead it.
    internal const int MaxEvictionScanPerReTouch = 1;

    internal static void ConfigureLinuxNativeAllocator()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            mallopt(LinuxMallocArenaMax, LinuxMaxMallocArenas);
            mallopt(LinuxMallocTrimThreshold, LinuxTrimThresholdBytes);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // These allocator controls are glibc-specific. Other Linux libc implementations keep their defaults.
        }
    }

    public void Touch(ImageItem item)
    {
        lock (_lock)
        {
            var estimatedBytes = item.EstimatedPreviewBytes;

            // A re-touch of an unchanged entry is byte- and count-neutral, so it is never what pushed the
            // cache over budget and does not need a full scan. It still makes one attempt: re-touches are
            // most of the traffic, and taking their share of the trimming to zero leaves the cache parked
            // at its high-water mark until the next admission happens to arrive.
            if (item.CacheNode is { } existingNode &&
                existingNode.List == _lruList &&
                item.CachedPreviewBytes == estimatedBytes)
            {
                _lruList.Remove(existingNode);
                item.CacheNode = _lruList.AddLast(item);
                EvictLocked(MaxEvictionScanPerReTouch);
                return;
            }

            RemoveFromCacheLocked(item);

            item.CachedPreviewBytes = estimatedBytes;
            _estimatedBytes += item.CachedPreviewBytes;
            item.CacheNode = _lruList.AddLast(item);
            EvictLocked(MaxEvictionScanPerTouch);
        }
    }

    // Protected items are moved to the tail as they are examined, so consecutive calls keep advancing
    // through the list instead of rescanning the same protected run.
    private void EvictLocked(int allowance)
    {
        var attemptsRemaining = Math.Min(_lruList.Count, allowance);
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

    internal static bool ExceedsBudget(int count, long estimatedBytes)
    {
        return count > MaxCapacity || estimatedBytes > MaxEstimatedBytes;
    }

    private void RemoveFromCacheLocked(ImageItem item)
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

    public void Remove(ImageItem item)
    {
        lock (_lock)
        {
            RemoveFromCacheLocked(item);
        }
    }

    public void ClearAndReleaseAll()
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

        void ReleaseItems()
        {
            foreach (var item in itemsToRelease)
            {
                item.ReleasePreview();
            }

            TrimLinuxNativeHeap();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ReleaseItems();
        }
        else
        {
            Dispatcher.UIThread.Post(ReleaseItems);
        }
    }

    private static void TrimLinuxNativeHeap()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            malloc_trim(0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // malloc_trim is a glibc optimization and is not available on every Linux libc.
        }
    }

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int malloc_trim(nuint padding);

    [DllImport("libc", EntryPoint = "mallopt")]
    private static extern int mallopt(int parameter, int value);
}
