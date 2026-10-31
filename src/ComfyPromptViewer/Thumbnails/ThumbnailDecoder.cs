using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace ComfyPromptViewer;

// Pixels from a source file: the resized thumbnail, its JPEG payload for the pack, and the sidebar's larger
// preview. Stateless; ThumbnailService decides what to cache.
internal static class ThumbnailDecoder
{
    private const int ThumbnailJpegQuality = 82;
    private const int SelectedPreviewMaxWidth = 2048;
    private const long SelectedPreviewMaxPixels = 8_000_000;
    private const double PreviewDownscaleThreshold = 1.15;

    // Zero for either dimension means not known yet; the source header is read instead.
    public static Bitmap DecodeSelectedPreview(string sourcePath, int knownSourceWidth, int knownSourceHeight)
    {
        if (MediaFiles.IsVideo(sourcePath))
        {
            using var frame = VideoPlayback.DecodeFirstFrame(sourcePath);
            return ToBitmap(frame, AlphaFormat.Opaque);
        }

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

    // Bytes only; durability is the pack's problem, so no temp file or atomic move per thumbnail.
    internal static byte[] EncodeJpegThumbnail(string sourcePath, int thumbnailWidth)
    {
        using var resized = DecodeResized(sourcePath, thumbnailWidth);
        return EncodeJpeg(resized) ?? throw new InvalidDataException($"Could not encode JPEG thumbnail for {sourcePath}.");
    }

    public static byte[]? EncodeJpeg(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, ThumbnailJpegQuality);
        return data?.ToArray();
    }

    // Display keeps these pixels as they are; only the pack payload is lossy.
    public static Bitmap ToBitmap(SKBitmap bitmap, AlphaFormat alphaFormat = AlphaFormat.Premul)
    {
        return new Bitmap(PixelFormat.Bgra8888, alphaFormat,
            bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);
    }

    public static SKBitmap DecodeResized(string sourcePath, int thumbnailWidth)
    {
        if (MediaFiles.IsVideo(sourcePath))
        {
            using var frame = VideoPlayback.DecodeFirstFrame(sourcePath);
            var frameWidth = Math.Min(frame.Width, thumbnailWidth);
            return Resize(frame, frameWidth, ScaledHeight(frame.Width, frame.Height, frameWidth), sourcePath);
        }

        using var codec = CreateCodec(sourcePath)
            ?? throw new InvalidDataException($"Could not open thumbnail source {sourcePath}.");
        var sourceInfo = codec.Info;
        var width = Math.Min(sourceInfo.Width, thumbnailWidth);
        var height = ScaledHeight(sourceInfo.Width, sourceInfo.Height, width);
        var decodedSize = codec.GetScaledDimensions(width / (float)sourceInfo.Width);
        var decodedInfo = new SKImageInfo(
            decodedSize.Width,
            decodedSize.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var decoded = SKBitmap.Decode(codec, decodedInfo)
            ?? throw new InvalidDataException($"Could not decode thumbnail source {sourcePath}.");
        return Resize(decoded, width, height, sourcePath);
    }

    private static int ScaledHeight(int sourceWidth, int sourceHeight, int width) =>
        Math.Max(1, (int)Math.Round(sourceHeight * (width / (double)sourceWidth)));

    private static SKBitmap Resize(SKBitmap source, int width, int height, string sourcePath)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var resized = new SKBitmap(info);
        if (!source.ScalePixels(resized, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
        {
            resized.Dispose();
            throw new InvalidDataException($"Could not resize thumbnail source {sourcePath}.");
        }

        return resized;
    }

    private static (int Width, int Height) TryReadSourceSize(string sourcePath)
    {
        try
        {
            using var codec = CreateCodec(sourcePath);
            return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to read source dimensions for {sourcePath}: {ex.Message}");
            return (0, 0);
        }
    }

    // Never SKCodec.Create(path). That opens the file by name through Skia's native file stream, so it is
    // capped at MAX_PATH unless the process manifest is longPathAware and the machine has long paths enabled
    // - neither of which holds for the shipped exe - and it reports the cap as a plain null. Prompt-named
    // generator output clears 260 characters routinely, which is why those files decoded fine for display
    // (every other read here goes through a managed FileStream, and .NET applies the \\?\ prefix itself)
    // while the cache write failed on each pass. Handing Skia a managed stream puts this read on the same
    // footing. The codec adopts the stream, and disposes it itself if creation fails.
    private static SKCodec? CreateCodec(string sourcePath)
    {
        var stream = new SKManagedStream(File.OpenRead(sourcePath), disposeManagedStream: true);
        var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            stream.Dispose();
        }

        return codec;
    }
}
