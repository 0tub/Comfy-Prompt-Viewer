using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ComfyPromptViewer;

public sealed record ImageReadResult(int Width, int Height, Dictionary<string, string> TextMetadata);

public static class ImageFileReader
{
    private const int MaxTextChunkBytes = 16 * 1024 * 1024;
    private const ushort ExifIfdPointerTag = 0x8769;
    private const ushort UserCommentTag = 0x9286;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool IsSupportedImage(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var span = path.AsSpan();
        var dotIndex = span.LastIndexOf('.');
        if (dotIndex < 0)
        {
            return false;
        }

        var ext = span.Slice(dotIndex);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    public static ImageReadResult Read(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        var span = path.AsSpan();
        var dotIndex = span.LastIndexOf('.');
        if (dotIndex < 0)
        {
            throw new InvalidDataException("Unsupported image type.");
        }

        var ext = span.Slice(dotIndex);
        using var stream = File.OpenRead(path);

        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPng(stream);
        }
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return ReadJpeg(stream);
        }
        if (ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return ReadWebP(stream);
        }

        throw new InvalidDataException("Unsupported image type.");
    }

    private static ImageReadResult ReadPng(Stream stream)
    {
        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        Span<byte> signature = stackalloc byte[8];
        ReadExactly(stream, signature);
        if (!signature.SequenceEqual(pngSignature))
        {
            throw new InvalidDataException("Invalid PNG signature.");
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var width = 0;
        var height = 0;
        Span<byte> typeBytes = stackalloc byte[4];
        Span<byte> ihdrData = stackalloc byte[13];

        while (stream.Position < stream.Length)
        {
            var length = ReadUInt32BigEndian(stream);
            ReadExactly(stream, typeBytes);

            if (length > int.MaxValue)
            {
                throw new InvalidDataException("PNG chunk is too large.");
            }

            var dataLength = checked((int)length);

            if (typeBytes.SequenceEqual("IHDR"u8))
            {
                if (dataLength != 13)
                {
                    throw new InvalidDataException("Invalid PNG header chunk.");
                }

                ReadExactly(stream, ihdrData);
                SkipCrc(stream);
                width = ReadInt32BigEndian(ihdrData[..4]);
                height = ReadInt32BigEndian(ihdrData.Slice(4, 4));
            }
            else if (typeBytes.SequenceEqual("tEXt"u8))
            {
                if (dataLength > MaxTextChunkBytes)
                {
                    throw new InvalidDataException("PNG text chunk is too large.");
                }

                var data = ReadChunkData(stream, dataLength);
                SkipCrc(stream);
                ReadTextChunk(data, metadata);
            }
            else if (typeBytes.SequenceEqual("iTXt"u8))
            {
                if (dataLength > MaxTextChunkBytes)
                {
                    throw new InvalidDataException("PNG text chunk is too large.");
                }

                var data = ReadChunkData(stream, dataLength);
                SkipCrc(stream);
                ReadInternationalTextChunk(data, metadata);
            }
            else if (typeBytes.SequenceEqual("zTXt"u8))
            {
                if (dataLength > MaxTextChunkBytes)
                {
                    throw new InvalidDataException("PNG text chunk is too large.");
                }

                var data = ReadChunkData(stream, dataLength);
                SkipCrc(stream);
                ReadCompressedTextChunk(data, metadata);
            }
            else if (typeBytes.SequenceEqual("IEND"u8))
            {
                SkipChunk(stream, dataLength);
                return new ImageReadResult(width, height, metadata);
            }
            else
            {
                SkipChunk(stream, dataLength);
            }
        }

        return new ImageReadResult(width, height, metadata);
    }

    private static ImageReadResult ReadJpeg(Stream stream)
    {
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
        {
            throw new InvalidDataException("Invalid JPEG signature.");
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var width = 0;
        var height = 0;

        while (stream.Position < stream.Length)
        {
            var markerStart = stream.ReadByte();
            if (markerStart != 0xFF)
            {
                continue;
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xFF);

            if (marker < 0)
            {
                break;
            }

            if (marker == 0xD9)
            {
                break;
            }

            if (JpegMarkerHasNoPayload(marker))
            {
                continue;
            }

            var length = ReadUInt16BigEndian(stream);
            if (length < 2)
            {
                throw new InvalidDataException("Invalid JPEG segment.");
            }

            var dataLength = length - 2;
            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                var data = ReadChunkData(stream, dataLength);
                if (data.Length < 5)
                {
                    throw new InvalidDataException("Invalid JPEG frame segment.");
                }

                height = ReadUInt16BigEndian(data.AsSpan(1, 2));
                width = ReadUInt16BigEndian(data.AsSpan(3, 2));
                continue;
            }

            if (marker == 0xE1)
            {
                var data = ReadChunkData(stream, dataLength);
                ReadExifMetadata(data, metadata);
                continue;
            }

            if (marker == 0xDA)
            {
                stream.Position += dataLength;
                break;
            }

            stream.Position += dataLength;
        }

        if (width > 0 && height > 0)
        {
            return new ImageReadResult(width, height, metadata);
        }

        throw new InvalidDataException("JPEG dimensions not found.");
    }

    private static ImageReadResult ReadWebP(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        ReadExactly(stream, header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WEBP"u8))
        {
            throw new InvalidDataException("Invalid WebP signature.");
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var width = 0;
        var height = 0;
        Span<byte> chunkHeader = stackalloc byte[8];

        while (stream.Position + 8 <= stream.Length)
        {
            ReadExactly(stream, chunkHeader);
            var type = chunkHeader[..4];
            var dataLength = ReadInt32LittleEndian(chunkHeader[4..8]);
            if (dataLength < 0)
            {
                throw new InvalidDataException("Invalid WebP chunk length.");
            }

            if (type.SequenceEqual("VP8X"u8))
            {
                (width, height) = ReadWebPExtended(stream, dataLength);
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                (width, height) = ReadWebPLossless(stream, dataLength);
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                (width, height) = ReadWebPLossy(stream, dataLength);
            }
            else if (type.SequenceEqual("EXIF"u8))
            {
                if (dataLength <= MaxTextChunkBytes)
                {
                    var data = ReadChunkData(stream, dataLength);
                    ReadExifMetadata(data, metadata);
                    SkipRiffChunkTail(stream, dataLength, data.Length);
                }
                else
                {
                    SkipRiffChunk(stream, dataLength);
                }
            }
            else
            {
                SkipRiffChunk(stream, dataLength);
            }
        }

        if (width > 0 && height > 0)
        {
            return new ImageReadResult(width, height, metadata);
        }

        throw new InvalidDataException("Unsupported WebP variant.");
    }

    private static (int Width, int Height) ReadWebPExtended(Stream stream, int dataLength)
    {
        if (dataLength < 10)
        {
            throw new InvalidDataException("Invalid extended WebP data.");
        }

        Span<byte> data = stackalloc byte[10];
        ReadExactly(stream, data);
        SkipRiffChunkTail(stream, dataLength, data.Length);

        return (
            ReadUInt24LittleEndian(data.Slice(4, 3)) + 1,
            ReadUInt24LittleEndian(data.Slice(7, 3)) + 1);
    }

    private static (int Width, int Height) ReadWebPLossless(Stream stream, int dataLength)
    {
        if (dataLength < 5)
        {
            throw new InvalidDataException("Invalid lossless WebP data.");
        }

        Span<byte> data = stackalloc byte[5];
        ReadExactly(stream, data);
        SkipRiffChunkTail(stream, dataLength, data.Length);

        if (data[0] != 0x2F)
        {
            throw new InvalidDataException("Invalid lossless WebP data.");
        }

        var bits = BitConverter.ToUInt32(data[1..]);
        var width = (int)(bits & 0x3FFF) + 1;
        var height = (int)((bits >> 14) & 0x3FFF) + 1;
        return (width, height);
    }

    private static (int Width, int Height) ReadWebPLossy(Stream stream, int dataLength)
    {
        if (dataLength < 10)
        {
            throw new InvalidDataException("Invalid lossy WebP data.");
        }

        Span<byte> data = stackalloc byte[10];
        ReadExactly(stream, data);
        SkipRiffChunkTail(stream, dataLength, data.Length);

        if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A)
        {
            throw new InvalidDataException("Lossy WebP dimensions not found.");
        }

        var width = BitConverter.ToUInt16(data.Slice(6, 2)) & 0x3FFF;
        var height = BitConverter.ToUInt16(data.Slice(8, 2)) & 0x3FFF;
        return (width, height);
    }

    private static void ReadTextChunk(byte[] data, Dictionary<string, string> metadata)
    {
        var separator = Array.IndexOf(data, (byte)0);
        if (separator <= 0)
        {
            return;
        }

        var key = Encoding.Latin1.GetString(data, 0, separator);
        var value = Encoding.UTF8.GetString(data, separator + 1, data.Length - separator - 1);
        metadata[key] = value;
    }

    private static void ReadInternationalTextChunk(byte[] data, Dictionary<string, string> metadata)
    {
        var cursor = 0;
        var keywordEnd = Array.IndexOf(data, (byte)0, cursor);
        if (keywordEnd <= 0 || keywordEnd + 3 >= data.Length)
        {
            return;
        }

        var key = Encoding.UTF8.GetString(data, cursor, keywordEnd - cursor);
        cursor = keywordEnd + 1;
        var compressionFlag = data[cursor++];
        cursor++;

        var languageEnd = Array.IndexOf(data, (byte)0, cursor);
        if (languageEnd < 0)
        {
            return;
        }

        cursor = languageEnd + 1;
        var translatedEnd = Array.IndexOf(data, (byte)0, cursor);
        if (translatedEnd < 0)
        {
            return;
        }

        cursor = translatedEnd + 1;
        metadata[key] = compressionFlag == 1
            ? Inflate(data, cursor, data.Length - cursor)
            : Encoding.UTF8.GetString(data, cursor, data.Length - cursor);
    }

    private static void ReadCompressedTextChunk(byte[] data, Dictionary<string, string> metadata)
    {
        var separator = Array.IndexOf(data, (byte)0);
        if (separator <= 0 || separator + 2 >= data.Length)
        {
            return;
        }

        var key = Encoding.Latin1.GetString(data, 0, separator);
        metadata[key] = Inflate(data, separator + 2, data.Length - separator - 2);
    }

    private static void ReadExifMetadata(ReadOnlySpan<byte> data, Dictionary<string, string> metadata)
    {
        if (data.StartsWith("Exif\0\0"u8))
        {
            data = data[6..];
        }

        if (data.Length < 8)
        {
            return;
        }

        var littleEndian = data[0] == (byte)'I' && data[1] == (byte)'I';
        var bigEndian = data[0] == (byte)'M' && data[1] == (byte)'M';
        if (!littleEndian && !bigEndian)
        {
            return;
        }

        if (ReadUInt16(data.Slice(2, 2), littleEndian) != 42)
        {
            return;
        }

        var firstIfdOffset = ReadUInt32(data.Slice(4, 4), littleEndian);
        uint exifIfdOffset = 0;
        ReadExifIfd(data, firstIfdOffset, littleEndian, metadata, ref exifIfdOffset);

        if (exifIfdOffset != 0)
        {
            ReadExifIfd(data, exifIfdOffset, littleEndian, metadata, ref exifIfdOffset);
        }
    }

    private static void ReadExifIfd(
        ReadOnlySpan<byte> tiff,
        uint ifdOffset,
        bool littleEndian,
        Dictionary<string, string> metadata,
        ref uint exifIfdOffset)
    {
        if (ifdOffset > tiff.Length - 2)
        {
            return;
        }

        var entryCount = ReadUInt16(tiff.Slice((int)ifdOffset, 2), littleEndian);
        var entriesOffset = checked((int)ifdOffset + 2);
        if (entryCount > (tiff.Length - entriesOffset) / 12)
        {
            return;
        }

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = entriesOffset + index * 12;
            var tag = ReadUInt16(tiff.Slice(entryOffset, 2), littleEndian);
            var type = ReadUInt16(tiff.Slice(entryOffset + 2, 2), littleEndian);
            var count = ReadUInt32(tiff.Slice(entryOffset + 4, 4), littleEndian);

            if (tag == ExifIfdPointerTag && TryReadExifEntryUInt(tiff, entryOffset, type, count, littleEndian, out var pointer))
            {
                exifIfdOffset = pointer;
                continue;
            }

            if (!TryGetExifEntryBytes(tiff, entryOffset, type, count, littleEndian, out var value))
            {
                continue;
            }

            if (tag == UserCommentTag)
            {
                StoreMetadataText(metadata, "UserComment", DecodeExifUserComment(value));
            }
        }
    }

    private static bool TryReadExifEntryUInt(
        ReadOnlySpan<byte> tiff,
        int entryOffset,
        ushort type,
        uint count,
        bool littleEndian,
        out uint value)
    {
        value = 0;
        if (count != 1)
        {
            return false;
        }

        if (type == 3)
        {
            value = ReadUInt16(tiff.Slice(entryOffset + 8, 2), littleEndian);
            return true;
        }

        if (type == 4)
        {
            value = ReadUInt32(tiff.Slice(entryOffset + 8, 4), littleEndian);
            return true;
        }

        return false;
    }

    private static bool TryGetExifEntryBytes(
        ReadOnlySpan<byte> tiff,
        int entryOffset,
        ushort type,
        uint count,
        bool littleEndian,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        var unitSize = type switch
        {
            1 or 2 or 7 => 1,
            3 => 2,
            4 or 9 => 4,
            5 or 10 => 8,
            _ => 0
        };

        if (unitSize == 0 || count > int.MaxValue / unitSize)
        {
            return false;
        }

        var byteCount = checked((int)count * unitSize);
        if (byteCount <= 4)
        {
            value = tiff.Slice(entryOffset + 8, byteCount);
            return true;
        }

        var valueOffset = ReadUInt32(tiff.Slice(entryOffset + 8, 4), littleEndian);
        if (valueOffset > tiff.Length || byteCount > tiff.Length - valueOffset)
        {
            return false;
        }

        value = tiff.Slice((int)valueOffset, byteCount);
        return true;
    }

    private static string DecodeExifUserComment(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8)
        {
            var prefix = Encoding.ASCII.GetString(data[..8]);
            var payload = data[8..];
            if (prefix.StartsWith("ASCII", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeUtf8OrLatin1(payload);
            }

            if (prefix.StartsWith("UNICODE", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeUtf16Guess(payload);
            }

            if (prefix.StartsWith("JIS", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeUtf8OrLatin1(payload);
            }

            if (data[..8].SequenceEqual(stackalloc byte[8]))
            {
                return DecodeUtf8OrLatin1(payload);
            }
        }

        return DecodeUtf8OrLatin1(data);
    }

    private static string DecodeUtf16Guess(ReadOnlySpan<byte> data)
    {
        var evenZeros = 0;
        var oddZeros = 0;
        for (var index = 0; index + 1 < data.Length; index += 2)
        {
            if (data[index] == 0) evenZeros++;
            if (data[index + 1] == 0) oddZeros++;
        }

        return DecodeUtf16(data, littleEndian: oddZeros >= evenZeros);
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> data, bool littleEndian)
    {
        if ((data.Length & 1) == 1)
        {
            data = data[..^1];
        }

        var text = littleEndian
            ? Encoding.Unicode.GetString(data)
            : Encoding.BigEndianUnicode.GetString(data);
        return TrimMetadataText(text);
    }

    private static string DecodeUtf8OrLatin1(ReadOnlySpan<byte> data)
    {
        var nullIndex = data.IndexOf((byte)0);
        if (nullIndex >= 0)
        {
            data = data[..nullIndex];
        }

        try
        {
            return TrimMetadataText(StrictUtf8.GetString(data));
        }
        catch (DecoderFallbackException)
        {
            return TrimMetadataText(Encoding.Latin1.GetString(data));
        }
    }

    private static void StoreMetadataText(Dictionary<string, string> metadata, string key, string value)
    {
        value = TrimMetadataText(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        metadata[key] = value;
        if (!metadata.ContainsKey("parameters") && LooksLikeGenerationParameters(value))
        {
            metadata["parameters"] = value;
        }
    }

    private static string TrimMetadataText(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim('\uFEFF', '\0', ' ', '\t', '\n');
    }

    private static bool LooksLikeGenerationParameters(string value)
    {
        return value.Contains("Steps:", StringComparison.OrdinalIgnoreCase) &&
               (value.Contains("Sampler:", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Seed:", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("CFG", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Negative prompt:", StringComparison.OrdinalIgnoreCase));
    }

    private static string Inflate(byte[] data, int offset, int count)
    {
        using var input = new MemoryStream(data, offset, count, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(zlib, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadChunkData(Stream stream, int length)
    {
        var data = new byte[length];
        ReadExactly(stream, data);
        return data;
    }

    private static void SkipChunk(Stream stream, int length)
    {
        stream.Position += length;
        SkipCrc(stream);
    }

    private static void SkipCrc(Stream stream)
    {
        stream.Position += 4;
    }

    private static void SkipRiffChunkTail(Stream stream, int chunkLength, int bytesRead)
    {
        var paddedLength = chunkLength + (chunkLength & 1);
        stream.Position += paddedLength - bytesRead;
    }

    private static void SkipRiffChunk(Stream stream, int chunkLength)
    {
        stream.Position += chunkLength + (chunkLength & 1);
    }

    private static bool JpegMarkerHasNoPayload(int marker)
    {
        return marker is 0x01 or >= 0xD0 and <= 0xD8;
    }

    private static uint ReadUInt32BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExactly(stream, buffer);
        return ((uint)buffer[0] << 24) | ((uint)buffer[1] << 16) | ((uint)buffer[2] << 8) | buffer[3];
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> bytes)
    {
        return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
    }

    private static int ReadInt32LittleEndian(ReadOnlySpan<byte> bytes)
    {
        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
    }

    private static int ReadUInt16BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExactly(stream, buffer);
        return (buffer[0] << 8) | buffer[1];
    }

    private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> bytes)
    {
        return (ushort)((bytes[0] << 8) | bytes[1]);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian)
    {
        return littleEndian
            ? (ushort)(bytes[0] | (bytes[1] << 8))
            : (ushort)((bytes[0] << 8) | bytes[1]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian)
    {
        return littleEndian
            ? (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24))
            : ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes)
    {
        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        if (stream.ReadAtLeast(buffer, buffer.Length) != buffer.Length)
        {
            throw new EndOfStreamException();
        }
    }
}
