using System;
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
                _allImageItems.Count == 0)
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
                if (token.IsCancellationRequested || !_folderLoader.IsCurrent(loadGeneration) || _allImageItems.Count == 0)
                {
                    return;
                }

                _metadataScanner.Start(_allImageItems, HasSearchQueryActive, () => ApplyFilter(resetScroll: false));
                UpdateCountText();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool HasSearchQueryActive()
    {
        return _hasSearchQueryActive;
    }

    private void ImageItem_MetadataLoaded(ImageItem item)
    {
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
