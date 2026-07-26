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

// Columnar search storage: one flat array per field, indexed by slot, plus a 64-bit character-presence
// mask per row that rejects most non-matching rows without touching the string.
//
// Not a token inverted index: queries here are substring matches, so a token index would silently change
// what "cat" matches.
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
    private ulong[] _fileNameMasks = new ulong[InitialCapacity];
    private ulong[] _promptMasks = new ulong[InitialCapacity];
    private ulong[] _negativePromptMasks = new ulong[InitialCapacity];
    private ulong[] _settingsMasks = new ulong[InitialCapacity];
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
        _fileNameMasks[slot] = 0;
        _promptMasks[slot] = 0;
        _negativePromptMasks[slot] = 0;
        _settingsMasks[slot] = 0;
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
        Array.Clear(_fileNameMasks, 0, _slotCount);
        Array.Clear(_promptMasks, 0, _slotCount);
        Array.Clear(_negativePromptMasks, 0, _slotCount);
        Array.Clear(_settingsMasks, 0, _slotCount);
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
        var fileNameMasks = new ulong[count];
        var promptMasks = new ulong[count];
        var negativePromptMasks = new ulong[count];
        var settingsMasks = new ulong[count];
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
                fileNameMasks[index] = ComputeMask(item.FileName);
                prompts[index] = "";
                negativePrompts[index] = "";
                settings[index] = "";
                continue;
            }

            fileNames[index] = _fileNames[slot];
            prompts[index] = _prompts[slot];
            negativePrompts[index] = _negativePrompts[slot];
            settings[index] = _settings[slot];
            fileNameMasks[index] = _fileNameMasks[slot];
            promptMasks[index] = _promptMasks[slot];
            negativePromptMasks[index] = _negativePromptMasks[slot];
            settingsMasks[index] = _settingsMasks[slot];
            hasMetadata[index] = _hasMetadata[slot];
        }

        return new SearchSnapshot(
            items,
            fileNames,
            prompts,
            negativePrompts,
            settings,
            fileNameMasks,
            promptMasks,
            negativePromptMasks,
            settingsMasks,
            hasMetadata);
    }

    private bool OwnsSlot(int slot, ImageItem item)
    {
        return (uint)slot < (uint)_slotCount && ReferenceEquals(_items[slot], item);
    }

    private void WriteRow(int slot, ImageItem item)
    {
        _items[slot] = item;
        _fileNames[slot] = item.FileName;
        _fileNameMasks[slot] = ComputeMask(item.FileName);

        if (!item.HasLoadedMetadata)
        {
            _prompts[slot] = "";
            _negativePrompts[slot] = "";
            _settings[slot] = "";
            _promptMasks[slot] = 0;
            _negativePromptMasks[slot] = 0;
            _settingsMasks[slot] = 0;
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
        _promptMasks[slot] = ComputeMask(item.Prompt);
        _negativePromptMasks[slot] = ComputeMask(item.NegativePrompt);
        _settingsMasks[slot] = ComputeMask(settingsText);
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
            Array.Resize(ref _fileNameMasks, capacity);
            Array.Resize(ref _promptMasks, capacity);
            Array.Resize(ref _negativePromptMasks, capacity);
            Array.Resize(ref _settingsMasks, capacity);
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

    // False positives are fine, false negatives silently drop search results. So anything OrdinalIgnoreCase
    // could equate must light the same bit: ASCII folds to uppercase, and a non-ASCII character also lights
    // whichever ASCII letter its invariant upper/lower mapping reaches (U+212A -> K, U+017F -> S).
    internal static ulong ComputeMask(string text)
    {
        var mask = 0UL;
        foreach (var value in text)
        {
            if (value < 128)
            {
                mask |= 1UL << FoldAscii(value);
                continue;
            }

            var upper = char.ToUpperInvariant(value);
            mask |= upper < 128 ? 1UL << FoldAscii(upper) : NonAsciiBit;

            var lowerUpper = char.ToUpperInvariant(char.ToLowerInvariant(value));
            if (lowerUpper < 128)
            {
                mask |= 1UL << FoldAscii(lowerUpper);
            }
        }

        return mask;
    }

    // Must be a subset of any row that can contain the term. A character whose folding is not ASCII has no
    // safe subset, so 0 disables the filter for that term.
    internal static ulong ComputeTermMask(string term)
    {
        var mask = 0UL;
        foreach (var value in term)
        {
            var folded = value < 128 ? value : char.ToUpperInvariant(value);
            if (folded >= 128)
            {
                return 0;
            }

            mask |= 1UL << FoldAscii(folded);
        }

        return mask;
    }

    private const ulong NonAsciiBit = 1UL << 63;

    private static int FoldAscii(char value)
    {
        var upper = value is >= 'a' and <= 'z' ? (char)(value - 32) : value;
        return upper & 63;
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
    private readonly ulong[] _fileNameMasks;
    private readonly ulong[] _promptMasks;
    private readonly ulong[] _negativePromptMasks;
    private readonly ulong[] _settingsMasks;
    private readonly bool[] _hasMetadata;

    internal SearchSnapshot(
        ImageItem[] items,
        string[] fileNames,
        string[] prompts,
        string[] negativePrompts,
        string[] settings,
        ulong[] fileNameMasks,
        ulong[] promptMasks,
        ulong[] negativePromptMasks,
        ulong[] settingsMasks,
        bool[] hasMetadata)
    {
        _items = items;
        _fileNames = fileNames;
        _prompts = prompts;
        _negativePrompts = negativePrompts;
        _settings = settings;
        _fileNameMasks = fileNameMasks;
        _promptMasks = promptMasks;
        _negativePromptMasks = negativePromptMasks;
        _settingsMasks = settingsMasks;
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
            SearchScope.Filename => FieldMatches(_fileNames[index], _fileNameMasks[index], query),
            // Items whose metadata has not loaded yet stay visible while scanning.
            SearchScope.PositivePrompt => !_hasMetadata[index] ||
                                          FieldMatches(_prompts[index], _promptMasks[index], query),
            SearchScope.NegativePrompt => !_hasMetadata[index] ||
                                          FieldMatches(_negativePrompts[index], _negativePromptMasks[index], query),
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
            if (Contains(_fileNames[index], _fileNameMasks[index], term))
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
        return Contains(_fileNames[index], _fileNameMasks[index], term) ||
               _hasMetadata[index] && RowMetadataMatches(index, term);
    }

    private bool RowMetadataMatches(int index, in CompiledTerm term)
    {
        return Contains(_prompts[index], _promptMasks[index], term) ||
               Contains(_negativePrompts[index], _negativePromptMasks[index], term) ||
               ContainsNormalized(_settings[index], _settingsMasks[index], term);
    }

    private static bool FieldMatches(string text, ulong textMask, CompiledQuery query)
    {
        foreach (var term in query.PositiveTerms)
        {
            if (!Contains(text, textMask, term))
            {
                return false;
            }
        }

        foreach (var term in query.NegativeTerms)
        {
            if (Contains(text, textMask, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(string text, ulong textMask, in CompiledTerm term)
    {
        return (textMask & term.Mask) == term.Mask && SearchEngine.IsMatch(text, term.Term);
    }

    private static bool ContainsNormalized(string text, ulong textMask, in CompiledTerm term)
    {
        return (textMask & term.NormalizedMask) == term.NormalizedMask &&
               SearchEngine.IsMatch(text, term.NormalizedTerm);
    }
}

// Per-term masks precomputed once per query rather than once per row.
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
        Mask = SearchIndex.ComputeTermMask(term.Text);
        NormalizedMask = SearchIndex.ComputeTermMask(term.NormalizedText);
    }

    public SearchTerm Term { get; }

    // The settings column is stored separator-normalized, so it is matched with the normalized term.
    public SearchTerm NormalizedTerm { get; }
    public ulong Mask { get; }
    public ulong NormalizedMask { get; }
}
