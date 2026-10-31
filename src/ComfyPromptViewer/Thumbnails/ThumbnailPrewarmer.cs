using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// Fills the open folder's thumbnail pack in the background, outward from wherever the viewport is. Cache
// writes still pause while visible thumbnails load, and a folder swap (the session gate moving on) drops the
// pass. Counters are touched only on the UI thread; the pass itself runs on the pool.
internal sealed class ThumbnailPrewarmer(ThumbnailService thumbnailService, SessionGate folderGate, bool enabled)
{
    // Shallow on purpose: the queue is ordered by distance from the viewport when items enter it, so a
    // deep queue would keep caching where the user was rather than where they are.
    private const int QueueHighWaterMark = 32;
    private const int ProgressBatch = 32;
    // The parallel writers drain a full queue in about 100 ms, so a longer wait left them idle; at 250 ms
    // this poll, not decoding, set the pace of a folder fill.
    private static readonly TimeSpan QueuePollInterval = TimeSpan.FromMilliseconds(20);

    private volatile bool _enabled = enabled;
    private volatile int _focusIndex;
    private volatile int _direction = 1;
    private int _remaining;
    private int _total;

    // Remaining or total moved; the count line shows them.
    public event Action? ProgressChanged;

    public int Remaining => _remaining;
    public int Total => _total;

    // What the gallery is drawing at, so the pass caches the width it will actually ask for.
    public double TileSize { get; set; }
    public double RenderScaling { get; set; } = 1.0;

    // Turning it off drops what is queued and clears the progress. Turning it on queues nothing by itself.
    public void SetEnabled(bool value)
    {
        _enabled = value;
        if (!value)
        {
            thumbnailService.ClearDeferredWrites();
            _remaining = 0;
            _total = 0;
            ProgressChanged?.Invoke();
        }
    }

    public bool IsEnabled => _enabled;

    // The catalog index in the middle of the viewport and which way the user is scrolling.
    public void FollowViewport(int catalogIndex, int direction)
    {
        if (catalogIndex >= 0)
        {
            _focusIndex = catalogIndex;
        }

        _direction = direction;
    }

    // A new folder: no progress, and the viewport starts at the top.
    public void Reset()
    {
        _remaining = 0;
        _total = 0;
        _focusIndex = 0;
        _direction = 1;
    }

    // A whole-folder pass. catalogItems must be the catalog in catalog order, so an item's index is its position.
    public void QueueFolder(IReadOnlyList<ImageItem> catalogItems, int loadGeneration, CancellationToken token)
    {
        if (catalogItems.Count == 0 || !folderGate.IsCurrent(loadGeneration))
        {
            return;
        }

        Queue([.. catalogItems], loadGeneration, token, followViewport: true);
    }

    // Files that arrive after the initial pass, in no particular order.
    public void QueueAdded(IReadOnlyList<ImageItem> items, int loadGeneration, CancellationToken token)
    {
        Queue(items, loadGeneration, token, followViewport: false);
    }

    private void Queue(IReadOnlyList<ImageItem> items, int loadGeneration, CancellationToken token, bool followViewport)
    {
        if (items.Count == 0 || !_enabled)
        {
            return;
        }

        DebugLog.Observe(RunAsync(items, loadGeneration, token, followViewport), "Thumbnail cache prewarm");
    }

    private bool IsWanted(int loadGeneration) => _enabled && folderGate.IsCurrent(loadGeneration);

    // Two passes. The first is one in-memory pack lookup per image and decides whether there is any work at
    // all, so reopening a folder that is already cached queues nothing, wakes no writer, and shows no
    // progress. Only the second pass counts toward the label, which is why it tracks thumbnails that are
    // genuinely missing instead of images walked.
    private async Task RunAsync(
        IReadOnlyList<ImageItem> items,
        int loadGeneration,
        CancellationToken token,
        bool followViewport)
    {
        var announced = 0;
        var completed = 0;
        try
        {
            await Task.Run(async () =>
            {
                var (pending, positions) = CollectCandidates(items, loadGeneration);
                if (pending.Count == 0)
                {
                    return;
                }

                announced = pending.Count;
                AddWork(announced, loadGeneration);
                var order = followViewport ? new PrewarmOrder(positions) : null;

                for (var queued = 0; queued < pending.Count; queued++)
                {
                    // Enqueue against a high-water mark so a large folder does not sit in the queue whole.
                    while (thumbnailService.PendingWriteCount >= QueueHighWaterMark)
                    {
                        await Task.Delay(QueuePollInterval, token);
                        if (!IsWanted(loadGeneration))
                        {
                            return;
                        }
                    }

                    if (!IsWanted(loadGeneration))
                    {
                        return;
                    }

                    // Picked at enqueue time, not up front, so the pass follows the user as they scroll.
                    var item = order is null
                        ? pending[queued]
                        : pending[order.Take(_focusIndex, _direction)];
                    thumbnailService.TryQueueCacheWrite(item.ThumbnailSource, item.GetThumbnailKey());

                    if (Interlocked.Increment(ref completed) % ProgressBatch == 0)
                    {
                        ReportProgress(ProgressBatch, loadGeneration);
                    }
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Whatever the last partial batch did not report, so an early return still clears the label.
            ReportProgress(announced - (completed / ProgressBatch * ProgressBatch), loadGeneration);
        }
    }

    // An item's decode bucket is only refreshed for visible and ahead tiles, so an off-screen item can still
    // be carrying the tile size the folder was opened with. Aligning it here keeps the pass from caching a
    // width the gallery will never ask for and then leaving those images cold at the size actually shown.
    // Positions are each candidate's index in items, ascending, for PrewarmOrder.
    private (List<ImageItem> Pending, int[] Positions) CollectCandidates(IReadOnlyList<ImageItem> items, int loadGeneration)
    {
        var pending = new List<ImageItem>();
        var positions = new List<int>();
        var tileSize = TileSize;
        var renderScaling = RenderScaling;
        for (var index = 0; index < items.Count; index++)
        {
            if (!IsWanted(loadGeneration))
            {
                return ([], []);
            }

            var item = items[index];
            item.SetTileSize(tileSize, renderScaling);
            if (item.CacheScope is { IsRetired: false } scope && scope.Pack.CanWrite &&
                !thumbnailService.HasCachedThumbnail(scope, item.GetThumbnailKey()))
            {
                pending.Add(item);
                positions.Add(index);
            }
        }

        return (pending, positions.ToArray());
    }

    // Both counter posts carry the load generation: a pass that was still current when it queued work can
    // land after a folder swap, and a stale add would report caching for a folder that is gone while a
    // stale decrement would eat the new folder's progress.
    private void AddWork(int count, int loadGeneration)
    {
        PostIfCurrent(loadGeneration, () =>
        {
            _total += count;
            _remaining += count;
        });
    }

    private void ReportProgress(int completedCount, int loadGeneration)
    {
        if (completedCount <= 0)
        {
            return;
        }

        PostIfCurrent(loadGeneration, () =>
        {
            _remaining = Math.Max(0, _remaining - completedCount);
            if (_remaining == 0)
            {
                _total = 0;
            }
        });
    }

    private void PostIfCurrent(int loadGeneration, Action update)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (folderGate.IsCurrent(loadGeneration))
            {
                update();
                ProgressChanged?.Invoke();
            }
        }, DispatcherPriority.Background);
    }
}
