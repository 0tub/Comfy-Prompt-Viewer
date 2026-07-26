using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

internal enum SearchScope
{
    All,
    PositivePrompt,
    NegativePrompt,
    Filename
}

// Columnar search storage: one flat array per field, indexed by slot. The scan reads contiguous memory and
// never touches an ImageItem, which is what makes a full rescan per query cheap enough that there is no
// narrowing path and no retained match list to go stale.
//
// Matching is substring (Contains), not token lookup, so this is a column store rather than an inverted
// index. A token index would change what a query matches.
//
// Owned by GalleryCatalog, which maintains it from the same calls that own membership. Do not mutate it
// from anywhere else.
internal sealed class SearchIndex
{
    private const int InitialCapacity = 256;

    private ImageItem?[] _items = new ImageItem?[InitialCapacity];
    private string[] _fileNames = NewTextColumn(InitialCapacity);
    private string[] _prompts = NewTextColumn(InitialCapacity);
    private string[] _negativePrompts = NewTextColumn(InitialCapacity);
    private string[] _settings = NewTextColumn(InitialCapacity);
    private bool[] _hasMetadata = new bool[InitialCapacity];
    private readonly Stack<int> _freeSlots = new();
    private int _slotCount;

    public void Add(ImageItem item)
    {
        // Only reuse a slot this index handed out; one from a previous catalog would clobber a live row.
        if (OwnsSlot(item.SearchSlot, item))
        {
            WriteRow(item.SearchSlot, item);
            return;
        }

        var slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : GrowForNextSlot();
        item.SearchSlot = slot;
        WriteRow(slot, item);
    }

    public void Remove(ImageItem item)
    {
        var slot = item.SearchSlot;
        if (!OwnsSlot(slot, item))
        {
            return;
        }

        _items[slot] = null;
        _fileNames[slot] = "";
        _prompts[slot] = "";
        _negativePrompts[slot] = "";
        _settings[slot] = "";
        _hasMetadata[slot] = false;
        item.SearchSlot = -1;
        _freeSlots.Push(slot);
    }

    // The only mutation a query result can race, and it only ever adds text, which is why a result from an
    // older snapshot stays a valid superset of the current match set.
    public void SetMetadata(ImageItem item)
    {
        var slot = item.SearchSlot;
        if (OwnsSlot(slot, item))
        {
            WriteRow(slot, item);
        }
    }

    public void Clear()
    {
        for (var slot = 0; slot < _slotCount; slot++)
        {
            if (_items[slot] is { } item)
            {
                item.SearchSlot = -1;
            }
        }

        Array.Clear(_items, 0, _slotCount);
        Array.Clear(_fileNames, 0, _slotCount);
        Array.Clear(_prompts, 0, _slotCount);
        Array.Clear(_negativePrompts, 0, _slotCount);
        Array.Clear(_settings, 0, _slotCount);
        Array.Clear(_hasMetadata, 0, _slotCount);
        _freeSlots.Clear();
        _slotCount = 0;
    }

    // Gathered into catalog order so the background scan is sequential and immutable.
    public SearchSnapshot CreateSnapshot(IReadOnlyList<ImageItem> orderedItems)
    {
        var count = orderedItems.Count;
        var items = new ImageItem[count];
        var fileNames = new string[count];
        var prompts = new string[count];
        var negativePrompts = new string[count];
        var settings = new string[count];
        var hasMetadata = new bool[count];

        for (var index = 0; index < count; index++)
        {
            var item = orderedItems[index];
            items[index] = item;
            var slot = item.SearchSlot;
            if (!OwnsSlot(slot, item))
            {
                // Should not happen; filename-only keeps it visible rather than dropping it from search.
                fileNames[index] = item.FileName;
                prompts[index] = "";
                negativePrompts[index] = "";
                settings[index] = "";
                continue;
            }

            fileNames[index] = _fileNames[slot];
            prompts[index] = _prompts[slot];
            negativePrompts[index] = _negativePrompts[slot];
            settings[index] = _settings[slot];
            hasMetadata[index] = _hasMetadata[slot];
        }

        return new SearchSnapshot(items, fileNames, prompts, negativePrompts, settings, hasMetadata);
    }

    private bool OwnsSlot(int slot, ImageItem item)
    {
        return (uint)slot < (uint)_slotCount && ReferenceEquals(_items[slot], item);
    }

    private void WriteRow(int slot, ImageItem item)
    {
        _items[slot] = item;
        _fileNames[slot] = item.FileName;

        if (!item.HasLoadedMetadata)
        {
            _prompts[slot] = "";
            _negativePrompts[slot] = "";
            _settings[slot] = "";
            _hasMetadata[slot] = false;
            return;
        }

        // Separator-normalized once here so matching never normalizes a metadata field per query.
        var settingsText = SearchEngine.NormalizeSeparators(string.Join(
            '\0',
            item.Tool,
            item.Model,
            item.Sampler,
            item.Seed,
            item.Settings,
            item.Lora,
            item.Resources));

        _prompts[slot] = item.Prompt;
        _negativePrompts[slot] = item.NegativePrompt;
        _settings[slot] = settingsText;
        _hasMetadata[slot] = true;
    }

    private int GrowForNextSlot()
    {
        var slot = _slotCount;
        if (slot == _items.Length)
        {
            var capacity = _items.Length * 2;
            Array.Resize(ref _items, capacity);
            Array.Resize(ref _fileNames, capacity);
            Array.Resize(ref _prompts, capacity);
            Array.Resize(ref _negativePrompts, capacity);
            Array.Resize(ref _settings, capacity);
            Array.Resize(ref _hasMetadata, capacity);
            for (var index = slot; index < capacity; index++)
            {
                _fileNames[index] = "";
                _prompts[index] = "";
                _negativePrompts[index] = "";
                _settings[index] = "";
            }
        }

        _slotCount = slot + 1;
        return slot;
    }

    private static string[] NewTextColumn(int capacity)
    {
        var column = new string[capacity];
        Array.Fill(column, "");
        return column;
    }
}

// Immutable columnar view in catalog order; index i describes the same image in every array.
internal sealed class SearchSnapshot
{
    // Below this a range split costs more than the scan it saves.
    internal const int ParallelMinimumRows = 2048;

    private readonly ImageItem[] _items;
    private readonly string[] _fileNames;
    private readonly string[] _prompts;
    private readonly string[] _negativePrompts;
    private readonly string[] _settings;
    private readonly bool[] _hasMetadata;

    internal SearchSnapshot(
        ImageItem[] items,
        string[] fileNames,
        string[] prompts,
        string[] negativePrompts,
        string[] settings,
        bool[] hasMetadata)
    {
        _items = items;
        _fileNames = fileNames;
        _prompts = prompts;
        _negativePrompts = negativePrompts;
        _settings = settings;
        _hasMetadata = hasMetadata;
    }

    public int Count => _items.Length;

    // Partitions by index range and concatenates in range order, which preserves catalog order for free.
    public List<ImageItem> Filter(
        CompiledQuery query,
        SearchScope searchScope,
        CancellationToken token)
    {
        if (Count < ParallelMinimumRows)
        {
            var matches = new List<ImageItem>(Count);
            AppendMatches(0, Count, query, searchScope, token, matches);
            return matches;
        }

        var partitionCount = Math.Min(
            Environment.ProcessorCount,
            Math.Max(1, Count / ParallelMinimumRows));
        var partitionSize = ((Count - 1) / partitionCount) + 1;
        var partitions = new List<ImageItem>[partitionCount];

        Parallel.For(
            0,
            partitionCount,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = partitionCount },
            partitionIndex =>
            {
                var start = partitionIndex * partitionSize;
                var end = Math.Min(Count, start + partitionSize);
                var partitionMatches = new List<ImageItem>();
                partitions[partitionIndex] = partitionMatches;
                AppendMatches(start, end, query, searchScope, token, partitionMatches);
            });

        var total = 0;
        foreach (var partition in partitions)
        {
            total += partition.Count;
        }

        var results = new List<ImageItem>(total);
        foreach (var partition in partitions)
        {
            results.AddRange(partition);
        }

        return results;
    }

    private void AppendMatches(
        int startIndex,
        int endIndex,
        CompiledQuery query,
        SearchScope searchScope,
        CancellationToken token,
        List<ImageItem> matches)
    {
        for (var index = startIndex; index < endIndex; index++)
        {
            token.ThrowIfCancellationRequested();
            if (RowMatches(index, query, searchScope))
            {
                matches.Add(_items[index]);
            }
        }
    }

    internal bool RowMatches(int index, CompiledQuery query, SearchScope searchScope)
    {
        return searchScope switch
        {
            SearchScope.Filename => FieldMatches(_fileNames[index], query),
            // Items whose metadata has not loaded yet stay visible while scanning.
            SearchScope.PositivePrompt => !_hasMetadata[index] || FieldMatches(_prompts[index], query),
            SearchScope.NegativePrompt => !_hasMetadata[index] || FieldMatches(_negativePrompts[index], query),
            _ => RowMatchesAll(index, query)
        };
    }

    private bool RowMatchesAll(int index, CompiledQuery query)
    {
        foreach (var term in query.NegativeTerms)
        {
            if (RowMatchesAnyField(index, term))
            {
                return false;
            }
        }

        foreach (var term in query.PositiveTerms)
        {
            if (SearchEngine.IsMatch(_fileNames[index], term.Term))
            {
                continue;
            }

            if (!_hasMetadata[index])
            {
                return true;
            }

            if (!RowMetadataMatches(index, term))
            {
                return false;
            }
        }

        return true;
    }

    private bool RowMatchesAnyField(int index, in CompiledTerm term)
    {
        return SearchEngine.IsMatch(_fileNames[index], term.Term) ||
               _hasMetadata[index] && RowMetadataMatches(index, term);
    }

    private bool RowMetadataMatches(int index, in CompiledTerm term)
    {
        return SearchEngine.IsMatch(_prompts[index], term.Term) ||
               SearchEngine.IsMatch(_negativePrompts[index], term.Term) ||
               SearchEngine.IsMatch(_settings[index], term.NormalizedTerm);
    }

    private static bool FieldMatches(string text, CompiledQuery query)
    {
        foreach (var term in query.PositiveTerms)
        {
            if (!SearchEngine.IsMatch(text, term.Term))
            {
                return false;
            }
        }

        foreach (var term in query.NegativeTerms)
        {
            if (SearchEngine.IsMatch(text, term.Term))
            {
                return false;
            }
        }

        return true;
    }
}

// The separator-normalized variant of each term, built once per query rather than once per row.
internal sealed class CompiledQuery
{
    public CompiledQuery(List<SearchTerm> positiveTerms, List<SearchTerm> negativeTerms)
    {
        PositiveTerms = Compile(positiveTerms);
        NegativeTerms = Compile(negativeTerms);
    }

    public CompiledTerm[] PositiveTerms { get; }
    public CompiledTerm[] NegativeTerms { get; }

    public bool IsEmpty => PositiveTerms.Length == 0 && NegativeTerms.Length == 0;

    private static CompiledTerm[] Compile(List<SearchTerm> terms)
    {
        var compiled = new CompiledTerm[terms.Count];
        for (var index = 0; index < terms.Count; index++)
        {
            compiled[index] = new CompiledTerm(terms[index]);
        }

        return compiled;
    }
}

internal readonly struct CompiledTerm
{
    public CompiledTerm(SearchTerm term)
    {
        Term = term;
        NormalizedTerm = term with { Text = term.NormalizedText };
    }

    public SearchTerm Term { get; }

    // The settings column is stored separator-normalized, so it is matched with the normalized term.
    public SearchTerm NormalizedTerm { get; }
}
