using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteDB;

namespace ComfyPromptViewer;

internal sealed class MetadataRepository : IDisposable
{
    private const int CurrentVersion = 2;
    private const string CollectionName = "metadata";
    private readonly object _lock = new();
    private readonly string _databasePath;
    private LiteDatabase? _database;
    private ILiteCollection<BsonDocument>? _collection;
    private bool _disposed;

    public MetadataRepository(string appDataDirectory)
    {
        _databasePath = Path.Combine(appDataDirectory, "metadata.db");
    }

    public bool TryLoad(string path, SourceFingerprint fingerprint, out MetadataIndexEntry entry)
    {
        entry = new MetadataIndexEntry();

        try
        {
            lock (_lock)
            {
                var collection = GetCollection();
                var loaded = FromDocument(collection.FindById(BuildKey(
                    path,
                    fingerprint.LastWriteTimeUtcTicks,
                    fingerprint.FileLength)));
                if (!IsCurrent(
                    loaded,
                    path,
                    fingerprint.LastWriteTimeUtcTicks,
                    fingerprint.FileLength))
                {
                    return false;
                }

                entry = loaded!;
                return true;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load metadata index for {path}: {ex.Message}");
            return false;
        }
    }

    public Dictionary<string, MetadataIndexEntry> LoadMany(
        IEnumerable<MetadataLookup> lookups,
        CancellationToken token)
    {
        var entries = new Dictionary<string, MetadataIndexEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            lock (_lock)
            {
                var collection = GetCollection();
                foreach (var lookup in lookups)
                {
                    token.ThrowIfCancellationRequested();
                    var loaded = FromDocument(collection.FindById(BuildKey(
                        lookup.Path,
                        lookup.Fingerprint.LastWriteTimeUtcTicks,
                        lookup.Fingerprint.FileLength)));
                    if (IsCurrent(
                        loaded,
                        lookup.Path,
                        lookup.Fingerprint.LastWriteTimeUtcTicks,
                        lookup.Fingerprint.FileLength))
                    {
                        entries[lookup.Path] = loaded!;
                    }
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

    public void Save(
        string path,
        SourceFingerprint fingerprint,
        ImageReadResult readResult,
        ExtractedPromptMetadata extracted)
    {
        Save(CreateEntry(path, fingerprint, readResult, extracted));
    }

    internal MetadataIndexEntry CreateEntry(
        string path,
        SourceFingerprint fingerprint,
        ImageReadResult readResult,
        ExtractedPromptMetadata extracted)
    {
        return new MetadataIndexEntry
        {
            Id = BuildKey(path, fingerprint.LastWriteTimeUtcTicks, fingerprint.FileLength),
            Version = CurrentVersion,
            FolderPath = Path.GetDirectoryName(path) ?? "",
            SourcePath = path,
            LastWriteTimeUtcTicks = fingerprint.LastWriteTimeUtcTicks,
            FileLength = fingerprint.FileLength,
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
    }

    internal void Save(MetadataIndexEntry entry)
    {
        try
        {
            lock (_lock)
            {
                GetCollection().Upsert(ToDocument(entry));
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save metadata index for {entry.SourcePath}: {ex.Message}");
        }
    }

    public void DeletePaths(IEnumerable<string> paths)
    {
        try
        {
            var pathSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            if (pathSet.Count == 0)
            {
                return;
            }

            lock (_lock)
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

    public void PruneMissing(IEnumerable<string> currentPaths, bool includeSubfolders)
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

            lock (_lock)
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

    public void Clear()
    {
        try
        {
            lock (_lock)
            {
                CloseDatabase();

                if (File.Exists(_databasePath))
                {
                    File.Delete(_databasePath);
                }

                var logPath = Path.Combine(
                    Path.GetDirectoryName(_databasePath) ?? "",
                    $"{Path.GetFileNameWithoutExtension(_databasePath)}-log.db");
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

    internal bool RoundTripsForSelfCheck(string path)
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
        Save(path, fingerprint, result, extracted);
        return TryLoad(path, fingerprint, out var loaded) &&
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

    private ILiteCollection<BsonDocument> GetCollection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_collection is not null)
        {
            return _collection;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var database = new LiteDatabase($"Filename={_databasePath};Connection=direct");
        var collection = database.GetCollection(CollectionName);
        collection.EnsureIndex("SourcePath", "$.SourcePath");
        collection.EnsureIndex("FolderPath", "$.FolderPath");
        collection.EnsureIndex("Version", "$.Version");

        _database = database;
        _collection = collection;
        return _collection;
    }

    private void CloseDatabase()
    {
        _collection = null;
        _database?.Dispose();
        _database = null;
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

    private static bool IsCurrent(MetadataIndexEntry? entry, string path, long lastWriteTimeUtcTicks, long fileLength)
    {
        return entry is not null &&
               entry.Version == CurrentVersion &&
               string.Equals(entry.SourcePath, path, StringComparison.OrdinalIgnoreCase) &&
               entry.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks &&
               entry.FileLength == fileLength;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseDatabase();
        }
    }

    internal void SaveMany(IReadOnlyList<MetadataIndexEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        try
        {
            lock (_lock)
            {
                var collection = GetCollection();
                var database = _database!;
                database.BeginTrans();
                try
                {
                    foreach (var entry in entries)
                    {
                        collection.Upsert(ToDocument(entry));
                    }
                    database.Commit();
                }
                catch
                {
                    database.Rollback();
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to batch-save metadata index: {ex.Message}");
        }
    }

}

internal readonly record struct MetadataLookup(string Path, SourceFingerprint Fingerprint);

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
