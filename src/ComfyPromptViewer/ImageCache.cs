using System.Collections.Generic;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public static class ImageCache
{
    private static readonly LinkedList<ImageItem> _lruList = new();
    private static readonly object _lock = new();
    private const int MaxCapacity = 128;

    public static void Touch(ImageItem item)
    {
        lock (_lock)
        {
            if (item.CacheNode != null)
            {
                if (item.CacheNode.List == _lruList)
                {
                    _lruList.Remove(item.CacheNode);
                }
                item.CacheNode = null;
            }

            item.CacheNode = _lruList.AddLast(item);

            var attemptsRemaining = _lruList.Count;
            while (_lruList.Count > MaxCapacity && attemptsRemaining > 0)
            {
                attemptsRemaining--;
                var firstNode = _lruList.First;
                if (firstNode is null)
                {
                    break;
                }

                _lruList.Remove(firstNode);
                var evicted = firstNode.Value;
                evicted.CacheNode = null;

                if (evicted.IsSelected || evicted.IsRealized)
                {
                    evicted.CacheNode = _lruList.AddLast(evicted);
                    continue;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    evicted.ReleasePreview(skipIfCached: true);
                });
            }
        }
    }

    public static void Remove(ImageItem item)
    {
        lock (_lock)
        {
            if (item.CacheNode != null)
            {
                if (item.CacheNode.List == _lruList)
                {
                    _lruList.Remove(item.CacheNode);
                }
                item.CacheNode = null;
            }
        }
    }

    public static void ClearAndReleaseAll()
    {
        List<ImageItem> itemsToRelease;
        lock (_lock)
        {
            itemsToRelease = new List<ImageItem>(_lruList.Count);
            foreach (var item in _lruList)
            {
                itemsToRelease.Add(item);
                item.CacheNode = null;
            }
            _lruList.Clear();
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in itemsToRelease)
            {
                item.ReleasePreview();
            }
        });
    }
}
