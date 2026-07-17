using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ComfyPromptViewer;

internal sealed class MetadataScanCoordinator
{
    private const int MaxDegreeOfParallelism = 2;
    private const int WarmUiBatchSize = 64;
    private const int WarmLoadBatchSize = 256;
    private static readonly TimeSpan SearchRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cancellation;
    private int _generation;

    public bool HasActiveSession
    {
        get
        {
            lock (_stateLock)
            {
                return _cancellation is { IsCancellationRequested: false };
            }
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_stateLock)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _generation++;
        }

        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        cancellation.Dispose();
    }

    public void Start(List<ImageItem> items, Func<bool> hasSearchQuery, Action applyFilter)
    {
        var session = Restart();
        DebugLog.Observe(Task.Run(
            () => ScanInitialAsync(items.ToList(), session, hasSearchQuery, applyFilter)),
            "Initial metadata scanner");
    }

    public void ScanAdded(List<ImageItem> items, Func<bool> hasSearchQuery, Action applyFilter)
    {
        var session = Snapshot();
        DebugLog.Observe(Task.Run(
            () => ScanAddedAsync(items, session, hasSearchQuery, applyFilter)),
            "Watcher metadata scanner");
    }

    private async Task ScanInitialAsync(
        List<ImageItem> items,
        ScanSession session,
        Func<bool> hasSearchQuery,
        Action applyFilter)
    {
        try
        {
            var lastRefreshTime = DateTime.UtcNow;
            if (!await ApplyWarmEntriesAsync(items, session, hasSearchQuery, applyFilter))
            {
                return;
            }

            var uncachedItems = items.Where(item => !item.HasLoadedMetadata).ToList();
            await Parallel.ForEachAsync(uncachedItems, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                CancellationToken = session.Token
            }, async (item, cancellationToken) =>
            {
                try
                {
                    await item.EnsureMetadataLoadedAsync(cancellationToken);
                    if (hasSearchQuery() && TryClaimRefresh(ref lastRefreshTime, items))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (IsCurrent(session))
                            {
                                applyFilter();
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"Metadata scanner worker failed: {ex.Message}");
                }
            });

            if (IsCurrent(session))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (IsCurrent(session))
                    {
                        applyFilter();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> ApplyWarmEntriesAsync(
        List<ImageItem> items,
        ScanSession session,
        Func<bool> hasSearchQuery,
        Action applyFilter)
    {
        for (var loadStart = 0; loadStart < items.Count; loadStart += WarmLoadBatchSize)
        {
            session.Token.ThrowIfCancellationRequested();
            var loadEnd = Math.Min(items.Count, loadStart + WarmLoadBatchSize);
            var cachedEntries = MetadataIndex.LoadMany(
                items.GetRange(loadStart, loadEnd - loadStart).Select(item => item.Path),
                session.Token);

            for (var uiStart = loadStart; cachedEntries.Count > 0 && uiStart < loadEnd; uiStart += WarmUiBatchSize)
            {
                var batchStart = uiStart;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!IsCurrent(session))
                    {
                        return;
                    }

                    var batchEnd = Math.Min(loadEnd, batchStart + WarmUiBatchSize);
                    for (var index = batchStart; index < batchEnd; index++)
                    {
                        var item = items[index];
                        if (!item.HasLoadedMetadata && cachedEntries.TryGetValue(item.Path, out var entry))
                        {
                            item.ApplyMetadataEntry(entry);
                        }
                    }
                }, DispatcherPriority.Background);

                if (!IsCurrent(session))
                {
                    return false;
                }
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (IsCurrent(session) && hasSearchQuery())
            {
                applyFilter();
            }
        }, DispatcherPriority.Background);
        return IsCurrent(session);
    }

    private async Task ScanAddedAsync(
        List<ImageItem> items,
        ScanSession session,
        Func<bool> hasSearchQuery,
        Action applyFilter)
    {
        try
        {
            var lastRefreshTime = DateTime.UtcNow;
            await Parallel.ForEachAsync(items, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                CancellationToken = session.Token
            }, async (item, cancellationToken) =>
            {
                try
                {
                    await item.EnsureMetadataLoadedAsync(cancellationToken);
                    if (hasSearchQuery() && TryClaimRefresh(ref lastRefreshTime, items))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (IsCurrent(session))
                            {
                                applyFilter();
                            }
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"Failed to scan metadata for watcher-added file {item.Path}: {ex}");
                }
            });

            if (hasSearchQuery())
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsCurrent(session))
                    {
                        applyFilter();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsCurrent(ScanSession session)
    {
        lock (_stateLock)
        {
            return session.Generation == _generation && !session.Token.IsCancellationRequested;
        }
    }

    private ScanSession Restart()
    {
        Cancel();
        lock (_stateLock)
        {
            _cancellation = new CancellationTokenSource();
            return new ScanSession(_cancellation.Token, ++_generation);
        }
    }

    private ScanSession Snapshot()
    {
        lock (_stateLock)
        {
            return new ScanSession(_cancellation?.Token ?? CancellationToken.None, _generation);
        }
    }

    private static bool TryClaimRefresh(ref DateTime lastRefreshTime, object refreshLock)
    {
        var now = DateTime.UtcNow;
        lock (refreshLock)
        {
            if (now - lastRefreshTime <= SearchRefreshInterval)
            {
                return false;
            }

            lastRefreshTime = now;
            return true;
        }
    }

    private readonly record struct ScanSession(CancellationToken Token, int Generation);
}
