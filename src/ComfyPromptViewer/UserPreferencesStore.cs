using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ComfyPromptViewer;

internal sealed class UserPreferencesStore
{
    private readonly string _appDataDirectory;
    private readonly string _tileSizePath;
    private readonly string _lastFolderPath;
    private readonly string _recentFoldersPath;
    private readonly string _includeSubfoldersPath;
    private readonly string _prewarmThumbnailsPath;
    private readonly string _themeModePath;

    public UserPreferencesStore(string appDataDirectory)
    {
        _appDataDirectory = appDataDirectory;
        _tileSizePath = Path.Combine(appDataDirectory, "tile-size.txt");
        _lastFolderPath = Path.Combine(appDataDirectory, "last-folder.txt");
        _recentFoldersPath = Path.Combine(appDataDirectory, "recent-folders.txt");
        _includeSubfoldersPath = Path.Combine(appDataDirectory, "include-subfolders.txt");
        _prewarmThumbnailsPath = Path.Combine(appDataDirectory, "prewarm-thumbnails.txt");
        _themeModePath = Path.Combine(appDataDirectory, "theme-mode.txt");
    }

    public double LoadTileSize(double defaultValue, double minValue, double maxValue)
    {
        if (TryReadPreference(_tileSizePath, "tile size", out var text) &&
            double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var savedValue))
        {
            return Math.Clamp(savedValue, minValue, maxValue);
        }

        return defaultValue;
    }

    public void SaveTileSize(double value)
    {
        SavePreference(_tileSizePath, value.ToString(CultureInfo.InvariantCulture), "tile size");
    }

    public string? LoadLastFolderPath()
    {
        return TryReadPreference(_lastFolderPath, "last folder path", out var text) ? text : null;
    }

    public void SaveLastFolderPath(string folderPath)
    {
        SavePreference(_lastFolderPath, folderPath, "last folder path");
    }

    public List<RecentFolder> LoadRecentFolders()
    {
        var list = new List<RecentFolder>();
        try
        {
            if (!File.Exists(_recentFoldersPath))
            {
                return list;
            }

            foreach (var line in File.ReadLines(_recentFoldersPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var parts = trimmed.Split('|');
                var folder = new RecentFolder { Path = parts[0] };
                    
                if (parts.Length > 1 && int.TryParse(parts[1], out var count))
                {
                    folder.ImageCount = count;
                }
                if (parts.Length > 2 && long.TryParse(parts[2], out var ticks))
                {
                    try
                    {
                        folder.LastOpened = new DateTime(ticks, DateTimeKind.Utc);
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        DebugLog.Write($"Failed to parse recent folder timestamp for {folder.Path}: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        if (Directory.Exists(folder.Path))
                        {
                            folder.LastOpened = Directory.GetLastWriteTimeUtc(folder.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write($"Failed to read recent folder write time for {folder.Path}: {ex.Message}");
                    }
                }

                if (list.TrueForAll(x => !string.Equals(x.Path, folder.Path, StringComparison.OrdinalIgnoreCase)))
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

    public void SaveRecentFolders(List<RecentFolder> folders)
    {
        try
        {
            Directory.CreateDirectory(_appDataDirectory);
            var lines = new List<string>(folders.Count);
            foreach (var f in folders)
            {
                lines.Add($"{f.Path}|{f.ImageCount}|{f.LastOpened.Ticks}");
            }
            File.WriteAllLines(_recentFoldersPath, lines);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save recent folders: {ex.Message}");
        }
    }

    public void AddRecentFolder(string folderPath, int imageCount)
    {
        var list = LoadRecentFolders();
        list.RemoveAll(x => string.Equals(x.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, new RecentFolder
        {
            Path = folderPath,
            ImageCount = imageCount,
            LastOpened = DateTime.UtcNow
        });
        if (list.Count > 10)
        {
            list.RemoveRange(10, list.Count - 10);
        }
        SaveRecentFolders(list);
    }

    public bool LoadIncludeSubfolders()
    {
        return TryReadPreference(_includeSubfoldersPath, "include-subfolders setting", out var text) &&
               bool.TryParse(text, out var value) &&
               value;
    }

    public void SaveIncludeSubfolders(bool includeSubfolders)
    {
        SavePreference(
            _includeSubfoldersPath,
            includeSubfolders.ToString(CultureInfo.InvariantCulture),
            "include-subfolders setting");
    }

    public bool LoadPrewarmThumbnails()
    {
        return !TryReadPreference(_prewarmThumbnailsPath, "prewarm-thumbnails setting", out var text) ||
               !bool.TryParse(text, out var value) ||
               value;
    }

    public void SavePrewarmThumbnails(bool prewarmThumbnails)
    {
        SavePreference(
            _prewarmThumbnailsPath,
            prewarmThumbnails.ToString(CultureInfo.InvariantCulture),
            "prewarm-thumbnails setting");
    }

    public ThemeMode LoadThemeMode()
    {
        if (TryReadPreference(_themeModePath, "theme mode", out var text) &&
            Enum.TryParse<ThemeMode>(text, ignoreCase: true, out var value) &&
            Enum.IsDefined(value))
        {
            return value;
        }

        return ThemeMode.DarkGray;
    }

    public void SaveThemeMode(ThemeMode themeMode)
    {
        SavePreference(_themeModePath, themeMode.ToString(), "theme mode");
    }

    private static bool TryReadPreference(string path, string label, out string text)
    {
        try
        {
            if (File.Exists(path))
            {
                text = File.ReadAllText(path).Trim();
                return true;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load {label}: {ex.Message}");
        }

        text = "";
        return false;
    }

    private void SavePreference(string path, string value, string label)
    {
        try
        {
            Directory.CreateDirectory(_appDataDirectory);
            File.WriteAllText(path, value);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save {label}: {ex.Message}");
        }
    }
}

public enum ThemeMode
{
    Brown,
    DarkGray,
    DarkBlue,
    DarkGreen,
    Plum
}

public class RecentFolder
{
    public string Path { get; set; } = string.Empty;
    public int ImageCount { get; set; } = -1;
    public DateTime LastOpened { get; set; } = DateTime.MinValue;
}
