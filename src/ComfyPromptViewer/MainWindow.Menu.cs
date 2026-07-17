using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowMainMenu();
    }

    private async void CopyPathButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFolderPath)) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard != null)
        {
            await topLevel.Clipboard.SetTextAsync(_currentFolderPath);
        }
    }

    private string TruncatePath(string path, int maxSegments = 3)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "Open an image folder to start scrolling.";
        }

        try
        {
            var separator = Path.DirectorySeparatorChar;
            var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            
            if (parts.Length <= maxSegments)
            {
                return path;
            }

            var lastSegments = parts.Skip(parts.Length - maxSegments).ToArray();
            return "..." + separator + string.Join(separator, lastSegments);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to shorten path {path}: {ex.Message}");
            return path;
        }
    }

    private async void CloseMenuError_Click(object? sender, RoutedEventArgs e)
    {
        if (!MenuErrorBanner.IsVisible) return;

        MenuErrorBanner.Opacity = 0;
        await Task.Delay(120);
        if (MenuErrorBanner.Opacity == 0)
        {
            MenuErrorBanner.IsVisible = false;
        }
    }

    private async void ClearCacheButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_thumbnailCacheClearInProgress)
        {
            return;
        }

        _thumbnailCacheClearInProgress = true;
        _thumbnailLoads.Clear();
        var cacheWritesPaused = false;
        try
        {
            await ImageItem.PauseAndDrainThumbnailCacheWritesAsync();
            cacheWritesPaused = true;
            foreach (var item in _allImageItems)
            {
                item.InvalidateThumbnailCacheState();
            }
            ImageCache.ClearAndReleaseAll();
            if (Directory.Exists(ImageItem.ThumbnailCacheRootDir))
            {
                Directory.Delete(ImageItem.ThumbnailCacheRootDir, recursive: true);
            }

            Directory.CreateDirectory(ImageItem.ThumbnailCacheRootDir);
            ShowAdvancedMaintenanceStatus("Thumbnail cache cleared.");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to clear thumbnail cache: {ex}");
            ClearAdvancedMaintenanceStatus();
            ShowMenuError($"Could not clear cache: {ex.Message}");
        }
        finally
        {
            if (cacheWritesPaused)
            {
                ImageItem.ResumeThumbnailCacheWrites();
            }
            _thumbnailCacheClearInProgress = false;
            QueueViewportThumbnailSchedule(force: true);
        }
    }

    private void ClearMetadataCacheButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            MetadataIndex.Clear();
            ShowAdvancedMaintenanceStatus("Metadata cache cleared.");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to clear metadata cache: {ex}");
            ClearAdvancedMaintenanceStatus();
            ShowMenuError($"Could not clear metadata cache: {ex.Message}");
        }
    }

    private void OpenAppDataButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ImageItem.ThumbnailCacheRootDir);
            OpenFolderInFileManager(UserPreferences.AppDataDir);
            ShowAdvancedMaintenanceStatus("App data folder opened.");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to open app data folder: {ex}");
            ClearAdvancedMaintenanceStatus();
            ShowMenuError($"Could not open app data: {ex.Message}");
        }
    }

    private async void ShowAdvancedMaintenanceStatus(string message)
    {
        CancelAndDispose(ref _advancedMaintenanceStatusCancellation);
        _advancedMaintenanceStatusCancellation = new CancellationTokenSource();
        var token = _advancedMaintenanceStatusCancellation.Token;

        AdvancedMaintenanceStatus.Text = message;
        AdvancedMaintenanceStatus.Opacity = 0;
        AdvancedMaintenanceStatus.IsVisible = true;
        await Task.Yield();
        if (token.IsCancellationRequested)
        {
            return;
        }

        AdvancedMaintenanceStatus.Opacity = 1;

        try
        {
            await Task.Delay(AdvancedMaintenanceStatusDuration, token);
            AdvancedMaintenanceStatus.Opacity = 0;
            await Task.Delay(120, token);
            if (!token.IsCancellationRequested)
            {
                AdvancedMaintenanceStatus.IsVisible = false;
                AdvancedMaintenanceStatus.Text = "";
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ClearAdvancedMaintenanceStatus()
    {
        CancelAndDispose(ref _advancedMaintenanceStatusCancellation);
        AdvancedMaintenanceStatus.Opacity = 0;
        AdvancedMaintenanceStatus.IsVisible = false;
        AdvancedMaintenanceStatus.Text = "";
    }

    private void ShowMainMenu()
    {
        StopFolderWatcher();
        _metadataScanner.Cancel();
        _folderLoader.Cancel();
        _thumbnailLoads.Clear();
        ImageItem.ClearDeferredThumbnailCacheWrites();
        ImageCache.ClearAndReleaseAll();
        SelectItem(null);
        ClearImageItems();
        _currentFolderPath = null;
        HeaderBorder.IsVisible = false;
        MainMenu.IsVisible = true;
        
        MenuErrorBanner.Opacity = 0;
        MenuErrorBanner.IsVisible = false;
        
        PopulateRecentFolders();
    }

    private async void ShowMenuError(string message)
    {
        MenuErrorText.Text = message;
        MenuErrorBanner.Opacity = 0;
        MenuErrorBanner.IsVisible = true;
        await Task.Yield();
        MenuErrorBanner.Opacity = 1;
    }

    private static void OpenFolderInFileManager(string folderPath)
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            System.Diagnostics.Process.Start("explorer.exe", Path.GetFullPath(folderPath));
        }
        else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "open",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });
        }
        else
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-open",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            });
        }
    }

    private void PopulateRecentFolders()
    {
        RecentFoldersList.Children.Clear();
        var recent = UserPreferences.LoadRecentFolders();

        if (recent.Count == 0)
        {
            NoRecentFoldersText.IsVisible = true;
            return;
        }

        NoRecentFoldersText.IsVisible = false;

        foreach (var folder in recent)
        {
            var folderPath = folder.Path;
            var trimmedPath = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folderName = Path.GetFileName(trimmedPath);
            if (string.IsNullOrEmpty(folderName))
            {
                folderName = folderPath;
            }

            var itemBorder = new Border
            {
                Classes = { "recent-item" },
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(14, 12),
                Cursor = new Cursor(StandardCursorType.Hand),
                Focusable = true
            };
            itemBorder.SetValue(AutomationProperties.NameProperty, $"Open recent folder {folderName}");
            ToolTip.SetTip(itemBorder, folderPath);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };

            var iconBorder = new Border
            {
                Width = 32,
                Height = 32,
                Background = (IBrush)Application.Current!.FindResource("SurfaceInput")!,
                BorderBrush = (IBrush)Application.Current!.FindResource("BorderSubtle")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock
                {
                    Text = "📁",
                    FontSize = 14,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };
            Grid.SetColumn(iconBorder, 0);
            grid.Children.Add(iconBorder);

            var clickPanel = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            var nameText = new TextBlock
            {
                Text = folderName,
                Foreground = (IBrush)Application.Current!.FindResource("TextPrimary")!,
                FontSize = 13.5,
                FontWeight = FontWeight.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var pathText = new TextBlock
            {
                Text = folderPath,
                Foreground = (IBrush)Application.Current!.FindResource("TextMuted")!,
                FontFamily = (FontFamily)Application.Current!.FindResource("FontMono")!,
                FontSize = 10.5,
                TextTrimming = TextTrimming.PrefixCharacterEllipsis
            };

            clickPanel.Children.Add(nameText);
            clickPanel.Children.Add(pathText);

            var metaParts = new System.Collections.Generic.List<string>();
            if (folder.ImageCount >= 0)
            {
                metaParts.Add($"{folder.ImageCount:n0} image" + (folder.ImageCount == 1 ? "" : "s"));
            }
            var relTime = GetRelativeTime(folder.LastOpened);
            if (!string.IsNullOrEmpty(relTime))
            {
                metaParts.Add(relTime);
            }

            if (metaParts.Count > 0)
            {
                var metaText = new TextBlock
                {
                    Text = string.Join(" • ", metaParts),
                    Foreground = (IBrush)Application.Current!.FindResource("TextAccent")!,
                    FontSize = 10.5,
                    FontWeight = FontWeight.Normal,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                clickPanel.Children.Add(metaText);
            }

            Grid.SetColumn(clickPanel, 1);
            grid.Children.Add(clickPanel);

            var closeButton = new Button
            {
                Classes = { "close-btn" },
                Content = "✕",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(2),
                FontSize = 10,
                Padding = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Arrow),
                Margin = new Thickness(8, 0, 0, 0)
            };
            closeButton.SetValue(AutomationProperties.NameProperty, $"Remove {folderName} from Recent");
            ToolTip.SetTip(closeButton, "Remove from Recent");
            closeButton.Click += (s, e) =>
            {
                RemoveRecentFolder(folderPath);
            };
            Grid.SetColumn(closeButton, 2);
            grid.Children.Add(closeButton);

            itemBorder.Child = grid;

            itemBorder.PointerPressed += async (s, e) =>
            {
                if (e.GetCurrentPoint(itemBorder).Properties.IsLeftButtonPressed)
                {
                    var source = e.Source as Visual;
                    bool clickedClose = false;
                    while (source != null)
                    {
                        if (source == closeButton)
                        {
                            clickedClose = true;
                            break;
                        }
                        source = source.GetVisualParent();
                    }

                    if (!clickedClose)
                    {
                        await LoadFolderAsync(folderPath);
                    }
                }
            };
            itemBorder.KeyDown += async (s, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    e.Handled = true;
                    await LoadFolderAsync(folderPath);
                }
            };

            RecentFoldersList.Children.Add(itemBorder);
        }
    }

    private void RemoveRecentFolder(string folderPath)
    {
        var recent = UserPreferences.LoadRecentFolders();
        recent.RemoveAll(x => string.Equals(x.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        UserPreferences.SaveRecentFolders(recent);
        PopulateRecentFolders();
    }

    private static string GetRelativeTime(DateTime utcTime)
    {
        if (utcTime == DateTime.MinValue) return string.Empty;

        var span = DateTime.UtcNow - utcTime;
        if (span.TotalSeconds < 0) return "just now";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return utcTime.ToLocalTime().ToString("MMM d, yyyy");
    }
    private void AdvancedToggle_Click(object? sender, RoutedEventArgs e)
    {
        AdvancedPanel.IsVisible = AdvancedToggle.IsChecked == true;
        if (!AdvancedPanel.IsVisible)
        {
            ClearAdvancedMaintenanceStatus();
        }
    }
}
