using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

internal sealed class FolderLoadCoordinator
{
    private readonly object _stateLock = new();
    private CancellationTokenSource? _cancellation;
    private int _generation;

    public int Generation
    {
        get
        {
            lock (_stateLock)
            {
                return _generation;
            }
        }
    }

    public CancellationToken? CurrentToken
    {
        get
        {
            lock (_stateLock)
            {
                return _cancellation?.Token;
            }
        }
    }

    public FolderLoadSession Restart()
    {
        CancellationTokenSource? previousCancellation;
        FolderLoadSession session;
        lock (_stateLock)
        {
            previousCancellation = _cancellation;
            _cancellation = new CancellationTokenSource();
            session = new FolderLoadSession(_cancellation.Token, ++_generation);
        }

        CancelAndDispose(previousCancellation);
        return session;
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_stateLock)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _generation++;
        }

        CancelAndDispose(cancellation);
    }

    public bool IsCurrent(FolderLoadSession session)
    {
        lock (_stateLock)
        {
            return session.Generation == _generation && !session.Token.IsCancellationRequested;
        }
    }

    public bool IsCurrent(int generation)
    {
        lock (_stateLock)
        {
            return generation == _generation && _cancellation is { IsCancellationRequested: false };
        }
    }

    public static Task<List<ImageFileEntry>> ReadFolderAsync(
        string folderPath,
        bool includeSubfolders,
        Comparison<ImageFileEntry> comparison,
        CancellationToken token)
    {
        return Task.Run(
            () =>
            {
                var entries = ReadEntries(EnumerateFiles(folderPath, includeSubfolders, token), token);
                entries.Sort(comparison);
                return entries;
            },
            token);
    }

    public static Task<List<ImageFileEntry>> ReadEntriesAsync(
        IEnumerable<string> paths,
        CancellationToken token)
    {
        return Task.Run(() => ReadEntries(paths, token), token);
    }

    public static Task<bool> HasImagesAsync(string folderPath, bool includeSubfolders)
    {
        return Task.Run(() =>
        {
            try
            {
                return EnumerateFiles(folderPath, includeSubfolders, CancellationToken.None)
                    .Any(file => ImageFileReader.IsSupportedImage(file.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugLog.Write($"Failed to scan folder '{folderPath}' for images: {ex.Message}");
                return false;
            }
        });
    }

    private static IEnumerable<FileInfo> EnumerateFiles(
        string folderPath,
        bool includeSubfolders,
        CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = includeSubfolders
        };

        foreach (var file in new DirectoryInfo(folderPath).EnumerateFiles("*", options))
        {
            token.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private static List<ImageFileEntry> ReadEntries(IEnumerable<FileInfo> files, CancellationToken token)
    {
        var entries = new List<ImageFileEntry>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (!ImageFileReader.IsSupportedImage(file.Name))
            {
                continue;
            }

            try
            {
                entries.Add(new ImageFileEntry(
                    file.FullName,
                    new SourceFingerprint(file.LastWriteTimeUtc.Ticks, file.Length)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugLog.Write($"Skipped image file {file.FullName}: {ex.Message}");
            }
        }

        return entries;
    }

    private static List<ImageFileEntry> ReadEntries(IEnumerable<string> paths, CancellationToken token)
    {
        return ReadEntries(paths.Select(path => new FileInfo(path)), token);
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        cancellation.Dispose();
    }
}

internal readonly record struct FolderLoadSession(CancellationToken Token, int Generation);
internal readonly record struct SourceFingerprint(long LastWriteTimeUtcTicks, long FileLength)
{
    public DateTime LastWriteTimeUtc => new(LastWriteTimeUtcTicks, DateTimeKind.Utc);
}

internal readonly record struct ImageFileEntry(string Path, SourceFingerprint Fingerprint)
{
    public DateTime LastWriteTimeUtc => Fingerprint.LastWriteTimeUtc;
}
