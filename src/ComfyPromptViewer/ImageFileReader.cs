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

        while (stream.Position < stream.Length)
        {
            var length = ReadUInt32BigEndian(stream);
            var typeBuffer = new byte[4];
            Span<byte> typeBytes = typeBuffer;
            ReadExactly(stream, typeBytes);
            var type = Encoding.ASCII.GetString(typeBytes);

            if (length > int.MaxValue)
            {
                throw new InvalidDataException("PNG chunk is too large.");
            }

            var dataLength = checked((int)length);

            switch (type)
            {
                case "IHDR":
                {
                    if (dataLength != 13)
                    {
                        throw new InvalidDataException("Invalid PNG header chunk.");
                    }

                    Span<byte> data = stackalloc byte[13];
                    ReadExactly(stream, data);
                    SkipCrc(stream);
                    width = ReadInt32BigEndian(data[..4]);
                    height = ReadInt32BigEndian(data.Slice(4, 4));
                    break;
                }
                case "tEXt":
                {
                    if (dataLength > MaxTextChunkBytes)
                    {
                        throw new InvalidDataException("PNG text chunk is too large.");
                    }

                    var data = ReadChunkData(stream, dataLength);
                    SkipCrc(stream);
                    ReadTextChunk(data, metadata);
                    break;
                }
                case "iTXt":
                {
                    if (dataLength > MaxTextChunkBytes)
                    {
                        throw new InvalidDataException("PNG text chunk is too large.");
                    }

                    var data = ReadChunkData(stream, dataLength);
                    SkipCrc(stream);
                    ReadInternationalTextChunk(data, metadata);
                    break;
                }
                case "zTXt":
                {
                    if (dataLength > MaxTextChunkBytes)
                    {
                        throw new InvalidDataException("PNG text chunk is too large.");
                    }

                    var data = ReadChunkData(stream, dataLength);
                    SkipCrc(stream);
                    ReadCompressedTextChunk(data, metadata);
                    break;
                }
                case "IEND":
                    SkipChunk(stream, dataLength);
                    return new ImageReadResult(width, height, metadata);
                default:
                    SkipChunk(stream, dataLength);
                    break;
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

            if (marker is 0xD8 or 0xD9)
            {
                continue;
            }

            var length = ReadUInt16BigEndian(stream);
            if (length < 2)
            {
                throw new InvalidDataException("Invalid JPEG segment.");
            }

            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                stream.ReadByte();
                var height = ReadUInt16BigEndian(stream);
                var width = ReadUInt16BigEndian(stream);
                return new ImageReadResult(width, height, []);
            }

            stream.Position += length - 2;
        }

        throw new InvalidDataException("JPEG dimensions not found.");
    }

    private static ImageReadResult ReadWebP(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        ReadExactly(stream, header);
        if (Encoding.ASCII.GetString(header[..4]) != "RIFF" || Encoding.ASCII.GetString(header[8..12]) != "WEBP")
        {
            throw new InvalidDataException("Invalid WebP signature.");
        }

        Span<byte> chunkHeader = stackalloc byte[8];
        ReadExactly(stream, chunkHeader);
        var type = Encoding.ASCII.GetString(chunkHeader[..4]);
        var dataLength = BitConverter.ToInt32(chunkHeader[4..8]);
        if (dataLength < 0)
        {
            throw new InvalidDataException("Invalid WebP chunk length.");
        }

        return type switch
        {
            "VP8X" => ReadWebPExtended(stream, dataLength),
            "VP8L" => ReadWebPLossless(stream, dataLength),
            "VP8 " => ReadWebPLossy(stream, dataLength),
            _ => throw new InvalidDataException("Unsupported WebP variant.")
        };
    }

    private static ImageReadResult ReadWebPExtended(Stream stream, int dataLength)
    {
        if (dataLength < 10)
        {
            throw new InvalidDataException("Invalid extended WebP data.");
        }

        Span<byte> data = stackalloc byte[10];
        ReadExactly(stream, data);
        SkipRiffChunkTail(stream, dataLength, data.Length);

        return new ImageReadResult(
            ReadUInt24LittleEndian(data.Slice(4, 3)) + 1,
            ReadUInt24LittleEndian(data.Slice(7, 3)) + 1,
            []);
    }

    private static ImageReadResult ReadWebPLossless(Stream stream, int dataLength)
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
        return new ImageReadResult(width, height, []);
    }

    private static ImageReadResult ReadWebPLossy(Stream stream, int dataLength)
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
        return new ImageReadResult(width, height, []);
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

    private static int ReadUInt16BigEndian(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExactly(stream, buffer);
        return (buffer[0] << 8) | buffer[1];
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
