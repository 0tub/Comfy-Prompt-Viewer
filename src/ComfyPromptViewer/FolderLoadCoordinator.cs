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
        CancellationToken token)
    {
        return Task.Run(
            () => ReadEntries(EnumeratePaths(folderPath, includeSubfolders, token), token),
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
                return EnumeratePaths(folderPath, includeSubfolders, CancellationToken.None)
                    .Any(ImageFileReader.IsSupportedImage);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugLog.Write($"Failed to scan folder '{folderPath}' for images: {ex.Message}");
                return false;
            }
        });
    }

    private static IEnumerable<string> EnumeratePaths(
        string folderPath,
        bool includeSubfolders,
        CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = includeSubfolders
        };

        foreach (var path in Directory.EnumerateFiles(folderPath, "*", options))
        {
            token.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    private static List<ImageFileEntry> ReadEntries(IEnumerable<string> paths, CancellationToken token)
    {
        var entries = new List<ImageFileEntry>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!ImageFileReader.IsSupportedImage(path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    entries.Add(new ImageFileEntry(path, File.GetLastWriteTimeUtc(path)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugLog.Write($"Skipped image file {path}: {ex.Message}");
            }
        }

        return entries;
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
internal readonly record struct ImageFileEntry(string Path, DateTime LastWriteTimeUtc);
