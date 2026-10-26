using System;
using System.IO;

namespace ComfyPromptViewer;

// The gallery's three orderings. Used both for the open folder's catalog and for a scan's results before
// they reach it, so the two can never disagree about where a file belongs.
internal static class GallerySort
{
    public static int Compare(GalleryEntry left, GalleryEntry right, SortMode sortMode)
    {
        return CompareKeys(left.Path, left.Fingerprint, right.Path, right.Fingerprint, sortMode);
    }

    public static int Compare(ImageFileEntry left, ImageFileEntry right, SortMode sortMode)
    {
        return CompareKeys(left.Path, left.Fingerprint, right.Path, right.Fingerprint, sortMode);
    }

    private static int CompareKeys(
        string leftPath,
        SourceFingerprint leftFingerprint,
        string rightPath,
        SourceFingerprint rightFingerprint,
        SortMode sortMode)
    {
        if (sortMode == SortMode.Name)
        {
            return CompareNames(leftPath, rightPath);
        }

        // Compare raw ticks so the sort inner loop does not construct a DateTime per comparison.
        var compare = leftFingerprint.LastWriteTimeUtcTicks.CompareTo(rightFingerprint.LastWriteTimeUtcTicks);
        if (sortMode == SortMode.NewestFirst)
        {
            compare = -compare;
        }

        return compare != 0
            ? compare
            : StringComparer.OrdinalIgnoreCase.Compare(leftPath, rightPath);
    }

    private static int CompareNames(string left, string right)
    {
        // Span slicing keeps the file-name comparison allocation-free across the whole n log n sort.
        var fileNameCompare = Path.GetFileName(left.AsSpan())
            .CompareTo(Path.GetFileName(right.AsSpan()), StringComparison.CurrentCultureIgnoreCase);
        return fileNameCompare != 0
            ? fileNameCompare
            : string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
    }
}
