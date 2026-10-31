using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ComfyPromptViewer;

// Plain files under app-local storage, one per setting. This is the parsing boundary: a value that does not
// parse, or is not finite, loads as absent and the caller's default applies. Range rules (tile size, sidebar
// width) belong to the caller, which applies them to every value it receives, saved or not.
internal sealed class UserPreferencesStore(string appDataDirectory)
{
    // Pinned folders are never dropped; this caps only the unpinned history.
    internal const int MaxUnpinnedRecentFolders = 25;

    private static readonly Setting TileSizeSetting = new("tile-size.txt", "tile size");
    private static readonly Setting LastFolderSetting = new("last-folder.txt", "last folder path");
    private static readonly Setting RecentFoldersSetting = new("recent-folders.txt", "recent folders");
    private static readonly Setting IncludeSubfoldersSetting = new("include-subfolders.txt", "include-subfolders setting");
    private static readonly Setting PrewarmThumbnailsSetting = new("prewarm-thumbnails.txt", "prewarm-thumbnails setting");
    private static readonly Setting SoftwareRenderingSetting = new("software-rendering.txt", "software-rendering setting");
    private static readonly Setting ThemeModeSetting = new("theme-mode.txt", "theme mode");
    private static readonly Setting WindowPlacementSetting = new("window-placement.txt", "window placement");
    private static readonly Setting SidebarWidthSetting = new("sidebar-width.txt", "sidebar width");
    private static readonly Setting ThumbnailCacheLocationSetting = new("thumbnail-cache-location.txt", "thumbnail cache location");

    public double? LoadTileSize() => ReadDouble(TileSizeSetting);

    public void SaveTileSize(double value) => Write(TileSizeSetting, value.ToString(CultureInfo.InvariantCulture));

    public string? LoadLastFolderPath() => Read(LastFolderSetting);

    public void SaveLastFolderPath(string folderPath) => Write(LastFolderSetting, folderPath);

    public string? LoadThumbnailCacheLocation() => Read(ThumbnailCacheLocationSetting);

    public void SaveThumbnailCacheLocation(string location) => Write(ThumbnailCacheLocationSetting, location);

    public bool LoadIncludeSubfolders() => ReadBool(IncludeSubfoldersSetting) ?? false;

    public void SaveIncludeSubfolders(bool value) => WriteBool(IncludeSubfoldersSetting, value);

    public bool LoadPrewarmThumbnails() => ReadBool(PrewarmThumbnailsSetting) ?? true;

    public void SavePrewarmThumbnails(bool value) => WriteBool(PrewarmThumbnailsSetting, value);

    // Read before the window exists (Program.BuildAvaloniaApp), so the toggle only takes effect on the next launch.
    public bool LoadSoftwareRendering() => ReadBool(SoftwareRenderingSetting) ?? false;

    public void SaveSoftwareRendering(bool value) => WriteBool(SoftwareRenderingSetting, value);

    public ThemeMode LoadThemeMode()
    {
        return Read(ThemeModeSetting) is { } text &&
               Enum.TryParse<ThemeMode>(text, ignoreCase: true, out var value) &&
               Enum.IsDefined(value)
            ? value
            : ThemeMode.DarkGray;
    }

    public void SaveThemeMode(ThemeMode themeMode) => Write(ThemeModeSetting, themeMode.ToString());

    public double? LoadSidebarWidth() => ReadDouble(SidebarWidthSetting);

    public void SaveSidebarWidth(double value) => Write(SidebarWidthSetting, value.ToString(CultureInfo.InvariantCulture));

    // Stored as `width|height|x|y|maximized`. Position is in screen pixels because that is the unit
    // Window.Position uses; restoring it is only safe after checking it still lands on a connected screen.
    public WindowPlacement? LoadWindowPlacement()
    {
        if (Read(WindowPlacementSetting) is not { } text)
        {
            return null;
        }

        var parts = text.Split('|');
        if (parts.Length < 5 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
            !bool.TryParse(parts[4], out var isMaximized))
        {
            return null;
        }

        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return null;
        }

        return new WindowPlacement(width, height, x, y, isMaximized);
    }

    public void SaveWindowPlacement(WindowPlacement placement)
    {
        Write(WindowPlacementSetting, string.Join(
            '|',
            placement.Width.ToString(CultureInfo.InvariantCulture),
            placement.Height.ToString(CultureInfo.InvariantCulture),
            placement.X.ToString(CultureInfo.InvariantCulture),
            placement.Y.ToString(CultureInfo.InvariantCulture),
            placement.IsMaximized.ToString(CultureInfo.InvariantCulture)));
    }

    // Each line is `path|count|ticks[|pinned]`. A line from before timestamps existed falls back to the
    // folder's write time; a malformed count or timestamp loads as unknown rather than dropping the folder.
    public List<RecentFolder> LoadRecentFolders()
    {
        var list = new List<RecentFolder>();
        try
        {
            var path = PathOf(RecentFoldersSetting);
            if (!File.Exists(path))
            {
                return list;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (ParseRecentFolder(line.Trim()) is { } folder &&
                    list.TrueForAll(x => !IsSamePath(x.Path, folder.Path)))
                {
                    list.Add(folder);
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load recent folders: {ex.Message}");
        }

        return list;
    }

    private static RecentFolder? ParseRecentFolder(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        var parts = line.Split('|');
        var path = parts[0];
        int? imageCount = parts.Length > 1 && int.TryParse(parts[1], out var count) && count >= 0 ? count : null;
        DateTime? lastOpened = parts.Length > 2
            ? ParseUtcTicks(parts[2], path)
            : ReadLegacyLastOpened(path);
        return new RecentFolder(path, imageCount, lastOpened, IsPinned: parts.Length > 3 && parts[3] == "pinned");
    }

    private static DateTime? ParseUtcTicks(string text, string folderPath)
    {
        // Zero is what an unknown time is saved as.
        if (!long.TryParse(text, out var ticks) || ticks == 0)
        {
            return null;
        }

        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            DebugLog.Write($"Ignored an out-of-range recent folder timestamp for {folderPath}.");
            return null;
        }

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static DateTime? ReadLegacyLastOpened(string folderPath)
    {
        try
        {
            return Directory.Exists(folderPath) ? Directory.GetLastWriteTimeUtc(folderPath) : null;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to read recent folder write time for {folderPath}: {ex.Message}");
            return null;
        }
    }

    public void AddRecentFolder(string folderPath, int imageCount)
    {
        var list = LoadRecentFolders();
        var wasPinned = list.Exists(x => x.IsPinned && IsSamePath(x.Path, folderPath));
        list.RemoveAll(x => IsSamePath(x.Path, folderPath));
        list.Insert(0, new RecentFolder(folderPath, imageCount, DateTime.UtcNow, wasPinned));

        // The list is most-recent-first, so the unpinned entries past the cap are the oldest ones.
        var unpinnedSeen = 0;
        list.RemoveAll(x => !x.IsPinned && ++unpinnedSeen > MaxUnpinnedRecentFolders);
        SaveRecentFolders(list);
    }

    public void SetRecentFolderPinned(string folderPath, bool isPinned)
    {
        var list = LoadRecentFolders();
        for (var index = 0; index < list.Count; index++)
        {
            if (IsSamePath(list[index].Path, folderPath))
            {
                list[index] = list[index] with { IsPinned = isPinned };
            }
        }

        SaveRecentFolders(list);
    }

    public void RemoveRecentFolder(string folderPath)
    {
        var list = LoadRecentFolders();
        if (list.RemoveAll(x => IsSamePath(x.Path, folderPath)) > 0)
        {
            SaveRecentFolders(list);
        }
    }

    private void SaveRecentFolders(List<RecentFolder> folders)
    {
        var lines = new List<string>(folders.Count);
        foreach (var folder in folders)
        {
            var line = $"{folder.Path}|{folder.ImageCount ?? -1}|{folder.LastOpened?.Ticks ?? 0}";
            lines.Add(folder.IsPinned ? line + "|pinned" : line);
        }

        Write(RecentFoldersSetting, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static bool IsSamePath(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private string PathOf(Setting setting) => Path.Combine(appDataDirectory, setting.FileName);

    private string? Read(Setting setting)
    {
        try
        {
            var path = PathOf(setting);
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim();
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load {setting.Label}: {ex.Message}");
        }

        return null;
    }

    private double? ReadDouble(Setting setting)
    {
        return Read(setting) is { } text &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
               double.IsFinite(value)
            ? value
            : null;
    }

    private bool? ReadBool(Setting setting)
    {
        return Read(setting) is { } text && bool.TryParse(text, out var value) ? value : null;
    }

    private void WriteBool(Setting setting, bool value) => Write(setting, value.ToString(CultureInfo.InvariantCulture));

    // Written beside the target and moved over it, so a crash or a full disk mid-write leaves the old file
    // rather than a truncated one: a half-written recent-folders file would lose every pin.
    private void Write(Setting setting, string value)
    {
        try
        {
            Directory.CreateDirectory(appDataDirectory);
            var path = PathOf(setting);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save {setting.Label}: {ex.Message}");
        }
    }

    private sealed record Setting(string FileName, string Label);
}

public readonly record struct WindowPlacement(
    double Width,
    double Height,
    int X,
    int Y,
    bool IsMaximized);

public enum ThemeMode
{
    Brown,
    DarkGray,
    DarkBlue,
    DarkGreen,
    Plum
}

// Null means unknown: a legacy or hand-edited line that lacks the field.
public sealed record RecentFolder(string Path, int? ImageCount, DateTime? LastOpened, bool IsPinned);
