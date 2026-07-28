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
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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

    private static string TruncatePath(string path, int maxSegments = 3)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "Open an image folder to start scrolling.";
        }

        var separator = Path.DirectorySeparatorChar;
        var parts = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= maxSegments)
        {
            return path;
        }

        return "..." + separator + string.Join(separator, parts.Skip(parts.Length - maxSegments));
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

    // Only the open folder's pack. Its thumbnails go; every other folder's cache is untouched, which is the
    // whole point of the per-folder layout.
    private async void ClearFolderCacheButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_thumbnailCacheClearInProgress)
        {
            return;
        }

        if (_folderCacheScope is not { IsRetired: false } scope)
        {
            ShowFolderCacheStatus("No folder cache to clear.");
            return;
        }

        SetCacheMaintenanceEnabled(false);
        _thumbnailLoads.Clear();
        try
        {
            _decodedImageCache.ClearAndReleaseAll();
            await _thumbnailService.ClearFolderCacheAsync(scope);
            ShowFolderCacheStatus("Folder thumbnail cache cleared.");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to clear folder thumbnail cache: {ex}");
            ShowFolderCacheStatus($"Could not clear folder cache: {ex.Message}");
        }
        finally
        {
            SetCacheMaintenanceEnabled(true);
            // The decoded bitmaps just went with the pack entries, so the visible tiles have to be reloaded.
            QueueViewportThumbnailSchedule(force: true);
        }
    }

    private async void ClearAllCachesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_thumbnailCacheClearInProgress)
        {
            return;
        }

        SetCacheMaintenanceEnabled(false);
        _thumbnailLoads.Clear();
        try
        {
            _decodedImageCache.ClearAndReleaseAll();
            // Two file truncations for the open folder, plus a directory delete per other folder cache and
            // the pre-upgrade global pack. No per-item state to invalidate: every item resolves "is this
            // cached" through its folder's pack index.
            await _thumbnailService.ClearAllCachesAsync();
            ShowAdvancedMaintenanceStatus("All thumbnail caches cleared.");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to clear thumbnail caches: {ex}");
            ClearAdvancedMaintenanceStatus();
            ShowMenuError($"Could not clear cache: {ex.Message}");
        }
        finally
        {
            SetCacheMaintenanceEnabled(true);
            QueueViewportThumbnailSchedule(force: true);
        }
    }

    private void SetCacheMaintenanceEnabled(bool isEnabled)
    {
        _thumbnailCacheClearInProgress = !isEnabled;
        ClearFolderCacheButton.IsEnabled = isEnabled;
        ClearAllCachesButton.IsEnabled = isEnabled;
    }

    private void ClearMetadataCacheButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _metadataRepository.Clear();
            ShowAdvancedMaintenanceStatus("Metadata cache cleared.");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to clear metadata cache: {ex}");
            ClearAdvancedMaintenanceStatus();
            ShowMenuError($"Could not clear metadata cache: {ex.Message}");
        }
    }

    private async void OpenAppDataButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_thumbnailService.CacheRootDirectory);
            if (await OpenFolderInFileManagerAsync(AppPaths.LocalDataDirectory))
            {
                ShowAdvancedMaintenanceStatus("App data folder opened.");
            }
            else
            {
                ClearAdvancedMaintenanceStatus();
                ShowMenuError("Could not open the app data folder.");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to open app data folder: {ex}");
            ClearAdvancedMaintenanceStatus();
            ShowMenuError($"Could not open app data: {ex.Message}");
        }
    }

    private void ShowAdvancedMaintenanceStatus(string message) =>
        ShowTransientStatus(AdvancedMaintenanceStatus, _advancedMaintenanceStatusGate, message);

    private void ClearAdvancedMaintenanceStatus() =>
        ClearTransientStatus(AdvancedMaintenanceStatus, _advancedMaintenanceStatusGate);

    private void ShowFolderCacheStatus(string message) =>
        ShowTransientStatus(FolderCacheStatus, _folderCacheStatusGate, message);

    private void ClearFolderCacheStatus() =>
        ClearTransientStatus(FolderCacheStatus, _folderCacheStatusGate);

    // Fades a label in, holds it, fades it out. The gate is per label, so the maintenance panel and the
    // folder-cache control can each be showing their own message without cancelling the other's.
    private static async void ShowTransientStatus(TextBlock status, SessionGate gate, string message)
    {
        var session = gate.Restart();

        status.Text = message;
        status.Opacity = 0;
        status.IsVisible = true;
        await Task.Yield();
        if (session.IsStale)
        {
            return;
        }

        status.Opacity = 1;

        try
        {
            await Task.Delay(TransientStatusDuration, session.Token);
            status.Opacity = 0;
            await Task.Delay(TransientStatusFadeDuration, session.Token);
            if (session.IsCurrent)
            {
                status.IsVisible = false;
                status.Text = "";
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void ClearTransientStatus(TextBlock status, SessionGate gate)
    {
        gate.Cancel();
        status.Opacity = 0;
        status.IsVisible = false;
        status.Text = "";
    }

    private void ShowMainMenu()
    {
        StopFolderWatcher();
        _metadataScanner.Cancel();
        _folderLoader.Cancel();
        _thumbnailLoads.Clear();
        _thumbnailService.ClearDeferredWrites();
        _decodedImageCache.ClearAndReleaseAll();
        SelectItem(null);
        ClearImageItems();
        _currentFolderPath = null;
        // Detached synchronously; only the drain and the pack close finish in the background, so a folder
        // reopened immediately cannot adopt this scope.
        _folderCacheScope = null;
        DebugLog.Observe(_thumbnailService.RetireFolderScopeAsync(), "Thumbnail folder scope retirement");
        ClearFolderCacheStatus();
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

    // ILauncher is the platform's own "hand this to the default handler" path, so there is no
    // explorer.exe / open / xdg-open branching to keep in sync, and no UseShellExecute behavior to
    // reason about under single-file publish.
    private async Task<bool> OpenFolderInFileManagerAsync(string folderPath)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
        {
            return false;
        }

        return await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Path.GetFullPath(folderPath)));
    }

    // Recent-folder rows are built in code, so they need the same DynamicResource lookup the XAML gets.
    // `Application.Current.FindResource(key)` is not theme-variant aware and now throws for every themed key,
    // because those keys live in ThemeManager's theme dictionaries rather than directly in
    // Application.Resources. Binding also keeps these rows correct when the theme combo (which sits on this
    // very screen) changes the palette while the list is on display.
    private static void BindThemeResource(Control control, AvaloniaProperty property, string resourceKey)
    {
        control[!property] = new DynamicResourceExtension(resourceKey);
    }

    private void PopulateRecentFolders()
    {
        RecentFoldersList.Children.Clear();
        var recent = _preferences.LoadRecentFolders();

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
                Cursor = HandCursor,
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
            BindThemeResource(iconBorder, Border.BackgroundProperty, "SurfaceInput");
            BindThemeResource(iconBorder, Border.BorderBrushProperty, "BorderSubtle");
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
                FontSize = 13.5,
                FontWeight = FontWeight.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            BindThemeResource(nameText, TextBlock.ForegroundProperty, "TextPrimary");

            var pathText = new TextBlock
            {
                Text = folderPath,
                FontSize = 10.5,
                TextTrimming = TextTrimming.PrefixCharacterEllipsis
            };
            BindThemeResource(pathText, TextBlock.ForegroundProperty, "TextMuted");
            BindThemeResource(pathText, TextBlock.FontFamilyProperty, "FontMono");

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
                    FontSize = 10.5,
                    FontWeight = FontWeight.Normal,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                BindThemeResource(metaText, TextBlock.ForegroundProperty, "TextAccent");
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
                Cursor = ArrowCursor,
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
        var recent = _preferences.LoadRecentFolders();
        recent.RemoveAll(x => string.Equals(x.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        _preferences.SaveRecentFolders(recent);
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
    private void PrewarmThumbnailsToggle_Click(object? sender, RoutedEventArgs e)
    {
        _prewarmThumbnails = PrewarmThumbnailsToggle.IsChecked == true;
        _preferences.SavePrewarmThumbnails(_prewarmThumbnails);

        if (!_prewarmThumbnails)
        {
            _thumbnailService.ClearDeferredWrites();
            _prewarmRemaining = 0;
            _prewarmTotal = 0;
            UpdateCountText();
            ShowAdvancedMaintenanceStatus("Background thumbnail caching off.");
            return;
        }

        ShowAdvancedMaintenanceStatus("Background thumbnail caching on.");
        if (_catalog.Count > 0 &&
            _folderLoader.CurrentToken is { IsCancellationRequested: false } token)
        {
            QueueThumbnailCachePrewarm(_folderLoader.Generation, token);
        }
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
