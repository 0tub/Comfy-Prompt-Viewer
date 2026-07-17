using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        var lastFolder = UserPreferences.LoadLastFolderPath();
        if (!string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder))
        {
            await LoadFolderAsync(lastFolder);
        }
        else
        {
            ShowMainMenu();
        }
    }

    private async void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open image folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is not { Length: > 0 } folderPath)
        {
            return;
        }

        await LoadFolderAsync(folderPath);
    }

    private async Task LoadFolderAsync(string folderPath)
    {
        StopFolderWatcher();
        var selectedPath = _selectedItem?.Path;
        var includeSubfolders = _includeSubfolders;
        var loadSession = _folderLoader.Restart();
        var token = loadSession.Token;

        _metadataScanner.Cancel();
        _thumbnailLoads.Clear();
        ImageItem.ClearDeferredThumbnailCacheWrites();

        _scrollMonitorTimer?.Stop();
        _isFastScrollingStatic = false;
        _lastFastScrollScheduleTime = 0;
        _lastScrollOffsetY = 0;
        _lastScrollTimestamp = 0;
        ImageCache.ClearAndReleaseAll();
        SelectItem(null);
        ClearImageItems();
        FolderText.Text = TruncatePath(folderPath);
        CopyPathButton.IsVisible = true;
        _currentFolderPath = folderPath;
        CountText.Text = "Scanning...";
        MainMenu.IsVisible = false;
        HeaderBorder.IsVisible = true;

        try
        {
            var imageFiles = await FolderLoadCoordinator.ReadFolderAsync(folderPath, includeSubfolders, token);

            if (!_folderLoader.IsCurrent(loadSession))
            {
                return;
            }

            foreach (var imageFile in imageFiles)
            {
                _allImagePaths.Add(imageFile.Path);
                _imageLastWriteTimes[imageFile.Path] = imageFile.LastWriteTimeUtc;
            }
            UserPreferences.SaveLastFolderPath(folderPath);
            UserPreferences.AddRecentFolder(folderPath, imageFiles.Count);
            DebugLog.Observe(
                Task.Run(() => MetadataIndex.PruneMissing(imageFiles.Select(file => file.Path), includeSubfolders)),
                "MetadataIndex.PruneMissing");
            ApplySort();
            CountText.Text = $"{_allImagePaths.Count:n0} images";

            if (_allImagePaths.Count == 0)
            {
                ShowMainMenu();
                var hasNestedImages = !includeSubfolders &&
                                      await FolderLoadCoordinator.HasImagesAsync(folderPath, includeSubfolders: true);
                ShowMenuError(hasNestedImages
                    ? "No top-level PNG, JPG, or WebP images found. Enable Include subfolders to scan nested folders."
                    : "No PNG, JPG, or WebP images found in that folder.");
                return;
            }

            foreach (var path in _allImagePaths)
            {
                _allImageItems.Add(CreateImageItem(path));
            }

            ApplyFilter(resetScroll: true);
            QueueViewportThumbnailSchedule();

            if (!string.IsNullOrEmpty(selectedPath))
            {
                var match = _allImageItems.FirstOrDefault(item => item.Path == selectedPath);
                if (match != null)
                {
                    SelectItem(match);
                }
            }

            StartFolderWatcher(folderPath, includeSubfolders);
            QueueInitialMetadataScanner(loadSession.Generation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"Failed to load folder '{folderPath}': {ex}");
            if (!_folderLoader.IsCurrent(loadSession))
            {
                return;
            }

            ShowMainMenu();
            ShowMenuError(ex.Message);
        }
    }


    private async void IncludeSubfoldersToggle_Click(object? sender, RoutedEventArgs e)
    {
        var includeSubfolders = sender is ToggleButton toggle
            ? toggle.IsChecked == true
            : _includeSubfolders;

        await SetIncludeSubfoldersAsync(includeSubfolders);
    }

    private async Task SetIncludeSubfoldersAsync(bool includeSubfolders)
    {
        if (includeSubfolders == _includeSubfolders)
        {
            SyncIncludeSubfoldersToggles();
            return;
        }

        var currentFolderPath = _currentFolderPath;
        if (!includeSubfolders &&
            !string.IsNullOrEmpty(currentFolderPath) &&
            !await FolderLoadCoordinator.HasImagesAsync(currentFolderPath, includeSubfolders))
        {
            SyncIncludeSubfoldersToggles();
            CountText.Text = "No top-level images; kept subfolders on";
            return;
        }

        _includeSubfolders = includeSubfolders;
        SyncIncludeSubfoldersToggles();
        UserPreferences.SaveIncludeSubfolders(includeSubfolders);

        if (!string.IsNullOrEmpty(currentFolderPath))
        {
            await LoadFolderAsync(currentFolderPath);
        }
    }

    private void SyncIncludeSubfoldersToggles()
    {
        IncludeSubfoldersToggle.IsChecked = _includeSubfolders;
        MenuIncludeSubfoldersToggle.IsChecked = _includeSubfolders;
    }

    private ImageItem CreateImageItem(string path)
    {
        var item = new ImageItem(path, _tileSize);
        item.MetadataLoaded += ImageItem_MetadataLoaded;
        return item;
    }
}
