using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ComfyPromptViewer;

// One append-only data file plus one append-only offset log, instead of up to four small JPEGs per image
// spread over hashed per-folder directories. That layout cost a directory hash, a CreateDirectory, a path
// build, and a File.Exists probe per image per width, and clearing the cache meant recursively deleting
// tens of thousands of files. Here a lookup is a dictionary hit, a read is one positional read, and
// clearing is two truncations.
//
// Layout:
//   thumbnails.pack  [magic:4][version:4] then raw JPEG payloads back to back.
//   thumbnails.idx   [magic:4][version:4] then fixed 32-byte records, replayed in order at startup so a
//                    later record supersedes an earlier one for the same key and Length 0 is a tombstone.
//
// Only one process may write. A second instance falls back to read-only and simply re-decodes what it
// cannot find, which is the same cost as a cold cache.
internal sealed class ThumbnailPack : IDisposable
{
    private const uint DataMagic = 0x4B505443; // "CTPK"
    private const uint IndexMagic = 0x58445443; // "CTDX"
    private const uint FormatVersion = 1;
    private const int HeaderSize = 8;
    private const int IndexRecordSize = 32;
    private const int MaxEntryLength = 32 * 1024 * 1024;

    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<ThumbnailKey, PackEntry> _entries = new();
    private readonly string _dataPath;
    private readonly string _indexPath;
    private SafeFileHandle? _dataHandle;
    private SafeFileHandle? _indexHandle;
    private long _dataLength;
    private long _indexLength;
    private bool _canWrite;

    public ThumbnailPack(string cacheDirectory)
    {
        _dataPath = Path.Combine(cacheDirectory, "thumbnails.pack");
        _indexPath = Path.Combine(cacheDirectory, "thumbnails.idx");

        try
        {
            Directory.CreateDirectory(cacheDirectory);
            Open();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Thumbnail pack unavailable at {cacheDirectory}: {ex.Message}");
            Close();
        }
    }

    public bool Contains(in ThumbnailKey key)
    {
        return _entries.ContainsKey(key);
    }

    public bool TryRead(in ThumbnailKey key, out byte[] data)
    {
        data = [];
        if (_dataHandle is not { } handle || !_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        try
        {
            var buffer = new byte[entry.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                // Positional reads are thread-safe, so every thumbnail worker can read the pack directly.
                var chunk = RandomAccess.Read(handle, buffer.AsSpan(read), entry.Offset + read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            if (read != buffer.Length)
            {
                Remove(key);
                return false;
            }

            data = buffer;
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to read thumbnail pack entry at {entry.Offset}: {ex.Message}");
            Remove(key);
            return false;
        }
    }

    public bool Write(in ThumbnailKey key, ReadOnlySpan<byte> data)
    {
        if (!_canWrite || data.Length is 0 or > MaxEntryLength)
        {
            return false;
        }

        lock (_writeLock)
        {
            if (_dataHandle is not { } dataHandle || _indexHandle is not { } indexHandle)
            {
                return false;
            }

            try
            {
                // Data first: a torn write leaves an unreferenced payload, which is only wasted bytes.
                // An index record ahead of its payload would point at nothing.
                var offset = _dataLength;
                RandomAccess.Write(dataHandle, data, offset);
                _dataLength = offset + data.Length;

                AppendIndexRecord(indexHandle, key, offset, data.Length);
                _entries[key] = new PackEntry(offset, data.Length);
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to append thumbnail pack entry: {ex.Message}");
                return false;
            }
        }
    }

    public void Remove(in ThumbnailKey key)
    {
        if (!_entries.TryRemove(key, out _) || !_canWrite)
        {
            return;
        }

        lock (_writeLock)
        {
            if (_indexHandle is { } indexHandle)
            {
                try
                {
                    AppendIndexRecord(indexHandle, key, 0, 0);
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"Failed to append thumbnail pack tombstone: {ex.Message}");
                }
            }
        }
    }

    // Two truncations and a dictionary clear, whatever the cache size. This is the whole reason the pack
    // exists: the old cache had to walk and delete every file.
    public void Clear()
    {
        lock (_writeLock)
        {
            _entries.Clear();
            if (!_canWrite || _dataHandle is not { } dataHandle || _indexHandle is not { } indexHandle)
            {
                return;
            }

            try
            {
                RandomAccess.SetLength(dataHandle, HeaderSize);
                RandomAccess.SetLength(indexHandle, HeaderSize);
                _dataLength = HeaderSize;
                _indexLength = HeaderSize;
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to clear thumbnail pack: {ex.Message}");
            }
        }
    }

    public static ThumbnailKey CreateKey(string sourcePath, long lastWriteTimeUtcTicks, int thumbnailWidth)
    {
        var text = $"{sourcePath}\0{lastWriteTimeUtcTicks}\0{thumbnailWidth}";
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(text), hash);
        return new ThumbnailKey(
            BinaryPrimitives.ReadUInt64LittleEndian(hash),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]));
    }

    public void Dispose()
    {
        Close();
    }

    private void Open()
    {
        try
        {
            _dataHandle = File.OpenHandle(
                _dataPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);
            _indexHandle = File.OpenHandle(
                _indexPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);
            _canWrite = true;
        }
        catch (IOException)
        {
            // Another instance owns the writer. Read what is already there and skip caching.
            Close();
            _dataHandle = File.OpenHandle(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _indexHandle = File.OpenHandle(_indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _canWrite = false;
            DebugLog.Write("Thumbnail pack opened read-only; another instance owns the writer.");
        }

        _dataLength = RandomAccess.GetLength(_dataHandle!);
        _indexLength = RandomAccess.GetLength(_indexHandle!);

        if (!EnsureHeader(_dataHandle!, ref _dataLength, DataMagic) ||
            !EnsureHeader(_indexHandle!, ref _indexLength, IndexMagic))
        {
            Reset();
            return;
        }

        LoadIndex();
    }

    private bool EnsureHeader(SafeFileHandle handle, ref long length, uint magic)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (length == 0)
        {
            if (!_canWrite)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(header, magic);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], FormatVersion);
            RandomAccess.Write(handle, header, 0);
            length = HeaderSize;
            return true;
        }

        if (length < HeaderSize || RandomAccess.Read(handle, header, 0) != HeaderSize)
        {
            return false;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(header) == magic &&
               BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == FormatVersion;
    }

    private void LoadIndex()
    {
        var recordBytes = _indexLength - HeaderSize;
        var recordCount = (int)(recordBytes / IndexRecordSize);
        if (recordCount <= 0)
        {
            return;
        }

        var buffer = new byte[recordCount * IndexRecordSize];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = RandomAccess.Read(_indexHandle!, buffer.AsSpan(read), HeaderSize + read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        var usable = read / IndexRecordSize;
        for (var record = 0; record < usable; record++)
        {
            var span = buffer.AsSpan(record * IndexRecordSize, IndexRecordSize);
            var key = new ThumbnailKey(
                BinaryPrimitives.ReadUInt64LittleEndian(span),
                BinaryPrimitives.ReadUInt64LittleEndian(span[8..]));
            var offset = BinaryPrimitives.ReadInt64LittleEndian(span[16..]);
            var length = BinaryPrimitives.ReadInt32LittleEndian(span[24..]);

            // Length 0 is a tombstone; anything pointing past the data file is a torn append.
            if (length <= 0 || offset < HeaderSize || offset + length > _dataLength)
            {
                _entries.TryRemove(key, out _);
                continue;
            }

            _entries[key] = new PackEntry(offset, length);
        }
    }

    private void AppendIndexRecord(SafeFileHandle handle, in ThumbnailKey key, long offset, int length)
    {
        Span<byte> record = stackalloc byte[IndexRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(record, key.High);
        BinaryPrimitives.WriteUInt64LittleEndian(record[8..], key.Low);
        BinaryPrimitives.WriteInt64LittleEndian(record[16..], offset);
        BinaryPrimitives.WriteInt32LittleEndian(record[24..], length);
        RandomAccess.Write(handle, record, _indexLength);
        _indexLength += IndexRecordSize;
    }

    // A header we cannot recognize means the files are foreign or corrupt. Start over rather than reading
    // arbitrary bytes as thumbnails.
    private void Reset()
    {
        _entries.Clear();
        if (!_canWrite)
        {
            Close();
            return;
        }

        try
        {
            RandomAccess.SetLength(_dataHandle!, 0);
            RandomAccess.SetLength(_indexHandle!, 0);
            _dataLength = 0;
            _indexLength = 0;
            EnsureHeader(_dataHandle!, ref _dataLength, DataMagic);
            EnsureHeader(_indexHandle!, ref _indexLength, IndexMagic);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to reinitialize thumbnail pack: {ex.Message}");
            Close();
        }
    }

    private void Close()
    {
        _dataHandle?.Dispose();
        _indexHandle?.Dispose();
        _dataHandle = null;
        _indexHandle = null;
        _canWrite = false;
        _entries.Clear();
    }

    private readonly record struct PackEntry(long Offset, int Length);
}

internal readonly record struct ThumbnailKey(ulong High, ulong Low);
