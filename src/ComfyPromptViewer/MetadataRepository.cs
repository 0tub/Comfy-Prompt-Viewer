using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteDB;

namespace ComfyPromptViewer;

internal sealed class MetadataRepository : IDisposable
{
    // Records from any other build are stale. The compiler derives the module version id from the compiled code, so a
    // change to how metadata is read or extracted invalidates the cache without anyone remembering to bump a number.
    // It also changes with unrelated code; one background re-read per folder after an update is cheap.
    private static readonly string CurrentBuild = typeof(MetadataRepository).Module.ModuleVersionId.ToString("N");
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
            ResetIfCorrupt(ex);
            return false;
        }
    }

    public Dictionary<string, MetadataIndexEntry> LoadMany(
        IEnumerable<MetadataLookup> lookups,
        CancellationToken token)
    {
        var entries = new Dictionary<string, MetadataIndexEntry>(StringComparer.OrdinalIgnoreCase);

        // Keyed by _id so each returned document can be paired back to the fingerprint it must satisfy.
        var pending = new Dictionary<string, MetadataLookup>(StringComparer.Ordinal);
        foreach (var lookup in lookups)
        {
            token.ThrowIfCancellationRequested();
            pending[BuildKey(
                lookup.Path,
                lookup.Fingerprint.LastWriteTimeUtcTicks,
                lookup.Fingerprint.FileLength)] = lookup;
        }

        if (pending.Count == 0)
        {
            return entries;
        }

        try
        {
            var keys = new BsonArray();
            foreach (var key in pending.Keys)
            {
                keys.Add(new BsonValue(key));
            }

            lock (_lock)
            {
                var collection = GetCollection();
                var documents = collection.Query()
                    .Where("_id IN @0", keys)
                    .ToList();

                foreach (var document in documents)
                {
                    token.ThrowIfCancellationRequested();
                    if (!document.TryGetValue("_id", out var id) ||
                        !id.IsString ||
                        !pending.TryGetValue(id.AsString, out var lookup))
                    {
                        continue;
                    }

                    var loaded = FromDocument(document);
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
            ResetIfCorrupt(ex);
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
            Build = CurrentBuild,
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
            ResetIfCorrupt(ex);
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
                // One transaction: auto-committing each delete made a bulk delete hold the lock ~4x longer.
                RunInTransactionLocked(collection =>
                {
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
                });
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to delete metadata index paths: {ex.Message}");
        }
    }

    public void PruneMissing(IEnumerable<string> currentPaths, bool includeSubfolders, CancellationToken token = default)
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

            foreach (var folder in folderSet)
            {
                token.ThrowIfCancellationRequested();
                List<BsonDocument> documents;
                lock (_lock)
                {
                    // Read the folder index once. Offset paging would repeatedly walk its earlier rows.
                    documents = GetCollection().Query()
                        .Where("FolderPath = @0", folder)
                        .Select("{ _id: _id, SourcePath: SourcePath }")
                        .ToList();
                }

                for (var start = 0; start < documents.Count; start += 256)
                {
                    token.ThrowIfCancellationRequested();
                    lock (_lock)
                    {
                        var batchStart = start;
                        RunInTransactionLocked(collection =>
                        {
                            var end = Math.Min(documents.Count, batchStart + 256);
                            for (var index = batchStart; index < end; index++)
                            {
                                var document = documents[index];
                                if (!document.TryGetValue("SourcePath", out var sourcePath) ||
                                    !sourcePath.IsString || currentSet.Contains(sourcePath.AsString))
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
                        });
                    }
                    // Release the repository between bounded batches so interactive reads can proceed.
                    Thread.Yield();
                }
            }
        }
        catch (OperationCanceledException)
        {
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

    // A corrupt LiteDB index throws IndexOutOfRangeException on every query and Rebuild() can't repair it. The
    // database is only a cache, so drop it and let the next scan refill it.
    private void ResetIfCorrupt(Exception ex)
    {
        if (ex is not (IndexOutOfRangeException or LiteException { ErrorCode: not LiteException.LOCK_TIMEOUT }))
        {
            return;
        }

        try
        {
            Clear();
            DebugLog.Write("Reset corrupt metadata index.");
        }
        catch
        {
            // Clear already logged the failure.
        }
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
            ["Build"] = entry.Build,
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
            Build = GetString(document, "Build"),
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
               entry.Build == CurrentBuild &&
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
                RunInTransactionLocked(collection =>
                {
                    foreach (var entry in entries)
                    {
                        collection.Upsert(ToDocument(entry));
                    }
                });
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to batch-save metadata index: {ex.Message}");
            ResetIfCorrupt(ex);
        }
    }

    // Caller holds _lock.
    private void RunInTransactionLocked(Action<ILiteCollection<BsonDocument>> work)
    {
        var collection = GetCollection();
        var database = _database!;
        database.BeginTrans();
        try
        {
            work(collection);
            database.Commit();
        }
        catch
        {
            database.Rollback();
            throw;
        }
    }

}

internal readonly record struct MetadataLookup(string Path, SourceFingerprint Fingerprint);

internal sealed class MetadataIndexEntry
{
    public string Id { get; set; } = "";
    public string Build { get; set; } = "";
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
