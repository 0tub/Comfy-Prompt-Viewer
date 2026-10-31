using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// Decodes the larger bitmap the sidebar and the preview overlay show for the active image. One decode runs
// at a time, and a result for an image that is no longer selected is dropped.
internal sealed class SelectedPreviewLoader
{
    private readonly SemaphoreSlim _limiter = new(1);
    // UI thread only. Entries go when their load completes.
    private readonly Dictionary<ImageItem, Task> _loads = new(ReferenceEqualityComparer.Instance);

    // Does nothing while a decode for this item is still running or its bitmap is already set.
    public void EnsureLoaded(ImageItem item, CancellationToken token)
    {
        if (item.SelectedPreview is not null || _loads.GetValueOrDefault(item) is { IsCompleted: false })
        {
            return;
        }

        item.ResetSelectedPreviewUnavailable();
        var load = LoadAsync(item, token);
        _loads[item] = load;
        DebugLog.Observe(load, "Selected preview load for", item.Path);
    }

    private async Task LoadAsync(ImageItem item, CancellationToken token)
    {
        try
        {
            await _limiter.WaitAsync(token);
            try
            {
                if (!item.IsSelected)
                {
                    return;
                }

                var metadata = item.Metadata;
                var bitmap = await Task.Run(
                    () => ThumbnailDecoder.DecodeSelectedPreview(item.Path, metadata.Width, metadata.Height),
                    token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !item.IsSelected || item.SelectedPreview is not null)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    item.SetSelectedPreview(bitmap);
                });
            }
            finally
            {
                _limiter.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load selected preview for {item.Path}: {ex}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (item.IsSelected)
                {
                    item.MarkSelectedPreviewUnavailable();
                }
            });
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_loads.GetValueOrDefault(item) is { IsCompleted: true })
                {
                    _loads.Remove(item);
                }
            });
        }
    }
}
