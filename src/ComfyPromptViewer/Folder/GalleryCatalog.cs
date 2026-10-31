using System;
using System.Collections.Generic;

namespace ComfyPromptViewer;

// The open folder's images in sort order, by path, and as search columns. Each item carries its own path and
// fingerprint, so there is no separate entry that could disagree with it.
internal sealed class GalleryCatalog
{
    private readonly List<ImageItem> _items = [];
    private readonly Dictionary<string, ImageItem> _itemsByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SearchIndex _searchIndex = new();
    private int _loadedMetadataCount;

    public int Count => _items.Count;

    // Columnar searchable text. It is maintained by the same add/replace/remove/metadata calls that own
    // membership, so it cannot drift out of sync with the catalog. Take a snapshot to search it; never
    // mutate it from outside this class.
    public SearchSnapshot CreateSearchSnapshot() => _searchIndex.CreateSnapshot(_items);

    // Maintained by the same add/replace/remove calls that own membership, so scan progress stays exact
    // without an O(n) sweep per update.
    public int LoadedMetadataCount => _loadedMetadataCount;

    public IReadOnlyList<ImageItem> Items => _items;

    public void Add(ImageItem entry)
    {
        _items.Add(entry);
        _itemsByPath.Add(entry.Path, entry);
        _searchIndex.Add(entry);
        if (entry.HasLoadedMetadata)
        {
            _loadedMetadataCount++;
        }
    }

    public void Insert(int index, ImageItem entry)
    {
        _items.Insert(index, entry);
        _itemsByPath.Add(entry.Path, entry);
        _searchIndex.Add(entry);
        if (entry.HasLoadedMetadata)
        {
            _loadedMetadataCount++;
        }
    }

    // Membership is verified so a late event from an already-replaced item cannot inflate the counter or
    // write search text for an item the gallery no longer shows.
    public void MarkMetadataLoaded(ImageItem item)
    {
        if (_itemsByPath.TryGetValue(item.Path, out var entry) && ReferenceEquals(entry, item))
        {
            _loadedMetadataCount++;
            _searchIndex.SetMetadata(item);
        }
    }

    public bool TryGet(string path, out ImageItem entry)
    {
        return _itemsByPath.TryGetValue(path, out entry!);
    }

    // Assumes the catalog is already sorted by comparison. Binary-searches the old entry and re-inserts
    // the replacement at its own sorted position, so small watcher batches avoid a full re-sort.
    public bool TryReplaceSorted(
        ImageItem replacement,
        Comparison<ImageItem> comparison,
        out ImageItem previous)
    {
        if (!_itemsByPath.TryGetValue(replacement.Path, out previous!))
        {
            return false;
        }

        var index = FindEntryIndex(previous, comparison);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        _items.Insert(FindSortedInsertIndex(replacement, comparison), replacement);
        _itemsByPath[replacement.Path] = replacement;
        ApplyReplacement(previous, replacement);
        return true;
    }

    // Single pass. Order is not preserved, so callers must re-sort afterwards.
    public void ReplaceMany(IReadOnlyDictionary<string, ImageItem> replacements)
    {
        if (replacements.Count == 0)
        {
            return;
        }

        for (var index = 0; index < _items.Count; index++)
        {
            var previous = _items[index];
            if (!replacements.TryGetValue(previous.Path, out var replacement))
            {
                continue;
            }

            _items[index] = replacement;
            _itemsByPath[replacement.Path] = replacement;
            ApplyReplacement(previous, replacement);
        }
    }

    public List<ImageItem> RemovePaths(IReadOnlySet<string> paths)
    {
        var removed = new List<ImageItem>();
        for (var index = _items.Count - 1; index >= 0; index--)
        {
            var entry = _items[index];
            if (!paths.Contains(entry.Path))
            {
                continue;
            }

            _items.RemoveAt(index);
            _itemsByPath.Remove(entry.Path);
            _searchIndex.Remove(entry);
            if (entry.HasLoadedMetadata)
            {
                _loadedMetadataCount--;
            }
            removed.Add(entry);
        }

        return removed;
    }

    private void ApplyReplacement(ImageItem previous, ImageItem replacement)
    {
        _searchIndex.Remove(previous);
        _searchIndex.Add(replacement);

        if (previous.HasLoadedMetadata)
        {
            _loadedMetadataCount--;
        }

        if (replacement.HasLoadedMetadata)
        {
            _loadedMetadataCount++;
        }
    }

    // Comparisons can tie for distinct paths under culture-aware name sorting, so the equal-comparison
    // run is scanned before giving up.
    private int FindEntryIndex(ImageItem target, Comparison<ImageItem> comparison)
    {
        var low = 0;
        var high = _items.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var order = comparison(_items[middle], target);
            if (order == 0)
            {
                return ScanEqualRunForEntry(middle, target, comparison);
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return _items.IndexOf(target);
    }

    private int ScanEqualRunForEntry(int middle, ImageItem target, Comparison<ImageItem> comparison)
    {
        for (var index = middle; index >= 0 && comparison(_items[index], target) == 0; index--)
        {
            if (ReferenceEquals(_items[index], target))
            {
                return index;
            }
        }

        for (var index = middle + 1; index < _items.Count && comparison(_items[index], target) == 0; index++)
        {
            if (ReferenceEquals(_items[index], target))
            {
                return index;
            }
        }

        return _items.IndexOf(target);
    }

    // O(log n) for a sorted catalog, so the viewport can report its position on every scroll pass.
    public int IndexOf(ImageItem item, Comparison<ImageItem> comparison)
    {
        return _itemsByPath.TryGetValue(item.Path, out var entry) && ReferenceEquals(entry, item)
            ? FindEntryIndex(entry, comparison)
            : -1;
    }

    public void Sort(Comparison<ImageItem> comparison)
    {
        _items.Sort(comparison);
    }

    // Takes an order computed elsewhere, which must hold exactly the catalog's current items. Returns false,
    // changing nothing, when membership moved on in the meantime.
    public bool TryReorder(ImageItem[] sorted)
    {
        if (sorted.Length != _items.Count)
        {
            return false;
        }

        foreach (var item in sorted)
        {
            if (!_itemsByPath.TryGetValue(item.Path, out var current) || !ReferenceEquals(current, item))
            {
                return false;
            }
        }

        _items.Clear();
        _items.AddRange(sorted);
        return true;
    }

    public int FindSortedInsertIndex(ImageItem entry, Comparison<ImageItem> comparison)
    {
        var low = 0;
        var high = _items.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (comparison(_items[middle], entry) <= 0)
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

    public void Clear()
    {
        _items.Clear();
        _itemsByPath.Clear();
        _searchIndex.Clear();
        _loadedMetadataCount = 0;
    }
}
