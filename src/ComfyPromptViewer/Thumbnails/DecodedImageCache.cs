using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// Owns the lifetime of every decoded tile bitmap: a bounded LRU over items whose Preview is set. Releasing a
// preview goes through here so the accounting and the bitmap can never disagree. Its bookkeeping lives in
// this class, not on the items.
internal sealed class DecodedImageCache
{
    private const int LinuxMallocTrimThreshold = -1;
    private const int LinuxMallocArenaMax = -8;
    private const int LinuxTrimThresholdBytes = 64 * 1024;
    private const int LinuxMaxMallocArenas = 4;
    private readonly LinkedList<ImageItem> _lruList = new();
    private readonly Dictionary<ImageItem, Entry> _entries = new(ReferenceEqualityComparer.Instance);
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
            var estimatedBytes = EstimateBytes(item.Preview);

            // A re-touch of an unchanged entry is byte- and count-neutral, so it is never what pushed the
            // cache over budget and does not need a full scan. It still makes one attempt: re-touches are
            // most of the traffic, and taking their share of the trimming to zero leaves the cache parked
            // at its high-water mark until the next admission happens to arrive.
            if (_entries.TryGetValue(item, out var existing) && existing.Bytes == estimatedBytes)
            {
                _lruList.Remove(existing.Node);
                _lruList.AddLast(existing.Node);
                EvictLocked(MaxEvictionScanPerReTouch);
                return;
            }

            RemoveLocked(item);
            _entries[item] = new Entry(_lruList.AddLast(item), estimatedBytes);
            _estimatedBytes += estimatedBytes;
            EvictLocked(MaxEvictionScanPerTouch);
        }
    }

    // Drops the item's bitmap now. UI thread only, like every Preview change.
    public void Release(ImageItem item)
    {
        lock (_lock)
        {
            RemoveLocked(item);
        }

        item.ClearPreview();
    }

    public void ClearAndReleaseAll()
    {
        List<ImageItem> itemsToRelease;
        lock (_lock)
        {
            itemsToRelease = [.. _lruList];
            _lruList.Clear();
            _entries.Clear();
            _estimatedBytes = 0;
        }

        void ReleaseItems()
        {
            foreach (var item in itemsToRelease)
            {
                item.ClearPreview();
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

    internal static bool ExceedsBudget(int count, long estimatedBytes)
    {
        return count > MaxCapacity || estimatedBytes > MaxEstimatedBytes;
    }

    private static long EstimateBytes(Bitmap? preview)
    {
        return preview is null
            ? 0
            : Math.Max(1, preview.PixelSize.Width) * (long)Math.Max(1, preview.PixelSize.Height) * 4;
    }

    // Protected items are moved to the tail as they are examined, so consecutive calls keep advancing
    // through the list instead of rescanning the same protected run.
    private void EvictLocked(int allowance)
    {
        var attemptsRemaining = Math.Min(_lruList.Count, allowance);
        while (ExceedsBudget(_lruList.Count, _estimatedBytes) && attemptsRemaining > 0)
        {
            attemptsRemaining--;
            if (_lruList.First?.Value is not { } evicted)
            {
                break;
            }

            if (evicted.IsSelected || evicted.IsRealized)
            {
                // Re-measured, since the bitmap may have been swapped since it was admitted.
                RemoveLocked(evicted);
                var bytes = EstimateBytes(evicted.Preview);
                _entries[evicted] = new Entry(_lruList.AddLast(evicted), bytes);
                _estimatedBytes += bytes;
                continue;
            }

            RemoveLocked(evicted);
            Dispatcher.UIThread.Post(() =>
            {
                // Touched again before this ran: it is back in use, so it keeps its bitmap.
                lock (_lock)
                {
                    if (_entries.ContainsKey(evicted))
                    {
                        return;
                    }
                }

                evicted.ClearPreview();
            });
        }
    }

    private void RemoveLocked(ImageItem item)
    {
        if (_entries.Remove(item, out var entry))
        {
            _lruList.Remove(entry.Node);
            _estimatedBytes -= entry.Bytes;
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

    private sealed record Entry(LinkedListNode<ImageItem> Node, long Bytes);

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int malloc_trim(nuint padding);

    [DllImport("libc", EntryPoint = "mallopt")]
    private static extern int mallopt(int parameter, int value);
}
