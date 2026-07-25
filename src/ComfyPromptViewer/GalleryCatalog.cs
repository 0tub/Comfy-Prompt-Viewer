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

    public GalleryCatalog()
    {
        _items = new ItemView(_entries);
    }

    public int Count => _entries.Count;
    public IReadOnlyList<GalleryEntry> Entries => _entries;
    public IReadOnlyList<ImageItem> Items => _items;

    public void Add(GalleryEntry entry)
    {
        _entries.Add(entry);
        _entriesByPath.Add(entry.Path, entry);
    }

    public void Insert(int index, GalleryEntry entry)
    {
        _entries.Insert(index, entry);
        _entriesByPath.Add(entry.Path, entry);
    }

    public bool TryGet(string path, out GalleryEntry entry)
    {
        return _entriesByPath.TryGetValue(path, out entry!);
    }

    public bool Replace(string path, GalleryEntry replacement, out GalleryEntry previous)
    {
        if (!_entriesByPath.TryGetValue(path, out previous!))
        {
            return false;
        }

        var index = _entries.IndexOf(previous);
        if (index < 0)
        {
            return false;
        }

        _entries[index] = replacement;
        _entriesByPath[path] = replacement;
        return true;
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
            removed.Add(entry);
        }

        return removed;
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
