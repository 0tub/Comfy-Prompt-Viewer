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

        _prewarmRemaining += items.Count;
        UpdateCountText();
        DebugLog.Observe(
            PrewarmThumbnailCacheAsync(items, loadGeneration, token),
            "Thumbnail cache prewarm");
    }

    private async Task PrewarmThumbnailCacheAsync(
        IReadOnlyList<ImageItem> items,
        int loadGeneration,
        CancellationToken token)
    {
        var completed = 0;
        try
        {
            await Task.Run(async () =>
            {
                foreach (var item in items)
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

                    if (!item.HasCachedThumbnail())
                    {
                        _thumbnailService.TryQueueCacheWrite(item, item.GetThumbnailKey());
                    }

                    if (Interlocked.Increment(ref completed) % PrewarmProgressBatch == 0)
                    {
                        ReportPrewarmProgress(PrewarmProgressBatch);
                    }
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ReportPrewarmProgress(items.Count - (completed / PrewarmProgressBatch * PrewarmProgressBatch));
        }
    }

    private void ReportPrewarmProgress(int completedCount)
    {
        if (completedCount <= 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                _prewarmRemaining = Math.Max(0, _prewarmRemaining - completedCount);
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
