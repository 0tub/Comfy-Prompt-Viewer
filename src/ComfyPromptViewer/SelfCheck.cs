using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

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
        CheckSearchParsing();
        CheckSearchResultGenerations();
        CheckGalleryScrollAnchoring();
        CheckGalleryItemReconciliation();
        CheckGalleryCatalog();
        CheckSortedInsertion();
        CheckFolderLoadSessions();
        CheckThemeModes();
        CheckPromptExtraction();
        CheckPngMetadataRead();
        CheckPngMetadataLimit();
        CheckMetadataIndexRoundTrip();
        CheckMetadataBatchSave();
        CheckMetadataIndexCleanup();
        CheckMetadataFailureClassification();
        CheckThumbnailCacheWriteBackpressure();
        CheckDeferredThumbnailCacheWriteQueue();
        CheckDeferredThumbnailCacheWritePause();
        CheckJpegThumbnailEncoding();
        CheckThumbnailCacheBudget();
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
        Check(coordinator.IsCurrent(first), "Expected the new folder load session to be current.");

        var second = coordinator.Restart();
        Check(!coordinator.IsCurrent(first), "Expected restarting folder loading to invalidate the previous session.");
        Check(first.Token.IsCancellationRequested, "Expected restarting folder loading to cancel the previous token.");
        Check(coordinator.IsCurrent(second), "Expected the replacement folder load session to be current.");

        coordinator.Cancel();
        Check(!coordinator.IsCurrent(second), "Expected canceling folder loading to invalidate the active session.");
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

    private static void CheckGalleryCatalog()
    {
        var catalog = new GalleryCatalog();
        var older = CreateImageItem(Path.Combine(Path.GetTempPath(), "catalog-older.png"));
        var newer = CreateImageItem(Path.Combine(Path.GetTempPath(), "catalog-newer.png"));
        catalog.Add(new GalleryEntry(older.Path, new SourceFingerprint(10, 0), older));
        catalog.Add(new GalleryEntry(newer.Path, new SourceFingerprint(20, 0), newer));
        catalog.Sort((left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));

        Check(ReferenceEquals(catalog.Items[0], newer), "Expected catalog sort to keep item and timestamp together.");
        var replacement = CreateImageItem(newer.Path);
        Check(
            catalog.Replace(
                newer.Path,
                new GalleryEntry(newer.Path, new SourceFingerprint(30, 0), replacement),
                out var previous) &&
            ReferenceEquals(previous.Item, newer) &&
            ReferenceEquals(catalog.Items[0], replacement),
            "Expected catalog replacement to update the authoritative entry.");
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

        SearchEngine.ParseQuery("watermark", out positive, out negative);
        Check(!MainWindow.ItemMatchesSearch(item, positive, negative, MainWindow.SearchScope.PositivePrompt),
            "Expected positive prompt search to ignore negative prompt text.");
        Check(MainWindow.ItemMatchesSearch(item, positive, negative, MainWindow.SearchScope.NegativePrompt),
            "Expected negative prompt search to match negative prompt text.");

        SearchEngine.ParseQuery("cosmos-predict", out positive, out negative);
        Check(MainWindow.ItemMatchesSearch(item, positive, negative, MainWindow.SearchScope.All),
            "Expected all search to match normalized LoRA metadata.");
        Check(!MainWindow.ItemMatchesSearch(item, positive, negative, MainWindow.SearchScope.Filename),
            "Expected filename search to ignore LoRA metadata.");

        SearchEngine.ParseQuery("\"cosmos predict lora (1.00)\"", out positive, out negative);
        Check(MainWindow.ItemMatchesSearch(item, positive, negative, MainWindow.SearchScope.All),
            "Expected cached search projection to preserve separator-insensitive exact matching.");

        SearchEngine.ParseQuery("-easynegative", out positive, out negative);
        Check(!MainWindow.ItemMatchesSearch(item, positive, negative, MainWindow.SearchScope.All),
            "Expected all search exclusions to check resource metadata.");
    }

    private static void CheckSearchResultGenerations()
    {
        Check(
            MainWindow.IsCurrentSearchResult(3, 3, 8, 8, isCancellationRequested: false),
            "Expected matching search and data generations to be current.");
        Check(
            !MainWindow.IsCurrentSearchResult(2, 3, 8, 8, isCancellationRequested: false) &&
            !MainWindow.IsCurrentSearchResult(3, 3, 7, 8, isCancellationRequested: false) &&
            !MainWindow.IsCurrentSearchResult(3, 3, 8, 8, isCancellationRequested: true),
            "Expected stale or canceled search results to be rejected.");
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

            var firstItem = CreateImageItem(firstPath);
            var secondItem = CreateImageItem(secondPath);
            firstItem.ApplyMetadataResult(MetadataLoadResult.Success(firstEntry, needsSave: false));
            secondItem.ApplyMetadataResult(MetadataLoadResult.Success(secondEntry, needsSave: false));
            Check(
                firstItem.HasLoadedMetadata &&
                secondItem.HasLoadedMetadata &&
                firstItem.SearchProjection.Prompt == "first" &&
                secondItem.SearchProjection.Prompt == "second",
                "Expected a metadata result batch to update item state and search projections.");
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

    private static void CheckThumbnailCacheWriteBackpressure()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-1.jpg");
        var secondPath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-2.jpg");

        Check(ItemThumbnailService.TryBeginCacheWrite(firstPath), "Expected first thumbnail cache write slot.");
        try
        {
            Check(!ItemThumbnailService.TryBeginCacheWrite(secondPath), "Expected busy thumbnail cache writer to reject queued writes.");
        }
        finally
        {
            ItemThumbnailService.EndCacheWrite(firstPath);
        }
    }

    private static void CheckDeferredThumbnailCacheWriteQueue()
    {
        var activePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-active.jpg");
        var deferredPath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-deferred.jpg");
        var item = CreateImageItem(Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-missing.png"));

        Check(ItemThumbnailService.TryBeginCacheWrite(activePath), "Expected active thumbnail cache write slot.");
        try
        {
            Check(ItemThumbnailService.TryQueueCacheWrite(item, deferredPath), "Expected deferred thumbnail cache write to queue.");
            Check(!ItemThumbnailService.TryQueueCacheWrite(item, deferredPath), "Expected duplicate deferred thumbnail cache write to be ignored.");
            ItemThumbnailService.ClearDeferredWrites();
        }
        finally
        {
            ItemThumbnailService.EndCacheWrite(activePath);
        }
    }

    private static void CheckDeferredThumbnailCacheWritePause()
    {
        var activePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-active.jpg");
        var deferredPath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-deferred.jpg");
        var item = CreateImageItem(Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-missing.png"));

        ItemThumbnailService.SetCacheWritePause(() => true);
        try
        {
            Check(ItemThumbnailService.TryQueueCacheWrite(item, deferredPath), "Expected paused deferred thumbnail cache write to queue.");
            Check(ItemThumbnailService.TryBeginCacheWrite(activePath), "Expected paused deferred thumbnail cache writer not to take active slot.");
            ItemThumbnailService.EndCacheWrite(activePath);
            ItemThumbnailService.ClearDeferredWrites();
        }
        finally
        {
            ItemThumbnailService.SetCacheWritePause(null);
        }
    }

    private static void CheckThumbnailCacheBudget()
    {
        Check(!DecodedImageCache.ExceedsBudget(DecodedImageCache.MaxCapacity, DecodedImageCache.MaxEstimatedBytes), "Expected exact thumbnail cache budget to fit.");
        Check(DecodedImageCache.ExceedsBudget(DecodedImageCache.MaxCapacity + 1, 0), "Expected thumbnail count budget overflow.");
        Check(DecodedImageCache.ExceedsBudget(1, DecodedImageCache.MaxEstimatedBytes + 1), "Expected thumbnail byte budget overflow.");
    }

    private static void CheckJpegThumbnailEncoding()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.png");
        var cachePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(sourcePath, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            ThumbnailService.SaveJpegThumbnailAtomically(sourcePath, cachePath, thumbnailWidth: 180);
            var signature = File.ReadAllBytes(cachePath);
            Check(signature.Length > 3 && signature[0] == 0xff && signature[1] == 0xd8 && signature[2] == 0xff,
                "Expected thumbnail cache output to contain a JPEG signature.");
        }
        finally
        {
            TryDelete(sourcePath);
            TryDelete(cachePath);
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
