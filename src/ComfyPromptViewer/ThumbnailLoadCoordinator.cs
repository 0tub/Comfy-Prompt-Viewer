using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

public sealed class ThumbnailLoadCoordinator
{
    // Bounded: past a handful of workers the disk and UI thread are the limit, not cores.
    private static readonly int MaxActiveLoads = Math.Clamp(Environment.ProcessorCount / 2, 4, 8);
    private static readonly int MaxVisibleLoads = Math.Clamp(Environment.ProcessorCount / 2, 3, 6);

    // A cold ahead load decodes the full-resolution source, so it still yields entirely to the viewport.
    private const int MaxColdAheadLoads = 1;

    // A warm one is a small JPEG out of the pack, cheap enough to overlap visible work.
    private static readonly int MaxWarmAheadLoads = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);

    private readonly object _lock = new();
    private readonly DecodedImageCache _decodedImageCache;
    private readonly LinkedList<ImageItem> _visibleQueue = new();
    private readonly LinkedList<ImageItem> _aheadQueue = new();
    private readonly Dictionary<ImageItem, QueuedThumbnail> _queuedItems = new();
    private readonly HashSet<ImageItem> _activeItems = [];
    private readonly HashSet<ImageItem> _retainedViewportItems = [];
    private int _activeVisibleLoads;
    private int _activeAheadLoads;
    // Invalidating this drops every in-flight decode.
    private readonly GenerationGate _loadGate = new();
    private CancellationToken _currentToken;
    public Action? VisibleWorkDrained { get; set; }

    internal ThumbnailLoadCoordinator(DecodedImageCache decodedImageCache)
    {
        _decodedImageCache = decodedImageCache;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _loadGate.Invalidate();
            _visibleQueue.Clear();
            _aheadQueue.Clear();
            _queuedItems.Clear();
            _activeItems.Clear();
            _retainedViewportItems.Clear();
            _activeVisibleLoads = 0;
            _activeAheadLoads = 0;
        }
    }

    public void ScheduleViewport(
        IReadOnlyList<ImageItem> visibleItems,
        IReadOnlyList<ImageItem> aheadItems,
        CancellationToken token)
    {
        lock (_lock)
        {
            _currentToken = token;
            if (token.IsCancellationRequested)
            {
                _retainedViewportItems.Clear();
                return;
            }

            _retainedViewportItems.Clear();
            foreach (var item in visibleItems)
            {
                _retainedViewportItems.Add(item);
            }
            foreach (var item in aheadItems)
            {
                _retainedViewportItems.Add(item);
            }

            RemoveQueuedItemsNotIn(_visibleQueue, _retainedViewportItems);
            RemoveQueuedItemsNotIn(_aheadQueue, _retainedViewportItems);

            foreach (var item in visibleItems)
            {
                EnqueueLocked(item, ThumbnailQueueKind.Visible);
            }

            foreach (var item in aheadItems)
            {
                EnqueueLocked(item, ThumbnailQueueKind.Ahead);
            }

            ProcessQueuesLocked();
        }
    }

    public void EnqueueVisible(ImageItem item, CancellationToken token)
    {
        lock (_lock)
        {
            _currentToken = token;
            // A selection outside the last scheduled window must not read as abandoned.
            _retainedViewportItems.Add(item);
            EnqueueLocked(item, ThumbnailQueueKind.Visible);
            ProcessQueuesLocked();
        }
    }

    // Empty until the first viewport schedule, which must not abandon everything.
    internal bool IsRetained(ImageItem item)
    {
        lock (_lock)
        {
            return _retainedViewportItems.Count == 0 || _retainedViewportItems.Contains(item);
        }
    }

    public bool HasVisibleWork
    {
        get
        {
            lock (_lock)
            {
                return _visibleQueue.Count > 0 || _activeVisibleLoads > 0;
            }
        }
    }

    private void EnqueueLocked(ImageItem item, ThumbnailQueueKind kind)
    {
        if (item.Preview is not null)
        {
            _decodedImageCache.Touch(item);
            return;
        }

        if (_activeItems.Contains(item))
        {
            return;
        }

        if (_queuedItems.TryGetValue(item, out var queued))
        {
            if (queued.Kind == ThumbnailQueueKind.Ahead && kind == ThumbnailQueueKind.Visible)
            {
                _aheadQueue.Remove(queued.Node);
                var node = _visibleQueue.AddLast(item);
                _queuedItems[item] = new QueuedThumbnail(ThumbnailQueueKind.Visible, node);
            }

            return;
        }

        LinkedListNode<ImageItem> newNode;
        if (kind == ThumbnailQueueKind.Visible)
        {
            newNode = _visibleQueue.AddLast(item);
        }
        else
        {
            newNode = _aheadQueue.AddLast(item);
        }

        _queuedItems[item] = new QueuedThumbnail(kind, newNode);
    }

    private void ProcessQueuesLocked()
    {
        while (_visibleQueue.Count > 0 &&
               ActiveLoadCount < MaxActiveLoads &&
               _activeVisibleLoads < MaxVisibleLoads)
        {
            StartNextLocked(_visibleQueue, ThumbnailQueueKind.Visible);
        }

        // Per item at start time: prewarm fills the pack underneath, so a queued cold item may now be warm.
        while (_aheadQueue.First is { Value: var nextAhead } &&
               CanStartAheadLoad(
                   nextAhead.HasCachedThumbnail(),
                   HasVisibleWorkLocked,
                   ActiveLoadCount,
                   _activeAheadLoads))
        {
            StartNextLocked(_aheadQueue, ThumbnailQueueKind.Ahead);
        }
    }

    // Cold pays a full-resolution source decode and must never compete with the viewport; warm is cheap
    // enough to run alongside it, which is what stops tiles arriving blank in a cached folder.
    internal static bool CanStartAheadLoad(
        bool isWarm,
        bool hasVisibleWork,
        int activeLoadCount,
        int activeAheadLoads)
    {
        if (activeLoadCount >= MaxActiveLoads)
        {
            return false;
        }

        return isWarm
            ? activeAheadLoads < MaxWarmAheadLoads
            : !hasVisibleWork && activeAheadLoads < MaxColdAheadLoads;
    }

    private void StartNextLocked(LinkedList<ImageItem> queue, ThumbnailQueueKind kind)
    {
        var node = queue.First;
        if (node is null)
        {
            return;
        }

        var item = node.Value;
        queue.RemoveFirst();
        _queuedItems.Remove(item);

        if (item.Preview is not null)
        {
            _decodedImageCache.Touch(item);
            return;
        }

        _activeItems.Add(item);
        if (kind == ThumbnailQueueKind.Visible)
        {
            _activeVisibleLoads++;
        }
        else
        {
            _activeAheadLoads++;
        }

        var token = _currentToken;
        DebugLog.Observe(
            RunLoadAsync(item, kind, token, _loadGate.Current),
            $"Thumbnail load for {item.Path}");
    }

    private async Task RunLoadAsync(
        ImageItem item,
        ThumbnailQueueKind kind,
        CancellationToken token,
        Generation load)
    {
        Action? visibleWorkDrained = null;
        try
        {
            await item.LoadThumbnailAsync(token, () => load.IsCurrent, () => IsRetained(item));
        }
        finally
        {
            lock (_lock)
            {
                if (load.IsCurrent && _activeItems.Remove(item))
                {
                    if (kind == ThumbnailQueueKind.Visible)
                    {
                        _activeVisibleLoads--;
                    }
                    else
                    {
                        _activeAheadLoads--;
                    }

                    ProcessQueuesLocked();
                    if (!HasVisibleWorkLocked)
                    {
                        visibleWorkDrained = VisibleWorkDrained;
                    }
                }
            }
        }

        visibleWorkDrained?.Invoke();
    }

    private void RemoveQueuedItemsNotIn(LinkedList<ImageItem> queue, HashSet<ImageItem> retainedItems)
    {
        var node = queue.First;
        while (node is not null)
        {
            var next = node.Next;
            if (!retainedItems.Contains(node.Value))
            {
                _queuedItems.Remove(node.Value);
                queue.Remove(node);
            }
            node = next;
        }
    }

    private int ActiveLoadCount => _activeVisibleLoads + _activeAheadLoads;
    private bool HasVisibleWorkLocked => _visibleQueue.Count > 0 || _activeVisibleLoads > 0;

    private enum ThumbnailQueueKind
    {
        Visible,
        Ahead
    }

    private readonly record struct QueuedThumbnail(
        ThumbnailQueueKind Kind,
        LinkedListNode<ImageItem> Node);
}
