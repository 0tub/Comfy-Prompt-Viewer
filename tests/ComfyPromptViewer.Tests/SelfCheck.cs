using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ComfyPromptViewer;

public class SelfCheck
{
    private static readonly MetadataRepository ItemMetadataRepository =
        new(Path.Combine(Path.GetTempPath(), "comfypromptviewer-selfcheck-items"));
    private static readonly DecodedImageCache ItemDecodedImageCache = new();
    private static readonly ThumbnailService ItemThumbnailService =
        new(Path.Combine(Path.GetTempPath(), "comfypromptviewer-selfcheck-items"));
    private static readonly ThumbnailFolderScope ItemThumbnailScope =
        ItemThumbnailService
            .OpenFolderScopeAsync(Path.Combine(Path.GetTempPath(), "comfypromptviewer-selfcheck-scope"))
            .GetAwaiter()
            .GetResult();

    // One ordered pass, not 38 independent facts: the checks share the static fixtures above, and several
    // hand the next one the state it expects (a held cache-write slot, a populated pack).
    [Fact]
    public async Task SelfCheckSuitePasses()
    {
        CheckStalenessGates();
        CheckSearchParsing();
        CheckSearchResultGenerations();
        CheckSearchIndexMaintenance();
        CheckMetadataLoadOnlyRemovesMatches();
        CheckValueSearchAndHighlights();
        CheckPrewarmOrder();
        CheckParallelSearchFiltering();
        CheckGalleryScrollAnchoring();
        CheckGalleryItemReconciliation();
        CheckGalleryInsertionDiff();
        CheckGallerySelection();
        CheckGalleryCatalog();
        CheckSortedInsertion();
        CheckThemeModes();
        CheckPromptExtraction();
        CheckPromptExtractionHeuristics();
        CheckEditorMetadataIsNotAPrompt();
        CheckPromptLinkWalk();
        CheckPngMetadataRead();
        CheckPngMetadataLimit();
        CheckPngTextAfterImageData();
        CheckMetadataIndexRoundTrip();
        CheckComfyWebPExif();
        CheckMp4Metadata();
        CheckMetadataBatchSave();
        CheckMetadataBatchLoad();
        CheckMetadataIndexCleanup();
        await CheckMetadataFailureClassification();
        CheckThumbnailCacheWriteBackpressure();
        CheckDeferredThumbnailCacheWriteQueue();
        CheckDeferredThumbnailCacheWriteGivesUpAfterFailure();
        CheckDeferredThumbnailCacheWritePause();
        CheckThumbnailPrefetchPolicy();
        CheckRetainedViewportWindow();
        CheckSelectedPreviewDecodeWidth();
        CheckSelectedPreviewFailureIsObservable();
        CheckJpegThumbnailEncoding();
        CheckLongPathJpegThumbnailEncoding();
        CheckThumbnailPackRoundTrip();
        CheckPreviewZoomPan();
        CheckSidebarViewModel();
        CheckStartMenuViewModel();
        CheckHeaderViewModel();
        CheckCountText();
        CheckViewportScheduler();
        CheckGalleryViewModel();
        CheckThumbnailFolderScopeIdentity();
        await CheckFolderThumbnailCacheIsolation();
        await CheckFolderSession();
        CheckThumbnailCacheBudget();
        CheckThumbnailDecodeWidths();
        CheckSidebarWidthClamping();
        CheckWindowPlacementRoundTrip();
        CheckRecentFolders();
    }

    private void CheckSidebarWidthClamping()
    {
        // A comfortable window leaves a dragged width exactly where the user put it.
        Check(SidebarWidthLimits.FitToWindow(420, 1600) == 420,
            "Expected a wide window to preserve the dragged sidebar width.");

        // Narrowing the window pulls the sidebar back rather than letting it eat the gallery.
        var narrow = SidebarWidthLimits.FitToWindow(520, 900);
        Check(narrow < 520, "Expected a narrow window to shrink an oversized sidebar.");
        Check(narrow <= 900 * 0.45 + 0.001, "Expected the clamped sidebar to respect the window ratio ceiling.");

        // The floor wins over the ratio, so the sidebar never collapses to nothing on a tiny window.
        Check(SidebarWidthLimits.FitToWindow(300, 200) == 260,
            "Expected the sidebar minimum width to win over the window ratio.");

        // The absolute maximum still applies on an ultrawide window.
        Check(SidebarWidthLimits.FitToWindow(9000, 5000) == 560,
            "Expected the sidebar maximum width to bound an absurd stored value.");
    }

    private void CheckRecentFolders()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cpv-recent-{Guid.NewGuid():N}");
        try
        {
            var store = new UserPreferencesStore(directory);

            // Lines written before pinning existed have three fields and must still load, unpinned.
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "recent-folders.txt"), ["/old/a|5|638000000000000000"]);
            var legacy = store.LoadRecentFolders();
            Check(legacy.Count == 1 && legacy[0].ImageCount == 5 && !legacy[0].IsPinned,
                "Expected a legacy recent-folder line to load unpinned.");

            store.SetRecentFolderPinned("/old/a", true);
            var total = UserPreferencesStore.MaxUnpinnedRecentFolders + 5;
            for (var index = 0; index < total; index++)
            {
                store.AddRecentFolder($"/f/{index}", index);
            }

            var recent = store.LoadRecentFolders();
            Check(recent.Exists(folder => folder.Path == "/old/a" && folder.IsPinned),
                "Expected a pinned folder to survive any number of newer opens.");
            Check(recent.FindAll(folder => !folder.IsPinned).Count == UserPreferencesStore.MaxUnpinnedRecentFolders,
                "Expected unpinned history to be capped.");
            Check(recent[0].Path == $"/f/{total - 1}" && !recent.Exists(folder => folder.Path == "/f/0"),
                "Expected the cap to drop the oldest unpinned folders.");

            store.AddRecentFolder("/old/a", 9);
            var reopened = store.LoadRecentFolders();
            Check(reopened[0].Path == "/old/a" && reopened[0].IsPinned && reopened[0].ImageCount == 9,
                "Expected reopening a pinned folder to keep it pinned and refresh it.");

            store.SetRecentFolderPinned("/old/a", false);
            Check(!store.LoadRecentFolders()[0].IsPinned, "Expected unpinning to persist.");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to clean up recent-folder self-check directory: {ex.Message}");
            }
        }

        var sep = Path.DirectorySeparatorChar;
        var names = StartMenuViewModel.BuildRecentFolderDisplayNames([
            $"{sep}home{sep}ComfyUI{sep}output{sep}",
            $"{sep}home{sep}ComfyUI-linux{sep}output",
            $"{sep}mnt{sep}meesh"
        ]);
        Check(names[0] == $"ComfyUI{sep}output" && names[1] == $"ComfyUI-linux{sep}output",
            "Expected colliding recent-folder names to gain their parent folder.");
        Check(names[2] == "meesh", "Expected a unique recent-folder name to stay bare.");
    }

    private void CheckWindowPlacementRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cpv-placement-{Guid.NewGuid():N}");
        try
        {
            var store = new UserPreferencesStore(directory);
            Check(store.LoadWindowPlacement() is null, "Expected no window placement before one is saved.");

            store.SaveWindowPlacement(new WindowPlacement(1280.5, 800.25, -1720, 40, true));
            var loaded = store.LoadWindowPlacement();
            Check(loaded is not null, "Expected a saved window placement to load.");
            Check(loaded!.Value.Width == 1280.5 && loaded.Value.Height == 800.25,
                "Expected window placement size to round-trip exactly.");
            // Negative coordinates are normal for a monitor left of the primary one.
            Check(loaded.Value.X == -1720 && loaded.Value.Y == 40,
                "Expected a negative window position to round-trip.");
            Check(loaded.Value.IsMaximized, "Expected the maximized flag to round-trip.");

            File.WriteAllText(Path.Combine(directory, "window-placement.txt"), "garbage|not|a|placement|x");
            Check(store.LoadWindowPlacement() is null, "Expected an unparseable placement to be ignored.");

            File.WriteAllText(Path.Combine(directory, "window-placement.txt"), "Infinity|800|0|0|False");
            Check(store.LoadWindowPlacement() is null, "Expected an infinite window width to be ignored, not applied.");
            File.WriteAllText(Path.Combine(directory, "tile-size.txt"), "NaN");
            Check(store.LoadTileSize() is null, "Expected a non-finite tile size to fall back to the default.");
            File.WriteAllText(Path.Combine(directory, "sidebar-width.txt"), "Infinity");
            Check(store.LoadSidebarWidth() is null, "Expected a non-finite sidebar width to fall back to the default.");

            store.SaveSidebarWidth(420);
            store.AddRecentFolder("/pics/atomic", 3);
            Check(Directory.GetFiles(directory, "*.tmp").Length == 0 && store.LoadSidebarWidth() == 420 &&
                  store.LoadRecentFolders().Exists(folder => folder.Path == "/pics/atomic"),
                "Expected saves to leave no temporary file behind and to replace the old contents.");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to clean up placement self-check directory: {ex.Message}");
            }
        }
    }

    // The two staleness primitives replaced six hand-rolled counters. Everything that used to be an
    // "did this path remember to bump/check" bug now lives or dies here.
    private void CheckStalenessGates()
    {
        var gate = new GenerationGate();
        var first = gate.Begin();
        Check(first.IsCurrent, "Expected a freshly begun generation to be current.");

        var second = gate.Begin();
        Check(first.IsStale, "Expected beginning a generation to supersede the previous one.");
        Check(second.IsCurrent, "Expected the newest generation to be current.");

        gate.Invalidate();
        Check(second.IsStale, "Expected invalidating a gate to supersede every outstanding generation.");
        Check(default(Generation).IsStale, "Expected an unassigned generation to read as stale.");

        var sessions = new SessionGate();
        var firstSession = sessions.Restart();
        Check(firstSession.IsCurrent && sessions.IsActive, "Expected a restarted session to be current.");
        Check(sessions.IsCurrent(firstSession.Generation),
            "Expected a generation carried on its own to resolve to the active session.");

        var joined = sessions.Snapshot();
        Check(joined.IsCurrent, "Expected a snapshot of the active session to be current.");

        var secondSession = sessions.Restart();
        Check(firstSession.IsStale && firstSession.Token.IsCancellationRequested,
            "Expected restarting a session gate to cancel and supersede the previous session.");
        Check(secondSession.IsCurrent, "Expected the replacement session to be current.");
        Check(!sessions.IsCurrent(firstSession.Generation),
            "Expected a superseded generation carried on its own to be rejected.");

        sessions.Cancel();
        Check(secondSession.IsStale && secondSession.Token.IsCancellationRequested,
            "Expected canceling a session gate to cancel and supersede the active session.");
        Check(!sessions.IsActive, "Expected a canceled session gate to report no active session.");
        Check(!sessions.IsCurrent(secondSession.Generation),
            "Expected canceling a session gate to reject its generation.");
        Check(default(Session).IsStale, "Expected an unassigned session to read as stale.");
    }

    // The index is only correct because GalleryCatalog is its single owner: every membership change and
    // every metadata load goes through one call that updates both.
    private void CheckSearchIndexMaintenance()
    {
        var catalog = new GalleryCatalog();
        var kept = CreateImageItem(Path.Combine(Path.GetTempPath(), "index-kept.png"));
        var removed = CreateImageItem(Path.Combine(Path.GetTempPath(), "index-removed.png"));
        catalog.Add(kept);
        catalog.Add(removed);

        Check(MatchCount(catalog, "index", SearchScope.Filename) == 2,
            "Expected both catalog items to be indexed on add.");

        catalog.RemovePaths(new HashSet<string>([removed.Path], StringComparer.OrdinalIgnoreCase));
        Check(MatchCount(catalog, "index", SearchScope.Filename) == 1 && removed.SearchSlot < 0,
            "Expected removing a catalog entry to release its search row.");

        kept.ApplyMetadata(ImageMetadata.Empty with
        {
            Prompt = "a golden retriever",
            Lora = "detail_tweaker (0.80)"
        });
        Check(MatchCount(catalog, "zebra", SearchScope.All) == 1,
            "Expected a row the catalog has not been told about yet to still read as unscanned.");

        catalog.MarkMetadataLoaded(kept);
        Check(MatchCount(catalog, "zebra", SearchScope.All) == 0,
            "Expected the catalog's metadata-loaded call to write the search row.");
        Check(MatchCount(catalog, "retriever", SearchScope.All) == 1,
            "Expected the prompt column to be searchable once written.");
        Check(MatchCount(catalog, "detail-tweaker", SearchScope.All) == 1,
            "Expected the settings column to stay separator-normalized.");

        var replacement = CreateImageItem(kept.Path);
        catalog.ReplaceMany(new Dictionary<string, ImageItem>(StringComparer.OrdinalIgnoreCase)
        {
            [kept.Path] = replacement
        });
        Check(kept.SearchSlot < 0 && replacement.SearchSlot >= 0,
            "Expected replacing an entry to move the search row to the replacement item.");
        Check(MatchCount(catalog, "zebra", SearchScope.All) == 1,
            "Expected the replacement item's row to read as unscanned again.");
        Check(MatchCount(catalog, "index", SearchScope.Filename) == 1,
            "Expected exactly the replacement item to be indexed.");

        catalog.Clear();
        Check(MatchCount(catalog, "index", SearchScope.Filename) == 0 && replacement.SearchSlot < 0,
            "Expected clearing the catalog to release every search row.");
    }

    private void CheckParallelSearchFiltering()
    {
        // Exceeds the parallel threshold so the partitioned path runs.
        const int candidateCount = 20000;
        var catalog = new GalleryCatalog();
        var expected = new List<ImageItem>();
        for (var index = 0; index < candidateCount; index++)
        {
            var isMatch = index % 3 == 0;
            var item = CreateImageItem(Path.Combine(
                Path.GetTempPath(),
                $"parallel-{(isMatch ? "keep" : "drop")}-{index}.png"));
            catalog.Add(item);
            if (isMatch)
            {
                expected.Add(item);
            }
        }

        var matches = Filter(catalog, "keep", SearchScope.Filename);

        Check(matches.Count == expected.Count, "Expected the parallel search filter to find every match.");
        for (var index = 0; index < expected.Count; index++)
        {
            if (!ReferenceEquals(matches[index], expected[index]))
            {
                Check(false, "Expected the parallel search filter to preserve catalog order.");
                catalog.Clear();
                return;
            }
        }

        catalog.Clear();
    }

    private static List<ImageItem> Filter(GalleryCatalog catalog, string query, SearchScope searchScope)
    {
        SearchEngine.ParseQuery(query, out var positive, out var negative);
        return catalog.CreateSearchSnapshot()
            .Filter(new CompiledQuery(positive, negative), searchScope, CancellationToken.None);
    }

    private static int MatchCount(GalleryCatalog catalog, string query, SearchScope searchScope)
    {
        return Filter(catalog, query, searchScope).Count;
    }

    // Indexes one item on its own so a single-item query reads as a plain "does this match".
    private static bool ItemMatchesSearch(ImageItem item, string query, SearchScope searchScope)
    {
        var catalog = new GalleryCatalog();
        catalog.Add(item);
        var matched = MatchCount(catalog, query, searchScope) == 1;
        catalog.Clear();
        return matched;
    }

    private void CheckSortedInsertion()
    {
        int[] values = [1, 3, 3, 5];
        Check(GalleryListSync.FindSortedInsertIndex(values, 0, static (left, right) => left.CompareTo(right)) == 0,
            "Expected sorted insertion before the first item.");
        Check(GalleryListSync.FindSortedInsertIndex(values, 3, static (left, right) => left.CompareTo(right)) == 3,
            "Expected sorted insertion after equivalent items.");
        Check(GalleryListSync.FindSortedInsertIndex(values, 6, static (left, right) => left.CompareTo(right)) == values.Length,
            "Expected sorted insertion after the last item.");
    }

    private void CheckGalleryScrollAnchoring()
    {
        var offset = GalleryListSync.CalculateAnchoredGalleryOffset(
            oldIndex: 20,
            newIndex: 24,
            columns: 4,
            itemExtent: 136,
            oldOffset: 700,
            maxOffset: 5000);

        Check(offset == 836, "Expected a new row above the viewport to preserve the visible gallery row.");

        offset = GalleryListSync.CalculateAnchoredGalleryOffset(
            oldIndex: 20,
            newIndex: -1,
            columns: 4,
            itemExtent: 136,
            oldOffset: 700,
            maxOffset: 5000);

        Check(offset == 700, "Expected deleting the first visible gallery item to preserve the current scroll offset.");
    }

    private void CheckGalleryItemReconciliation()
    {
        var a = CreateImageItem(Path.Combine(Path.GetTempPath(), "gallery-a.png"));
        var b = CreateImageItem(Path.Combine(Path.GetTempPath(), "gallery-b.png"));
        var c = CreateImageItem(Path.Combine(Path.GetTempPath(), "gallery-c.png"));
        var added = CreateImageItem(Path.Combine(Path.GetTempPath(), "gallery-added.png"));

        Check(GalleryListSync.CanSynchronizeGalleryItemsIncrementally([a, b, c], [added, a, b, c], maximumChanges: 2),
            "Expected a small watcher insertion to retain the existing gallery order.");
        Check(!GalleryListSync.CanSynchronizeGalleryItemsIncrementally([a, b, c], [c, b, a], maximumChanges: 2),
            "Expected a reorder to use a gallery reset instead of per-item moves.");
        Check(!GalleryListSync.CanSynchronizeGalleryItemsIncrementally([a, b, c], [added, a, b, c], maximumChanges: 0),
            "Expected the incremental gallery change limit to be enforced.");
    }

    // The allocation-free watcher path. A wrong diff here silently corrupts the gallery order rather than
    // failing loudly, so every rejection case is covered as well as the applied result.
    private void CheckGalleryInsertionDiff()
    {
        var a = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-a.png"));
        var b = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-b.png"));
        var c = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-c.png"));
        var first = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-first.png"));
        var middle = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-middle.png"));
        var insertions = new List<GalleryListSync.GalleryInsertion>();

        bool Apply(List<ImageItem> current, IReadOnlyList<ImageItem> target, int maximumInsertions)
        {
            if (!GalleryListSync.TryFindGalleryInsertions(current, target, maximumInsertions, insertions))
            {
                return false;
            }

            foreach (var insertion in insertions)
            {
                current.Insert(insertion.Index, insertion.Item);
            }

            return true;
        }

        var items = new List<ImageItem> { a, b, c };
        Check(Apply(items, [first, a, middle, b, c], maximumInsertions: 4),
            "Expected interleaved watcher insertions to be recognized.");
        Check(items is [var i0, var i1, var i2, var i3, var i4] &&
              ReferenceEquals(i0, first) && ReferenceEquals(i1, a) && ReferenceEquals(i2, middle) &&
              ReferenceEquals(i3, b) && ReferenceEquals(i4, c),
            "Expected applied insertions to reproduce the target sequence exactly.");

        Check(!GalleryListSync.TryFindGalleryInsertions([a, b, c], [a, c], 4, insertions),
            "Expected a removal to fall back to the general gallery sync path.");
        Check(!GalleryListSync.TryFindGalleryInsertions([a, b, c], [c, b, a], 4, insertions),
            "Expected a reorder to fall back to the general gallery sync path.");
        Check(!GalleryListSync.TryFindGalleryInsertions([a, b, c], [a, b, c], 4, insertions),
            "Expected an unchanged sequence to report no insertions.");
        Check(!GalleryListSync.TryFindGalleryInsertions([a, b, c], [first, middle, a, b, c], 1, insertions),
            "Expected the insertion limit to be enforced.");
        Check(!GalleryListSync.TryFindGalleryInsertions([a, b, c], [first, a, b, middle], 4, insertions),
            "Expected an insertion paired with a removal to fall back to the general gallery sync path.");
    }

    private void CheckGallerySelection()
    {
        var items = new[]
        {
            CreateImageItem("sel-0.png"),
            CreateImageItem("sel-1.png"),
            CreateImageItem("sel-2.png"),
            CreateImageItem("sel-3.png")
        };
        var selection = new GallerySelection();

        selection.SelectOnly(items[1]);
        Check(selection.Count == 1 && items[1].IsMarkedSelected && selection.Anchor == items[1],
            "SelectOnly should select and anchor one item.");

        Check(selection.SelectRange(items, items[3]), "Range from a listed anchor should apply.");
        Check(selection.Count == 3 && !items[0].IsMarkedSelected && items[2].IsMarkedSelected &&
              selection.Anchor == items[1],
            "Range should cover anchor..item inclusive and keep the anchor.");

        Check(selection.SelectRange(items, items[0]) && selection.Count == 2 && !items[3].IsMarkedSelected,
            "A second range should re-extend from the same anchor and unmark the old range.");

        Check(!selection.Toggle(items[0]) && !items[0].IsMarkedSelected && selection.Anchor == items[0],
            "Toggling a selected item should unselect it and move the anchor.");
        Check(selection.Toggle(items[3]) && items[3].IsMarkedSelected, "Toggling an unselected item should select it.");
        Check(selection.FirstIn(items) == items[1], "FirstIn should follow the given order.");
        var inOrder = selection.InOrder(new[] { items[3], items[2], items[1], items[0] });
        Check(inOrder.Length == 2 && inOrder[0] == items[3] && inOrder[1] == items[1],
            "InOrder should follow the given order, not hash order.");

        var replacement = CreateImageItem("sel-3.png");
        selection.Replace(items[3], replacement);
        Check(!items[3].IsMarkedSelected && replacement.IsMarkedSelected && selection.Anchor == replacement,
            "Replace should carry membership, the mark, and the anchor to the replacement.");

        selection.Remove(replacement);
        Check(!replacement.IsMarkedSelected && selection.Anchor is null && selection.Count == 1,
            "Remove should unmark and drop the anchor.");
        Check(!selection.SelectRange(items, items[2]) && selection.Count == 1,
            "A range without an anchor should change nothing.");

        selection.Add(items[0]);
        Check(selection.Anchor is null, "Add should not move the anchor.");

        selection.Clear();
        Check(selection.Count == 0 && selection.Anchor is null && !items[0].IsMarkedSelected && !items[1].IsMarkedSelected,
            "Clear should unmark everything and drop the anchor.");
    }

    private void CheckGalleryCatalog()
    {
        int NewestFirst(ImageItem left, ImageItem right) =>
            right.SourceFingerprint.LastWriteTimeUtcTicks.CompareTo(left.SourceFingerprint.LastWriteTimeUtcTicks);

        var catalog = new GalleryCatalog();
        var older = CreateImageItem(Path.Combine(Path.GetTempPath(), "catalog-older.png"), new SourceFingerprint(10, 0));
        var newer = CreateImageItem(Path.Combine(Path.GetTempPath(), "catalog-newer.png"), new SourceFingerprint(20, 0));
        catalog.Add(older);
        catalog.Add(newer);
        catalog.Sort(NewestFirst);

        Check(ReferenceEquals(catalog.Items[0], newer), "Expected catalog sort to keep item and timestamp together.");

        var replacement = CreateImageItem(newer.Path, new SourceFingerprint(5, 0));
        Check(
            catalog.TryReplaceSorted(replacement, NewestFirst, out var previous) &&
            ReferenceEquals(previous, newer),
            "Expected catalog replacement to update the authoritative entry.");
        Check(
            ReferenceEquals(catalog.Items[0], older) && ReferenceEquals(catalog.Items[1], replacement),
            "Expected a sorted replacement to move the entry to its new ordered position.");
        Check(
            catalog.TryGet(newer.Path, out var relocated) && ReferenceEquals(relocated, replacement),
            "Expected a sorted replacement to stay reachable by path.");

        var bulkReplacement = CreateImageItem(older.Path, new SourceFingerprint(1, 0));
        catalog.ReplaceMany(new Dictionary<string, ImageItem>(StringComparer.OrdinalIgnoreCase)
        {
            [older.Path] = bulkReplacement
        });
        Check(
            catalog.TryGet(older.Path, out var bulkReplaced) && ReferenceEquals(bulkReplaced, bulkReplacement),
            "Expected a bulk replacement pass to swap the authoritative entry.");

        Check(catalog.LoadedMetadataCount == 0, "Expected an unscanned catalog to report no loaded metadata.");
        catalog.MarkMetadataLoaded(replacement);
        Check(catalog.LoadedMetadataCount == 1, "Expected a member metadata load to advance the loaded count.");
        catalog.MarkMetadataLoaded(newer);
        Check(catalog.LoadedMetadataCount == 1, "Expected a replaced item's late metadata load to be ignored.");

        catalog.RemovePaths(new HashSet<string>([older.Path], StringComparer.OrdinalIgnoreCase));
        Check(catalog.Count == 1, "Expected removal to drop the entry from the catalog.");
        Check(catalog.LoadedMetadataCount == 1, "Expected removing an unscanned entry to leave the loaded count alone.");

        catalog.Clear();
        Check(catalog.LoadedMetadataCount == 0, "Expected clearing the catalog to reset the loaded count.");
    }

    private void CheckSearchParsing()
    {
        SearchEngine.ParseQuery("cat \"red dress\" -bad -\"low quality\"", out var positive, out var negative);

        Check(positive.Count == 2, "Expected two positive search terms.");
        Check(positive[0] is { Text: "cat", IsExact: false }, "Expected plain positive term.");
        Check(positive[1] is { Text: "red dress", IsExact: true }, "Expected exact positive term.");
        Check(negative.Count == 2, "Expected two negative search terms.");
        Check(negative[0] is { Text: "bad", IsExact: false }, "Expected plain negative term.");
        Check(negative[1] is { Text: "low quality", IsExact: true }, "Expected exact negative term.");

        var item = CreateImageItem(Path.Combine(Path.GetTempPath(), "search-scope-selfcheck.png"));
        item.ApplyMetadata(ImageMetadata.Empty with
        {
            Prompt = "sunlit portrait",
            NegativePrompt = "blurry watermark",
            Lora = "cosmos_predict_lora (1.00)",
            Resources = "Embedding: easynegative"
        });

        Check(!ItemMatchesSearch(item, "watermark", SearchScope.PositivePrompt),
            "Expected positive prompt search to ignore negative prompt text.");
        Check(ItemMatchesSearch(item, "watermark", SearchScope.NegativePrompt),
            "Expected negative prompt search to match negative prompt text.");

        Check(ItemMatchesSearch(item, "cosmos-predict", SearchScope.All),
            "Expected all search to match normalized LoRA metadata.");
        Check(!ItemMatchesSearch(item, "cosmos-predict", SearchScope.Filename),
            "Expected filename search to ignore LoRA metadata.");

        Check(ItemMatchesSearch(item, "\"cosmos predict lora (1.00)\"", SearchScope.All),
            "Expected the settings column to preserve separator-insensitive exact matching.");

        Check(!ItemMatchesSearch(item, "-easynegative", SearchScope.All),
            "Expected all search exclusions to check resource metadata.");
    }

    private void CheckSearchResultGenerations()
    {
        var gate = new SessionGate();
        var session = gate.Restart();
        Check(session.IsCurrent, "Expected a result for the current query to be applied.");

        var superseded = session;
        session = gate.Restart();
        Check(superseded.IsStale, "Expected a result for a superseded query to be rejected.");

        gate.Cancel();
        Check(session.IsStale, "Expected a result from a canceled pass to be rejected.");
    }

    // Applying a background result computed from an older snapshot depends on metadata loading only ever
    // removing matches. If this stops holding, searches silently lose items.
    private void CheckMetadataLoadOnlyRemovesMatches()
    {
        foreach (var scope in new[] { SearchScope.All, SearchScope.PositivePrompt, SearchScope.NegativePrompt })
        {
            var catalog = new GalleryCatalog();
            var item = CreateImageItem(Path.Combine(Path.GetTempPath(), $"monotonic-{scope}.png"));
            catalog.Add(item);

            Check(MatchCount(catalog, "dragon", scope) == 1,
                $"Expected an unscanned item to stay visible for scope {scope}.");

            item.ApplyMetadata(ImageMetadata.Empty with
            {
                Prompt = "sunlit portrait",
                NegativePrompt = "blurry watermark"
            });
            catalog.MarkMetadataLoaded(item);

            Check(MatchCount(catalog, "dragon", scope) == 0,
                $"Expected loading non-matching metadata to drop the item for scope {scope}.");
            catalog.Clear();
        }

        // A negative term behaves the same way: metadata can only add reasons to exclude.
        var excludeCatalog = new GalleryCatalog();
        var excluded = CreateImageItem(Path.Combine(Path.GetTempPath(), "monotonic-negative.png"));
        excludeCatalog.Add(excluded);
        Check(MatchCount(excludeCatalog, "-watermark", SearchScope.All) == 1,
            "Expected an unscanned item to survive a negative term.");

        excluded.ApplyMetadata(ImageMetadata.Empty with
        {
            Prompt = "sunlit portrait",
            NegativePrompt = "blurry watermark"
        });
        excludeCatalog.MarkMetadataLoaded(excluded);
        Check(MatchCount(excludeCatalog, "-watermark", SearchScope.All) == 0,
            "Expected loaded metadata to newly satisfy a negative term and drop the item.");
        excludeCatalog.Clear();
    }

    // Clicking a sidebar value must find the image it came from, and only images that share that value.
    private void CheckValueSearchAndHighlights()
    {
        var catalog = new GalleryCatalog();
        ImageMetadata Entry(ImageItem item, string model, string sampler, string seed, string lora) => ImageMetadata.Empty with
        {
            Prompt = "green cat, sharp teeth",
            Model = model,
            Sampler = sampler,
            Seed = seed,
            Lora = lora
        };

        var source = CreateImageItem(Path.Combine(Path.GetTempPath(), "value-source.png"));
        var other = CreateImageItem(Path.Combine(Path.GetTempPath(), "value-other.png"));
        foreach (var (item, entry) in new[]
                 {
                     (source, Entry(source, "anima-base-v1.0.safetensors", "euler", "846874715736056",
                         "bagelcollie3 (1.00), my lora (0.50)")),
                     (other, Entry(other, "anima-base-v1.0-pruned.safetensors", "euler_ancestral", "12",
                         "bagelcollie3_v2 (0.80)"))
                 })
        {
            catalog.Add(item);
            item.ApplyMetadata(entry);
            catalog.MarkMetadataLoaded(item);
        }

        int Count(string value, bool exact) =>
            MatchCount(catalog, SearchEngine.BuildValueQuery(value, exact)!, SearchScope.All);

        Check(Count("anima-base-v1.0.safetensors", exact: true) == 1,
            "Expected a clicked model to match only the exact model.");
        Check(Count("euler", exact: true) == 1, "Expected a clicked sampler not to match a longer sampler name.");
        Check(Count("846874715736056", exact: true) == 1, "Expected a clicked seed to match its image.");

        var loras = PromptExtractor.SplitLoraEntries("bagelcollie3 (1.00), my lora (0.50), plain");
        Check(loras.Count == 3 &&
              loras[0] == ("bagelcollie3 (1.00)", "bagelcollie3") &&
              loras[1] == ("my lora (0.50)", "my lora") &&
              loras[2] == ("plain", "plain"),
            "Expected LoRA entries to split and drop their strengths.");
        Check(Count("my lora", exact: false) == 1, "Expected a LoRA name with a space to match through the settings column.");
        Check(Count("bagelcollie3", exact: false) == 2,
            "A LoRA click is a substring term, so it also finds versioned variants of the name.");
        Check(SearchEngine.BuildValueQuery("-dash", exact: false) == "dash",
            "Expected a leading dash not to turn a LoRA click into an exclusion.");
        Check(SearchEngine.BuildValueQuery("  ", exact: true) is null, "Expected an empty value to build no query.");
        Check(SearchEngine.AppendQueryTerm("", "\"euler\"") == "\"euler\"", "Expected a click into an empty search to set it.");
        Check(SearchEngine.AppendQueryTerm("green cat  ", "\"euler\"") == "green cat \"euler\"",
            "Expected a click to add to what is already typed.");
        Check(SearchEngine.AppendQueryTerm("cat \"EULER\"", "\"euler\"") == "cat \"EULER\"",
            "Expected a term already in the search not to be added twice.");
        Check(SearchEngine.AppendQueryTerm("\"bagelcollie3\"", "bagelcollie3") == "\"bagelcollie3\" bagelcollie3",
            "An exact term and a plain term match differently, so both are kept.");
        Check(GalleryView.GetWholeColumnWidth(1100, 146) == 1022 && GalleryView.GetWholeColumnWidth(100, 146) == 146,
            "Expected the gallery width to snap to whole columns, never below one.");
        catalog.Clear();

        SearchEngine.ParseQuery("cat \"teeth\" -dog", out var positive, out _);
        var ranges = SearchEngine.FindMatchRanges("green cat, sharp teeth, catalog", positive);
        Check(ranges.Count == 3 && ranges[0] == (6, 3) && ranges[1] == (17, 5) && ranges[2] == (24, 3),
            "Expected plain terms to highlight every occurrence and exact terms only whole words.");

        SearchEngine.ParseQuery("cat catalog", out var overlapping, out _);
        var merged = SearchEngine.FindMatchRanges("catalog", overlapping);
        Check(merged.Count == 1 && merged[0] == (0, 7), "Expected overlapping highlights to merge.");
    }

    private void CheckPrewarmOrder()
    {
        var order = new PrewarmOrder([0, 10, 20, 30, 40, 50]);
        // Scrolling down from 25: 30 (5 ahead) wins, then 20 (5 behind, weighted to 10) beats 40 (15 ahead),
        // then 40 beats 10 (15 behind, weighted to 30).
        var taken = new List<int>();
        for (var index = order.Take(25, 1); index >= 0; index = order.Take(25, 1))
        {
            taken.Add(index);
        }

        Check(taken.Count == 6 && taken[0] == 3 && taken[1] == 2 && taken[2] == 4,
            "Expected prewarm to start next to the viewport and favor the scroll direction.");

        var moved = new PrewarmOrder([0, 10, 20, 30, 40, 50]);
        moved.Take(0, 1);
        Check(moved.Take(50, 1) == 5 && moved.Take(50, 1) == 4,
            "Expected prewarm to re-center when the viewport moves.");
        Check(moved.Remaining == 3, "Expected every candidate to be handed out once.");
    }

    private void CheckThemeModes()
    {
        Check(Enum.GetValues<ThemeMode>().Length == 5, "Expected five theme modes.");
        Check((int)ThemeMode.Brown == 0 &&
              (int)ThemeMode.DarkGray == 1 &&
              (int)ThemeMode.DarkBlue == 2 &&
              (int)ThemeMode.DarkGreen == 3 &&
              (int)ThemeMode.Plum == 4,
            "Expected theme mode order to match ThemeComboBox.");
    }

    private void CheckPromptExtraction()
    {
        var extracted = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["parameters"] = "masterpiece portrait\nNegative prompt: blurry\nSteps: 20, Sampler: Euler, CFG scale: 7, Seed: 123, Model: test-model"
        });

        Check(extracted.Prompt == "masterpiece portrait", "Expected positive prompt from parameters.");
        Check(extracted.NegativePrompt == "blurry", "Expected negative prompt from parameters.");
        Check(extracted.GenerationSettings.Seed == "123", "Expected seed from settings line.");
        Check(extracted.GenerationSettings.Settings == "Steps 20, CFG 7", "Expected compact settings summary.");

        var rich = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["parameters"] = "portrait <lora:ink style:0.8>, embedding:easynegative\nSteps: 30, Sampler: Euler, CFG scale: 6, Seed: 456, Model: rich-model, VAE: vae-ft, Clip skip: 2, ControlNet 0: \"Module: ip-adapter_clip_sd15, Model: ip-adapter_sd15_light [932b88cf], Weight: 0.75\", Lora hashes: \"detailer: abc12345\", Version: Forge"
        });

        Check(rich.GenerationSettings.Tool == "Forge", "Expected tool detection from version metadata.");
        Check(rich.GenerationSettings.Settings.Contains("VAE vae-ft", StringComparison.Ordinal), "Expected VAE in settings summary.");
        Check(rich.GenerationSettings.Lora.Contains("ink_style (0.80)", StringComparison.Ordinal) &&
              rich.GenerationSettings.Lora.Contains("detailer", StringComparison.Ordinal),
            $"Expected prompt and hash LoRA extraction, got '{rich.GenerationSettings.Lora}'.");
        Check(rich.GenerationSettings.Resources.Contains("Embedding: easynegative", StringComparison.Ordinal) &&
              rich.GenerationSettings.Resources.Contains("IP-Adapter: ip_adapter_sd15_light", StringComparison.Ordinal),
            "Expected extra resource extraction.");

        var drawThings = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["XML:com.adobe.xmp"] = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:xmp="http://ns.adobe.com/xap/1.0/">
                  <dc:description><rdf:Alt><rdf:li xml:lang="x-default">draw prompt
            -draw negative
            Steps: 20, Sampler: Euler Ancestral, Guidance Scale: 4.0, Seed: 4279116933, Model: draw_model.ckpt</rdf:li></rdf:Alt></dc:description>
                  <xmp:CreatorTool>Draw Things</xmp:CreatorTool>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """
        });

        Check(drawThings.GenerationSettings.Tool == "Draw Things", "Expected Draw Things XMP metadata to set the tool.");

        var comfy = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = """
            {
              "1": {"class_type":"CLIPTextEncode","inputs":{"text":"positive landscape"}},
              "2": {"class_type":"CLIPTextEncode","inputs":{"text":"low quality"}},
              "3": {"class_type":"KSampler","inputs":{"positive":["1",0],"negative":["2",0],"steps":20}}
            }
            """
        });

        Check(comfy.Prompt == "positive landscape", "Expected ComfyUI positive link extraction.");
        Check(comfy.NegativePrompt == "low quality", "Expected ComfyUI negative link extraction.");
        Check(comfy.GenerationSettings.Tool == "ComfyUI", "Expected ComfyUI prompt metadata to set the tool.");

        var noNegative = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = """
            {
              "1": {"class_type":"CLIPTextEncode","inputs":{"text":"positive landscape"}},
              "3": {"class_type":"KSampler","inputs":{"positive":["1",0],"negative":["99",0],"sampler_name":"er_sde","seed":721861089590642}}
            }
            """
        });

        Check(noNegative.NegativePrompt == "", "Expected sampler_name not to be treated as a negative prompt.");
        Check(noNegative.GenerationSettings.Sampler == "er_sde", "Expected sampler_name to remain in generation settings.");

        var oldCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var comfyLora = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["prompt"] = """
                {
                  "1": {"class_type":"KSampler","inputs":{"steps":20}},
                  "2": {"class_type":"ExampleLoraNode","inputs":{"lora_data":"[{\"name\":\"unused.safetensors\",\"strength\":1,\"enabled\":false},{\"name\":\"folder/example-style.safetensors\",\"strength\":0.75,\"enabled\":true}]" }}
                }
                """
            });

            Check(comfyLora.GenerationSettings.Lora == "example_style (0.75)",
                $"Expected culture-invariant ComfyUI lora_data extraction, got '{comfyLora.GenerationSettings.Lora}'.");
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
        }

        var workflowLora = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["workflow"] = """
            {
              "nodes": [
                {"id":1,"type":"LoraLoaderModelOnly","mode":0,"properties":{"Node name for S&R":"LoraLoaderModelOnly"},"widgets_values":["anima-preview3\\Cosmos-Predict2.5-2B-base-distilled-LoRA.safetensors",1]},
                {"id":2,"type":"LoraLoaderModelOnly","mode":4,"widgets_values":["disabled-lora.safetensors",1]}
              ]
            }
            """
        });

        Check(workflowLora.GenerationSettings.Lora == "cosmos_predict2.5_2b_base_distilled_lora (1.00)",
            $"Expected workflow widget LoRA extraction, got '{workflowLora.GenerationSettings.Lora}'.");
        Check(workflowLora.GenerationSettings.Tool == "ComfyUI", "Expected ComfyUI workflow metadata to set the tool.");
    }

    // Three ways the graph heuristics used to produce confidently wrong prompts.
    private void CheckPromptExtractionHeuristics()
    {
        // No sampler, so both prompts come from the fallbacks. "negative space" is ordinary positive
        // vocabulary; matching it against the node's raw JSON dropped the positive and promoted it.
        var negativeVocabulary = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = """
            {
              "1": {"class_type":"CLIPTextEncode","inputs":{"text":"portrait with negative space and dramatic lighting"}},
              "2": {"class_type":"CLIPTextEncode","inputs":{"text":"blurry"},"_meta":{"title":"Negative Prompt"}}
            }
            """
        });

        Check(negativeVocabulary.Prompt == "portrait with negative space and dramatic lighting",
            $"Expected prompt vocabulary not to classify a node as negative, got '{negativeVocabulary.Prompt}'.");
        Check(negativeVocabulary.NegativePrompt == "blurry",
            $"Expected the titled node to supply the negative prompt, got '{negativeVocabulary.NegativePrompt}'.");

        // LoRA nodes on both sides of the sampler. The partial list captured mid-walk did not merge with
        // the full one, so every LoRA ahead of the sampler was listed twice.
        var straddlingLoras = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = """
            {
              "0": {"class_type":"LoraLoader","inputs":{"lora_name":"early-style.safetensors","strength_model":0.4}},
              "1": {"class_type":"KSampler","inputs":{"positive":["2",0],"negative":["3",0],"steps":20,"sampler_name":"euler"}},
              "2": {"class_type":"CLIPTextEncode","inputs":{"text":"a cat"}},
              "3": {"class_type":"CLIPTextEncode","inputs":{"text":"blurry"}},
              "9": {"class_type":"LoraLoader","inputs":{"lora_name":"late-style.safetensors","strength_model":0.6}}
            }
            """
        });

        Check(straddlingLoras.GenerationSettings.Lora == "early_style (0.40), late_style (0.60)",
            $"Expected each LoRA listed once regardless of node order, got '{straddlingLoras.GenerationSettings.Lora}'.");
        Check(straddlingLoras.GenerationSettings.Sampler == "euler",
            "Expected the sampler settings to survive the deferred LoRA assignment.");

        // A sampler this build has never heard of. Matching on conditioning inputs still follows its links
        // instead of dropping the whole workflow onto the longest-string guess.
        var customSampler = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = """
            {
              "1": {"class_type":"CLIPTextEncode","inputs":{"text":"a mountain"}},
              "2": {"class_type":"CLIPTextEncode","inputs":{"text":"lowres"}},
              "3": {"class_type":"ThirdPartyPackSampler","inputs":{"positive":["1",0],"negative":["2",0],"steps":25,"sampler_name":"dpmpp_2m"}}
            }
            """
        });

        Check(customSampler.Prompt == "a mountain" && customSampler.NegativePrompt == "lowres",
            $"Expected an unrecognized sampler's links to be followed, got '{customSampler.NegativePrompt}'.");
        Check(customSampler.GenerationSettings.Sampler == "dpmpp_2m",
            "Expected settings to be read from an unrecognized sampler.");

        // A conditioning node carries the same inputs but no settings; reading from it would blank them.
        var conditioningFirst = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = """
            {
              "1": {"class_type":"CLIPTextEncode","inputs":{"text":"a harbour"}},
              "2": {"class_type":"CLIPTextEncode","inputs":{"text":"grainy"}},
              "3": {"class_type":"ControlNetApplyAdvanced","inputs":{"positive":["1",0],"negative":["2",0]}},
              "4": {"class_type":"KSampler","inputs":{"positive":["3",0],"negative":["3",1],"steps":30,"sampler_name":"heun"}}
            }
            """
        });

        Check(conditioningFirst.GenerationSettings.Sampler == "heun",
            $"Expected a settings-free conditioning node not to lock out the sampler, got '{conditioningFirst.GenerationSettings.Sampler}'.");
    }

    private void CheckPngMetadataRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.png");
        try
        {
            WriteTinyPng(path, "parameters", "tiny prompt\nSteps: 1, Seed: 2");
            var result = ImageFileReader.Read(path);

            Check(result.Width == 1 && result.Height == 1, "Expected PNG dimensions.");
            Check(result.TextMetadata.TryGetValue("parameters", out var parameters) &&
                  parameters == "tiny prompt\nSteps: 1, Seed: 2",
                "Expected PNG text metadata.");
        }
        finally
        {
            DeleteFileQuietly(path);
        }
    }

    private void CheckPngMetadataLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.png");
        try
        {
            WritePngWithOversizedCompressedText(path);
            var result = ImageFileReader.Read(path);
            Check(result.TextMetadata.TryGetValue("parameters", out var parameters) && parameters == "searchable prompt",
                "Expected valid PNG metadata to remain searchable when another chunk exceeds the safety limit.");
            Check(!result.TextMetadata.ContainsKey("oversized"),
                "Expected oversized compressed PNG metadata to be skipped.");
        }
        finally
        {
            DeleteFileQuietly(path);
        }
    }

    // The reader stops at the first IDAT once it has text, so it must still walk to the end when it has none.
    private void CheckPngTextAfterImageData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.png");
        try
        {
            using (var stream = File.Create(path))
            {
                stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
                Span<byte> ihdr = stackalloc byte[13];
                BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], 1);
                BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), 1);
                ihdr[8] = 8;
                ihdr[9] = 2;
                WriteChunk(stream, "IHDR", ihdr);
                WriteChunk(stream, "IDAT", [0]);
                WriteChunk(stream, "IDAT", [0]);
                WriteChunk(stream, "tEXt", Encoding.UTF8.GetBytes("parameters\0late prompt"));
                WriteChunk(stream, "IEND", []);
            }

            var result = ImageFileReader.Read(path);
            Check(result.TextMetadata.TryGetValue("parameters", out var parameters) && parameters == "late prompt",
                "Expected PNG text after IDAT to be read when no text precedes it.");
        }
        finally
        {
            DeleteFileQuietly(path);
        }
    }

    private void CheckMetadataIndexRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.png");
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-metadata-{Guid.NewGuid():N}");
        try
        {
            WriteTinyPng(path, "parameters", "cached prompt\nSteps: 1, Seed: 2");
            Check(File.Exists(path), "Expected temporary metadata index source file.");
            using var repository = new MetadataRepository(databaseDirectory);
            Check(RoundTripsThroughIndex(repository, path), "Expected metadata index round trip.");
            var fingerprint = GetFingerprint(path);
            Check(repository.TryLoad(path, fingerprint, out _), "Expected matching source fingerprint to hit metadata cache.");
            Check(
                !repository.TryLoad(
                    path,
                    fingerprint with { FileLength = fingerprint.FileLength + 1 },
                    out _),
                "Expected a changed source fingerprint to miss metadata cache.");

            // A record written by another build of the app is stale, however well its file fingerprint matches.
            var otherBuild = MetadataRepository.CreateEntry(path, fingerprint, ImageMetadata.From(ImageFileReader.Read(path), new ExtractedPromptMetadata()));
            repository.Save(otherBuild with { Build = "someotherbuild" });
            Check(!repository.TryLoad(path, fingerprint, out _), "Expected a record from another build to miss metadata cache.");
        }
        finally
        {
            DeleteFileQuietly(path);
            DeleteDirectoryQuietly(databaseDirectory);
        }
    }

    // Same layout ComfyUI's SaveAnimatedWEBP writes: prompt in Model (0x0110), workflow in Make (0x010F).
    // Text reached through more than one link used to fall to the longest-string guess, which picked model files.
    private static void CheckPromptLinkWalk()
    {
        Dictionary<string, string> Prompt(string json) => new(StringComparer.OrdinalIgnoreCase) { ["prompt"] = json };

        // Flux: SamplerCustomAdvanced > CFGGuider > FluxGuidance > CLIPTextEncode, settings on separate nodes, and
        // a zeroed-out negative.
        var flux = PromptExtractor.ExtractAll(Prompt("""
            {
              "12": {"class_type":"UNETLoader","inputs":{"unet_name":"flux1-dev-fp8.safetensors"}},
              "6": {"class_type":"CLIPTextEncode","inputs":{"text":"a lighthouse at dusk","clip":["11",0]}},
              "26": {"class_type":"FluxGuidance","inputs":{"guidance":3.5,"conditioning":["6",0]}},
              "30": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["6",0]}},
              "22": {"class_type":"CFGGuider","inputs":{"model":["12",0],"positive":["26",0],"negative":["30",0],"cfg":1}},
              "16": {"class_type":"KSamplerSelect","inputs":{"sampler_name":"euler"}},
              "25": {"class_type":"RandomNoise","inputs":{"noise_seed":42}},
              "17": {"class_type":"BasicScheduler","inputs":{"scheduler":"simple","steps":20,"denoise":1,"model":["12",0]}},
              "13": {"class_type":"SamplerCustomAdvanced","inputs":{"guider":["22",0],"noise":["25",0],"sampler":["16",0],"sigmas":["17",0],"latent_image":["27",0]}}
            }
            """));
        Check(flux.Prompt == "a lighthouse at dusk" && flux.NegativePrompt == "",
            $"Expected the Flux prompt through guider and guidance nodes, got '{flux.Prompt}' / '{flux.NegativePrompt}'.");
        Check(flux.GenerationSettings is { Model: "flux1-dev-fp8.safetensors", Sampler: "euler", Seed: "42" } &&
              flux.GenerationSettings.Settings == "Steps 20, CFG 1, Scheduler simple, Denoise 1",
            $"Expected Flux settings from the nodes feeding the sampler, got '{flux.GenerationSettings.Settings}'.");

        // A refine pass wired to one conditioning on both sides, and steps fed by a primitive node.
        var refine = PromptExtractor.ExtractAll(Prompt("""
            {
              "20": {"class_type":"PrimitiveInt","inputs":{"value":8}},
              "9": {"class_type":"BasicScheduler","inputs":{"scheduler":"beta","steps":["20",0],"model":["6",0]}},
              "4": {"class_type":"CustomVideoConditioning","inputs":{"prompt":"a wolf waves","clip":["3",0]}},
              "16": {"class_type":"BasicGuider","inputs":{"model":["6",0],"conditioning":["4",0]}},
              "14": {"class_type":"SamplerCustomAdvanced","inputs":{"guider":["16",0],"sigmas":["9",0],"latent_image":["4",1]}},
              "30": {"class_type":"AudioRefineSampler","inputs":{"steps":6,"positive":["4",0],"negative":["4",0],"latent":["14",0]}}
            }
            """));
        Check(refine.Prompt == "a wolf waves" && refine.NegativePrompt == "",
            $"Expected a negative wired to the positive's conditioning to be dropped, got '{refine.NegativePrompt}'.");
        Check(refine.GenerationSettings.Settings == "Steps 8, Scheduler beta",
            $"Expected steps from the primitive node feeding the scheduler, got '{refine.GenerationSettings.Settings}'.");

        var stringNode = PromptExtractor.ExtractAll(Prompt("""
            {
              "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"realisticVision.safetensors"}},
              "20": {"class_type":"PrimitiveStringMultiline","inputs":{"value":"a cozy cabin in the woods"}},
              "6": {"class_type":"CLIPTextEncode","inputs":{"text":["20",0],"clip":["4",1]}},
              "7": {"class_type":"CLIPTextEncode","inputs":{"text":"bad hands","clip":["4",1]}},
              "3": {"class_type":"KSampler","inputs":{"positive":["6",0],"negative":["7",0],"model":["4",0],"seed":1,"steps":20,"sampler_name":"euler"}}
            }
            """));
        Check(stringNode.Prompt == "a cozy cabin in the woods" && stringNode.NegativePrompt == "bad hands",
            $"Expected text from the string node feeding the encoder, got '{stringNode.Prompt}'.");

        var noNegativeInfotext = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["parameters"] = "a cat in a hat\nSteps: 20, Sampler: Euler a, CFG scale: 7, Seed: 1"
        });
        Check(noNegativeInfotext.Prompt == "a cat in a hat" && noNegativeInfotext.GenerationSettings.Seed == "1",
            $"Expected the settings line kept out of a prompt without a negative, got '{noNegativeInfotext.Prompt}'.");

        var workflow = PromptExtractor.ExtractAll(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["workflow"] = """
            {"nodes":[
              {"id":1,"type":"LoraLoaderModelOnly","properties":{"cnr_id":"comfy-core","Node name for S&R":"LoraLoaderModelOnly"},"widgets_values":["some-style.safetensors",1]},
              {"id":3,"type":"CLIPTextEncode","properties":{"cnr_id":"comfy-core"},"widgets_values":["a quiet path"]}
            ]}
            """
        });
        Check(workflow.Prompt == "a quiet path",
            $"Expected workflow node properties and file names not to be taken as the prompt, got '{workflow.Prompt}'.");
    }

    // Editors and screenshot tools write text chunks too; the longest of them used to be shown as the prompt.
    private static void CheckEditorMetadataIsNotAPrompt()
    {
        Dictionary<string, string> Text(params (string Key, string Value)[] entries)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in entries)
            {
                metadata[key] = value;
            }

            return metadata;
        }

        var editor = PromptExtractor.ExtractAll(Text(
            ("Comment", "Created with GIMP"),
            ("Software", "gnome-screenshot"),
            ("Author", "someone"),
            ("Creation Time", "Sun 28 Sep 2026 12:00:00 PM EDT"),
            ("date:create", "2026-09-28T12:00:00+00:00"),
            ("XML:com.adobe.xmp", "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF /></x:xmpmeta>")));
        Check(editor.Prompt == "" && editor.GenerationSettings.Tool == "",
            "Expected editor and screenshot text chunks not to be taken as a prompt.");

        var easyDiffusion = PromptExtractor.ExtractAll(Text(
            ("prompt", "a castle on a hill, oil painting"),
            ("negative_prompt", "blurry"),
            ("seed", "42")));
        Check(easyDiffusion.Prompt == "a castle on a hill, oil painting",
            "Expected a plain-text prompt key to be used as the prompt.");
        Check(easyDiffusion.GenerationSettings.Tool != "ComfyUI",
            "Expected a plain-text prompt key not to be labeled ComfyUI.");

        var novelAi = PromptExtractor.ExtractAll(Text(
            ("Title", "AI generated image"),
            ("Description", "1girl, cherry blossoms"),
            ("Software", "NovelAI"),
            ("Comment", """{"prompt": "1girl, cherry blossoms", "uc": "lowres"}""")));
        Check(novelAi.Prompt == "1girl, cherry blossoms", "Expected NovelAI's Description to be used as the prompt.");
    }

    private static void CheckComfyWebPExif()
    {
        const string prompt = """{"1":{"class_type":"CLIPTextEncode","inputs":{"text":"fox in snow"}},"2":{"class_type":"CLIPTextEncode","inputs":{"text":"blurry"}},"3":{"class_type":"KSampler","inputs":{"positive":["1",0],"negative":["2",0],"steps":20}}}""";
        var comfy = ImageFileReader.ReadWebP(new MemoryStream(ExtendedWebPHeader(
            (0x010F, """workflow:{"nodes":[]}"""),
            (0x0110, "prompt:" + prompt))));
        Check(comfy.TextMetadata.TryGetValue("prompt", out var promptJson) && promptJson == prompt,
            "Expected the ComfyUI prompt to be read from EXIF Model.");
        Check(comfy.TextMetadata.TryGetValue("workflow", out var workflowJson) && workflowJson == """{"nodes":[]}""",
            "Expected the ComfyUI workflow to be read from EXIF Make.");
        var extracted = PromptExtractor.ExtractAll(comfy.TextMetadata);
        Check(extracted.Prompt == "fox in snow" && extracted.NegativePrompt == "blurry",
            "Expected prompts to be extracted from ComfyUI WebP EXIF.");

        var camera = ImageFileReader.ReadWebP(new MemoryStream(ExtendedWebPHeader(
            (0x010F, "Canon"),
            (0x0110, "Canon EOS R5: {not comfy}"))));
        Check(camera.TextMetadata.Count == 0, "Expected ordinary camera Make/Model EXIF to be ignored.");
    }

    // Layout ffmpeg writes for ComfyUI video savers with `movflags use_metadata_tags`, moov after mdat.
    private static void CheckMp4Metadata()
    {
        const string prompt = """{"1":{"class_type":"CLIPTextEncode","inputs":{"text":"waves at night"}}}""";
        var tkhd = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), 832u << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), 480u << 16);
        var videoHandler = new byte[24];
        "vide"u8.CopyTo(videoHandler.AsSpan(8));
        var soundHandler = new byte[24];
        "soun"u8.CopyTo(soundHandler.AsSpan(8));

        byte[] Key(string name) => [.. BigEndian((uint)(8 + name.Length)), .. "mdta"u8, .. Encoding.UTF8.GetBytes(name)];
        byte[] Item(uint index, string value) =>
            Mp4Box(index, Mp4Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(value)]));

        var file = new MemoryStream();
        file.Write(Mp4Box("ftyp", "isom"u8.ToArray()));
        file.Write(Mp4Box("mdat", new byte[4096]));
        file.Write(Mp4Box("moov",
            // An audio track first, so its (zero) size must not be taken for the frame size.
            Mp4Box("trak", Mp4Box("tkhd", new byte[84]), Mp4Box("mdia", Mp4Box("hdlr", soundHandler))),
            Mp4Box("trak", Mp4Box("tkhd", tkhd), Mp4Box("mdia", Mp4Box("hdlr", videoHandler))),
            Mp4Box("udta", Mp4Box("meta", [0, 0, 0, 0],
                Mp4Box("hdlr", new byte[25]),
                Mp4Box("keys", [0, 0, 0, 0, .. BigEndian(3), .. Key("prompt"), .. Key("workflow"), .. Key("encoder")]),
                Mp4Box("ilst", Item(1, prompt), Item(2, """{"nodes":[]}"""), Item(3, "Lavf62.12.103"))))));
        file.Position = 0;

        var result = Mp4MetadataReader.Read(file);
        Check(result is { Width: 832, Height: 480 }, "Expected the MP4 video track size.");
        Check(result.TextMetadata.TryGetValue("prompt", out var promptJson) && promptJson == prompt,
            "Expected the MP4 prompt metadata key.");
        Check(result.TextMetadata.TryGetValue("workflow", out var workflowJson) && workflowJson == """{"nodes":[]}""",
            "Expected the MP4 workflow metadata key.");
        Check(PromptExtractor.ExtractAll(result.TextMetadata).Prompt == "waves at night",
            "Expected prompts to be extracted from MP4 metadata.");
        Check(!result.TextMetadata.ContainsKey("encoder"), "Expected plain MP4 tags such as the encoder to be ignored.");

        // Older VideoHelperSuite: one iTunes-style comment, no keys box, the prompt graph as JSON text inside it.
        var comment = JsonSerializer.Serialize(new Dictionary<string, object> { ["prompt"] = prompt, ["workflow"] = new { nodes = Array.Empty<int>() } });
        var commentFile = new MemoryStream();
        commentFile.Write(Mp4Box("ftyp", "isom"u8.ToArray()));
        commentFile.Write(Mp4Box("moov",
            Mp4Box("trak", Mp4Box("tkhd", tkhd), Mp4Box("mdia", Mp4Box("hdlr", videoHandler))),
            Mp4Box("udta", Mp4Box("meta", [0, 0, 0, 0],
                Mp4Box("hdlr", new byte[25]),
                Mp4Box("ilst", Mp4Box(0xA9636D74, Mp4Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(comment)])))))));
        commentFile.Position = 0;
        var commentResult = Mp4MetadataReader.Read(commentFile);
        Check(commentResult.TextMetadata.TryGetValue("prompt", out var unpacked) && unpacked == prompt &&
              commentResult.TextMetadata.TryGetValue("workflow", out var unpackedWorkflow) && unpackedWorkflow == """{"nodes":[]}""",
            "Expected the prompt and workflow unpacked from an MP4 comment.");
        Check(PromptExtractor.ExtractAll(commentResult.TextMetadata).Prompt == "waves at night",
            "Expected prompts to be extracted from an MP4 comment.");
    }

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    // A box's payload is its own fields followed by child boxes, so both are just concatenated parts.
    private static byte[] Mp4Box(string type, params byte[][] parts) =>
        Mp4Box(BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(type)), parts);

    private static byte[] Mp4Box(uint type, params byte[][] parts)
    {
        var payload = new List<byte>();
        foreach (var part in parts)
        {
            payload.AddRange(part);
        }

        return [.. BigEndian((uint)(8 + payload.Count)), .. BigEndian(type), .. payload];
    }

    private static byte[] ExtendedWebPHeader(params (ushort Tag, string Text)[] exifAscii)
    {
        using var stream = new MemoryStream();
        var exif = exifAscii.Length == 0 ? [] : BuildAsciiExif(exifAscii);
        var exifChunkLength = exif.Length == 0 ? 0 : 8 + exif.Length + (exif.Length & 1);
        stream.Write("RIFF"u8);
        stream.Write(BitConverter.GetBytes(4 + 8 + 10 + exifChunkLength));
        stream.Write("WEBPVP8X"u8);
        stream.Write(BitConverter.GetBytes(10));
        // Flags, 3 reserved bytes, then 24-bit width-1 and height-1.
        stream.Write([(byte)(exif.Length > 0 ? 0x08 : 0), 0, 0, 0, 63, 0, 0, 31, 0, 0]);
        if (exif.Length > 0)
        {
            stream.Write("EXIF"u8);
            stream.Write(BitConverter.GetBytes(exif.Length));
            stream.Write(exif);
            if ((exif.Length & 1) != 0)
            {
                stream.WriteByte(0);
            }
        }

        return stream.ToArray();
    }

    // Little-endian TIFF with one IFD of ASCII entries, values stored after the IFD.
    private static byte[] BuildAsciiExif((ushort Tag, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("II"u8);
        writer.Write((ushort)42);
        writer.Write(8u);
        writer.Write((ushort)entries.Length);
        var valueOffset = 8 + 2 + entries.Length * 12 + 4;
        var values = new List<byte[]>();
        foreach (var (tag, text) in entries)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\0");
            writer.Write(tag);
            writer.Write((ushort)2);
            writer.Write((uint)bytes.Length);
            writer.Write((uint)valueOffset);
            valueOffset += bytes.Length;
            values.Add(bytes);
        }

        writer.Write(0u);
        foreach (var bytes in values)
        {
            writer.Write(bytes);
        }

        return stream.ToArray();
    }

    // A batched lookup must return only rows whose fingerprint still matches, paired to the right path.
    private void CheckMetadataBatchLoad()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-load-{Guid.NewGuid():N}");
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-load-db-{Guid.NewGuid():N}");
        var firstPath = Path.Combine(folder, "first.png");
        var secondPath = Path.Combine(folder, "second.png");
        var missingPath = Path.Combine(folder, "missing.png");
        try
        {
            Directory.CreateDirectory(folder);
            WriteTinyPng(firstPath, "parameters", "first");
            WriteTinyPng(secondPath, "parameters", "second");
            WriteTinyPng(missingPath, "parameters", "missing");
            using var repository = new MetadataRepository(databaseDirectory);
            var readResult = new ImageReadResult(1, 1, new(StringComparer.OrdinalIgnoreCase));
            var firstFingerprint = GetFingerprint(firstPath);
            var secondFingerprint = GetFingerprint(secondPath);
            var missingFingerprint = GetFingerprint(missingPath);
            repository.SaveMany(
            [
                MetadataRepository.CreateEntry(firstPath, firstFingerprint, ImageMetadata.From(readResult, new ExtractedPromptMetadata { Prompt = "first" })),
                MetadataRepository.CreateEntry(secondPath, secondFingerprint, ImageMetadata.From(readResult, new ExtractedPromptMetadata { Prompt = "second" }))
            ]);

            var loaded = repository.LoadMany(
            [
                new MetadataLookup(firstPath, firstFingerprint),
                new MetadataLookup(secondPath, secondFingerprint),
                new MetadataLookup(missingPath, missingFingerprint)
            ], CancellationToken.None);

            Check(loaded.Count == 2, "Expected a batch load to return only the records that exist.");
            Check(loaded.TryGetValue(firstPath, out var first) && first.Metadata.Prompt == "first",
                "Expected a batch load to pair each record with its own path.");
            Check(loaded.TryGetValue(secondPath, out var second) && second.Metadata.Prompt == "second",
                "Expected a batch load to pair each record with its own path.");
            Check(!loaded.ContainsKey(missingPath), "Expected an unsaved image to miss the batch load.");

            // Same paths, changed files: every row is stale and none may be served.
            var staleLoad = repository.LoadMany(
            [
                new MetadataLookup(firstPath, firstFingerprint with { FileLength = firstFingerprint.FileLength + 1 }),
                new MetadataLookup(secondPath, secondFingerprint with
                {
                    LastWriteTimeUtcTicks = secondFingerprint.LastWriteTimeUtcTicks + 1
                })
            ], CancellationToken.None);
            Check(staleLoad.Count == 0, "Expected a changed source fingerprint to miss the batch load.");

            Check(repository.LoadMany([], CancellationToken.None).Count == 0,
                "Expected an empty batch to load nothing.");
        }
        finally
        {
            DeleteFileQuietly(firstPath);
            DeleteFileQuietly(secondPath);
            DeleteFileQuietly(missingPath);
            DeleteDirectoryQuietly(folder);
            DeleteDirectoryQuietly(databaseDirectory);
        }
    }

    private void CheckMetadataBatchSave()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-batch-{Guid.NewGuid():N}");
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-batch-db-{Guid.NewGuid():N}");
        var firstPath = Path.Combine(folder, "first.png");
        var secondPath = Path.Combine(folder, "second.png");
        try
        {
            Directory.CreateDirectory(folder);
            WriteTinyPng(firstPath, "parameters", "first");
            WriteTinyPng(secondPath, "parameters", "second");
            using var repository = new MetadataRepository(databaseDirectory);
            var readResult = new ImageReadResult(1, 1, new(StringComparer.OrdinalIgnoreCase));
            var firstFingerprint = GetFingerprint(firstPath);
            var secondFingerprint = GetFingerprint(secondPath);
            var firstEntry = MetadataRepository.CreateEntry(firstPath, firstFingerprint, ImageMetadata.From(readResult, new ExtractedPromptMetadata { Prompt = "first" }));
            var secondEntry = MetadataRepository.CreateEntry(secondPath, secondFingerprint, ImageMetadata.From(readResult, new ExtractedPromptMetadata { Prompt = "second" }));
            repository.SaveMany([firstEntry, secondEntry]);

            Check(
                repository.TryLoad(firstPath, firstFingerprint, out var first) &&
                repository.TryLoad(secondPath, secondFingerprint, out var second) &&
                first.Metadata.Prompt == "first" &&
                second.Metadata.Prompt == "second",
                "Expected batched metadata entries to persist together.");

            var batchCatalog = new GalleryCatalog();
            var firstItem = CreateImageItem(firstPath);
            var secondItem = CreateImageItem(secondPath);
            batchCatalog.Add(firstItem);
            batchCatalog.Add(secondItem);
            firstItem.ApplyMetadataResult(MetadataLoadResult.Success(firstEntry, needsSave: false));
            secondItem.ApplyMetadataResult(MetadataLoadResult.Success(secondEntry, needsSave: false));
            batchCatalog.MarkMetadataLoaded(firstItem);
            batchCatalog.MarkMetadataLoaded(secondItem);
            Check(
                firstItem.HasLoadedMetadata &&
                secondItem.HasLoadedMetadata &&
                MatchCount(batchCatalog, "first", SearchScope.PositivePrompt) == 1 &&
                MatchCount(batchCatalog, "second", SearchScope.PositivePrompt) == 1,
                "Expected a metadata result batch to update item state and search rows.");
            batchCatalog.Clear();
        }
        finally
        {
            DeleteDirectoryQuietly(folder);
            DeleteDirectoryQuietly(databaseDirectory);
        }
    }

    private void CheckMetadataIndexCleanup()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-metadata-{Guid.NewGuid():N}");
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-index-{Guid.NewGuid():N}");
        var keepPath = Path.Combine(folder, "keep.png");
        var deletePath = Path.Combine(folder, "delete.png");
        var prunePath = Path.Combine(folder, "prune.png");

        try
        {
            Directory.CreateDirectory(folder);
            WriteTinyPng(keepPath, "parameters", "keep prompt");
            WriteTinyPng(deletePath, "parameters", "delete prompt");
            WriteTinyPng(prunePath, "parameters", "prune prompt");

            using var repository = new MetadataRepository(databaseDirectory);
            SaveSelfCheckMetadata(repository, keepPath, "keep prompt");
            SaveSelfCheckMetadata(repository, deletePath, "delete prompt");
            SaveSelfCheckMetadata(repository, prunePath, "prune prompt");

            repository.DeletePaths([deletePath]);
            Check(!repository.TryLoad(deletePath, GetFingerprint(deletePath), out _), "Expected deleted metadata index path to be removed.");

            repository.PruneMissing([keepPath], includeSubfolders: false);
            Check(repository.TryLoad(keepPath, GetFingerprint(keepPath), out _), "Expected current metadata index path to remain.");
            Check(!repository.TryLoad(prunePath, GetFingerprint(prunePath), out _), "Expected missing metadata index path to be pruned.");
        }
        finally
        {
            DeleteDirectoryQuietly(folder);
            DeleteDirectoryQuietly(databaseDirectory);
        }
    }

    private async Task CheckMetadataFailureClassification()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"comfypromptviewer-selfcheck-failure-{Guid.NewGuid():N}");
        try
        {
            using var repository = new MetadataRepository(directory);
            var service = new ImageMetadataService(repository);
            var missingPath = Path.Combine(directory, "missing.png");
            var result = await service.LoadAsync(
                missingPath,
                default,
                skipCacheLookup: false,
                persistResult: true,
                default);
            Check(
                result.Status == MetadataLoadStatus.TransientIoFailure,
                "Expected a missing image to be classified as a transient I/O failure.");
        }
        finally
        {
            DeleteDirectoryQuietly(directory);
        }
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Self-check cleanup failed to delete file {path}: {ex.Message}");
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Self-check cleanup failed to delete directory {path}: {ex.Message}");
        }
    }

    private static void SaveSelfCheckMetadata(MetadataRepository repository, string path, string prompt)
    {
        repository.Save(MetadataRepository.CreateEntry(
            path,
            GetFingerprint(path),
            ImageMetadata.From(
                new ImageReadResult(1, 1, new(StringComparer.OrdinalIgnoreCase)),
                new ExtractedPromptMetadata { Prompt = prompt })));
    }

    private static ImageItem CreateImageItem(string path, SourceFingerprint fingerprint = default)
    {
        return new ImageItem(
            new ImageFileEntry(path, fingerprint),
            tileSize: 120,
            cacheScope: ItemThumbnailScope);
    }

    private static SourceFingerprint GetFingerprint(string path)
    {
        var fileInfo = new FileInfo(path);
        return new SourceFingerprint(fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);
    }

    private static ThumbnailKey NewThumbnailKey(string label)
    {
        return ThumbnailPack.CreateKey($"{Path.GetTempPath()}{label}-{Guid.NewGuid():N}.png", 1, 180);
    }

    private void CheckThumbnailCacheWriteBackpressure()
    {
        var firstKey = NewThumbnailKey("backpressure-1");
        var secondKey = NewThumbnailKey("backpressure-2");

        Check(ItemThumbnailService.TryBeginCacheWrite(ItemThumbnailScope, firstKey), "Expected first thumbnail cache write slot.");
        try
        {
            Check(!ItemThumbnailService.TryBeginCacheWrite(ItemThumbnailScope, secondKey), "Expected busy thumbnail cache writer to reject queued writes.");
        }
        finally
        {
            ItemThumbnailService.EndCacheWrite(ItemThumbnailScope, firstKey);
        }
    }

    private void CheckDeferredThumbnailCacheWriteQueue()
    {
        var activeKey = NewThumbnailKey("queue-active");
        var deferredKey = NewThumbnailKey("queue-deferred");
        var item = CreateImageItem(Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-missing.png"));

        Check(ItemThumbnailService.TryBeginCacheWrite(ItemThumbnailScope, activeKey), "Expected active thumbnail cache write slot.");
        try
        {
            var queuedBefore = ItemThumbnailService.PendingWriteCount;
            Check(ItemThumbnailService.TryQueueCacheWrite(item.ThumbnailSource, deferredKey), "Expected deferred thumbnail cache write to queue.");
            Check(ItemThumbnailService.PendingWriteCount == queuedBefore + 1,
                "Expected the pending write count to track the deferred queue so bulk producers can throttle.");
            Check(!ItemThumbnailService.TryQueueCacheWrite(item.ThumbnailSource, deferredKey), "Expected duplicate deferred thumbnail cache write to be ignored.");
            ItemThumbnailService.ClearDeferredWrites();
            Check(ItemThumbnailService.PendingWriteCount == 0,
                "Expected clearing deferred writes to empty the pending write count.");
        }
        finally
        {
            ItemThumbnailService.EndCacheWrite(ItemThumbnailScope, activeKey);
        }
    }

    // The deferred queue refills from the viewport on every scroll pass, so a source that cannot be encoded
    // has to be given up on. Retrying it was writing an identical log line per pass, dozens per minute.
    private void CheckDeferredThumbnailCacheWriteGivesUpAfterFailure()
    {
        var item = CreateImageItem(
            Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-missing.png"));
        var failingKey = NewThumbnailKey("give-up-failing");

        Check(ItemThumbnailService.TryQueueCacheWrite(item.ThumbnailSource, failingKey),
            "Expected the first deferred thumbnail cache write to queue.");
        DrainThumbnailCacheWrites();
        Check(!ItemThumbnailService.TryQueueCacheWrite(item.ThumbnailSource, failingKey),
            "Expected a deferred thumbnail cache write that already failed not to be queued again.");

        // Only the attempt that failed is remembered. A replaced file or a resized tile yields a different
        // key, and that has to get a real attempt rather than inherit the give-up.
        Check(ItemThumbnailService.TryQueueCacheWrite(item.ThumbnailSource, NewThumbnailKey("give-up-retry")),
            "Expected a different thumbnail key for the same item to still be attempted.");
        DrainThumbnailCacheWrites();
    }

    private static void DrainThumbnailCacheWrites()
    {
        ItemThumbnailService.PauseAndDrainWritesAsync().GetAwaiter().GetResult();
        ItemThumbnailService.ResumeWrites();
    }

    private void CheckDeferredThumbnailCacheWritePause()
    {
        var activeKey = NewThumbnailKey("pause-active");
        var deferredKey = NewThumbnailKey("pause-deferred");
        var item = CreateImageItem(Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-missing.png"));

        ItemThumbnailService.SetCacheWritePause(() => true);
        try
        {
            Check(ItemThumbnailService.TryQueueCacheWrite(item.ThumbnailSource, deferredKey), "Expected paused deferred thumbnail cache write to queue.");
            Check(ItemThumbnailService.TryBeginCacheWrite(ItemThumbnailScope, activeKey), "Expected paused deferred thumbnail cache writer not to take active slot.");
            ItemThumbnailService.EndCacheWrite(ItemThumbnailScope, activeKey);
            ItemThumbnailService.ClearDeferredWrites();
        }
        finally
        {
            ItemThumbnailService.SetCacheWritePause(null);
        }
    }

    // 0 means "decode natively". Ordinary generation resolutions must land there, because for PNG the
    // resample is pure extra work on top of a decode that happens regardless.
    private void CheckSelectedPreviewDecodeWidth()
    {
        Check(ThumbnailDecoder.GetPreviewDecodeWidth(832, 1216) == 0,
            "Expected a source narrower than the cap to decode natively rather than upscale.");
        Check(ThumbnailDecoder.GetPreviewDecodeWidth(1024, 1024) == 0,
            "Expected a square SDXL source to decode natively.");
        Check(ThumbnailDecoder.GetPreviewDecodeWidth(1536, 1536) == 0,
            "Expected a 1536 source to decode natively.");
        Check(ThumbnailDecoder.GetPreviewDecodeWidth(2048, 2048) == 0,
            "Expected a 2048 source to decode natively.");
        Check(ThumbnailDecoder.GetPreviewDecodeWidth(2176, 2176) == 0,
            "Expected a source marginally over the cap to skip resampling.");

        Check(ThumbnailDecoder.GetPreviewDecodeWidth(4096, 4096) == 2048,
            "Expected an upscaled source to be capped so preview memory stays bounded.");
        Check(ThumbnailDecoder.GetPreviewDecodeWidth(4096, 6144) == 2048,
            "Expected a tall upscale to be capped by width.");

        // Stays under the width cap but would decode to 67 MB natively.
        var extremeAspect = ThumbnailDecoder.GetPreviewDecodeWidth(2048, 8192);
        Check(extremeAspect is > 0 and < 2048,
            "Expected the pixel budget to catch an extreme aspect ratio the width cap misses.");

        Check(ThumbnailDecoder.GetPreviewDecodeWidth(0, 0) > 0,
            "Expected an unreadable header to fall back to the capped decode.");
    }

    // The sidebar keeps the previous image on screen until a replacement is displayed, so a decode that
    // produces nothing has to be distinguishable from one still in flight. Otherwise a partially written
    // or corrupt file leaves the previous picture next to the new file's metadata indefinitely.
    private void CheckSelectedPreviewFailureIsObservable()
    {
        var missing = CreateImageItem(
            Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-absent.png"));

        Check(!missing.SelectedPreviewUnavailable,
            "Expected a preview that has not been attempted to read as available.");

        var decodeFailed = false;
        try
        {
            ThumbnailDecoder.DecodeSelectedPreview(missing.Path, 0, 0).Dispose();
        }
        catch (IOException)
        {
            decodeFailed = true;
        }

        Check(decodeFailed, "Expected an unreadable source to produce no preview.");
        missing.MarkSelectedPreviewUnavailable();
        Check(missing.SelectedPreview is null && missing.SelectedPreviewUnavailable,
            "Expected a failed preview decode to be observable, not indistinguishable from decoding.");

        missing.ReleaseSelectedPreview();
        Check(!missing.SelectedPreviewUnavailable,
            "Expected releasing a preview to clear the unavailable state so a retry can run.");
    }

    // Prefetch is allowed to overlap the viewport only when the decode is cheap. If a cold ahead load
    // ever starts while visible work is pending, first-paint on a new folder regresses.
    private void CheckThumbnailPrefetchPolicy()
    {
        Check(!ThumbnailLoadCoordinator.CanStartAheadLoad(
                isWarm: false, hasVisibleWork: true, activeLoadCount: 0, activeAheadLoads: 0),
            "Expected a cold ahead load to yield to pending visible work.");
        Check(ThumbnailLoadCoordinator.CanStartAheadLoad(
                isWarm: false, hasVisibleWork: false, activeLoadCount: 0, activeAheadLoads: 0),
            "Expected a cold ahead load to run once the viewport is idle.");
        Check(ThumbnailLoadCoordinator.CanStartAheadLoad(
                isWarm: true, hasVisibleWork: true, activeLoadCount: 1, activeAheadLoads: 0),
            "Expected a warm ahead load to overlap visible work.");
        Check(!ThumbnailLoadCoordinator.CanStartAheadLoad(
                isWarm: true, hasVisibleWork: false, activeLoadCount: 64, activeAheadLoads: 0),
            "Expected the total active load cap to bound warm prefetch.");
        Check(!ThumbnailLoadCoordinator.CanStartAheadLoad(
                isWarm: true, hasVisibleWork: false, activeLoadCount: 1, activeAheadLoads: 64),
            "Expected the warm ahead cap to bound warm prefetch.");

        // The lookahead budget has to follow the scroll direction, not straddle it.
        var down = ViewportScheduler.GetAheadRowWindow(1);
        var up = ViewportScheduler.GetAheadRowWindow(-1);
        Check(down.RowsBelow > down.RowsAbove, "Expected scrolling down to prefetch mostly below.");
        Check(up.RowsAbove > up.RowsBelow, "Expected scrolling up to prefetch mostly above.");
        Check(down.RowsAbove > 0 && up.RowsBelow > 0,
            "Expected the trailing side to retain some prefetch so a reversal is not fully cold.");
        Check(down.RowsBelow == up.RowsAbove && down.RowsAbove == up.RowsBelow,
            "Expected the prefetch window to be symmetric under a direction flip.");
    }

    // Abandons scrolled-away tiles without abandoning a selection outside the last scheduled window.
    private void CheckRetainedViewportWindow()
    {
        var coordinator = new ThumbnailLoadCoordinator(ItemThumbnailService, ItemDecodedImageCache);
        var visible = CreateImageItem(Path.Combine(Path.GetTempPath(), "retained-visible.png"));
        var ahead = CreateImageItem(Path.Combine(Path.GetTempPath(), "retained-ahead.png"));
        var offscreen = CreateImageItem(Path.Combine(Path.GetTempPath(), "retained-offscreen.png"));

        Check(coordinator.IsRetained(offscreen),
            "Expected everything to be retained before the first viewport schedule.");

        using var cancellation = new CancellationTokenSource();
        try
        {
            coordinator.ScheduleViewport([visible], [ahead], cancellation.Token);
            Check(coordinator.IsRetained(visible) && coordinator.IsRetained(ahead),
                "Expected visible and ahead items to be retained.");
            Check(!coordinator.IsRetained(offscreen),
                "Expected an item outside the prefetch window to be abandoned.");

            coordinator.EnqueueVisible(offscreen, cancellation.Token);
            Check(coordinator.IsRetained(offscreen),
                "Expected selecting an item outside the last schedule to retain it.");

            coordinator.ScheduleViewport([visible], [], cancellation.Token);
            Check(!coordinator.IsRetained(ahead),
                "Expected a later schedule to abandon items that dropped out of the window.");
        }
        finally
        {
            // Scheduling starts real decode tasks against paths that do not exist. Drop them rather than
            // leaving them to fail against a shared cache after this check has moved on.
            cancellation.Cancel();
            coordinator.Clear();
        }
    }

    // One pack file replaced thousands of small JPEGs, so the round trip, the key's dependence on source
    // version and width, and instant clearing are all load-bearing.
    private async Task CheckFolderSession()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cpv-session-{Guid.NewGuid():N}");
        var photos = Path.Combine(root, "photos");
        var empty = Path.Combine(root, "empty");
        var nested = Path.Combine(root, "nested", "deep");
        Directory.CreateDirectory(photos);
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(photos, "b.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(photos, "a.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(photos, "c.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(nested, "x.png"), [1, 2, 3]);

        var preferences = new UserPreferencesStore(Path.Combine(root, "prefs"));
        var session = new FolderSession(
            preferences,
            ItemThumbnailService,
            new ThumbnailLoadCoordinator(ItemThumbnailService, ItemDecodedImageCache),
            ItemDecodedImageCache,
            ItemMetadataRepository,
            new MetadataScanCoordinator(ItemMetadataRepository))
        {
            TileSize = 120,
            SortMode = SortMode.Name
        };

        var log = new List<string>();
        string? failure = null;
        session.Clearing += () => log.Add("clearing");
        session.FolderOpening += _ => log.Add("opening");
        session.FolderScanned += count => log.Add($"scanned:{count}");
        session.FolderShown += () => log.Add("shown");
        session.FilterApplied += (items, reset) => log.Add($"filtered:{items.Count}:{reset}");
        session.CatalogLoaded += picked => log.Add($"loaded:{picked}");
        session.Closed += () => log.Add("closed");
        session.LoadFailed += message => failure = message;

        try
        {
            await session.OpenFolderAsync(photos);
            Check(string.Join(",", log) == "clearing,opening,scanned:3,shown,filtered:3:True,loaded:",
                "Expected opening a folder to clear, announce, scan, show, filter and finish in that order.");
            Check(session.CurrentFolderPath == photos && session.Catalog.Count == 3 && session.CacheScope is not null,
                "Expected the opened folder to fill the catalog and own a thumbnail cache scope.");
            Check(session.Catalog.Items.Select(item => item.FileName).SequenceEqual(["a.png", "b.png", "c.jpg"]),
                "Expected the catalog to follow the session's sort mode.");
            Check(preferences.LoadLastFolderPath() == photos && preferences.LoadRecentFolders()[0].ImageCount == 3,
                "Expected a loaded folder to be remembered as the last and most recent one.");

            var selected = Path.Combine(photos, "b.png");
            session.SelectedPath = selected;
            log.Clear();
            await session.OpenFolderAsync(photos);
            Check(log[^1] == $"loaded:{selected}",
                "Expected a reload to hand back the selected path so the window can select it again.");

            log.Clear();
            await session.OpenFolderAsync(empty);
            Check(failure!.StartsWith($"No {MediaFiles.SupportedTypesText} files found", StringComparison.Ordinal) &&
                  log.Contains("closed") && !log.Contains("shown") &&
                  session.CurrentFolderPath is null && session.Catalog.Count == 0,
                "Expected an empty folder to fail, close the session and leave nothing loaded.");

            await session.OpenFolderAsync(Path.Combine(root, "nested"));
            Check(failure.Contains("Include subfolders", StringComparison.Ordinal),
                "Expected a folder whose images are all nested to suggest turning on Include subfolders.");

            var changes = 0;
            session.IncludeSubfoldersChanged += () => changes++;
            Check(await session.SetIncludeSubfoldersAsync(true) && session.IncludeSubfolders && changes == 1,
                "Expected turning subfolders on to be accepted and announced.");
            await session.OpenFolderAsync(Path.Combine(root, "nested"));
            Check(session.Catalog.Count == 1 && session.Catalog.Items[0].FileName == "x.png",
                "Expected Include subfolders to find the nested image.");
            string? message = null;
            session.Message += text => message = text;
            Check(!await session.SetIncludeSubfoldersAsync(false) && session.IncludeSubfolders &&
                  message == "No top-level images; kept subfolders on",
                "Expected turning subfolders off to be refused when it would leave the folder empty.");

            session.Close();
            Check(log[^1] == "closed" && session.Catalog.Count == 0 && session.CurrentFolderPath is null,
                "Expected closing the session to empty the catalog.");
        }
        finally
        {
            session.Shutdown();
            DeleteDirectoryQuietly(root);
        }
    }

    private static string FormatCountForCheck(int filtered, bool scanning, int remaining, int selected) =>
        HeaderViewModel.FormatCount(300, filtered, 120, scanning, scanning, remaining, 40, selected);

    private void CheckCountText()
    {
        Check(HeaderViewModel.FormatCount(300, 300, 300, false, false, 0, 0, 0) == "300 images",
            "Expected an unfiltered folder to read as a plain count.");
        Check(FormatCountForCheck(12, false, 0, 0) == "12 of 300 images",
            "Expected a filtered folder to read 'N of total'.");
        Check(FormatCountForCheck(300, true, 0, 0) == "300 images (Scanning prompts 120/300)",
            "Expected an active scan to show its progress.");
        Check(FormatCountForCheck(300, false, 15, 0) == "300 images (Caching thumbnails 25/40)",
            "Expected thumbnail caching to show how many are done once the scan is over.");
        Check(FormatCountForCheck(300, true, 15, 0) == "300 images (Scanning prompts 120/300)",
            "Expected the scan to take priority over caching progress.");
        Check(FormatCountForCheck(12, false, 0, 3) == "12 of 300 images | 3 selected" &&
              !FormatCountForCheck(12, false, 0, 1).Contains("selected", StringComparison.Ordinal),
            "Expected a multi-selection to be counted, and a single one not to be.");
    }

    private void CheckGalleryViewModel()
    {
        var gallery = new GalleryViewModel();
        var items = Enumerable.Range(0, 5)
            .Select(index => CreateImageItem(Path.Combine(Path.GetTempPath(), $"selection-{index}.png")))
            .ToList();
        gallery.Items.AddRange(items);

        var activeChanges = new List<(ImageItem? Previous, ImageItem? Current)>();
        var selectionChanges = 0;
        gallery.ActiveItemChanged += (previous, current) => activeChanges.Add((previous, current));
        gallery.SelectionChanged += () => selectionChanges++;

        gallery.SelectItem(items[1]);
        Check(gallery.ActiveItem == items[1] && gallery.Selection.Count == 1 && items[1].IsSelected && items[1].IsMarkedSelected,
            "Expected a click to select exactly that item and make it active.");
        Check(activeChanges.Count == 1 && activeChanges[0] == (null, items[1]) && selectionChanges == 1,
            "Expected a click to announce the active item and the selection once each.");
        gallery.SelectItem(items[1]);
        Check(activeChanges.Count == 1, "Expected selecting the active item again not to announce a change.");

        gallery.SelectRange(items[3]);
        Check(gallery.Selection.Count == 3 && gallery.Selection.Contains(items[2]) && gallery.ActiveItem == items[3],
            "Expected Shift+click to select from the anchor to the clicked item and make it active.");
        gallery.SelectRange(items[0]);
        Check(gallery.Selection.Count == 2 && gallery.Selection.Contains(items[0]) && !gallery.Selection.Contains(items[3]),
            "Expected a second Shift+click to extend from the same anchor, not from the last click.");

        gallery.ToggleSelected(items[4]);
        Check(gallery.Selection.Count == 3 && gallery.ActiveItem == items[4],
            "Expected Ctrl+click on an unselected item to add it and make it active.");
        gallery.ToggleSelected(items[4]);
        Check(gallery.Selection.Count == 2 && !items[4].IsMarkedSelected && gallery.ActiveItem == items[0],
            "Expected Ctrl+click on the active item to deselect it and fall back to the first selected item.");

        gallery.AddToSelection(items[3]);
        Check(gallery.Selection.Count == 3 && gallery.ActiveItem == items[3],
            "Expected Ctrl+double-click to add an item and make it active.");

        gallery.SelectItem(items[2]);
        Check(gallery.MoveSelection(1) == 3 && gallery.ActiveItem == items[3] && gallery.Selection.Count == 1,
            "Expected an arrow key to select the next item alone.");
        Check(gallery.MoveSelection(100) == 4 && gallery.MoveSelection(-100) == 0,
            "Expected arrow keys to stop at the ends of the list.");
        gallery.SelectItem(null);
        Check(gallery.MoveSelection(-1) == 4, "Expected Left or Up with nothing selected to start from the last item.");
        gallery.SelectItem(null);
        Check(gallery.MoveSelection(1) == 0, "Expected Right or Down with nothing selected to start from the first item.");
        Check(gallery.SelectByIndex(99) == -1 && gallery.SelectByIndex(-1) == -1 && gallery.ActiveItem == items[0],
            "Expected an out-of-range index to change nothing.");
        Check(new GalleryViewModel().MoveSelection(1) == -1, "Expected an empty list to have nowhere to move.");

        gallery.SelectItem(items[1]);
        gallery.ToggleSelected(items[2]);
        Check(gallery.ActiveItem == items[2], "Expected the toggled item to be the active one.");
        gallery.Remove(items[2], new HashSet<string> { items[2].Path });
        Check(gallery.ActiveItem == items[1] && !gallery.Selection.Contains(items[2]),
            "Expected removing the active item to fall back to another selected item that is staying.");
        gallery.Remove(items[1], new HashSet<string> { items[1].Path });
        Check(gallery.ActiveItem is null && gallery.Selection.Count == 0,
            "Expected removing the last selected item to leave nothing active.");

        gallery.SelectItem(items[0]);
        var replacement = CreateImageItem(items[0].Path);
        gallery.Replace(items[0], replacement);
        Check(gallery.ActiveItem == replacement && gallery.Selection.Contains(replacement) && !gallery.Selection.Contains(items[0]),
            "Expected a replaced file to carry the selection and the active image over to its new item.");

        gallery.SelectItem(null);
        Check(gallery.Selection.Count == 0 && !replacement.IsMarkedSelected,
            "Expected clearing the selection to unmark its items.");
    }

    private void CheckViewportScheduler()
    {
        var items = new List<ImageItem>();
        for (var index = 0; index < 30; index++)
        {
            items.Add(CreateImageItem(Path.Combine(Path.GetTempPath(), $"viewport-{index}.png")));
        }

        static List<int> IndexesOf(IReadOnlyList<ImageItem> all, IReadOnlyList<ImageItem> chosen) =>
            chosen.Select(item => ((List<ImageItem>)all).IndexOf(item)).ToList();

        var scheduler = new ViewportScheduler();
        var (first, last) = scheduler.Plan(items, firstVisibleRow: 2, visibleRowCount: 3, columnCount: 5, 120, 1.0);
        Check(first == 10 && last == 24 && IndexesOf(items, scheduler.Visible).SequenceEqual(Enumerable.Range(10, 15)),
            "Expected the visible window to be the rows on screen, from the first column of the first row.");
        Check(scheduler.Direction == 1 &&
              IndexesOf(items, scheduler.Ahead).SequenceEqual(Enumerable.Range(25, 5).Concat(Enumerable.Range(5, 5)).Concat(Enumerable.Range(0, 5))),
            "Expected the first pass to prefetch below, then the rows above it from nearest to farthest.");

        scheduler.Plan(items, firstVisibleRow: 1, visibleRowCount: 3, columnCount: 5, 120, 1.0);
        Check(scheduler.Direction == -1 &&
              IndexesOf(items, scheduler.Ahead).SequenceEqual(Enumerable.Range(0, 5).Concat(Enumerable.Range(20, 10))),
            "Expected scrolling up to flip the lean and prefetch above first.");

        scheduler.Plan(items, firstVisibleRow: 1, visibleRowCount: 3, columnCount: 5, 120, 1.0);
        Check(scheduler.Direction == -1, "Expected an unchanged scroll position to keep the last direction.");

        var (tailFirst, tailLast) = scheduler.Plan(items, firstVisibleRow: 5, visibleRowCount: 4, columnCount: 5, 120, 1.0);
        Check(tailFirst == 25 && tailLast == 29 && scheduler.Visible.Count == 5 && scheduler.Direction == 1,
            "Expected the visible window to stop at the last item rather than run past the list.");

        scheduler.Reset();
        Check(scheduler.Visible.Count == 0 && scheduler.Ahead.Count == 0 && scheduler.Direction == 1,
            "Expected a reset to forget the old folder's window and direction.");
        scheduler.Plan(items, firstVisibleRow: 0, visibleRowCount: 2, columnCount: 5, 120, 1.0);
        Check(scheduler.Direction == 1 && scheduler.Ahead.Count > 0,
            "Expected the first plan after a reset to start downward.");
    }

    private void CheckHeaderViewModel()
    {
        var maintenance = new CacheMaintenance(
            ItemThumbnailService,
            new ThumbnailLoadCoordinator(ItemThumbnailService, ItemDecodedImageCache),
            ItemDecodedImageCache,
            ItemMetadataRepository,
            static () => { });
        var header = new HeaderViewModel(maintenance, 120);

        Check(header.SortMode == SortMode.NewestFirst && header.SearchScope == SearchScope.All && !header.HasSearchText,
            "Expected a new header to sort newest first, search all fields, and show no search text.");
        Check(header.TileSizeText == "120" && header.IsClearCacheEnabled && !header.IsCopyPathVisible,
            "Expected a new header to show the saved tile size with no folder open.");

        var sorts = new List<SortMode>();
        header.SortChanged += sorts.Add;
        header.SortIndex = 2;
        header.SortIndex = 2;
        header.SortIndex = -1;
        Check(sorts.Count == 1 && sorts[0] == SortMode.Name && header.SortMode == SortMode.Name,
            "Expected a sort change to be reported once, and an unselected combo to be ignored.");

        var filters = 0;
        header.FilterRequested += () => filters++;
        header.SearchScopeIndex = (int)SearchScope.Filename;
        Check(filters == 1 && header.SearchScope == SearchScope.Filename,
            "Expected changing the search scope to filter at once, without the typing debounce.");
        header.SearchScopeIndex = 42;
        Check(header.SearchScope == SearchScope.All,
            "Expected an unknown scope index to fall back to all fields.");

        var edits = 0;
        header.SearchEdited += () => edits++;
        header.SearchText = "red";
        header.SearchText = "red";
        Check(edits == 1 && header.HasSearchText,
            "Expected an edit to the search text to be announced once, and to show the clear button.");
        header.ResetSearch();
        Check(!header.HasSearchText && header.SearchScope == SearchScope.All,
            "Expected resetting the search for a new folder to clear the text and widen the scope.");

        var sizes = new List<double>();
        header.TileSizeChanged += sizes.Add;
        header.TileSize = 200;
        header.SyncTileSize(160);
        Check(sizes.Count == 1 && sizes[0] == 200 && header.TileSize == 160 && header.TileSizeText == "160",
            "Expected a dragged tile size to be reported, and a clamp the window settled on to be shown without being reported.");

        var includeRequests = new List<bool>();
        header.IncludeSubfoldersRequested += includeRequests.Add;
        header.IncludeSubfolders = true;
        header.SyncIncludeSubfolders(false);
        Check(includeRequests.Count == 1 && includeRequests[0] && !header.IncludeSubfolders,
            "Expected the subfolders toggle to be a request that the window can put back.");

        header.SetFolder("output", "/a/b/output");
        header.SetCountText("12 images");
        Check(header.FolderTitle == "output" && header.FolderPath == "/a/b/output" && header.IsCopyPathVisible &&
              header.CountText == "12 images",
            "Expected an opened folder to show its name, path and count.");
    }

    private void CheckStartMenuViewModel()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cpv-startmenu-{Guid.NewGuid():N}");
        try
        {
            var store = new UserPreferencesStore(directory);
            store.AddRecentFolder("/pics/one", 5);
            store.AddRecentFolder("/pics/two", 1);

            var appliedThemes = new List<ThemeMode>();
            var maintenance = new CacheMaintenance(
                ItemThumbnailService,
                new ThumbnailLoadCoordinator(ItemThumbnailService, ItemDecodedImageCache),
                ItemDecodedImageCache,
                ItemMetadataRepository,
                static () => { });
            var menu = new StartMenuViewModel(store, maintenance, appliedThemes.Add);

            menu.RefreshRecentFolders();
            Check(menu.HasRecentFolders && menu.RecentFolders.Count == 2 && menu.RecentFolders[0].DisplayName == "two",
                "Expected the recent list to show the newest folder first, named by its leaf.");
            Check(menu.RecentFolders[0].MetaText.StartsWith("1 image ", StringComparison.Ordinal) &&
                  menu.RecentFolders[1].MetaText.StartsWith("5 images ", StringComparison.Ordinal),
                "Expected the image count to be singular for one image and plural otherwise.");

            menu.RecentFolders[1].TogglePinCommand.Execute(null);
            Check(menu.RecentFolders[0].Path == "/pics/one" && menu.RecentFolders[0].IsPinned,
                "Expected pinning a folder to move it to the top of the list.");
            menu.RecentFolders[0].RemoveCommand.Execute(null);
            Check(menu.RecentFolders.Count == 1 && menu.RecentFolders[0].Path == "/pics/two",
                "Expected removing a recent folder to drop it from the list and the saved preferences.");

            string? requested = null;
            menu.FolderOpenRequested += path => requested = path;
            menu.RecentFolders[0].Open();
            Check(requested == "/pics/two", "Expected opening a recent row to request that folder from the host.");

            menu.ThemeIndex = (int)ThemeMode.DarkBlue;
            menu.ThemeIndex = 99;
            menu.ThemeIndex = -1;
            Check(appliedThemes.Count == 1 && appliedThemes[0] == ThemeMode.DarkBlue &&
                  store.LoadThemeMode() == ThemeMode.DarkBlue,
                "Expected a theme change to apply and persist once, and out-of-range indexes to be ignored.");

            bool? prewarm = null;
            menu.PrewarmThumbnailsChanged += enabled => prewarm = enabled;
            menu.PrewarmThumbnails = false;
            Check(prewarm == false && !store.LoadPrewarmThumbnails() &&
                  menu.AdvancedStatus.Text == "Background thumbnail caching off.",
                "Expected switching background caching off to persist, tell the host, and confirm in the status line.");

            var includeRequests = new List<bool>();
            menu.IncludeSubfoldersRequested += includeRequests.Add;
            menu.IncludeSubfolders = true;
            menu.SyncIncludeSubfolders(false);
            Check(includeRequests.Count == 1 && includeRequests[0] && !menu.IncludeSubfolders,
                "Expected the user's toggle to be a request, and the host's sync to set the value without one.");

            menu.ShowError("Could not open");
            Check(menu.IsErrorVisible && menu.ErrorMessage == "Could not open",
                "Expected an error to show its message in the banner.");
            menu.Open();
            Check(!menu.IsErrorVisible, "Expected returning to the start screen to clear a stale error.");

            menu.IsAdvancedOpen = true;
            menu.AdvancedStatus.Show("Metadata cache cleared.");
            menu.IsAdvancedOpen = false;
            Check(!menu.AdvancedStatus.IsVisible && menu.AdvancedStatus.Text == "",
                "Expected collapsing the maintenance panel to clear its status line.");
        }
        finally
        {
            DeleteDirectoryQuietly(directory);
        }
    }

    private void CheckSidebarViewModel()
    {
        var path = Path.Combine(Path.GetTempPath(), "sidebar-viewmodel-selfcheck.png");
        var shortItem = CreateImageItem(path);
        shortItem.ApplyMetadata(ImageMetadata.Empty with
        {
            Prompt = "sunlit red portrait",
            NegativePrompt = "blurry",
            Lora = "cosmos_predict_lora (1.00)"
        });
        var longItem = CreateImageItem(path + ".long.png");
        longItem.ApplyMetadata(ImageMetadata.Empty with
        {
            Prompt = new string('a', 600),
            NegativePrompt = string.Join('\n', new string[8].Select(_ => "bad"))
        });

        var sidebar = new SidebarViewModel();
        Check(sidebar.IsEmpty && !sidebar.HasItem && sidebar.Title == "Metadata",
            "Expected a new sidebar to show its empty state.");

        sidebar.Show(shortItem, "red", SearchScope.All);
        Check(sidebar.HasItem && !sidebar.IsEmpty && sidebar.Title == shortItem.FileName,
            "Expected showing an image to leave the empty state and title it by file name.");
        Check(sidebar.HasPrompt && sidebar.IsNegativeSectionVisible && sidebar.HasLora && !sidebar.IsPromptLong,
            "Expected the short image's prompt, negative prompt and LoRA to show without a collapse.");
        Check(double.IsPositiveInfinity(sidebar.PromptMaxHeight) && !sidebar.IsNegativeOpen,
            "Expected a short prompt to be uncapped and the negative prompt to start collapsed.");
        Check(sidebar.PromptTerms.Count == 1 && sidebar.NegativeTerms.Count == 1,
            "Expected the search terms to reach both prompts under the All scope.");

        var textEvents = 0;
        sidebar.PromptTextsChanged += _ => textEvents++;
        sidebar.RefreshHighlights("red", SearchScope.All);
        Check(textEvents == 0, "Expected an unchanged query not to rebuild the prompt runs and wipe a selection.");
        sidebar.RefreshHighlights("red", SearchScope.PositivePrompt);
        Check(textEvents == 1 && sidebar.PromptTerms.Count == 1 && sidebar.NegativeTerms.Count == 0,
            "Expected narrowing the scope to rebuild the runs and drop the negative highlights.");

        sidebar.ToggleNegativeOpenCommand.Execute(null);
        Check(sidebar.IsNegativeOpen && sidebar.NegativeArrow == "▾" && sidebar.SettingsMargin.Top == 20,
            "Expected opening the negative prompt to flip the arrow and push the settings card down.");

        sidebar.Show(longItem, "", SearchScope.All);
        Check(!sidebar.IsNegativeOpen && sidebar.NegativeArrow == "▸" && sidebar.SettingsMargin.Top == 8,
            "Expected showing another image to collapse the negative prompt again.");
        Check(sidebar.IsPromptLong && sidebar.IsPromptCollapsed && sidebar.PromptMaxHeight == SidebarViewModel.CollapsedPromptMaxHeight,
            "Expected a 600-character prompt to start collapsed at the capped height.");
        sidebar.TogglePromptExpandedCommand.Execute(null);
        Check(sidebar.IsPromptExpanded && !sidebar.IsPromptCollapsed && sidebar.PromptExpandLabel == "Show less" &&
              double.IsPositiveInfinity(sidebar.PromptMaxHeight),
            "Expected expanding a long prompt to lift the cap and relabel the toggle.");
        Check(sidebar.IsNegativeLong, "Expected a negative prompt of eight lines to count as long.");

        sidebar.Show(shortItem, "", SearchScope.All);
        Check(!sidebar.IsPromptExpanded, "Expected a new image's prompt to start unexpanded.");

        sidebar.Clear();
        Check(sidebar.IsEmpty && sidebar.Title == "Metadata" && !sidebar.HasPrompt && sidebar.IsHeaderDescVisible,
            "Expected clearing the sidebar to return to its empty state.");
    }

    private void CheckPreviewZoomPan()
    {
        var image = new Avalonia.PixelSize(2000, 1000);
        var viewport = new Avalonia.Size(500, 500);
        var zoom = new PreviewZoomPan();

        Check(PreviewZoomPan.TryGetFitScale(image, viewport, out var fit) && Math.Abs(fit - 0.25) < 1e-9,
            "Expected a 2000x1000 image to fit a 500x500 viewport at 25%.");
        Check(!PreviewZoomPan.TryGetFitScale(image, null, out _),
            "Expected no fit scale without a viewport.");
        Check(zoom.DescribeZoom(image, viewport, 2000) == "Fit 25%",
            "Expected a fitted preview to read 'Fit 25%'.");
        Check(zoom.DescribeZoom(image, viewport, 5000) == "Fit 10%",
            "Expected zoom text to be relative to the source's pixels, not the decoded preview's.");

        // Content smaller than the viewport is centered whatever the pan was.
        zoom.PanX = 300;
        zoom.PanY = -300;
        zoom.ClampPan(zoom.GetContentSize(image, viewport), viewport);
        Check(Math.Abs(zoom.PanX) < 1e-9 && Math.Abs(zoom.PanY - 125) < 1e-9,
            "Expected a fitted image to be centered on the axis it does not fill.");

        // Content larger than the viewport can only pan inside its own edges.
        zoom.Zoom = 1.0;
        zoom.PanX = 100;
        zoom.PanY = -5000;
        zoom.ClampPan(zoom.GetContentSize(image, viewport), viewport);
        Check(zoom.PanX == 0 && zoom.PanY == -500,
            "Expected a zoomed pan to clamp to the image edges.");

        // Zooming about a point keeps the image point under it fixed.
        zoom.Zoom = 1.0;
        zoom.PanX = -400;
        zoom.PanY = -100;
        var cursor = new Avalonia.Point(250, 250);
        var anchor = zoom.CaptureAnchor(cursor, image, viewport)!.Value;
        zoom.Zoom = 2.0;
        var zoomedContent = zoom.GetContentSize(image, viewport);
        zoom.RestoreAnchor(anchor, zoomedContent);
        Check(Math.Abs((cursor.X - zoom.PanX) / zoomedContent.Width - anchor.XRatio) < 1e-9 &&
              Math.Abs((cursor.Y - zoom.PanY) / zoomedContent.Height - anchor.YRatio) < 1e-9,
            "Expected an anchored zoom to keep the point under the cursor in place.");

        Check(PreviewZoomPan.GetMinimumZoom(image, viewport) == 0.25 &&
              PreviewZoomPan.GetMinimumZoom(null, viewport) == PreviewZoomPan.MinZoom,
            "Expected the minimum zoom to be the fit scale, or the floor without an image.");
        Check(PreviewZoomPan.NextWheelZoom(1.0, 1) > 1.0 && PreviewZoomPan.NextWheelZoom(1.0, 99) == Math.Pow(PreviewZoomPan.WheelZoomFactor, 3),
            "Expected wheel zoom to step by the factor and cap a single event at three notches.");
    }

    private void CheckThumbnailPackRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-pack-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(directory, "image.png");
        try
        {
            var pack = new ThumbnailPack(directory);
            var key = ThumbnailPack.CreateKey(sourcePath, 1000, 180);
            var other = ThumbnailPack.CreateKey(sourcePath, 1000, 240);
            var newerVersion = ThumbnailPack.CreateKey(sourcePath, 2000, 180);

            Check(key != other && key != newerVersion,
                "Expected thumbnail keys to separate width buckets and source versions.");
            Check(!pack.Contains(key), "Expected an empty thumbnail pack to report a miss.");

            byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
            Check(pack.Write(key, payload), "Expected a thumbnail pack write to succeed.");
            Check(pack.Contains(key) && !pack.Contains(other),
                "Expected only the written key to be present.");
            Check(pack.TryRead(key, out var read) && read.AsSpan().SequenceEqual(payload),
                "Expected a thumbnail pack entry to round trip.");

            byte[] second = [9, 9, 9];
            pack.Write(other, second);
            pack.Remove(key);
            Check(!pack.Contains(key), "Expected removing a thumbnail pack entry to drop it.");
            Check(pack.TryRead(other, out var stillThere) && stillThere.AsSpan().SequenceEqual(second),
                "Expected removing one entry to leave neighbouring payloads readable.");

            // FileShare alone does not exclude a second writer on Linux/macOS; the writer lock file has to.
            using (var secondInstance = new ThumbnailPack(directory))
            {
                Check(!secondInstance.CanWrite && pack.CanWrite,
                    "Expected a second pack on the same folder to open read-only while the first holds the writer.");
            }

            pack.Dispose();

            // Reopening replays the offset log, which is what makes a warm folder warm across restarts.
            var reopened = new ThumbnailPack(directory);
            Check(!reopened.Contains(key), "Expected a tombstoned entry to stay removed after reopening.");
            Check(reopened.TryRead(other, out var reloaded) && reloaded.AsSpan().SequenceEqual(second),
                "Expected thumbnail pack entries to survive reopening.");

            reopened.Clear();
            Check(!reopened.Contains(other), "Expected clearing the pack to drop every entry.");
            Check(new FileInfo(Path.Combine(directory, "thumbnails.pack")).Length == 8,
                "Expected clearing the pack to truncate the data file rather than delete files one by one.");
            reopened.Dispose();
        }
        finally
        {
            DeleteDirectoryQuietly(directory);
        }
    }

    // The scope directory is derived from the folder path, so any two spellings a user can reach the same
    // folder by have to collapse to one cache. A miss here silently builds a second cache for the folder.
    private void CheckThumbnailFolderScopeIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "comfypromptviewer-selfcheck-scope-identity");
        var plain = FolderNames.Key(root);

        Check(FolderNames.Key(root + Path.DirectorySeparatorChar) == plain,
            "Expected a trailing separator to resolve to the same folder cache scope.");
        Check(FolderNames.Key(root + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar) == plain,
            "Expected repeated trailing separators to resolve to the same folder cache scope.");

        if (OperatingSystem.IsWindows())
        {
            Check(FolderNames.Key(root.ToUpperInvariant()) == plain,
                "Expected Windows path casing to resolve to the same folder cache scope.");
        }

        // A root path has nothing to trim, and trimming it to nothing would collapse every drive together.
        var systemRoot = Path.GetPathRoot(Path.GetTempPath());
        if (!string.IsNullOrEmpty(systemRoot))
        {
            Check(FolderNames.Key(systemRoot).Length > 0,
                "Expected a filesystem root to normalize to a non-empty folder cache scope key.");
        }

        // A nested folder opened as its own root is deliberately its own cache.
        Check(FolderNames.Key(Path.Combine(root, "nested")) != plain,
            "Expected a nested folder opened as its own root to get its own cache scope.");

        // The directory name carries the folder name so the app data folder is readable, but every spelling
        // of one folder still has to land on the same directory.
        var directoryName = ThumbnailFolderScope.BuildDirectoryName(plain);
        Check(directoryName.StartsWith("comfypromptviewer-selfcheck-scope", StringComparison.Ordinal),
            "Expected the folder cache directory name to label itself with the folder name.");
        Check(ThumbnailFolderScope.BuildDirectoryName(
                FolderNames.Key(root + Path.DirectorySeparatorChar)) == directoryName,
            "Expected every spelling of one folder to build the same cache directory name.");

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            Check(!directoryName.Contains(invalid),
                $"Expected the folder cache directory name to drop the invalid character 0x{(int)invalid:x2}.");
        }

        // Long folder names must not push the cache path toward MAX_PATH, and a name made entirely of
        // invalid characters still has to produce a usable directory.
        var longName = ThumbnailFolderScope.BuildDirectoryName(
            FolderNames.Key(Path.Combine(root, new string('x', 300))));
        Check(longName.Length <= 40 + 1 + 32,
            "Expected the folder cache directory name to bound its readable segment.");
        Check(ThumbnailFolderScope.BuildDirectoryName("").Length == 32,
            "Expected an unlabelable folder key to fall back to a bare hash directory name.");
    }

    // The point of the per-folder layout: identical keys in two scopes do not collide, and clearing one
    // folder leaves the other's thumbnails readable.
    private async Task CheckFolderThumbnailCacheIsolation()
    {
        var appData = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-folders-{Guid.NewGuid():N}");
        var folderA = Path.Combine(appData, "source-a");
        var folderB = Path.Combine(appData, "source-b");
        var service = new ThumbnailService(Path.Combine(appData, "thumbnails"));
        try
        {
            // The same source path in two scopes produces the same key; only the scope separates them.
            var key = ThumbnailPack.CreateKey(Path.Combine(folderA, "image.png"), 1000, 180);
            byte[] payloadA = [1, 2, 3, 4];
            byte[] payloadB = [5, 6, 7, 8, 9];

            var scopeA = await service.OpenFolderScopeAsync(folderA);
            Check(scopeA.Pack.Write(key, payloadA), "Expected a write into the first folder cache to succeed.");

            // Reopening the same folder with a trailing separator must reuse the live scope, not build a
            // second one beside it.
            var sameScope = await service.OpenFolderScopeAsync(folderA + Path.DirectorySeparatorChar);
            Check(ReferenceEquals(sameScope, scopeA), "Expected reopening the same folder to reuse its cache scope.");

            var scopeB = await service.OpenFolderScopeAsync(folderB);
            Check(scopeA.IsRetired, "Expected a folder swap to retire the previous cache scope.");
            Check(!service.HasCachedThumbnail(scopeA, key),
                "Expected a retired scope to report a miss so stale work stops instead of racing a closing pack.");
            Check(!scopeB.Pack.Contains(key), "Expected an identical key in a second folder scope not to collide.");

            Check(scopeB.Pack.Write(key, payloadB), "Expected a write into the second folder cache to succeed.");
            Check(scopeB.Pack.TryRead(key, out var readB) && readB.AsSpan().SequenceEqual(payloadB),
                "Expected the second folder cache to round trip its own payload.");

            // Clearing folder B must not touch folder A's pack.
            await service.ClearFolderCacheAsync(scopeB);
            Check(!scopeB.Pack.Contains(key), "Expected clearing a folder cache to drop its entries.");

            var reopenedA = await service.OpenFolderScopeAsync(folderA);
            Check(reopenedA.Pack.TryRead(key, out var readA) && readA.AsSpan().SequenceEqual(payloadA),
                "Expected clearing one folder cache to leave another folder's thumbnails readable on reopen.");

            // The legacy global pack is only removed by the global clear, never by the upgrade itself.
            var legacyPack = Path.Combine(service.CacheRootDirectory, "thumbnails.pack");
            var legacyIndex = Path.Combine(service.CacheRootDirectory, "thumbnails.idx");
            File.WriteAllBytes(legacyPack, [0, 1, 2, 3]);
            File.WriteAllBytes(legacyIndex, [0, 1, 2, 3]);

            await service.ClearAllCachesAsync();
            Check(!reopenedA.Pack.Contains(key), "Expected the global clear to empty the active folder cache.");
            Check(!Directory.Exists(scopeB.Directory), "Expected the global clear to remove other folder caches.");
            Check(!File.Exists(legacyPack) && !File.Exists(legacyIndex),
                "Expected the global clear to remove the pre-upgrade global thumbnail cache.");
        }
        finally
        {
            service.Dispose();
            DeleteDirectoryQuietly(appData);
        }
    }

    private void CheckThumbnailCacheBudget()
    {
        Check(!DecodedImageCache.ExceedsBudget(DecodedImageCache.MaxCapacity, DecodedImageCache.MaxEstimatedBytes), "Expected exact thumbnail cache budget to fit.");
        Check(DecodedImageCache.ExceedsBudget(DecodedImageCache.MaxCapacity + 1, 0), "Expected thumbnail count budget overflow.");
        Check(DecodedImageCache.ExceedsBudget(1, DecodedImageCache.MaxEstimatedBytes + 1), "Expected thumbnail byte budget overflow.");
        Check(DecodedImageCache.MaxEvictionScanPerTouch > 0,
            "Expected a bounded eviction scan so an all-protected cache cannot walk the whole list per touch.");
    }

    private void CheckThumbnailDecodeWidths()
    {
        var item = CreateImageItem(Path.Combine(Path.GetTempPath(), "decode-width-selfcheck.png"));

        // Across every tile size and every DPI the app is likely to meet, the decode must cover the tile's
        // real pixel width without running far past it.
        foreach (var renderScaling in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            foreach (var tileSize in new double[] { 80, 100, 120, 160, 200, 240, 320 })
            {
                item.SetTileSize(tileSize, renderScaling);
                var decodeWidth = item.GetThumbnailDecodeWidth();
                var physicalWidth = tileSize * renderScaling;

                Check(decodeWidth >= physicalWidth,
                    $"Expected the decode width for tile size {tileSize} at {renderScaling}x to cover the rendered tile.");
                Check(decodeWidth <= physicalWidth * 2,
                    $"Expected the decode width for tile size {tileSize} at {renderScaling}x to stay within twice the rendered tile.");
            }
        }

        // At 100% the buckets are unchanged from the pre-DPI-aware ladder for the tile sizes that mattered.
        item.SetTileSize(80, 1.0);
        Check(item.GetThumbnailDecodeWidth() == 120,
            "Expected the smallest tile size to use the tiny decode bucket.");
        item.SetTileSize(320, 1.0);
        Check(item.GetThumbnailDecodeWidth() == 320,
            "Expected the largest tile size to use the large decode bucket.");

        // A HiDPI display must climb the ladder rather than decode a tile at less than its pixel width.
        item.SetTileSize(120, 2.0);
        Check(item.GetThumbnailDecodeWidth() == 240,
            "Expected a 120 tile on a 200% display to decode at its 240px physical width.");
        item.SetTileSize(320, 2.0);
        Check(item.GetThumbnailDecodeWidth() == 640,
            "Expected a 320 tile on a 200% display to reach the largest decode bucket.");

        // Scaling is clamped, so an absurd DPI cannot ask for an unbounded decode.
        item.SetTileSize(320, 16.0);
        Check(item.GetThumbnailDecodeWidth() == 640,
            "Expected extreme render scaling to clamp to the largest decode bucket.");
    }

    private void CheckJpegThumbnailEncoding()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(sourcePath, TinyPngBytes);
            CheckEncodesToJpeg(sourcePath, "Expected thumbnail cache output to contain a JPEG signature.");
        }
        finally
        {
            TryDelete(sourcePath);
        }
    }

    // Prompt-named generator output nests deep enough to pass MAX_PATH, and Skia's by-name file open silently
    // fails there while every managed decode on the same file succeeds - so only the cache write broke, once
    // per scroll pass, forever. The encode has to work from a path this long.
    // Run this one from ComfyPromptViewer.exe, not `dotnet ComfyPromptViewer.dll`: the dotnet host's manifest
    // is longPathAware, so under it even the broken by-name open succeeds and this check cannot fail.
    private void CheckLongPathJpegThumbnailEncoding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, new string('d', 120), new string('e', 120), "thumbnail-source.png");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Check(sourcePath.Length > 260, "Expected the long-path fixture to exceed MAX_PATH.");
            File.WriteAllBytes(sourcePath, TinyPngBytes);
            CheckEncodesToJpeg(sourcePath, "Expected a source path past MAX_PATH to still encode a thumbnail.");
        }
        finally
        {
            DeleteDirectoryQuietly(root);
        }
    }

    private static void CheckEncodesToJpeg(string sourcePath, string message)
    {
        var encoded = ThumbnailDecoder.EncodeJpegThumbnail(sourcePath, thumbnailWidth: 180);
        Check(encoded.Length > 3 && encoded[0] == 0xff && encoded[1] == 0xd8 && encoded[2] == 0xff, message);
    }

    private static byte[] TinyPngBytes => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void WriteTinyPng(string path, string key, string value)
    {
        using var stream = File.Create(path);
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], 1);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), 1);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(stream, "IHDR", ihdr);

        var keyword = Encoding.Latin1.GetBytes(key);
        var text = Encoding.UTF8.GetBytes(value);
        var textData = new byte[keyword.Length + 1 + text.Length];
        keyword.CopyTo(textData, 0);
        text.CopyTo(textData, keyword.Length + 1);
        WriteChunk(stream, "tEXt", textData);
        WriteChunk(stream, "IEND", []);
    }

    private static void WritePngWithOversizedCompressedText(string path)
    {
        using var stream = File.Create(path);
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], 1);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), 1);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(stream, "IHDR", ihdr);

        WriteChunk(stream, "tEXt", Encoding.UTF8.GetBytes("parameters\0searchable prompt"));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var zeros = new byte[8192];
            for (var remaining = 3 * 1024 * 1024; remaining > 0; remaining -= zeros.Length)
            {
                zlib.Write(zeros, 0, Math.Min(zeros.Length, remaining));
            }
        }

        var compressedBytes = compressed.ToArray();
        var textData = new byte["oversized".Length + 2 + compressedBytes.Length];
        Encoding.Latin1.GetBytes("oversized").CopyTo(textData, 0);
        compressedBytes.CopyTo(textData, "oversized".Length + 2);
        WriteChunk(stream, "zTXt", textData);
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write([0, 0, 0, 0]);
    }

    private static bool RoundTripsThroughIndex(MetadataRepository repository, string path)
    {
        var result = new ImageReadResult(1, 2, new(StringComparer.OrdinalIgnoreCase));
        var extracted = new ExtractedPromptMetadata
        {
            Prompt = "cached prompt",
            NegativePrompt = "cached negative",
            GenerationSettings = new GenerationSettings
            {
                Model = "model",
                Sampler = "sampler",
                Seed = "123",
                Settings = "Steps 1",
                Lora = "lora",
                Tool = "Forge",
                Resources = "Embedding: easynegative"
            }
        };

        var fileInfo = new FileInfo(path);
        var fingerprint = new SourceFingerprint(fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);
        repository.Save(MetadataRepository.CreateEntry(path, fingerprint, ImageMetadata.From(result, extracted)));
        return repository.TryLoad(path, fingerprint, out var entry) &&
               entry.Metadata is var loaded &&
               loaded.Width == 1 &&
               loaded.Height == 2 &&
               loaded.Prompt == "cached prompt" &&
               loaded.NegativePrompt == "cached negative" &&
               loaded.Model == "model" &&
               loaded.Sampler == "sampler" &&
               loaded.Seed == "123" &&
               loaded.Settings == "Steps 1" &&
               loaded.Lora == "lora" &&
               loaded.Tool == "Forge" &&
               loaded.Resources == "Embedding: easynegative";
    }

    private static void Check(bool condition, string message)
    {
        Assert.True(condition, message);
    }
}
