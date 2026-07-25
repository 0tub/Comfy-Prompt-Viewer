using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

internal sealed class MetadataScanCoordinator
{
    private const int MaxDegreeOfParallelism = 2;
    private const int WarmUiBatchSize = 64;
    private const int WarmLoadBatchSize = 256;
    private const int ColdUiBatchSize = 64;
    private static readonly TimeSpan SearchRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly object _stateLock = new();
    private readonly MetadataRepository _metadataRepository;
    private readonly IUiScheduler _uiScheduler;
    private CancellationTokenSource? _cancellation;
    private int _generation;

    public MetadataScanCoordinator(
        MetadataRepository metadataRepository,
        IUiScheduler uiScheduler)
    {
        _metadataRepository = metadataRepository;
        _uiScheduler = uiScheduler;
    }

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

    public void Start(IReadOnlyList<ImageItem> items, Func<bool> hasSearchQuery, Action applyFilter)
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

            if (IsCurrent(session))
            {
                await _uiScheduler.InvokeAsync(() =>
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
            var cachedEntries = _metadataRepository.LoadMany(
                items.GetRange(loadStart, loadEnd - loadStart)
                    .Select(item => new MetadataLookup(item.Path, item.SourceFingerprint)),
                session.Token);

            for (var uiStart = loadStart; cachedEntries.Count > 0 && uiStart < loadEnd; uiStart += WarmUiBatchSize)
            {
                var batchStart = uiStart;
                await _uiScheduler.InvokeBackgroundAsync(() =>
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
                });

                if (!IsCurrent(session))
                {
                    return false;
                }
            }
        }

        await _uiScheduler.InvokeBackgroundAsync(() =>
        {
            if (IsCurrent(session) && hasSearchQuery())
            {
                applyFilter();
            }
        });
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
            await ScanItemsAsync(
                items,
                session,
                skipCacheLookup: false,
                hasSearchQuery,
                applyFilter);

            if (hasSearchQuery())
            {
                _uiScheduler.Post(() =>
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

    private async Task ScanItemsAsync(
        List<ImageItem> items,
        ScanSession session,
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
                        _uiScheduler.Post(() =>
                        {
                            if (IsCurrent(session))
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

    private async Task ApplyColdBatchAsync(List<PendingMetadataResult> batch, ScanSession session)
    {
        if (batch.Count == 0 || !IsCurrent(session))
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

        await _uiScheduler.InvokeBackgroundAsync(() =>
        {
            if (!IsCurrent(session))
            {
                return;
            }

            foreach (var pending in batch)
            {
                pending.Item.ApplyMetadataResult(pending.Result);
            }
        });
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
    private readonly record struct PendingMetadataResult(ImageItem Item, MetadataLoadResult Result);
}
