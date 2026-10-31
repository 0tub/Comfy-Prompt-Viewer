using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

// Reads one file's metadata, from the cache when it is current. This is the boundary for metadata I/O: every
// failure is classified and logged here, so callers only ever see a status.
internal sealed class ImageMetadataService(MetadataRepository repository)
{
    public Task<MetadataLoadResult> LoadAsync(
        string path,
        SourceFingerprint fingerprint,
        bool skipCacheLookup,
        bool persistResult,
        CancellationToken token)
    {
        return Task.Run(() => Load(path, fingerprint, skipCacheLookup, persistResult), token);
    }

    public void Save(MetadataLoadResult result)
    {
        if (result is { NeedsSave: true, Entry: { } entry })
        {
            repository.Save(entry);
        }
    }

    private MetadataLoadResult Load(
        string path,
        SourceFingerprint fingerprint,
        bool skipCacheLookup,
        bool persistResult)
    {
        try
        {
            if (!skipCacheLookup && repository.TryLoad(path, fingerprint, out var cached))
            {
                return MetadataLoadResult.Success(cached, needsSave: false);
            }

            var read = ImageFileReader.Read(path);
            var entry = MetadataRepository.CreateEntry(
                path,
                fingerprint,
                ImageMetadata.From(read, PromptExtractor.ExtractAll(read.TextMetadata)));
            if (persistResult)
            {
                repository.Save(entry);
            }
            return MetadataLoadResult.Success(entry, needsSave: !persistResult);
        }
        catch (SignatureMismatchException)
        {
            return MetadataLoadResult.Failed(MetadataLoadStatus.UnsupportedOrCorrupt);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load metadata for {path}: {ex}");
            return MetadataLoadResult.Failed(ex is IOException or UnauthorizedAccessException
                ? MetadataLoadStatus.TransientIoFailure
                : MetadataLoadStatus.UnsupportedOrCorrupt);
        }
    }
}

internal enum MetadataLoadStatus
{
    NotLoaded,
    Success,
    UnsupportedOrCorrupt,
    TransientIoFailure,
    Cancelled
}

// Success always carries an entry and a failure never does; the factories are the only way to build one.
internal sealed record MetadataLoadResult
{
    private MetadataLoadResult(MetadataLoadStatus status, MetadataIndexEntry? entry, bool needsSave)
    {
        Status = status;
        Entry = entry;
        NeedsSave = needsSave;
    }

    public MetadataLoadStatus Status { get; }
    public MetadataIndexEntry? Entry { get; }
    public bool NeedsSave { get; }

    public static MetadataLoadResult Success(MetadataIndexEntry entry, bool needsSave) =>
        new(MetadataLoadStatus.Success, entry, needsSave);

    public static MetadataLoadResult Failed(MetadataLoadStatus status) =>
        status == MetadataLoadStatus.Success
            ? throw new ArgumentException("A failed load cannot have succeeded.", nameof(status))
            : new(status, null, needsSave: false);
}
