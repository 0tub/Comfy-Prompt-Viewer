using System;
using System.Collections.Generic;

namespace ComfyPromptViewer;

// The multi-selection set and the Shift+click anchor. It keeps each item's IsMarkedSelected flag in step
// with membership, which is the invariant that used to be spread across MainWindow.
//
// The active item (the sidebar target) is deliberately not here: choosing it drives the sidebar, preview,
// and metadata loads, so MainWindow owns it and asks this class which item to fall back to.
internal sealed class GallerySelection
{
    private readonly HashSet<ImageItem> _items = [];

    public ImageItem? Anchor { get; private set; }
    public int Count => _items.Count;

    public bool Contains(ImageItem item) => _items.Contains(item);

    // A plain click: exactly this item, which also becomes the range anchor. Null clears everything.
    public void SelectOnly(ImageItem? item)
    {
        ClearItems();
        if (item is not null)
        {
            Add(item);
        }

        Anchor = item;
    }

    // Adds without moving the anchor.
    public void Add(ImageItem item)
    {
        if (_items.Add(item))
        {
            item.IsMarkedSelected = true;
        }
    }

    // Ctrl+click. The item becomes the anchor either way. Returns whether it is now selected.
    public bool Toggle(ImageItem item)
    {
        Anchor = item;
        if (RemoveItem(item))
        {
            return false;
        }

        Add(item);
        return true;
    }

    // Shift+click: everything between the anchor and item in display order. The anchor stays put so a
    // second Shift+click re-extends from the same point. Returns false, changing nothing, when either end
    // is not in the list; the caller then treats it as a plain click.
    public bool SelectRange(IReadOnlyList<ImageItem> orderedItems, ImageItem item)
    {
        var anchorIndex = Anchor is null ? -1 : IndexOf(orderedItems, Anchor);
        var itemIndex = IndexOf(orderedItems, item);
        if (anchorIndex < 0 || itemIndex < 0)
        {
            return false;
        }

        ClearItems();
        var start = Math.Min(anchorIndex, itemIndex);
        var end = Math.Max(anchorIndex, itemIndex);
        for (var index = start; index <= end; index++)
        {
            Add(orderedItems[index]);
        }

        return true;
    }

    // The item left the gallery; it can no longer anchor a range either.
    public void Remove(ImageItem item)
    {
        RemoveItem(item);
        if (Anchor == item)
        {
            Anchor = null;
        }
    }

    // A watcher replacement stands in for the old item in both roles.
    public void Replace(ImageItem previous, ImageItem replacement)
    {
        if (RemoveItem(previous))
        {
            Add(replacement);
        }

        if (Anchor == previous)
        {
            Anchor = replacement;
        }
    }

    public void Clear()
    {
        ClearItems();
        Anchor = null;
    }

    public ImageItem? FirstIn(IEnumerable<ImageItem> orderedItems)
    {
        foreach (var item in orderedItems)
        {
            if (_items.Contains(item))
            {
                return item;
            }
        }

        return null;
    }

    // Selected items in the caller's order, so bulk actions follow the gallery rather than hash order.
    public ImageItem[] InOrder(IEnumerable<ImageItem> orderedItems)
    {
        var result = new List<ImageItem>(_items.Count);
        foreach (var item in orderedItems)
        {
            if (_items.Contains(item))
            {
                result.Add(item);
            }
        }

        return [.. result];
    }

    public bool Any(Func<ImageItem, bool> predicate)
    {
        foreach (var item in _items)
        {
            if (predicate(item))
            {
                return true;
            }
        }

        return false;
    }

    private bool RemoveItem(ImageItem item)
    {
        if (!_items.Remove(item))
        {
            return false;
        }

        item.IsMarkedSelected = false;
        return true;
    }

    private void ClearItems()
    {
        foreach (var item in _items)
        {
            item.IsMarkedSelected = false;
        }

        _items.Clear();
    }

    private static int IndexOf(IReadOnlyList<ImageItem> items, ImageItem item)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], item))
            {
                return index;
            }
        }

        return -1;
    }
}
