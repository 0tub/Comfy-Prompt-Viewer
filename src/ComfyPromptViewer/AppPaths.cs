using System;
using System.IO;

namespace ComfyPromptViewer;

internal static class AppPaths
{
    public static string LocalDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ComfyPromptViewer");

    // A chosen location gets its own ComfyPromptViewer subfolder: the cache root's other subdirectories are
    // deleted as legacy caches, so it must never be a folder the user keeps anything else in. Choosing
    // %LOCALAPPDATA% therefore lands back on the default.
    public static string ThumbnailCacheRoot(string? location) => Path.Combine(
        location is null ? LocalDataDirectory : Path.Combine(location, "ComfyPromptViewer"),
        "thumbnails");
}
