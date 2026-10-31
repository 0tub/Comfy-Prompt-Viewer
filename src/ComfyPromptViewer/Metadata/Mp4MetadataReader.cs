using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ComfyPromptViewer;

// ComfyUI's video savers write the generation metadata one of two ways:
// - `movflags use_metadata_tags` (core SaveVideo, newer VideoHelperSuite): moov/udta/meta holds a `keys` box naming
//   each `ilst` item, so `prompt` and `workflow` arrive as their own tags.
// - An iTunes-style `©cmt` comment (older VideoHelperSuite Video Combine) holding {"prompt": "<json>", "workflow": {...}},
//   unpacked here into the same keys.
// Either way they end up under the keys a PNG text chunk uses. The frame size comes from the video track header.
internal static class Mp4MetadataReader
{
    // moov holds the sample tables plus the workflow JSON; anything past this is not a ComfyUI clip.
    private const int MaxMoovBytes = 32 * 1024 * 1024;
    private const int MaxValueBytes = 2 * 1024 * 1024;

    private static readonly uint Moov = FourCc("moov");
    private static readonly uint Trak = FourCc("trak");
    private static readonly uint Tkhd = FourCc("tkhd");
    private static readonly uint Mdia = FourCc("mdia");
    private static readonly uint Hdlr = FourCc("hdlr");
    private static readonly uint Vide = FourCc("vide");
    private static readonly uint Udta = FourCc("udta");
    private static readonly uint Meta = FourCc("meta");
    private static readonly uint Keys = FourCc("keys");
    private static readonly uint Ilst = FourCc("ilst");
    private static readonly uint Data = FourCc("data");
    private static readonly uint Comment = 0xA9636D74; // "©cmt"

    public static ImageReadResult Read(Stream stream)
    {
        var moov = ReadTopLevelBox(stream, Moov) ?? throw new InvalidDataException("MP4 has no moov box.");
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (width, height) = (0, 0);
        foreach (var (type, body) in Children(moov))
        {
            if (type == Trak && width == 0)
            {
                (width, height) = ReadVideoTrackSize(body);
            }
            else if (type == Meta)
            {
                ReadMdtaMeta(body, metadata);
            }
            else if (type == Udta)
            {
                foreach (var (childType, childBody) in Children(body))
                {
                    if (childType == Meta)
                    {
                        ReadMdtaMeta(childBody, metadata);
                    }
                }
            }
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("MP4 has no video track.");
        }

        if (metadata.TryGetValue("comment", out var comment))
        {
            UnpackComment(comment, metadata);
        }

        return new ImageReadResult(width, height, metadata);
    }

    // moov usually follows mdat (no faststart), so top-level boxes are skipped by seeking, not read.
    private static ArraySegment<byte>? ReadTopLevelBox(Stream stream, uint wanted)
    {
        Span<byte> header = stackalloc byte[16];
        while (stream.Position + 8 <= stream.Length)
        {
            var start = stream.Position;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var headerLength = 8;
            if (size == 1)
            {
                stream.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = stream.Length - start;
            }

            if (size < headerLength || size > stream.Length - start)
            {
                throw new InvalidDataException("Invalid MP4 box size.");
            }

            if (type == wanted)
            {
                if (size - headerLength > MaxMoovBytes)
                {
                    throw new InvalidDataException("MP4 moov box is too large.");
                }

                var body = new byte[size - headerLength];
                stream.ReadExactly(body);
                return body;
            }

            stream.Position = start + size;
        }

        return null;
    }

    private static IEnumerable<(uint Type, ArraySegment<byte> Body)> Children(ArraySegment<byte> data)
    {
        var offset = 0;
        while (offset + 8 <= data.Count)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            var type = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4));
            var headerLength = 8;
            if (size == 1 && offset + 16 <= data.Count)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8));
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = data.Count - offset;
            }

            if (size < headerLength || size > data.Count - offset)
            {
                yield break;
            }

            yield return (type, data.Slice(offset + headerLength, (int)size - headerLength));
            offset += (int)size;
        }
    }

    private static (int Width, int Height) ReadVideoTrackSize(ArraySegment<byte> trak)
    {
        ArraySegment<byte> tkhd = default;
        var isVideo = false;
        foreach (var (type, body) in Children(trak))
        {
            if (type == Tkhd)
            {
                tkhd = body;
            }
            else if (type == Mdia)
            {
                foreach (var (childType, childBody) in Children(body))
                {
                    // Full box: version/flags, pre_defined, then the handler type.
                    isVideo |= childType == Hdlr && childBody.Count >= 12 &&
                               BinaryPrimitives.ReadUInt32BigEndian(childBody.AsSpan(8)) == Vide;
                }
            }
        }

        if (!isVideo || tkhd.Count < 8)
        {
            return (0, 0);
        }

        // tkhd ends with width and height as 16.16 fixed point, in both box versions.
        var size = tkhd.AsSpan(tkhd.Count - 8);
        return ((int)(BinaryPrimitives.ReadUInt32BigEndian(size) >> 16), (int)(BinaryPrimitives.ReadUInt32BigEndian(size[4..]) >> 16));
    }

    private static void ReadMdtaMeta(ArraySegment<byte> meta, Dictionary<string, string> metadata)
    {
        if (meta.Count < 4)
        {
            return;
        }

        var keyNames = new List<string>();
        foreach (var (type, body) in Children(meta.Slice(4)))
        {
            if (type == Keys && body.Count >= 8)
            {
                // Full box, entry count, then (size, namespace, name) per key.
                var offset = 8;
                while (offset + 8 <= body.Count)
                {
                    var entrySize = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(offset));
                    if (entrySize < 8 || entrySize > body.Count - offset)
                    {
                        break;
                    }

                    keyNames.Add(Encoding.UTF8.GetString(body.AsSpan(offset + 8, entrySize - 8)));
                    offset += entrySize;
                }
            }
            else if (type == Ilst)
            {
                // With a `keys` box, item types are 1-based indexes into it (ffmpeg writes `keys` first); otherwise
                // they are iTunes four-character codes.
                foreach (var (index, item) in Children(body))
                {
                    var name = index == Comment ? "comment"
                        : index > 0 && index <= keyNames.Count ? keyNames[(int)index - 1]
                        : null;
                    if (name is null)
                    {
                        continue;
                    }

                    foreach (var (itemType, value) in Children(item))
                    {
                        // Type indicator 1 is UTF-8; it is followed by a 4-byte locale. Only JSON is kept: ComfyUI
                        // writes nothing else, and plain tags like ffmpeg's `encoder` would otherwise be taken as
                        // the prompt of a clip that has none.
                        if (itemType == Data && value.Count > 8 && value.Count - 8 <= MaxValueBytes &&
                            BinaryPrimitives.ReadUInt32BigEndian(value) == 1 &&
                            value[8] is (byte)'{' or (byte)'[')
                        {
                            metadata.TryAdd(name, Encoding.UTF8.GetString(value.AsSpan(8)));
                        }
                    }
                }
            }
        }
    }

    private static void UnpackComment(string comment, Dictionary<string, string> metadata)
    {
        try
        {
            using var document = JsonDocument.Parse(comment);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var key in new[] { "prompt", "workflow" })
            {
                // The prompt arrives as JSON text inside a string; the workflow as a nested object.
                if (document.RootElement.TryGetProperty(key, out var value))
                {
                    metadata.TryAdd(key, value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText());
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static uint FourCc(string code) => BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(code));
}
