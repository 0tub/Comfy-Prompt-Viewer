using System;
using System.Collections;
using System.Collections.Generic;

namespace ComfyPromptViewer;

internal sealed class GalleryCatalog
{
    private readonly List<GalleryEntry> _entries = [];
    private readonly Dictionary<string, GalleryEntry> _entriesByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ItemView _items;
    private readonly SearchIndex _searchIndex = new();
    private int _loadedMetadataCount;

    public GalleryCatalog()
    {
        _items = new ItemView(_entries);
    }

    public int Count => _entries.Count;

    // Columnar searchable text. It is maintained by the same add/replace/remove/metadata calls that own
    // membership, so it cannot drift out of sync with the catalog. Take a snapshot to search it; never
    // mutate it from outside this class.
    public SearchSnapshot CreateSearchSnapshot() => _searchIndex.CreateSnapshot(_items);

    // Maintained by the same add/replace/remove calls that own membership, so scan progress stays exact
    // without an O(n) sweep per update.
    public int LoadedMetadataCount => _loadedMetadataCount;

    public IReadOnlyList<GalleryEntry> Entries => _entries;
    public IReadOnlyList<ImageItem> Items => _items;

    public void Add(GalleryEntry entry)
    {
        _entries.Add(entry);
        _entriesByPath.Add(entry.Path, entry);
        _searchIndex.Add(entry.Item);
        if (entry.Item.HasLoadedMetadata)
        {
            _loadedMetadataCount++;
        }
    }

    public void Insert(int index, GalleryEntry entry)
    {
        _entries.Insert(index, entry);
        _entriesByPath.Add(entry.Path, entry);
        _searchIndex.Add(entry.Item);
        if (entry.Item.HasLoadedMetadata)
        {
            _loadedMetadataCount++;
        }
    }

    // Membership is verified so a late event from an already-replaced item cannot inflate the counter or
    // write search text for an item the gallery no longer shows.
    public void MarkMetadataLoaded(ImageItem item)
    {
        if (_entriesByPath.TryGetValue(item.Path, out var entry) && ReferenceEquals(entry.Item, item))
        {
            _loadedMetadataCount++;
            _searchIndex.SetMetadata(item);
        }
    }

    public bool TryGet(string path, out GalleryEntry entry)
    {
        return _entriesByPath.TryGetValue(path, out entry!);
    }

    // Assumes the catalog is already sorted by comparison. Binary-searches the old entry and re-inserts
    // the replacement at its own sorted position, so small watcher batches avoid a full re-sort.
    public bool TryReplaceSorted(
        GalleryEntry replacement,
        Comparison<GalleryEntry> comparison,
        out GalleryEntry previous)
    {
        if (!_entriesByPath.TryGetValue(replacement.Path, out previous!))
        {
            return false;
        }

        var index = FindEntryIndex(previous, comparison);
        if (index < 0)
        {
            return false;
        }

        _entries.RemoveAt(index);
        _entries.Insert(FindSortedInsertIndex(replacement, comparison), replacement);
        _entriesByPath[replacement.Path] = replacement;
        ApplyReplacement(previous, replacement);
        return true;
    }

    // Single pass. Order is not preserved, so callers must re-sort afterwards.
    public void ReplaceMany(IReadOnlyDictionary<string, GalleryEntry> replacements)
    {
        if (replacements.Count == 0)
        {
            return;
        }

        for (var index = 0; index < _entries.Count; index++)
        {
            var previous = _entries[index];
            if (!replacements.TryGetValue(previous.Path, out var replacement))
            {
                continue;
            }

            _entries[index] = replacement;
            _entriesByPath[replacement.Path] = replacement;
            ApplyReplacement(previous, replacement);
        }
    }

    public List<GalleryEntry> RemovePaths(IReadOnlySet<string> paths)
    {
        var removed = new List<GalleryEntry>();
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            var entry = _entries[index];
            if (!paths.Contains(entry.Path))
            {
                continue;
            }

            _entries.RemoveAt(index);
            _entriesByPath.Remove(entry.Path);
            _searchIndex.Remove(entry.Item);
            if (entry.Item.HasLoadedMetadata)
            {
                _loadedMetadataCount--;
            }
            removed.Add(entry);
        }

        return removed;
    }

    private void ApplyReplacement(GalleryEntry previous, GalleryEntry replacement)
    {
        _searchIndex.Remove(previous.Item);
        _searchIndex.Add(replacement.Item);

        if (previous.Item.HasLoadedMetadata)
        {
            _loadedMetadataCount--;
        }

        if (replacement.Item.HasLoadedMetadata)
        {
            _loadedMetadataCount++;
        }
    }

    // Comparisons can tie for distinct paths under culture-aware name sorting, so the equal-comparison
    // run is scanned before giving up.
    private int FindEntryIndex(GalleryEntry target, Comparison<GalleryEntry> comparison)
    {
        var low = 0;
        var high = _entries.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var order = comparison(_entries[middle], target);
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

        return _entries.IndexOf(target);
    }

    private int ScanEqualRunForEntry(int middle, GalleryEntry target, Comparison<GalleryEntry> comparison)
    {
        for (var index = middle; index >= 0 && comparison(_entries[index], target) == 0; index--)
        {
            if (ReferenceEquals(_entries[index], target))
            {
                return index;
            }
        }

        for (var index = middle + 1; index < _entries.Count && comparison(_entries[index], target) == 0; index++)
        {
            if (ReferenceEquals(_entries[index], target))
            {
                return index;
            }
        }

        return _entries.IndexOf(target);
    }

    public void Sort(Comparison<GalleryEntry> comparison)
    {
        _entries.Sort(comparison);
    }

    public int FindSortedInsertIndex(GalleryEntry entry, Comparison<GalleryEntry> comparison)
    {
        var low = 0;
        var high = _entries.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (comparison(_entries[middle], entry) <= 0)
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
        _entries.Clear();
        _entriesByPath.Clear();
        _searchIndex.Clear();
        _loadedMetadataCount = 0;
    }

    private sealed class ItemView(List<GalleryEntry> entries) : IReadOnlyList<ImageItem>
    {
        public int Count => entries.Count;
        public ImageItem this[int index] => entries[index].Item;

        public IEnumerator<ImageItem> GetEnumerator()
        {
            foreach (var entry in entries)
            {
                yield return entry.Item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal sealed record GalleryEntry(string Path, SourceFingerprint Fingerprint, ImageItem Item)
{
    public DateTime LastWriteTimeUtc => Fingerprint.LastWriteTimeUtc;
}
