using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

public sealed class ThumbnailLoadCoordinator
{
    private const int MaxActiveLoads = 4;
    private const int MaxVisibleLoads = 3;
    private const int MaxAheadLoads = 1;

    private readonly object _lock = new();
    private readonly LinkedList<ImageItem> _visibleQueue = new();
    private readonly LinkedList<ImageItem> _aheadQueue = new();
    private readonly Dictionary<ImageItem, QueuedThumbnail> _queuedItems = new();
    private readonly HashSet<ImageItem> _activeItems = [];
    private readonly HashSet<ImageItem> _retainedViewportItems = [];
    private int _activeVisibleLoads;
    private int _activeAheadLoads;
    private int _generation;
    private CancellationToken _currentToken;
    public Action? VisibleWorkDrained { get; set; }

    public void Clear()
    {
        lock (_lock)
        {
            _generation++;
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
            EnqueueLocked(item, ThumbnailQueueKind.Visible);
            ProcessQueuesLocked();
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
            ImageCache.Touch(item);
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

        while (!HasVisibleWorkLocked &&
               _aheadQueue.Count > 0 &&
               ActiveLoadCount < MaxActiveLoads &&
               _activeAheadLoads < MaxAheadLoads)
        {
            StartNextLocked(_aheadQueue, ThumbnailQueueKind.Ahead);
        }
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
            ImageCache.Touch(item);
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
        var generation = _generation;
        _ = RunLoadAsync(item, kind, token, generation);
    }

    private async Task RunLoadAsync(ImageItem item, ThumbnailQueueKind kind, CancellationToken token, int generation)
    {
        Action? visibleWorkDrained = null;
        try
        {
            await item.LoadThumbnailAsync(token, () => IsCurrentGeneration(generation));
        }
        finally
        {
            lock (_lock)
            {
                if (generation == _generation && _activeItems.Remove(item))
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

    private bool IsCurrentGeneration(int generation)
    {
        lock (_lock)
        {
            return generation == _generation;
        }
    }

    private enum ThumbnailQueueKind
    {
        Visible,
        Ahead
    }

    private readonly record struct QueuedThumbnail(
        ThumbnailQueueKind Kind,
        LinkedListNode<ImageItem> Node);
}
