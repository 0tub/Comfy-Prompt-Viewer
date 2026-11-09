using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// Every metadata load for the open folder: the initial scan, files the watcher adds, and the selected image.
// They can ask for the same item at once, so in-flight loads are shared per item here. Results are applied
// to items on the UI thread.
internal sealed class MetadataScanCoordinator
{
    // Four workers measured about a sixth faster than two on a cold 4,900-file folder; six was not reliably
    // faster than four.
    private static readonly int MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
    private const int WarmUiBatchSize = 64;
    private const int WarmLoadBatchSize = 256;
    private const int ColdUiBatchSize = 64;
    private static readonly TimeSpan SearchRefreshInterval = TimeSpan.FromSeconds(1);
    // Scanner staleness is one SessionGate; see Staleness.cs. Do not add a second counter here.
    private readonly SessionGate _gate = new();
    private readonly MetadataRepository _metadataRepository;
    private readonly ImageMetadataService _metadataService;
    // Weak, so a load for an item from a folder that has since closed goes away with the item.
    private readonly ConditionalWeakTable<ImageItem, Task<MetadataLoadResult>> _inFlight = new();

    public MetadataScanCoordinator(MetadataRepository metadataRepository)
    {
        _metadataRepository = metadataRepository;
        _metadataService = new ImageMetadataService(metadataRepository);
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
        // Copied here, on the caller's thread: `items` is the live catalog view, which the UI keeps mutating.
        var snapshot = items.ToList();
        DebugLog.Observe(Task.Run(
            () => ScanInitialAsync(snapshot, session, hasSearchQuery, applyFilter, onCompleted)),
            "Initial metadata scanner");
    }

    public void ScanAdded(List<ImageItem> items, Func<bool> hasSearchQuery, Action applyFilter)
    {
        var session = _gate.Snapshot();
        DebugLog.Observe(Task.Run(
            () => ScanAddedAsync(items, session, hasSearchQuery, applyFilter)),
            "Watcher metadata scanner");
    }

    // The selected image, ahead of the scan: cache first, persisted, applied when it lands.
    public async Task EnsureLoadedAsync(ImageItem item, CancellationToken token)
    {
        if (item.HasLoadedMetadata)
        {
            return;
        }

        try
        {
            var result = await GetResultAsync(item, skipCacheLookup: false, persistResult: true, token);
            if (result.NeedsSave)
            {
                await Task.Run(() => _metadataService.Save(result), token);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    Apply(item, result);
                }
            });
        }
        catch (OperationCanceledException)
        {
            item.MarkMetadataCancelled();
        }
    }

    // Shares one load per item between the scanner and the selection. A cancelled or failed load is not
    // shared; the next caller starts a fresh one.
    private Task<MetadataLoadResult> GetResultAsync(
        ImageItem item,
        bool skipCacheLookup,
        bool persistResult,
        CancellationToken token)
    {
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(item, out var load) || load.IsCanceled || load.IsFaulted)
            {
                load = _metadataService.LoadAsync(item.Path, item.SourceFingerprint, skipCacheLookup, persistResult, token);
                _inFlight.AddOrUpdate(item, load);
            }

            return load.WaitAsync(token);
        }
    }

    // UI thread. The finished load holds a second copy of the entry for as long as the item lives, and
    // nothing asks again once the item has its metadata, so it is dropped here.
    private void Apply(ImageItem item, MetadataLoadResult result)
    {
        item.ApplyMetadataResult(result);
        Forget(item);
    }

    private void Apply(ImageItem item, ImageMetadata metadata)
    {
        item.ApplyMetadata(metadata);
        Forget(item);
    }

    private void Forget(ImageItem item)
    {
        lock (_inFlight)
        {
            _inFlight.Remove(item);
        }
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
                            Apply(item, entry.Metadata);
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

                var result = await GetResultAsync(
                    item,
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
                Apply(pending.Item, pending.Result);
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
