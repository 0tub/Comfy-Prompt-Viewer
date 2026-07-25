using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ComfyPromptViewer;

internal sealed class ThumbnailService
{
    private const int ThumbnailJpegQuality = 82;
    private const int ThumbnailFolderHashLength = 8;
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private readonly SemaphoreSlim _cacheWriteLimiter = new(1, 1);
    private readonly object _pendingWritesLock = new();
    private readonly HashSet<string> _pendingWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ThumbnailCacheWrite> _deferredWrites = new();
    private Func<bool>? _writesPaused;
    private bool _maintenancePaused;

    public ThumbnailService(string appDataDirectory)
    {
        CacheRootDirectory = Path.Combine(appDataDirectory, "thumbnails");
        try
        {
            Directory.CreateDirectory(CacheRootDirectory);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to create thumbnail cache root {CacheRootDirectory}: {ex.Message}");
        }
    }

    public string CacheRootDirectory { get; }
    public SemaphoreSlim SelectedPreviewLoadLimiter { get; } = new(1);

    public string BuildCachePath(string sourcePath, int thumbnailWidth)
    {
        try
        {
            var parentDirectory = Directory.GetParent(sourcePath);
            var parentPath = parentDirectory?.FullName ?? "";
            var parentName = string.IsNullOrWhiteSpace(parentDirectory?.Name)
                ? "root"
                : parentDirectory.Name;
            var folderHash = HashText(parentPath)[..ThumbnailFolderHashLength];
            var cacheDirectory = Path.Combine(
                CacheRootDirectory,
                $"{MakeSafePathSegment(parentName)}_{folderHash}");
            Directory.CreateDirectory(cacheDirectory);

            var input = $"{sourcePath}_{File.GetLastWriteTimeUtc(sourcePath).Ticks}";
            var hash = HashText(input);
            var legacyPath = Path.Combine(cacheDirectory, $"w{thumbnailWidth}_{hash}.jpg");
            try
            {
                File.Delete(legacyPath);
            }
            catch
            {
            }

            return Path.Combine(cacheDirectory, $"j1_w{thumbnailWidth}_{hash}.jpg");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to build thumbnail cache path for {sourcePath}: {ex.Message}");
            return "";
        }
    }

    public Bitmap LoadCachedThumbnail(string cachePath)
    {
        return new Bitmap(cachePath);
    }

    public Bitmap DecodeThumbnail(string sourcePath, int width)
    {
        using var stream = File.OpenRead(sourcePath);
        return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
    }

    public Bitmap DecodeSelectedPreview(string sourcePath, int width)
    {
        using var stream = File.OpenRead(sourcePath);
        return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
    }

    public bool TryQueueCacheWrite(ImageItem item, string cachePath)
    {
        if (string.IsNullOrEmpty(cachePath))
        {
            return false;
        }

        if (File.Exists(cachePath))
        {
            item.SetThumbnailCacheState(cachePath, exists: true);
            return false;
        }

        lock (_pendingWritesLock)
        {
            if (_maintenancePaused || !_pendingWrites.Add(cachePath))
            {
                return false;
            }

            _deferredWrites.Enqueue(new ThumbnailCacheWrite(
                item,
                cachePath,
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

    internal bool TryBeginCacheWrite(string cachePath)
    {
        if (!_cacheWriteLimiter.Wait(0))
        {
            return false;
        }

        lock (_pendingWritesLock)
        {
            if (_pendingWrites.Add(cachePath))
            {
                return true;
            }
        }

        _cacheWriteLimiter.Release();
        return false;
    }

    internal void EndCacheWrite(string cachePath)
    {
        lock (_pendingWritesLock)
        {
            _pendingWrites.Remove(cachePath);
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
                _pendingWrites.Remove(write.CachePath);
            }
        }
    }

    public async Task PauseAndDrainWritesAsync()
    {
        lock (_pendingWritesLock)
        {
            _maintenancePaused = true;
            while (_deferredWrites.TryDequeue(out var write))
            {
                _pendingWrites.Remove(write.CachePath);
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
                if (File.Exists(write.CachePath))
                {
                    write.Item.SetThumbnailCacheState(write.CachePath, exists: true);
                    return;
                }

                SaveJpegThumbnailAtomically(write.Item.Path, write.CachePath, write.ThumbnailWidth);
                write.Item.SetThumbnailCacheState(write.CachePath, exists: true);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to write deferred thumbnail cache for {write.Item.Path} to {write.CachePath}: {ex.Message}");
            }
            finally
            {
                EndCacheWrite(write.CachePath);
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

    internal static void SaveJpegThumbnailAtomically(string sourcePath, string cachePath, int thumbnailWidth)
    {
        var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        try
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
            using (var stream = File.Create(temporaryPath))
            {
                data.SaveTo(stream);
            }

            try
            {
                File.Move(temporaryPath, cachePath);
            }
            catch (IOException) when (File.Exists(cachePath))
            {
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private bool TryDequeue(out ThumbnailCacheWrite write)
    {
        lock (_pendingWritesLock)
        {
            return _deferredWrites.TryDequeue(out write);
        }
    }

    private static string HashText(string value)
    {
        return Convert.ToHexStringLower(
            System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string MakeSafePathSegment(string value)
    {
        var safeValue = value.Trim();
        if (safeValue.Length == 0)
        {
            return "folder";
        }

        var firstInvalidIndex = safeValue.IndexOfAny(InvalidFileNameChars);
        if (firstInvalidIndex < 0)
        {
            return safeValue;
        }

        var chars = safeValue.ToCharArray();
        for (var index = firstInvalidIndex; index < chars.Length; index++)
        {
            if (Array.IndexOf(InvalidFileNameChars, chars[index]) >= 0)
            {
                chars[index] = '_';
            }
        }

        return new string(chars);
    }

    private readonly record struct ThumbnailCacheWrite(
        ImageItem Item,
        string CachePath,
        int ThumbnailWidth);
}
