using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ComfyPromptViewer;

internal sealed class ThumbnailService : IDisposable
{
    private const int ThumbnailJpegQuality = 82;
    private const int SelectedPreviewMaxWidth = 2048;
    private const long SelectedPreviewMaxPixels = 8_000_000;
    private const double PreviewDownscaleThreshold = 1.15;
    private readonly SemaphoreSlim _cacheWriteLimiter = new(1, 1);
    private readonly object _pendingWritesLock = new();
    private readonly HashSet<ThumbnailKey> _pendingWrites = [];
    private readonly Queue<ThumbnailCacheWrite> _deferredWrites = new();
    private readonly ThumbnailPack _pack;
    private Func<bool>? _writesPaused;
    private bool _maintenancePaused;

    public ThumbnailService(string appDataDirectory)
    {
        CacheRootDirectory = Path.Combine(appDataDirectory, "thumbnails");
        _pack = new ThumbnailPack(CacheRootDirectory);
        DebugLog.Observe(Task.Run(RemoveLegacyCacheDirectories), "Legacy thumbnail cache cleanup");
    }

    public string CacheRootDirectory { get; }
    public SemaphoreSlim SelectedPreviewLoadLimiter { get; } = new(1);

    // Lets bulk producers throttle themselves instead of queueing a whole folder at once.
    public int PendingWriteCount
    {
        get
        {
            lock (_pendingWritesLock)
            {
                return _deferredWrites.Count;
            }
        }
    }

    public bool HasCachedThumbnail(in ThumbnailKey key)
    {
        return _pack.Contains(key);
    }

    public Bitmap? TryLoadCachedThumbnail(in ThumbnailKey key)
    {
        if (!_pack.TryRead(key, out var data))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            // A payload that will not decode is worse than a miss; drop it so the next pass re-encodes.
            _pack.Remove(key);
            throw;
        }
    }

    public void RemoveCachedThumbnail(in ThumbnailKey key)
    {
        _pack.Remove(key);
    }

    public Bitmap DecodeThumbnail(string sourcePath, int width)
    {
        using var stream = File.OpenRead(sourcePath);
        return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
    }

    public Bitmap DecodeSelectedPreview(string sourcePath, int knownSourceWidth = 0, int knownSourceHeight = 0)
    {
        var (sourceWidth, sourceHeight) = knownSourceWidth > 0 && knownSourceHeight > 0
            ? (knownSourceWidth, knownSourceHeight)
            : TryReadSourceSize(sourcePath);

        var decodeWidth = GetPreviewDecodeWidth(sourceWidth, sourceHeight);
        using var stream = File.OpenRead(sourcePath);
        return decodeWidth > 0
            ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality)
            : new Bitmap(stream);
    }

    // Returns 0 to decode at native size. PNG has no scaled decode, so the full image is decoded either
    // way and a resample is pure extra work; only real upscales are worth it. The pixel budget catches
    // aspect ratios that stay under the width cap but blow past it in total pixels.
    internal static int GetPreviewDecodeWidth(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return SelectedPreviewMaxWidth;
        }

        var targetWidth = Math.Min(sourceWidth, SelectedPreviewMaxWidth);
        var pixelBudgetWidth = (int)Math.Sqrt(
            SelectedPreviewMaxPixels * (double)sourceWidth / sourceHeight);
        targetWidth = Math.Min(targetWidth, Math.Max(1, pixelBudgetWidth));

        return sourceWidth > targetWidth * PreviewDownscaleThreshold ? targetWidth : 0;
    }

    private static (int Width, int Height) TryReadSourceSize(string sourcePath)
    {
        try
        {
            using var codec = SKCodec.Create(sourcePath);
            return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to read source dimensions for {sourcePath}: {ex.Message}");
            return (0, 0);
        }
    }

    public bool TryQueueCacheWrite(ImageItem item, in ThumbnailKey key)
    {
        if (_pack.Contains(key))
        {
            return false;
        }

        lock (_pendingWritesLock)
        {
            if (_maintenancePaused || !_pendingWrites.Add(key))
            {
                return false;
            }

            _deferredWrites.Enqueue(new ThumbnailCacheWrite(
                item,
                key,
                item.GetThumbnailDecodeWidth()));
        }

        StartWriter();
        return true;
    }

    public void SetCacheWritePause(Func<bool>? isPaused)
    {
        _writesPaused = isPaused;
    }

    public void ResumeDeferredWrites()
    {
        StartWriter();
    }

    internal bool TryBeginCacheWrite(in ThumbnailKey key)
    {
        if (!_cacheWriteLimiter.Wait(0))
        {
            return false;
        }

        lock (_pendingWritesLock)
        {
            if (_pendingWrites.Add(key))
            {
                return true;
            }
        }

        _cacheWriteLimiter.Release();
        return false;
    }

    internal void EndCacheWrite(in ThumbnailKey key)
    {
        lock (_pendingWritesLock)
        {
            _pendingWrites.Remove(key);
        }

        _cacheWriteLimiter.Release();
        StartWriter();
    }

    public void ClearDeferredWrites()
    {
        lock (_pendingWritesLock)
        {
            while (_deferredWrites.TryDequeue(out var write))
            {
                _pendingWrites.Remove(write.Key);
            }
        }
    }

    // Emptying is two truncations, so this no longer depends on how many thumbnails were cached.
    public async Task ClearCacheAsync()
    {
        await PauseAndDrainWritesAsync();
        try
        {
            _pack.Clear();
            await Task.Run(RemoveLegacyCacheDirectories);
        }
        finally
        {
            ResumeWrites();
        }
    }

    // The pack keeps two files at the root, so any subdirectory is left over from the pre-pack layout.
    private void RemoveLegacyCacheDirectories()
    {
        try
        {
            if (!Directory.Exists(CacheRootDirectory))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(CacheRootDirectory))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"Failed to remove legacy thumbnail cache directory {directory}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to enumerate legacy thumbnail cache directories: {ex.Message}");
        }
    }

    public async Task PauseAndDrainWritesAsync()
    {
        lock (_pendingWritesLock)
        {
            _maintenancePaused = true;
            while (_deferredWrites.TryDequeue(out var write))
            {
                _pendingWrites.Remove(write.Key);
            }
        }

        await _cacheWriteLimiter.WaitAsync();
        _cacheWriteLimiter.Release();
    }

    public void ResumeWrites()
    {
        lock (_pendingWritesLock)
        {
            _maintenancePaused = false;
        }

        StartWriter();
    }

    public void Dispose()
    {
        _pack.Dispose();
    }

    private void StartWriter()
    {
        lock (_pendingWritesLock)
        {
            if (_maintenancePaused)
            {
                return;
            }
        }

        if (ShouldPauseWrites() || !_cacheWriteLimiter.Wait(0))
        {
            return;
        }

        if (!TryDequeue(out var write))
        {
            _cacheWriteLimiter.Release();
            return;
        }

        DebugLog.Observe(Task.Run(() =>
        {
            try
            {
                if (_pack.Contains(write.Key))
                {
                    return;
                }

                _pack.Write(write.Key, EncodeJpegThumbnail(write.Item.Path, write.ThumbnailWidth));
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to write deferred thumbnail cache for {write.Item.Path}: {ex.Message}");
            }
            finally
            {
                EndCacheWrite(write.Key);
            }
        }), "Deferred thumbnail cache writer");
    }

    private bool ShouldPauseWrites()
    {
        try
        {
            return _writesPaused?.Invoke() == true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Deferred thumbnail cache pause callback failed: {ex.Message}");
            return false;
        }
    }

    // Bytes only; durability is the pack's problem, so no temp file or atomic move per thumbnail.
    internal static byte[] EncodeJpegThumbnail(string sourcePath, int thumbnailWidth)
    {
        using var codec = SKCodec.Create(sourcePath)
            ?? throw new InvalidDataException($"Could not open thumbnail source {sourcePath}.");
        var sourceInfo = codec.Info;
        var width = Math.Min(sourceInfo.Width, thumbnailWidth);
        var height = Math.Max(1, (int)Math.Round(sourceInfo.Height * (width / (double)sourceInfo.Width)));
        var decodedSize = codec.GetScaledDimensions(width / (float)sourceInfo.Width);
        var decodedInfo = new SKImageInfo(
            decodedSize.Width,
            decodedSize.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var decoded = SKBitmap.Decode(codec, decodedInfo)
            ?? throw new InvalidDataException($"Could not decode thumbnail source {sourcePath}.");
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var resized = new SKBitmap(info);
        if (!decoded.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
        {
            throw new InvalidDataException($"Could not resize thumbnail source {sourcePath}.");
        }

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, ThumbnailJpegQuality)
            ?? throw new InvalidDataException($"Could not encode JPEG thumbnail for {sourcePath}.");
        return data.ToArray();
    }

    private bool TryDequeue(out ThumbnailCacheWrite write)
    {
        lock (_pendingWritesLock)
        {
            return _deferredWrites.TryDequeue(out write);
        }
    }

    private readonly record struct ThumbnailCacheWrite(
        ImageItem Item,
        ThumbnailKey Key,
        int ThumbnailWidth);
}
