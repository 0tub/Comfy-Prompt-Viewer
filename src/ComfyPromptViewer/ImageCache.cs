using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public static class ImageCache
{
    private const int LinuxMallocTrimThreshold = -1;
    private const int LinuxMallocArenaMax = -8;
    private const int LinuxTrimThresholdBytes = 64 * 1024;
    private const int LinuxMaxMallocArenas = 4;
    private static readonly LinkedList<ImageItem> _lruList = new();
    private static readonly object _lock = new();
    private static long _estimatedBytes;
    internal const int MaxCapacity = 512;
    internal const long MaxEstimatedBytes = 128L * 1024 * 1024;

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

            TrimLinuxNativeHeap();
        });
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
