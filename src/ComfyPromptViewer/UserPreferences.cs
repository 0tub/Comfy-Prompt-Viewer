using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ComfyPromptViewer;

public static class UserPreferences
{
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ComfyPromptViewer");

    private static readonly string TileSizePath = Path.Combine(AppDataDir, "tile-size.txt");
    private static readonly string LastFolderPath = Path.Combine(AppDataDir, "last-folder.txt");
    private static readonly string RecentFoldersPath = Path.Combine(AppDataDir, "recent-folders.txt");
    private static readonly string IncludeSubfoldersPath = Path.Combine(AppDataDir, "include-subfolders.txt");
    private static readonly string ThemeModePath = Path.Combine(AppDataDir, "theme-mode.txt");

    public static double LoadTileSize(double defaultValue, double minValue, double maxValue)
    {
        if (TryReadPreference(TileSizePath, "tile size", out var text) &&
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

    public static void SaveTileSize(double value)
    {
        SavePreference(TileSizePath, value.ToString(CultureInfo.InvariantCulture), "tile size");
    }

    public static string? LoadLastFolderPath()
    {
        return TryReadPreference(LastFolderPath, "last folder path", out var text) ? text : null;
    }

    public static void SaveLastFolderPath(string folderPath)
    {
        SavePreference(LastFolderPath, folderPath, "last folder path");
    }

    public static List<RecentFolder> LoadRecentFolders()
    {
        var list = new List<RecentFolder>();
        try
        {
            if (!File.Exists(RecentFoldersPath))
            {
                return list;
            }

            foreach (var line in File.ReadLines(RecentFoldersPath))
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

    public static void SaveRecentFolders(List<RecentFolder> folders)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var lines = new List<string>(folders.Count);
            foreach (var f in folders)
            {
                lines.Add($"{f.Path}|{f.ImageCount}|{f.LastOpened.Ticks}");
            }
            File.WriteAllLines(RecentFoldersPath, lines);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save recent folders: {ex.Message}");
        }
    }

    public static void AddRecentFolder(string folderPath, int imageCount)
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

    public static bool LoadIncludeSubfolders()
    {
        return TryReadPreference(IncludeSubfoldersPath, "include-subfolders setting", out var text) &&
               bool.TryParse(text, out var value) &&
               value;
    }

    public static void SaveIncludeSubfolders(bool includeSubfolders)
    {
        SavePreference(
            IncludeSubfoldersPath,
            includeSubfolders.ToString(CultureInfo.InvariantCulture),
            "include-subfolders setting");
    }

    public static ThemeMode LoadThemeMode()
    {
        if (TryReadPreference(ThemeModePath, "theme mode", out var text) &&
            Enum.TryParse<ThemeMode>(text, ignoreCase: true, out var value) &&
            Enum.IsDefined(value))
        {
            return value;
        }

        return ThemeMode.DarkGray;
    }

    public static void SaveThemeMode(ThemeMode themeMode)
    {
        SavePreference(ThemeModePath, themeMode.ToString(), "theme mode");
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

    private static void SavePreference(string path, string value, string label)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
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
