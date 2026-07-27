using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private void QueueInitialMetadataScanner(int loadGeneration)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_folderLoader.CurrentToken is not { IsCancellationRequested: false } token ||
                !_folderLoader.IsCurrent(loadGeneration) ||
                _catalog.Count == 0)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested && _folderLoader.IsCurrent(loadGeneration))
                {
                    DebugLog.Observe(
                        StartInitialMetadataScannerWhenReadyAsync(loadGeneration, token),
                        "Initial metadata scanner startup");
                }
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Render);
    }

    private async Task StartInitialMetadataScannerWhenReadyAsync(int loadGeneration, CancellationToken token)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && _folderLoader.IsCurrent(loadGeneration))
                {
                    ScheduleViewportThumbnails();
                }
            }, DispatcherPriority.Background);

            for (var poll = 0; poll < InitialMetadataScannerMaxPolls; poll++)
            {
                if (token.IsCancellationRequested || !_folderLoader.IsCurrent(loadGeneration))
                {
                    return;
                }

                if (!_thumbnailLoads.HasVisibleWork)
                {
                    break;
                }

                await Task.Delay(InitialMetadataScannerPollInterval, token);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !_folderLoader.IsCurrent(loadGeneration) || _catalog.Count == 0)
                {
                    return;
                }

                _metadataScanStartedAt = Environment.TickCount64;
                _metadataScanner.Start(
                    _catalog.Items,
                    HasSearchQueryActive,
                    () => ApplyFilter(resetScroll: false),
                    () => QueueThumbnailCachePrewarm(loadGeneration, token));
                UpdateCountText();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Runs after the initial scan so it never competes with folder load or scanning; writes already pause
    // for visible work and a folder swap drops the queue.
    private void QueueThumbnailCachePrewarm(int loadGeneration, CancellationToken token)
    {
        if (_catalog.Count == 0 || !_folderLoader.IsCurrent(loadGeneration))
        {
            return;
        }

        if (!_prewarmThumbnails)
        {
            return;
        }

        var items = new ImageItem[_catalog.Count];
        for (var index = 0; index < items.Length; index++)
        {
            items[index] = _catalog.Items[index];
        }

        QueueThumbnailCachePrewarm(items, loadGeneration, token);
    }

    private void QueueThumbnailCachePrewarm(
        IReadOnlyList<ImageItem> items,
        int loadGeneration,
        CancellationToken token)
    {
        if (items.Count == 0 || !_prewarmThumbnails)
        {
            return;
        }

        DebugLog.Observe(
            PrewarmThumbnailCacheAsync(items, loadGeneration, token),
            "Thumbnail cache prewarm");
    }

    // Two passes. The first is one in-memory pack lookup per image and decides whether there is any work at
    // all, so reopening a folder that is already cached queues nothing, wakes no writer, and shows no
    // progress. Only the second pass counts toward the label, which is why it now tracks thumbnails that are
    // genuinely missing instead of images walked.
    private async Task PrewarmThumbnailCacheAsync(
        IReadOnlyList<ImageItem> items,
        int loadGeneration,
        CancellationToken token)
    {
        var announced = 0;
        var completed = 0;
        try
        {
            await Task.Run(async () =>
            {
                var pending = CollectPrewarmCandidates(items, loadGeneration);
                if (pending.Count == 0)
                {
                    return;
                }

                announced = pending.Count;
                AddPrewarmWork(announced, loadGeneration);

                foreach (var item in pending)
                {
                    // Enqueue against a high-water mark so a large folder does not sit in the queue whole.
                    while (_thumbnailService.PendingWriteCount >= PrewarmQueueHighWaterMark)
                    {
                        await Task.Delay(PrewarmQueuePollInterval, token);
                        if (!_folderLoader.IsCurrent(loadGeneration) || !_prewarmThumbnails)
                        {
                            return;
                        }
                    }

                    if (!_folderLoader.IsCurrent(loadGeneration) || !_prewarmThumbnails)
                    {
                        return;
                    }

                    _thumbnailService.TryQueueCacheWrite(item, item.GetThumbnailKey());

                    if (Interlocked.Increment(ref completed) % PrewarmProgressBatch == 0)
                    {
                        ReportPrewarmProgress(PrewarmProgressBatch, loadGeneration);
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
            ReportPrewarmProgress(
                announced - (completed / PrewarmProgressBatch * PrewarmProgressBatch),
                loadGeneration);
        }
    }

    // An item's decode bucket is only refreshed for visible and ahead tiles, so an off-screen item can still
    // be carrying the tile size the folder was opened with. Aligning it here keeps the pass from caching a
    // width the gallery will never ask for and then leaving those images cold at the size actually shown.
    private List<ImageItem> CollectPrewarmCandidates(IReadOnlyList<ImageItem> items, int loadGeneration)
    {
        var pending = new List<ImageItem>();
        foreach (var item in items)
        {
            if (!_folderLoader.IsCurrent(loadGeneration) || !_prewarmThumbnails)
            {
                pending.Clear();
                break;
            }

            item.SetTileSize(_tileSize, _renderScaling);
            if (!item.HasCachedThumbnail())
            {
                pending.Add(item);
            }
        }

        return pending;
    }

    // Both counter posts carry the load generation: a pass that was still current when it queued work can
    // land after a folder swap, and a stale add would report caching for a folder that is gone while a
    // stale decrement would eat the new folder's progress.
    private void AddPrewarmWork(int count, int loadGeneration)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_folderLoader.IsCurrent(loadGeneration))
                {
                    return;
                }

                _prewarmTotal += count;
                _prewarmRemaining += count;
                UpdateCountText();
            },
            DispatcherPriority.Background);
    }

    private void ReportPrewarmProgress(int completedCount, int loadGeneration)
    {
        if (completedCount <= 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_folderLoader.IsCurrent(loadGeneration))
                {
                    return;
                }

                _prewarmRemaining = Math.Max(0, _prewarmRemaining - completedCount);
                if (_prewarmRemaining == 0)
                {
                    _prewarmTotal = 0;
                }

                UpdateCountText();
            },
            DispatcherPriority.Background);
    }

    private bool HasSearchQueryActive()
    {
        return _hasSearchQueryActive;
    }

    // One call updates both the scan-progress counter and the search row, so neither can be forgotten.
    private void ImageItem_MetadataLoaded(ImageItem item)
    {
        _catalog.MarkMetadataLoaded(item);
        QueueMetadataCountTextUpdate();
    }

    private void QueueMetadataCountTextUpdate()
    {
        if (_metadataCountUpdateTimer is null)
        {
            _metadataCountUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = MetadataCountUpdateInterval
            };
            _metadataCountUpdateTimer.Tick += (s, e) =>
            {
                _metadataCountUpdateTimer.Stop();
                UpdateCountText();
            };
        }

        if (!_metadataCountUpdateTimer.IsEnabled)
        {
            _metadataCountUpdateTimer.Start();
        }
    }
}
