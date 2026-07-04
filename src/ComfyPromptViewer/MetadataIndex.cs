using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteDB;

namespace ComfyPromptViewer;

internal static class MetadataIndex
{
    private const int CurrentVersion = 2;
    private const string CollectionName = "metadata";
    private static readonly object Lock = new();
    private static string DatabasePath = Path.Combine(UserPreferences.AppDataDir, "metadata.db");
    private static LiteDatabase? Database;
    private static ILiteCollection<BsonDocument>? Collection;

    public static bool TryLoad(string path, out MetadataIndexEntry entry)
    {
        entry = new MetadataIndexEntry();

        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return false;
            }

            var key = BuildKey(path, fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);
            lock (Lock)
            {
                var collection = GetCollection();
                var loaded = FromDocument(collection.FindById(key));
                if (loaded is null ||
                    loaded.Version != CurrentVersion ||
                    !string.Equals(loaded.SourcePath, path, StringComparison.OrdinalIgnoreCase) ||
                    loaded.LastWriteTimeUtcTicks != fileInfo.LastWriteTimeUtc.Ticks ||
                    loaded.FileLength != fileInfo.Length)
                {
                    return false;
                }

                entry = loaded;
                return true;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load metadata index for {path}: {ex.Message}");
            return false;
        }
    }

    public static Dictionary<string, MetadataIndexEntry> LoadMany(IEnumerable<string> paths, CancellationToken token)
    {
        var entries = new Dictionary<string, MetadataIndexEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            lock (Lock)
            {
                var collection = GetCollection();

                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();

                    var fileInfo = new FileInfo(path);
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }

                    var loaded = FromDocument(collection.FindById(BuildKey(path, fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length)));
                    if (loaded is null ||
                        loaded.Version != CurrentVersion ||
                        !string.Equals(loaded.SourcePath, path, StringComparison.OrdinalIgnoreCase) ||
                        loaded.LastWriteTimeUtcTicks != fileInfo.LastWriteTimeUtc.Ticks ||
                        loaded.FileLength != fileInfo.Length)
                    {
                        continue;
                    }

                    entries[path] = loaded;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to batch load metadata index: {ex.Message}");
        }

        return entries;
    }

    public static void Save(string path, ImageReadResult readResult, ExtractedPromptMetadata extracted)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                DebugLog.Write($"Skipped metadata index save for missing file {path}");
                return;
            }

            var entry = new MetadataIndexEntry
            {
                Id = BuildKey(path, fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length),
                Version = CurrentVersion,
                FolderPath = Path.GetDirectoryName(path) ?? "",
                SourcePath = path,
                LastWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks,
                FileLength = fileInfo.Length,
                Width = readResult.Width,
                Height = readResult.Height,
                Prompt = extracted.Prompt,
                NegativePrompt = extracted.NegativePrompt,
                Tool = extracted.GenerationSettings.Tool,
                Model = extracted.GenerationSettings.Model,
                Sampler = extracted.GenerationSettings.Sampler,
                Seed = extracted.GenerationSettings.Seed,
                Settings = extracted.GenerationSettings.Settings,
                Lora = extracted.GenerationSettings.Lora,
                Resources = extracted.GenerationSettings.Resources
            };

            lock (Lock)
            {
                GetCollection().Upsert(ToDocument(entry));
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save metadata index for {path}: {ex.Message}");
        }
    }

    public static void DeletePaths(IEnumerable<string> paths)
    {
        try
        {
            var pathSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            if (pathSet.Count == 0)
            {
                return;
            }

            lock (Lock)
            {
                var collection = GetCollection();
                foreach (var path in pathSet)
                {
                    var documents = collection.Query()
                        .Where("SourcePath = @0", path)
                        .ToList();

                    foreach (var document in documents)
                    {
                        if (document.TryGetValue("_id", out var id) && id.IsString)
                        {
                            collection.Delete(id.AsString);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to delete metadata index paths: {ex.Message}");
        }
    }

    public static void PruneMissing(IEnumerable<string> currentPaths, bool includeSubfolders)
    {
        try
        {
            var currentSet = new HashSet<string>(currentPaths, StringComparer.OrdinalIgnoreCase);
            if (currentSet.Count == 0)
            {
                return;
            }

            var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in currentSet)
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    folderSet.Add(folder);
                }
            }

            if (folderSet.Count == 0)
            {
                return;
            }

            lock (Lock)
            {
                var collection = GetCollection();
                foreach (var folder in folderSet)
                {
                    var documents = collection.Query()
                        .Where("FolderPath = @0", folder)
                        .ToList();

                    foreach (var document in documents)
                    {
                        if (!document.TryGetValue("SourcePath", out var sourcePath) ||
                            !sourcePath.IsString ||
                            currentSet.Contains(sourcePath.AsString))
                        {
                            continue;
                        }

                        if (!includeSubfolders &&
                            !string.Equals(Path.GetDirectoryName(sourcePath.AsString), folder, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (document.TryGetValue("_id", out var id) && id.IsString)
                        {
                            collection.Delete(id.AsString);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to prune metadata index records: {ex.Message}");
        }
    }

    public static void Clear()
    {
        try
        {
            lock (Lock)
            {
                CloseDatabase();

                if (File.Exists(DatabasePath))
                {
                    File.Delete(DatabasePath);
                }

                var logPath = Path.Combine(
                    Path.GetDirectoryName(DatabasePath) ?? "",
                    $"{Path.GetFileNameWithoutExtension(DatabasePath)}-log.db");
                if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to clear metadata index: {ex.Message}");
            throw;
        }
    }

    internal static IDisposable UseDatabaseForSelfCheck(string databasePath)
    {
        lock (Lock)
        {
            var previousDatabasePath = DatabasePath;
            CloseDatabase();
            DatabasePath = databasePath;
            return new RestoreDatabasePath(() =>
            {
                lock (Lock)
                {
                    CloseDatabase();
                    DatabasePath = previousDatabasePath;
                }
            });
        }
    }

    internal static bool RoundTripsForSelfCheck(string path)
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

        Save(path, result, extracted);
        return TryLoad(path, out var loaded) &&
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

    private static ILiteCollection<BsonDocument> GetCollection()
    {
        if (Collection is not null)
        {
            return Collection;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var database = new LiteDatabase($"Filename={DatabasePath};Connection=direct");
        var collection = database.GetCollection(CollectionName);
        collection.EnsureIndex("SourcePath", "$.SourcePath");
        collection.EnsureIndex("FolderPath", "$.FolderPath");
        collection.EnsureIndex("Version", "$.Version");

        Database = database;
        Collection = collection;
        return Collection;
    }

    private static void CloseDatabase()
    {
        Collection = null;
        Database?.Dispose();
        Database = null;
    }

    private static BsonDocument ToDocument(MetadataIndexEntry entry)
    {
        return new BsonDocument
        {
            ["_id"] = entry.Id,
            ["Version"] = entry.Version,
            ["FolderPath"] = entry.FolderPath,
            ["SourcePath"] = entry.SourcePath,
            ["LastWriteTimeUtcTicks"] = entry.LastWriteTimeUtcTicks,
            ["FileLength"] = entry.FileLength,
            ["Width"] = entry.Width,
            ["Height"] = entry.Height,
            ["Prompt"] = entry.Prompt,
            ["NegativePrompt"] = entry.NegativePrompt,
            ["Tool"] = entry.Tool,
            ["Model"] = entry.Model,
            ["Sampler"] = entry.Sampler,
            ["Seed"] = entry.Seed,
            ["Settings"] = entry.Settings,
            ["Lora"] = entry.Lora,
            ["Resources"] = entry.Resources
        };
    }

    private static MetadataIndexEntry? FromDocument(BsonDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        return new MetadataIndexEntry
        {
            Id = GetString(document, "_id"),
            Version = GetInt32(document, "Version"),
            FolderPath = GetString(document, "FolderPath"),
            SourcePath = GetString(document, "SourcePath"),
            LastWriteTimeUtcTicks = GetInt64(document, "LastWriteTimeUtcTicks"),
            FileLength = GetInt64(document, "FileLength"),
            Width = GetInt32(document, "Width"),
            Height = GetInt32(document, "Height"),
            Prompt = GetString(document, "Prompt"),
            NegativePrompt = GetString(document, "NegativePrompt"),
            Tool = GetString(document, "Tool"),
            Model = GetString(document, "Model"),
            Sampler = GetString(document, "Sampler"),
            Seed = GetString(document, "Seed"),
            Settings = GetString(document, "Settings"),
            Lora = GetString(document, "Lora"),
            Resources = GetString(document, "Resources")
        };
    }

    private static string GetString(BsonDocument document, string fieldName)
    {
        return document.TryGetValue(fieldName, out var value) && value.IsString
            ? value.AsString
            : "";
    }

    private static int GetInt32(BsonDocument document, string fieldName)
    {
        return document.TryGetValue(fieldName, out var value) && value.IsInt32
            ? value.AsInt32
            : 0;
    }

    private static long GetInt64(BsonDocument document, string fieldName)
    {
        return document.TryGetValue(fieldName, out var value) && value.IsInt64
            ? value.AsInt64
            : 0;
    }

    private static string BuildKey(string path, long lastWriteTimeUtcTicks, long fileLength)
    {
        return $"{path}|{lastWriteTimeUtcTicks}|{fileLength}";
    }

    private sealed class RestoreDatabasePath(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

internal sealed class MetadataIndexEntry
{
    public string Id { get; set; } = "";
    public int Version { get; set; } = 1;
    public string FolderPath { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public long LastWriteTimeUtcTicks { get; set; }
    public long FileLength { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Prompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Model { get; set; } = "";
    public string Sampler { get; set; } = "";
    public string Seed { get; set; } = "";
    public string Settings { get; set; } = "";
    public string Lora { get; set; } = "";
    public string Resources { get; set; } = "";
}
