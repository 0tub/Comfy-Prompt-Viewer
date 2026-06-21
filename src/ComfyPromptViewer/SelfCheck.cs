using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ComfyPromptViewer;

internal static class SelfCheck
{
    public static void Run()
    {
        CheckSearchParsing();
        CheckThemeModes();
        CheckPromptExtraction();
        CheckPngMetadataRead();
        CheckMetadataIndexRoundTrip();
        CheckMetadataIndexCleanup();
        CheckThumbnailCacheWriteBackpressure();
        CheckDeferredThumbnailCacheWriteQueue();
        CheckDeferredThumbnailCacheWritePause();
        CheckThumbnailCacheBudget();
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

        var item = new ImageItem(Path.Combine(Path.GetTempPath(), "search-scope-selfcheck.png"), tileSize: 120);
        item.ApplyMetadataEntry(new MetadataIndexEntry
        {
            SourcePath = item.Path,
            Prompt = "sunlit portrait",
            NegativePrompt = "blurry watermark"
        });

        SearchEngine.ParseQuery("watermark", out positive, out negative);
        Check(!MainWindow.ItemMatchesSearch(item, positive, negative, MainWindow.SearchScope.PositivePrompt),
            "Expected positive prompt search to ignore negative prompt text.");
        Check(MainWindow.ItemMatchesSearch(item, positive, negative, MainWindow.SearchScope.NegativePrompt),
            "Expected negative prompt search to match negative prompt text.");
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
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    private static void CheckMetadataIndexRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}.png");
        var databasePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-metadata-{Guid.NewGuid():N}.db");
        try
        {
            WriteTinyPng(path, "parameters", "cached prompt\nSteps: 1, Seed: 2");
            Check(File.Exists(path), "Expected temporary metadata index source file.");
            using (MetadataIndex.UseDatabaseForSelfCheck(databasePath))
            {
                Check(MetadataIndex.RoundTripsForSelfCheck(path), "Expected metadata index round trip.");
            }
        }
        finally
        {
            try
            {
                File.Delete(path);
                File.Delete(databasePath);
            }
            catch
            {
            }
        }
    }

    private static void CheckMetadataIndexCleanup()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-metadata-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-metadata-{Guid.NewGuid():N}.db");
        var keepPath = Path.Combine(folder, "keep.png");
        var deletePath = Path.Combine(folder, "delete.png");
        var prunePath = Path.Combine(folder, "prune.png");

        try
        {
            Directory.CreateDirectory(folder);
            WriteTinyPng(keepPath, "parameters", "keep prompt");
            WriteTinyPng(deletePath, "parameters", "delete prompt");
            WriteTinyPng(prunePath, "parameters", "prune prompt");

            using (MetadataIndex.UseDatabaseForSelfCheck(databasePath))
            {
                SaveSelfCheckMetadata(keepPath, "keep prompt");
                SaveSelfCheckMetadata(deletePath, "delete prompt");
                SaveSelfCheckMetadata(prunePath, "prune prompt");

                MetadataIndex.DeletePaths([deletePath]);
                Check(!MetadataIndex.TryLoad(deletePath, out _), "Expected deleted metadata index path to be removed.");

                MetadataIndex.PruneMissing([keepPath], includeSubfolders: false);
                Check(MetadataIndex.TryLoad(keepPath, out _), "Expected current metadata index path to remain.");
                Check(!MetadataIndex.TryLoad(prunePath, out _), "Expected missing metadata index path to be pruned.");
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
                File.Delete(databasePath);
            }
            catch
            {
            }
        }
    }

    private static void SaveSelfCheckMetadata(string path, string prompt)
    {
        MetadataIndex.Save(
            path,
            new ImageReadResult(1, 1, new(StringComparer.OrdinalIgnoreCase)),
            new ExtractedPromptMetadata { Prompt = prompt });
    }

    private static void CheckThumbnailCacheWriteBackpressure()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-1.jpg");
        var secondPath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-2.jpg");

        Check(ImageItem.TryBeginThumbnailCacheWrite(firstPath), "Expected first thumbnail cache write slot.");
        try
        {
            Check(!ImageItem.TryBeginThumbnailCacheWrite(secondPath), "Expected busy thumbnail cache writer to reject queued writes.");
        }
        finally
        {
            ImageItem.EndThumbnailCacheWrite(firstPath);
        }
    }

    private static void CheckDeferredThumbnailCacheWriteQueue()
    {
        var activePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-active.jpg");
        var deferredPath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-deferred.jpg");
        var item = new ImageItem(Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-missing.png"), tileSize: 120);

        Check(ImageItem.TryBeginThumbnailCacheWrite(activePath), "Expected active thumbnail cache write slot.");
        try
        {
            Check(ImageItem.TryQueueDeferredThumbnailCacheWrite(item, deferredPath), "Expected deferred thumbnail cache write to queue.");
            Check(!ImageItem.TryQueueDeferredThumbnailCacheWrite(item, deferredPath), "Expected duplicate deferred thumbnail cache write to be ignored.");
            ImageItem.ClearDeferredThumbnailCacheWrites();
        }
        finally
        {
            ImageItem.EndThumbnailCacheWrite(activePath);
        }
    }

    private static void CheckDeferredThumbnailCacheWritePause()
    {
        var activePath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-active.jpg");
        var deferredPath = Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-deferred.jpg");
        var item = new ImageItem(Path.Combine(Path.GetTempPath(), $"comfypromptviewer-selfcheck-{Guid.NewGuid():N}-missing.png"), tileSize: 120);

        ImageItem.SetDeferredThumbnailCacheWritePause(() => true);
        try
        {
            Check(ImageItem.TryQueueDeferredThumbnailCacheWrite(item, deferredPath), "Expected paused deferred thumbnail cache write to queue.");
            Check(ImageItem.TryBeginThumbnailCacheWrite(activePath), "Expected paused deferred thumbnail cache writer not to take active slot.");
            ImageItem.EndThumbnailCacheWrite(activePath);
            ImageItem.ClearDeferredThumbnailCacheWrites();
        }
        finally
        {
            ImageItem.SetDeferredThumbnailCacheWritePause(null);
        }
    }

    private static void CheckThumbnailCacheBudget()
    {
        Check(!ImageCache.ExceedsBudget(ImageCache.MaxCapacity, ImageCache.MaxEstimatedBytes), "Expected exact thumbnail cache budget to fit.");
        Check(ImageCache.ExceedsBudget(ImageCache.MaxCapacity + 1, 0), "Expected thumbnail count budget overflow.");
        Check(ImageCache.ExceedsBudget(1, ImageCache.MaxEstimatedBytes + 1), "Expected thumbnail byte budget overflow.");
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
