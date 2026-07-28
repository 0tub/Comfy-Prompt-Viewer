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
    private const int ColdUiBatchSize = 64;
    private static readonly TimeSpan SearchRefreshInterval = TimeSpan.FromSeconds(1);
    // Scanner staleness is one SessionGate; see Staleness.cs. Do not add a second counter here.
    private readonly SessionGate _gate = new();
    private readonly MetadataRepository _metadataRepository;

    public MetadataScanCoordinator(MetadataRepository metadataRepository)
    {
        _metadataRepository = metadataRepository;
    }

    public bool HasActiveSession => _gate.IsActive;

    public void Cancel()
    {
        _gate.Cancel();
    }

    public void Start(
        IReadOnlyList<ImageItem> items,
        Func<bool> hasSearchQuery,
        Action applyFilter,
        Action? onCompleted = null)
    {
        var session = _gate.Restart();
        DebugLog.Observe(Task.Run(
            () => ScanInitialAsync(items.ToList(), session, hasSearchQuery, applyFilter, onCompleted)),
            "Initial metadata scanner");
    }

    public void ScanAdded(List<ImageItem> items, Func<bool> hasSearchQuery, Action applyFilter)
    {
        var session = _gate.Snapshot();
        DebugLog.Observe(Task.Run(
            () => ScanAddedAsync(items, session, hasSearchQuery, applyFilter)),
            "Watcher metadata scanner");
    }

    private async Task ScanInitialAsync(
        List<ImageItem> items,
        Session session,
        Func<bool> hasSearchQuery,
        Action applyFilter,
        Action? onCompleted)
    {
        try
        {
            if (!await ApplyWarmEntriesAsync(items, session, hasSearchQuery, applyFilter))
            {
                return;
            }

            var uncachedItems = items.Where(item => !item.HasLoadedMetadata).ToList();
            await ScanItemsAsync(
                uncachedItems,
                session,
                skipCacheLookup: true,
                hasSearchQuery,
                applyFilter);

            if (session.IsCurrent)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (session.IsCurrent)
                    {
                        applyFilter();
                        onCompleted?.Invoke();
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
        Session session,
        Func<bool> hasSearchQuery,
        Action applyFilter)
    {
        for (var loadStart = 0; loadStart < items.Count; loadStart += WarmLoadBatchSize)
        {
            session.Token.ThrowIfCancellationRequested();
            var loadEnd = Math.Min(items.Count, loadStart + WarmLoadBatchSize);
            var cachedEntries = _metadataRepository.LoadMany(
                items.GetRange(loadStart, loadEnd - loadStart)
                    .Select(item => new MetadataLookup(item.Path, item.SourceFingerprint)),
                session.Token);

            for (var uiStart = loadStart; cachedEntries.Count > 0 && uiStart < loadEnd; uiStart += WarmUiBatchSize)
            {
                var batchStart = uiStart;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!session.IsCurrent)
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

                if (!session.IsCurrent)
                {
                    return false;
                }
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (session.IsCurrent && hasSearchQuery())
            {
                applyFilter();
            }
        }, DispatcherPriority.Background);
        return session.IsCurrent;
    }

    private async Task ScanAddedAsync(
        List<ImageItem> items,
        Session session,
        Func<bool> hasSearchQuery,
        Action applyFilter)
    {
        try
        {
            await ScanItemsAsync(
                items,
                session,
                skipCacheLookup: false,
                hasSearchQuery,
                applyFilter);

            if (hasSearchQuery())
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (session.IsCurrent)
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

    private async Task ScanItemsAsync(
        List<ImageItem> items,
        Session session,
        bool skipCacheLookup,
        Func<bool> hasSearchQuery,
        Action applyFilter)
    {
        var batchLock = new object();
        var pendingResults = new List<PendingMetadataResult>(ColdUiBatchSize);
        var lastRefreshTime = DateTime.UtcNow;

        await Parallel.ForEachAsync(items, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxDegreeOfParallelism,
            CancellationToken = session.Token
        }, async (item, cancellationToken) =>
        {
            try
            {
                if (item.HasLoadedMetadata)
                {
                    return;
                }

                var result = await item.GetMetadataLoadResultAsync(
                    skipCacheLookup,
                    persistResult: false,
                    cancellationToken);
                List<PendingMetadataResult>? claimedBatch = null;
                lock (batchLock)
                {
                    pendingResults.Add(new PendingMetadataResult(item, result));
                    if (pendingResults.Count >= ColdUiBatchSize)
                    {
                        claimedBatch = pendingResults;
                        pendingResults = new List<PendingMetadataResult>(ColdUiBatchSize);
                    }
                }

                if (claimedBatch is not null)
                {
                    await ApplyColdBatchAsync(claimedBatch, session);
                    if (hasSearchQuery() && TryClaimRefresh(ref lastRefreshTime, items))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (session.IsCurrent)
                            {
                                applyFilter();
                            }
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Metadata scanner worker failed for {item.Path}: {ex}");
            }
        });

        List<PendingMetadataResult> finalBatch;
        lock (batchLock)
        {
            finalBatch = pendingResults;
        }
        await ApplyColdBatchAsync(finalBatch, session);
    }

    private async Task ApplyColdBatchAsync(List<PendingMetadataResult> batch, Session session)
    {
        if (batch.Count == 0 || !session.IsCurrent)
        {
            return;
        }

        var entriesToSave = new List<MetadataIndexEntry>(batch.Count);
        foreach (var pending in batch)
        {
            if (pending.Result is { NeedsSave: true, Entry: { } entry })
            {
                entriesToSave.Add(entry);
            }
        }
        _metadataRepository.SaveMany(entriesToSave);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!session.IsCurrent)
            {
                return;
            }

            foreach (var pending in batch)
            {
                pending.Item.ApplyMetadataResult(pending.Result);
            }
        }, DispatcherPriority.Background);
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

    private readonly record struct PendingMetadataResult(ImageItem Item, MetadataLoadResult Result);
}
