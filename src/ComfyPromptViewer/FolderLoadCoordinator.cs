using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

internal sealed class FolderLoadCoordinator
{
    // Folder-load staleness is one SessionGate; see Staleness.cs. Do not add a second counter here.
    private readonly SessionGate _gate = new();

    public int Generation => _gate.Generation;

    public CancellationToken? CurrentToken => _gate.CurrentToken;

    public Session Restart()
    {
        return _gate.Restart();
    }

    public void Cancel()
    {
        _gate.Cancel();
    }

    public bool IsCurrent(int generation)
    {
        return _gate.IsCurrent(generation);
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
}

internal readonly record struct SourceFingerprint(long LastWriteTimeUtcTicks, long FileLength)
{
    public DateTime LastWriteTimeUtc => new(LastWriteTimeUtcTicks, DateTimeKind.Utc);
}

internal readonly record struct ImageFileEntry(string Path, SourceFingerprint Fingerprint)
{
    public DateTime LastWriteTimeUtc => Fingerprint.LastWriteTimeUtc;
}
