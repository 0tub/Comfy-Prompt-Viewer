using System;
using System.IO;
using System.Linq;

namespace ComfyPromptViewer;

// How a folder path is identified and shown: the key that says two paths are the same folder, the header
// title, its truncated path, and recent-folder rows.
internal static class FolderNames
{
    // Trailing separators and Windows path casing have to land on the same key; otherwise reopening the same
    // folder typed slightly differently would count as a different folder (and build a second thumbnail cache).
    public static string Key(string folderPath)
    {
        var full = Path.GetFullPath(folderPath);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':'))
        {
            // A drive root or a filesystem root has nothing to trim.
            trimmed = full;
        }

        return OperatingSystem.IsWindows() ? trimmed.ToLowerInvariant() : trimmed;
    }

    // The last segment, ignoring a trailing separator. A root has none, so it is shown whole.
    public static string Leaf(string folderPath)
    {
        var name = Path.GetFileName(TrimEnd(folderPath));
        return string.IsNullOrEmpty(name) ? folderPath : name;
    }

    public static string Parent(string folderPath) => Path.GetFileName(Path.GetDirectoryName(TrimEnd(folderPath))) ?? "";

    public static string Truncate(string path, int maxSegments = 3)
    {
        var parts = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= maxSegments)
        {
            return path;
        }

        var separator = Path.DirectorySeparatorChar;
        return "..." + separator + string.Join(separator, parts.Skip(parts.Length - maxSegments));
    }

    private static string TrimEnd(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
