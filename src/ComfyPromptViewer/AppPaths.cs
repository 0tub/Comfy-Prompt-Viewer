using System;
using System.IO;

namespace ComfyPromptViewer;

internal static class AppPaths
{
    public static string LocalDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ComfyPromptViewer");
}
