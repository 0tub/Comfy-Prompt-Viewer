using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

internal sealed class ImageMetadataService(MetadataRepository repository)
{
    public MetadataRepository Repository { get; } = repository;

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
        if (result.NeedsSave && result.Entry is { } entry)
        {
            Repository.Save(entry);
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
            if (!skipCacheLookup && Repository.TryLoad(path, fingerprint, out var cached))
            {
                return MetadataLoadResult.Success(cached, needsSave: false);
            }

            var result = ImageFileReader.Read(path);
            var extracted = PromptExtractor.ExtractAll(result.TextMetadata);
            var entry = Repository.CreateEntry(path, fingerprint, result, extracted);
            if (persistResult)
            {
                Repository.Save(entry);
            }
            return MetadataLoadResult.Success(entry, needsSave: !persistResult);
        }
        catch (InvalidDataException ex)
        {
            return new MetadataLoadResult(MetadataLoadStatus.UnsupportedOrCorrupt, null, ex, NeedsSave: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MetadataLoadResult(MetadataLoadStatus.TransientIoFailure, null, ex, NeedsSave: false);
        }
        catch (Exception ex)
        {
            return new MetadataLoadResult(MetadataLoadStatus.UnsupportedOrCorrupt, null, ex, NeedsSave: false);
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

internal sealed record MetadataLoadResult(
    MetadataLoadStatus Status,
    MetadataIndexEntry? Entry,
    Exception? Exception,
    bool NeedsSave)
{
    public static MetadataLoadResult Success(MetadataIndexEntry entry, bool needsSave) =>
        new(MetadataLoadStatus.Success, entry, null, needsSave);
}
