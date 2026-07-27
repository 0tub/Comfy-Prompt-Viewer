using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace ComfyPromptViewer;

internal static class SelfCheck
{
    private static readonly MetadataRepository ItemMetadataRepository =
        new(Path.Combine(Path.GetTempPath(), "comfypromptviewer-selfcheck-items"));
    private static readonly ImageMetadataService ItemMetadataService = new(ItemMetadataRepository);
    private static readonly DecodedImageCache ItemDecodedImageCache = new();
    private static readonly ThumbnailService ItemThumbnailService =
        new(Path.Combine(Path.GetTempPath(), "comfypromptviewer-selfcheck-items"));

    public static void Run()
    {
        CheckStalenessGates();
        CheckSearchParsing();
        CheckSearchResultGenerations();
        CheckSearchIndexMaintenance();
        CheckMetadataLoadOnlyRemovesMatches();
        CheckParallelSearchFiltering();
        CheckGalleryScrollAnchoring();
        CheckGalleryItemReconciliation();
        CheckGalleryInsertionDiff();
        CheckGalleryCatalog();
        CheckSortedInsertion();
        CheckFolderLoadSessions();
        CheckThemeModes();
        CheckPromptExtraction();
        CheckPromptExtractionHeuristics();
        CheckPngMetadataRead();
        CheckPngMetadataLimit();
        CheckMetadataIndexRoundTrip();
        CheckMetadataBatchSave();
        CheckMetadataBatchLoad();
        CheckMetadataIndexCleanup();
        CheckMetadataFailureClassification();
        CheckThumbnailCacheWriteBackpressure();
        CheckDeferredThumbnailCacheWriteQueue();
        CheckDeferredThumbnailCacheWritePause();
        CheckThumbnailPrefetchPolicy();
        CheckRetainedViewportWindow();
        CheckSelectedPreviewDecodeWidth();
        CheckSelectedPreviewFailureIsObservable();
        CheckJpegThumbnailEncoding();
        CheckThumbnailPackRoundTrip();
        CheckThumbnailCacheBudget();
        CheckThumbnailDecodeWidths();
        CheckSidebarWidthClamping();
        CheckWindowPlacementRoundTrip();
    }

    private static void CheckSidebarWidthClamping()
    {
        // A comfortable window leaves a dragged width exactly where the user put it.
        Check(MainWindow.ComputeClampedSidebarWidth(420, 1600) == 420,
            "Expected a wide window to preserve the dragged sidebar width.");

        // Narrowing the window pulls the sidebar back rather than letting it eat the gallery.
        var narrow = MainWindow.ComputeClampedSidebarWidth(520, 900);
        Check(narrow < 520, "Expected a narrow window to shrink an oversized sidebar.");
        Check(narrow <= 900 * 0.45 + 0.001, "Expected the clamped sidebar to respect the window ratio ceiling.");

        // The floor wins over the ratio, so the sidebar never collapses to nothing on a tiny window.
        Check(MainWindow.ComputeClampedSidebarWidth(300, 200) == 260,
            "Expected the sidebar minimum width to win over the window ratio.");

        // The absolute maximum still applies on an ultrawide window.
        Check(MainWindow.ComputeClampedSidebarWidth(9000, 5000) == 560,
            "Expected the sidebar maximum width to bound an absurd stored value.");
    }

    private static void CheckWindowPlacementRoundTrip()
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
    private static void CheckStalenessGates()
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

        var joined = sessions.Snapshot();
        Check(joined.IsCurrent, "Expected a snapshot of the active session to be current.");

        var secondSession = sessions.Restart();
        Check(firstSession.IsStale && firstSession.Token.IsCancellationRequested,
            "Expected restarting a session gate to cancel and supersede the previous session.");
        Check(secondSession.IsCurrent, "Expected the replacement session to be current.");

        sessions.Cancel();
        Check(secondSession.IsStale && secondSession.Token.IsCancellationRequested,
            "Expected canceling a session gate to cancel and supersede the active session.");
        Check(!sessions.IsActive, "Expected a canceled session gate to report no active session.");
        Check(default(Session).IsStale, "Expected an unassigned session to read as stale.");
    }

    // The index is only correct because GalleryCatalog is its single owner: every membership change and
    // every metadata load goes through one call that updates both.
    private static void CheckSearchIndexMaintenance()
    {
        var catalog = new GalleryCatalog();
        var kept = CreateImageItem(Path.Combine(Path.GetTempPath(), "index-kept.png"));
        var removed = CreateImageItem(Path.Combine(Path.GetTempPath(), "index-removed.png"));
        catalog.Add(new GalleryEntry(kept.Path, default, kept));
        catalog.Add(new GalleryEntry(removed.Path, default, removed));

        Check(MatchCount(catalog, "index", SearchScope.Filename) == 2,
            "Expected both catalog items to be indexed on add.");

        catalog.RemovePaths(new HashSet<string>([removed.Path], StringComparer.OrdinalIgnoreCase));
        Check(MatchCount(catalog, "index", SearchScope.Filename) == 1 && removed.SearchSlot < 0,
            "Expected removing a catalog entry to release its search row.");

        kept.ApplyMetadataEntry(new MetadataIndexEntry
        {
            SourcePath = kept.Path,
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
        catalog.ReplaceMany(new Dictionary<string, GalleryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [kept.Path] = new GalleryEntry(kept.Path, default, replacement)
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

    private static void CheckParallelSearchFiltering()
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
            catalog.Add(new GalleryEntry(item.Path, default, item));
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
        catalog.Add(new GalleryEntry(item.Path, default, item));
        var matched = MatchCount(catalog, query, searchScope) == 1;
        catalog.Clear();
        return matched;
    }

    private static void CheckSortedInsertion()
    {
        int[] values = [1, 3, 3, 5];
        Check(MainWindow.FindSortedInsertIndex(values, 0, static (left, right) => left.CompareTo(right)) == 0,
            "Expected sorted insertion before the first item.");
        Check(MainWindow.FindSortedInsertIndex(values, 3, static (left, right) => left.CompareTo(right)) == 3,
            "Expected sorted insertion after equivalent items.");
        Check(MainWindow.FindSortedInsertIndex(values, 6, static (left, right) => left.CompareTo(right)) == values.Length,
            "Expected sorted insertion after the last item.");
    }

    private static void CheckFolderLoadSessions()
    {
        var coordinator = new FolderLoadCoordinator();
        var first = coordinator.Restart();
        Check(first.IsCurrent, "Expected the new folder load session to be current.");
        Check(coordinator.IsCurrent(first.Generation),
            "Expected a folder load generation carried on its own to resolve to the active session.");

        var second = coordinator.Restart();
        Check(first.IsStale, "Expected restarting folder loading to invalidate the previous session.");
        Check(first.Token.IsCancellationRequested, "Expected restarting folder loading to cancel the previous token.");
        Check(second.IsCurrent, "Expected the replacement folder load session to be current.");
        Check(!coordinator.IsCurrent(first.Generation),
            "Expected a superseded folder load generation to be rejected.");

        coordinator.Cancel();
        Check(second.IsStale, "Expected canceling folder loading to invalidate the active session.");
        Check(!coordinator.IsCurrent(second.Generation),
            "Expected canceling folder loading to reject its generation.");
        Check(second.Token.IsCancellationRequested, "Expected canceling folder loading to cancel the active token.");
    }

    private static void CheckGalleryScrollAnchoring()
    {
        var offset = MainWindow.CalculateAnchoredGalleryOffset(
            oldIndex: 20,
            newIndex: 24,
            columns: 4,
            itemExtent: 136,
            oldOffset: 700,
            maxOffset: 5000);

        Check(offset == 836, "Expected a new row above the viewport to preserve the visible gallery row.");

        offset = MainWindow.CalculateAnchoredGalleryOffset(
            oldIndex: 20,
            newIndex: -1,
            columns: 4,
            itemExtent: 136,
            oldOffset: 700,
            maxOffset: 5000);

        Check(offset == 700, "Expected deleting the first visible gallery item to preserve the current scroll offset.");
    }

    private static void CheckGalleryItemReconciliation()
    {
        var a = CreateImageItem(Path.Combine(Path.GetTempPath(), "gallery-a.png"));
        var b = CreateImageItem(Path.Combine(Path.GetTempPath(), "gallery-b.png"));
        var c = CreateImageItem(Path.Combine(Path.GetTempPath(), "gallery-c.png"));
        var added = CreateImageItem(Path.Combine(Path.GetTempPath(), "gallery-added.png"));

        Check(MainWindow.CanSynchronizeGalleryItemsIncrementally([a, b, c], [added, a, b, c], maximumChanges: 2),
            "Expected a small watcher insertion to retain the existing gallery order.");
        Check(!MainWindow.CanSynchronizeGalleryItemsIncrementally([a, b, c], [c, b, a], maximumChanges: 2),
            "Expected a reorder to use a gallery reset instead of per-item moves.");
        Check(!MainWindow.CanSynchronizeGalleryItemsIncrementally([a, b, c], [added, a, b, c], maximumChanges: 0),
            "Expected the incremental gallery change limit to be enforced.");
    }

    // The allocation-free watcher path. A wrong diff here silently corrupts the gallery order rather than
    // failing loudly, so every rejection case is covered as well as the applied result.
    private static void CheckGalleryInsertionDiff()
    {
        var a = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-a.png"));
        var b = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-b.png"));
        var c = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-c.png"));
        var first = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-first.png"));
        var middle = CreateImageItem(Path.Combine(Path.GetTempPath(), "insert-middle.png"));
        var insertions = new List<MainWindow.GalleryInsertion>();

        bool Apply(List<ImageItem> current, IReadOnlyList<ImageItem> target, int maximumInsertions)
        {
            if (!MainWindow.TryFindGalleryInsertions(current, target, maximumInsertions, insertions))
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

        Check(!MainWindow.TryFindGalleryInsertions([a, b, c], [a, c], 4, insertions),
            "Expected a removal to fall back to the general gallery sync path.");
        Check(!MainWindow.TryFindGalleryInsertions([a, b, c], [c, b, a], 4, insertions),
            "Expected a reorder to fall back to the general gallery sync path.");
        Check(!MainWindow.TryFindGalleryInsertions([a, b, c], [a, b, c], 4, insertions),
            "Expected an unchanged sequence to report no insertions.");
        Check(!MainWindow.TryFindGalleryInsertions([a, b, c], [first, middle, a, b, c], 1, insertions),
            "Expected the insertion limit to be enforced.");
        Check(!MainWindow.TryFindGalleryInsertions([a, b, c], [first, a, b, middle], 4, insertions),
            "Expected an insertion paired with a removal to fall back to the general gallery sync path.");
    }

    private static void CheckGalleryCatalog()
    {
        int NewestFirst(GalleryEntry left, GalleryEntry right) =>
            right.Fingerprint.LastWriteTimeUtcTicks.CompareTo(left.Fingerprint.LastWriteTimeUtcTicks);

        var catalog = new GalleryCatalog();
        var older = CreateImageItem(Path.Combine(Path.GetTempPath(), "catalog-older.png"));
        var newer = CreateImageItem(Path.Combine(Path.GetTempPath(), "catalog-newer.png"));
        catalog.Add(new GalleryEntry(older.Path, new SourceFingerprint(10, 0), older));
        catalog.Add(new GalleryEntry(newer.Path, new SourceFingerprint(20, 0), newer));
        catalog.Sort(NewestFirst);

        Check(ReferenceEquals(catalog.Items[0], newer), "Expected catalog sort to keep item and timestamp together.");

        var replacement = CreateImageItem(newer.Path);
        Check(
            catalog.TryReplaceSorted(
                new GalleryEntry(newer.Path, new SourceFingerprint(5, 0), replacement),
                NewestFirst,
                out var previous) &&
            ReferenceEquals(previous.Item, newer),
            "Expected catalog replacement to update the authoritative entry.");
        Check(
            ReferenceEquals(catalog.Items[0], older) && ReferenceEquals(catalog.Items[1], replacement),
            "Expected a sorted replacement to move the entry to its new ordered position.");
        Check(
            catalog.TryGet(newer.Path, out var relocated) && ReferenceEquals(relocated.Item, replacement),
            "Expected a sorted replacement to stay reachable by path.");

        var bulkReplacement = CreateImageItem(older.Path);
        catalog.ReplaceMany(new Dictionary<string, GalleryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [older.Path] = new GalleryEntry(older.Path, new SourceFingerprint(1, 0), bulkReplacement)
        });
        Check(
            catalog.TryGet(older.Path, out var bulkReplaced) && ReferenceEquals(bulkReplaced.Item, bulkReplacement),
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

    private static void CheckSearchParsing()
    {
        SearchEngine.ParseQuery("cat \"red dress\" -bad -\"low quality\"", out var positive, out var negative);

        Check(positive.Count == 2, "Expected two positive search terms.");
        Check(positive[0] is { Text: "cat", IsExact: false }, "Expected plain positive term.");
        Check(positive[1] is { Text: "red dress", IsExact: true }, "Expected exact positive term.");
        Check(negative.Count == 2, "Expected two negative search terms.");
        Check(negative[0] is { Text: "bad", IsExact: false }, "Expected plain negative term.");
        Check(negative[1] is { Text: "low quality", IsExact: true }, "Expected exact negative term.");

        var item = CreateImageItem(Path.Combine(Path.GetTempPath(), "search-scope-selfcheck.png"));
        item.ApplyMetadataEntry(new MetadataIndexEntry
        {
            SourcePath = item.Path,
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

    private static void CheckSearchResultGenerations()
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
    private static void CheckMetadataLoadOnlyRemovesMatches()
    {
        foreach (var scope in new[] { SearchScope.All, SearchScope.PositivePrompt, SearchScope.NegativePrompt })
        {
            var catalog = new GalleryCatalog();
            var item = CreateImageItem(Path.Combine(Path.GetTempPath(), $"monotonic-{scope}.png"));
            catalog.Add(new GalleryEntry(item.Path, default, item));

            Check(MatchCount(catalog, "dragon", scope) == 1,
                $"Expected an unscanned item to stay visible for scope {scope}.");

            item.ApplyMetadataEntry(new MetadataIndexEntry
            {
                SourcePath = item.Path,
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
        excludeCatalog.Add(new GalleryEntry(excluded.Path, default, excluded));
        Check(MatchCount(excludeCatalog, "-watermark", SearchScope.All) == 1,
            "Expected an unscanned item to survive a negative term.");

        excluded.ApplyMetadataEntry(new MetadataIndexEntry
        {
            SourcePath = excluded.Path,
            Prompt = "sunlit portrait",
            NegativePrompt = "blurry watermark"
        });
        excludeCatalog.MarkMetadataLoaded(excluded);
        Check(MatchCount(excludeCatalog, "-watermark", SearchScope.All) == 0,
            "Expected loaded metadata to newly satisfy a negative term and drop the item.");
        excludeCatalog.Clear();
    }

    private static void CheckThemeModes()
    {
        Check(Enum.GetValues<ThemeMode>().Length == 5, "Expected five theme modes.");
        Check((int)ThemeMode.Brown == 0 &&
              (int)ThemeMode.DarkGray == 1 &&
              (int)ThemeMode.DarkBlue == 2 &&
              (int)ThemeMode.DarkGreen == 3 &&
              (int)ThemeMode.Plum == 4,
            "Expected theme mode order to match ThemeComboBox.");
    }

    private static void CheckPromptExtraction()
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
    private static void CheckPromptExtractionHeuristics()
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

    private static void CheckPngMetadataRead()
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

    private static void CheckPngMetadataLimit()
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

    private static void CheckMetadataIndexRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.png");
        var databaseDirectory = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-metadata-{Guid.NewGuid():N}");
        try
        {
            WriteTinyPng(path, "parameters", "cached prompt\nSteps: 1, Seed: 2");
            Check(File.Exists(path), "Expected temporary metadata index source file.");
            using var repository = new MetadataRepository(databaseDirectory);
            Check(repository.RoundTripsForSelfCheck(path), "Expected metadata index round trip.");
            var fingerprint = GetFingerprint(path);
            Check(repository.TryLoad(path, fingerprint, out _), "Expected matching source fingerprint to hit metadata cache.");
            Check(
                !repository.TryLoad(
                    path,
                    fingerprint with { FileLength = fingerprint.FileLength + 1 },
                    out _),
                "Expected a changed source fingerprint to miss metadata cache.");
        }
        finally
        {
            DeleteFileQuietly(path);
            DeleteDirectoryQuietly(databaseDirectory);
        }
    }

    // A batched lookup must return only rows whose fingerprint still matches, paired to the right path.
    private static void CheckMetadataBatchLoad()
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
                repository.CreateEntry(firstPath, firstFingerprint, readResult,
                    new ExtractedPromptMetadata { Prompt = "first" }),
                repository.CreateEntry(secondPath, secondFingerprint, readResult,
                    new ExtractedPromptMetadata { Prompt = "second" })
            ]);

            var loaded = repository.LoadMany(
            [
                new MetadataLookup(firstPath, firstFingerprint),
                new MetadataLookup(secondPath, secondFingerprint),
                new MetadataLookup(missingPath, missingFingerprint)
            ], CancellationToken.None);

            Check(loaded.Count == 2, "Expected a batch load to return only the records that exist.");
            Check(loaded.TryGetValue(firstPath, out var first) && first.Prompt == "first",
                "Expected a batch load to pair each record with its own path.");
            Check(loaded.TryGetValue(secondPath, out var second) && second.Prompt == "second",
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

    private static void CheckMetadataBatchSave()
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
            var firstEntry = repository.CreateEntry(
                firstPath,
                firstFingerprint,
                readResult,
                new ExtractedPromptMetadata { Prompt = "first" });
            var secondEntry = repository.CreateEntry(
                secondPath,
                secondFingerprint,
                readResult,
                new ExtractedPromptMetadata { Prompt = "second" });
            repository.SaveMany([firstEntry, secondEntry]);

            Check(
                repository.TryLoad(firstPath, firstFingerprint, out var first) &&
                repository.TryLoad(secondPath, secondFingerprint, out var second) &&
                first.Prompt == "first" &&
                second.Prompt == "second",
                "Expected batched metadata entries to persist together.");

            var batchCatalog = new GalleryCatalog();
            var firstItem = CreateImageItem(firstPath);
            var secondItem = CreateImageItem(secondPath);
            batchCatalog.Add(new GalleryEntry(firstItem.Path, default, firstItem));
            batchCatalog.Add(new GalleryEntry(secondItem.Path, default, secondItem));
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

    private static void CheckMetadataIndexCleanup()
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

    private static void CheckMetadataFailureClassification()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"comfypromptviewer-selfcheck-failure-{Guid.NewGuid():N}");
        try
        {
            using var repository = new MetadataRepository(directory);
            var service = new ImageMetadataService(repository);
            var missingPath = Path.Combine(directory, "missing.png");
            var result = service.LoadAsync(
                missingPath,
                default,
                skipCacheLookup: false,
                persistResult: true,
                default).GetAwaiter().GetResult();
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
        repository.Save(
            path,
            GetFingerprint(path),
            new ImageReadResult(1, 1, new(StringComparer.OrdinalIgnoreCase)),
            new ExtractedPromptMetadata { Prompt = prompt });
    }

    private static ImageItem CreateImageItem(string path)
    {
        return new ImageItem(
            path,
            sourceFingerprint: default,
            tileSize: 120,
            metadataService: ItemMetadataService,
            decodedImageCache: ItemDecodedImageCache,
            thumbnailService: ItemThumbnailService);
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

    private static void CheckThumbnailCacheWriteBackpressure()
    {
        var firstKey = NewThumbnailKey("backpressure-1");
        var secondKey = NewThumbnailKey("backpressure-2");

        Check(ItemThumbnailService.TryBeginCacheWrite(firstKey), "Expected first thumbnail cache write slot.");
        try
        {
            Check(!ItemThumbnailService.TryBeginCacheWrite(secondKey), "Expected busy thumbnail cache writer to reject queued writes.");
        }
        finally
        {
            ItemThumbnailService.EndCacheWrite(firstKey);
        }
    }

    private static void CheckDeferredThumbnailCacheWriteQueue()
    {
        var activeKey = NewThumbnailKey("queue-active");
        var deferredKey = NewThumbnailKey("queue-deferred");
        var item = CreateImageItem(Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-missing.png"));

        Check(ItemThumbnailService.TryBeginCacheWrite(activeKey), "Expected active thumbnail cache write slot.");
        try
        {
            var queuedBefore = ItemThumbnailService.PendingWriteCount;
            Check(ItemThumbnailService.TryQueueCacheWrite(item, deferredKey), "Expected deferred thumbnail cache write to queue.");
            Check(ItemThumbnailService.PendingWriteCount == queuedBefore + 1,
                "Expected the pending write count to track the deferred queue so bulk producers can throttle.");
            Check(!ItemThumbnailService.TryQueueCacheWrite(item, deferredKey), "Expected duplicate deferred thumbnail cache write to be ignored.");
            ItemThumbnailService.ClearDeferredWrites();
            Check(ItemThumbnailService.PendingWriteCount == 0,
                "Expected clearing deferred writes to empty the pending write count.");
        }
        finally
        {
            ItemThumbnailService.EndCacheWrite(activeKey);
        }
    }

    private static void CheckDeferredThumbnailCacheWritePause()
    {
        var activeKey = NewThumbnailKey("pause-active");
        var deferredKey = NewThumbnailKey("pause-deferred");
        var item = CreateImageItem(Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-missing.png"));

        ItemThumbnailService.SetCacheWritePause(() => true);
        try
        {
            Check(ItemThumbnailService.TryQueueCacheWrite(item, deferredKey), "Expected paused deferred thumbnail cache write to queue.");
            Check(ItemThumbnailService.TryBeginCacheWrite(activeKey), "Expected paused deferred thumbnail cache writer not to take active slot.");
            ItemThumbnailService.EndCacheWrite(activeKey);
            ItemThumbnailService.ClearDeferredWrites();
        }
        finally
        {
            ItemThumbnailService.SetCacheWritePause(null);
        }
    }

    // 0 means "decode natively". Ordinary generation resolutions must land there, because for PNG the
    // resample is pure extra work on top of a decode that happens regardless.
    private static void CheckSelectedPreviewDecodeWidth()
    {
        Check(ThumbnailService.GetPreviewDecodeWidth(832, 1216) == 0,
            "Expected a source narrower than the cap to decode natively rather than upscale.");
        Check(ThumbnailService.GetPreviewDecodeWidth(1024, 1024) == 0,
            "Expected a square SDXL source to decode natively.");
        Check(ThumbnailService.GetPreviewDecodeWidth(1536, 1536) == 0,
            "Expected a 1536 source to decode natively.");
        Check(ThumbnailService.GetPreviewDecodeWidth(2048, 2048) == 0,
            "Expected a 2048 source to decode natively.");
        Check(ThumbnailService.GetPreviewDecodeWidth(2176, 2176) == 0,
            "Expected a source marginally over the cap to skip resampling.");

        Check(ThumbnailService.GetPreviewDecodeWidth(4096, 4096) == 2048,
            "Expected an upscaled source to be capped so preview memory stays bounded.");
        Check(ThumbnailService.GetPreviewDecodeWidth(4096, 6144) == 2048,
            "Expected a tall upscale to be capped by width.");

        // Stays under the width cap but would decode to 67 MB natively.
        var extremeAspect = ThumbnailService.GetPreviewDecodeWidth(2048, 8192);
        Check(extremeAspect is > 0 and < 2048,
            "Expected the pixel budget to catch an extreme aspect ratio the width cap misses.");

        Check(ThumbnailService.GetPreviewDecodeWidth(0, 0) > 0,
            "Expected an unreadable header to fall back to the capped decode.");
    }

    // The sidebar keeps the previous image on screen until a replacement is displayed, so a decode that
    // produces nothing has to be distinguishable from one still in flight. Otherwise a partially written
    // or corrupt file leaves the previous picture next to the new file's metadata indefinitely.
    private static void CheckSelectedPreviewFailureIsObservable()
    {
        var missing = CreateImageItem(
            Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-absent.png"));

        Check(!missing.SelectedPreviewUnavailable,
            "Expected a preview that has not been attempted to read as available.");

        missing.LoadSelectedPreviewSync();

        Check(missing.SelectedPreview is null,
            "Expected an unreadable source to produce no preview.");
        Check(missing.SelectedPreviewUnavailable,
            "Expected a failed preview decode to be observable, not indistinguishable from decoding.");

        missing.ReleaseSelectedPreview();
        Check(!missing.SelectedPreviewUnavailable,
            "Expected releasing a preview to clear the unavailable state so a retry can run.");
    }

    // Prefetch is allowed to overlap the viewport only when the decode is cheap. If a cold ahead load
    // ever starts while visible work is pending, first-paint on a new folder regresses.
    private static void CheckThumbnailPrefetchPolicy()
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
        var down = MainWindow.GetAheadRowWindow(1);
        var up = MainWindow.GetAheadRowWindow(-1);
        Check(down.RowsBelow > down.RowsAbove, "Expected scrolling down to prefetch mostly below.");
        Check(up.RowsAbove > up.RowsBelow, "Expected scrolling up to prefetch mostly above.");
        Check(down.RowsAbove > 0 && up.RowsBelow > 0,
            "Expected the trailing side to retain some prefetch so a reversal is not fully cold.");
        Check(down.RowsBelow == up.RowsAbove && down.RowsAbove == up.RowsBelow,
            "Expected the prefetch window to be symmetric under a direction flip.");
    }

    // Abandons scrolled-away tiles without abandoning a selection outside the last scheduled window.
    private static void CheckRetainedViewportWindow()
    {
        var coordinator = new ThumbnailLoadCoordinator(ItemDecodedImageCache);
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
    private static void CheckThumbnailPackRoundTrip()
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

    private static void CheckThumbnailCacheBudget()
    {
        Check(!DecodedImageCache.ExceedsBudget(DecodedImageCache.MaxCapacity, DecodedImageCache.MaxEstimatedBytes), "Expected exact thumbnail cache budget to fit.");
        Check(DecodedImageCache.ExceedsBudget(DecodedImageCache.MaxCapacity + 1, 0), "Expected thumbnail count budget overflow.");
        Check(DecodedImageCache.ExceedsBudget(1, DecodedImageCache.MaxEstimatedBytes + 1), "Expected thumbnail byte budget overflow.");
        Check(DecodedImageCache.MaxEvictionScanPerTouch > 0,
            "Expected a bounded eviction scan so an all-protected cache cannot walk the whole list per touch.");
    }

    private static void CheckThumbnailDecodeWidths()
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

    private static void CheckJpegThumbnailEncoding()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(sourcePath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var encoded = ThumbnailService.EncodeJpegThumbnail(sourcePath, thumbnailWidth: 180);
            Check(encoded.Length > 3 && encoded[0] == 0xff && encoded[1] == 0xd8 && encoded[2] == 0xff,
                "Expected thumbnail cache output to contain a JPEG signature.");
        }
        finally
        {
            TryDelete(sourcePath);
        }
    }

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

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
