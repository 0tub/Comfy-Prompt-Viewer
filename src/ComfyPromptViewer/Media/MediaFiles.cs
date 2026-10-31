using System;
using System.IO;

namespace ComfyPromptViewer;

public enum MediaKind
{
    Png,
    Jpeg,
    WebP,
    Mp4
}

// The one list of file types the app opens. Folder scans, the watcher, the metadata reader and the video
// paths all ask here, so supporting a new type starts and mostly ends with this file.
public static class MediaFiles
{
    public const string SupportedTypesText = "PNG, JPG, WebP, or MP4";

    public static bool TryGetKind(string path, out MediaKind kind)
    {
        var extension = Path.GetExtension(path.AsSpan());
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKind.Png;
        }
        else if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKind.Jpeg;
        }
        else if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKind.WebP;
        }
        else if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            kind = MediaKind.Mp4;
        }
        else
        {
            kind = default;
            return false;
        }

        return true;
    }

    public static bool IsSupported(string path) => TryGetKind(path, out _);

    public static bool IsVideo(string path) => TryGetKind(path, out var kind) && kind == MediaKind.Mp4;
}
