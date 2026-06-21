using System;
using System.IO;

namespace ComfyPromptViewer;

public static class UserPreferences
{
    public static readonly string AppDataDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ComfyPromptViewer");

    private static readonly string TileSizePath = System.IO.Path.Combine(AppDataDir, "tile-size.txt");
    private static readonly string LastFolderPath = System.IO.Path.Combine(AppDataDir, "last-folder.txt");
    private static readonly string RecentFoldersPath = System.IO.Path.Combine(AppDataDir, "recent-folders.txt");
    private static readonly string IncludeSubfoldersPath = System.IO.Path.Combine(AppDataDir, "include-subfolders.txt");
    private static readonly string ThemeModePath = System.IO.Path.Combine(AppDataDir, "theme-mode.txt");

    public static double LoadTileSize(double defaultValue, double minValue, double maxValue)
    {
        try
        {
            if (File.Exists(TileSizePath) &&
                double.TryParse(
                    File.ReadAllText(TileSizePath).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var savedValue))
            {
                return Math.Clamp(savedValue, minValue, maxValue);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load tile size: {ex.Message}");
        }

        return defaultValue;
    }

    public static void SaveTileSize(double value)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(TileSizePath, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save tile size: {ex.Message}");
        }
    }

    public static string? LoadLastFolderPath()
    {
        try
        {
            if (File.Exists(LastFolderPath))
            {
                return File.ReadAllText(LastFolderPath).Trim();
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load last folder path: {ex.Message}");
        }

        return null;
    }

    public static void SaveLastFolderPath(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(LastFolderPath, folderPath);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save last folder path: {ex.Message}");
        }
    }

    public static System.Collections.Generic.List<RecentFolder> LoadRecentFolders()
    {
        var list = new System.Collections.Generic.List<RecentFolder>();
        try
        {
            if (File.Exists(RecentFoldersPath))
            {
                foreach (var line in File.ReadLines(RecentFoldersPath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed)) continue;

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
                        catch {}
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
                        catch {}
                    }

                    if (list.TrueForAll(x => !string.Equals(x.Path, folder.Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        list.Add(folder);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load recent folders: {ex.Message}");
        }
        return list;
    }

    public static void SaveRecentFolders(System.Collections.Generic.List<RecentFolder> folders)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var lines = new System.Collections.Generic.List<string>();
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
        try
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
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to add recent folder: {ex.Message}");
        }
    }

    public static bool LoadIncludeSubfolders()
    {
        try
        {
            return File.Exists(IncludeSubfoldersPath) &&
                   bool.TryParse(File.ReadAllText(IncludeSubfoldersPath).Trim(), out var value) &&
                   value;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load include-subfolders setting: {ex.Message}");
            return false;
        }
    }

    public static void SaveIncludeSubfolders(bool includeSubfolders)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(IncludeSubfoldersPath, includeSubfolders.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save include-subfolders setting: {ex.Message}");
        }
    }

    public static ThemeMode LoadThemeMode()
    {
        try
        {
            if (File.Exists(ThemeModePath) &&
                Enum.TryParse<ThemeMode>(File.ReadAllText(ThemeModePath).Trim(), ignoreCase: true, out var value) &&
                Enum.IsDefined(value))
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load theme mode: {ex.Message}");
        }

        return ThemeMode.Brown;
    }

    public static void SaveThemeMode(ThemeMode themeMode)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(ThemeModePath, themeMode.ToString());
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to save theme mode: {ex.Message}");
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
