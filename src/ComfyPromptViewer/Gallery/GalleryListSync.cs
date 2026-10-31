using System;
using System.Collections.Generic;
using Avalonia.Collections;

namespace ComfyPromptViewer;

// Keeps the grid's list in step with a filtered result. Small insert and delete batches are applied one at a
// time so realized cards stay put; anything else is one reset. The scratch collections are reused across
// calls so a one-file watcher batch does not allocate catalog-sized sets, and are cleared after every use:
// holding a folder's items here would outlive the folder itself.
internal sealed class GalleryListSync
{
    public const int MaxIncrementalChanges = 32;

    // Reused across syncs so a one-file watcher batch does not allocate two catalog-sized collections.
    // Cleared after every use: holding a folder's items here would outlive the folder itself.
    private readonly HashSet<ImageItem> _targetSet = [];
    private readonly Dictionary<ImageItem, int> _currentIndexes = [];
    private readonly List<GalleryInsertion> _insertions = [];

    internal readonly record struct GalleryInsertion(int Index, ImageItem Item);

    private bool TrySynchronizeInsertions(AvaloniaList<ImageItem> items, IReadOnlyList<ImageItem> filtered)
    {
        var insertions = _insertions;
        if (!TryFindGalleryInsertions(items, filtered, MaxIncrementalChanges, insertions))
        {
            insertions.Clear();
            return false;
        }

        // Ascending order, so every earlier position is already final when the next insert lands.
        foreach (var insertion in insertions)
        {
            items.Insert(insertion.Index, insertion.Item);
        }

        insertions.Clear();
        return true;
    }

    // A watcher batch is normally a handful of inserts into an otherwise unchanged list. Two cursors settle
    // that in one comparison pass with no allocation, which matters because this runs per added file and
    // the set-based fallback is O(catalog) in both time and bytes. Returns false for anything else -
    // removals, reorders, or more insertions than the caller is willing to apply one at a time.
    //
    // On true, applying the reported insertions in order turns currentItems into targetItems exactly, so
    // there is no separate duplicate check to keep in sync: a target the walk cannot reproduce is rejected.
    internal static bool TryFindGalleryInsertions(
        IReadOnlyList<ImageItem> currentItems,
        IReadOnlyList<ImageItem> targetItems,
        int maximumInsertions,
        List<GalleryInsertion> insertions)
    {
        insertions.Clear();
        var insertionCount = targetItems.Count - currentItems.Count;
        if (insertionCount <= 0 || insertionCount > maximumInsertions)
        {
            return false;
        }

        var currentIndex = 0;
        for (var index = 0; index < targetItems.Count; index++)
        {
            var item = targetItems[index];
            if (currentIndex < currentItems.Count && ReferenceEquals(currentItems[currentIndex], item))
            {
                currentIndex++;
                continue;
            }

            if (insertions.Count == insertionCount)
            {
                // Something other than an insertion changed; let the general path decide what to do.
                return false;
            }

            insertions.Add(new GalleryInsertion(index, item));
        }

        // Postcondition, not a filter: the loop above already rejects anything that would leave current
        // items unconsumed. It states the property the caller relies on rather than trusting the arithmetic.
        return currentIndex == currentItems.Count;
    }

    // Brings the grid's list to `filtered` with as few inserts and removes as it can, so realized cards stay
    // put. Larger or reordering changes use one reset.
    public void Synchronize(AvaloniaList<ImageItem> items, IReadOnlyList<ImageItem> filtered)
    {
        if (TrySynchronizeInsertions(items, filtered))
        {
            return;
        }

        if (!CanSynchronizeGalleryItemsIncrementally(
                items,
                filtered,
                MaxIncrementalChanges,
                _targetSet,
                _currentIndexes))
        {
            _targetSet.Clear();
            _currentIndexes.Clear();
            items.Clear();
            items.AddRange(filtered);
            return;
        }

        // The check above already filled this with the target sequence.
        var targetItems = _targetSet;
        for (var index = 0; index < filtered.Count;)
        {
            var item = filtered[index];
            if (index == items.Count)
            {
                items.Insert(index, item);
                index++;
                continue;
            }

            if (ReferenceEquals(items[index], item))
            {
                index++;
                continue;
            }

            if (!targetItems.Contains(items[index]))
            {
                items.RemoveAt(index);
                continue;
            }

            items.Insert(index, item);
            index++;
        }

        while (items.Count > filtered.Count)
        {
            items.RemoveAt(items.Count - 1);
        }

        _targetSet.Clear();
        _currentIndexes.Clear();
    }

    internal static bool CanSynchronizeGalleryItemsIncrementally(
        IReadOnlyList<ImageItem> currentItems,
        IReadOnlyList<ImageItem> targetItems,
        int maximumChanges)
    {
        return CanSynchronizeGalleryItemsIncrementally(
            currentItems,
            targetItems,
            maximumChanges,
            [],
            []);
    }

    // The two collections are scratch space, not state: callers pass reusable instances so this does not
    // allocate two catalog-sized collections per watcher batch. On a true return, targetSet holds the
    // target sequence and the caller may read it.
    internal static bool CanSynchronizeGalleryItemsIncrementally(
        IReadOnlyList<ImageItem> currentItems,
        IReadOnlyList<ImageItem> targetItems,
        int maximumChanges,
        HashSet<ImageItem> targetSet,
        Dictionary<ImageItem, int> currentIndexes)
    {
        targetSet.Clear();
        currentIndexes.Clear();

        if (maximumChanges < 0)
        {
            return false;
        }

        foreach (var item in targetItems)
        {
            targetSet.Add(item);
        }

        if (targetSet.Count != targetItems.Count)
        {
            return false;
        }

        var changeCount = 0;
        for (var index = 0; index < currentItems.Count; index++)
        {
            var item = currentItems[index];
            if (!currentIndexes.TryAdd(item, index))
            {
                return false;
            }

            if (!targetSet.Contains(item) && ++changeCount > maximumChanges)
            {
                return false;
            }
        }

        var lastCurrentIndex = -1;
        foreach (var item in targetItems)
        {
            if (currentIndexes.TryGetValue(item, out var currentIndex))
            {
                if (currentIndex < lastCurrentIndex)
                {
                    return false;
                }

                lastCurrentIndex = currentIndex;
            }
            else if (++changeCount > maximumChanges)
            {
                return false;
            }
        }

        // Small insert/delete batches keep realized cards stable. Larger changes use one reset.
        return true;
    }

    internal static double CalculateAnchoredGalleryOffset(
        int oldIndex,
        int newIndex,
        int columns,
        double itemExtent,
        double oldOffset,
        double maxOffset)
    {
        columns = Math.Max(1, columns);
        itemExtent = Math.Max(1, itemExtent);
        if (newIndex < 0)
        {
            return Math.Clamp(oldOffset, 0, maxOffset);
        }

        var offsetWithinRow = oldOffset - ((oldIndex / columns) * itemExtent);
        return Math.Clamp(((newIndex / columns) * itemExtent) + offsetWithinRow, 0, maxOffset);
    }

    internal static int FindSortedInsertIndex<T>(IReadOnlyList<T> sortedItems, T item, Comparison<T> comparison)
    {
        var low = 0;
        var high = sortedItems.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (comparison(sortedItems[middle], item) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
