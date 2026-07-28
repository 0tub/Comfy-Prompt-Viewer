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
        var lastFolder = _preferences.LoadLastFolderPath();
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
        _thumbnailService.ClearDeferredWrites();

        _decodedImageCache.ClearAndReleaseAll();
        SelectItem(null);
        ClearImageItems();
        FolderText.Text = TruncatePath(folderPath);
        CopyPathButton.IsVisible = true;
        _currentFolderPath = folderPath;
        CountText.Text = "Scanning...";
        ClearFolderCacheStatus();

        // Retires and drains the previous folder's cache scope before opening this one, so no leftover work
        // is still holding that pack when its handles close, and none of it can reach this folder's pack.
        var cacheScope = await _thumbnailService.OpenFolderScopeAsync(folderPath);
        if (!loadSession.IsCurrent)
        {
            return;
        }

        _folderCacheScope = cacheScope;

        try
        {
            var sortMode = _sortMode;
            var imageFiles = await FolderScanner.ReadFolderAsync(
                folderPath,
                includeSubfolders,
                (left, right) => CompareImageFileEntries(left, right, sortMode),
                token);

            if (!loadSession.IsCurrent)
            {
                return;
            }

            while (sortMode != _sortMode)
            {
                sortMode = _sortMode;
                await Task.Run(
                    () => imageFiles.Sort(
                        (left, right) => CompareImageFileEntries(left, right, sortMode)),
                    token);
                if (!loadSession.IsCurrent)
                {
                    return;
                }
            }

            _preferences.SaveLastFolderPath(folderPath);
            _preferences.AddRecentFolder(folderPath, imageFiles.Count);
            DebugLog.Observe(
                Task.Run(() => _metadataRepository.PruneMissing(imageFiles.Select(file => file.Path), includeSubfolders)),
                "MetadataRepository.PruneMissing");
            CountText.Text = $"{imageFiles.Count:n0} images";

            if (imageFiles.Count == 0)
            {
                var hasNestedImages = !includeSubfolders &&
                                      await FolderScanner.HasImagesAsync(folderPath, includeSubfolders: true);
                if (!loadSession.IsCurrent)
                {
                    return;
                }

                ShowMainMenu();
                ShowMenuError(hasNestedImages
                    ? "No top-level PNG, JPG, or WebP images found. Enable Include subfolders to scan nested folders."
                    : "No PNG, JPG, or WebP images found in that folder.");
                return;
            }

            MainMenu.IsVisible = false;
            HeaderBorder.IsVisible = true;

            foreach (var imageFile in imageFiles)
            {
                _catalog.Add(new GalleryEntry(
                    imageFile.Path,
                    imageFile.Fingerprint,
                    CreateImageItem(imageFile.Path, imageFile.Fingerprint)));
            }

            ApplyFilter(resetScroll: true);
            QueueViewportThumbnailSchedule();

            if (!string.IsNullOrEmpty(selectedPath))
            {
                if (_catalog.TryGet(selectedPath, out var match))
                {
                    SelectItem(match.Item);
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
            if (!loadSession.IsCurrent)
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
            !await FolderScanner.HasImagesAsync(currentFolderPath, includeSubfolders))
        {
            SyncIncludeSubfoldersToggles();
            CountText.Text = "No top-level images; kept subfolders on";
            return;
        }

        _includeSubfolders = includeSubfolders;
        SyncIncludeSubfoldersToggles();
        _preferences.SaveIncludeSubfolders(includeSubfolders);

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

    private ImageItem CreateImageItem(string path, SourceFingerprint fingerprint)
    {
        var item = new ImageItem(
            path,
            fingerprint,
            _tileSize,
            _metadataService,
            _decodedImageCache,
            _thumbnailService,
            _folderCacheScope);
        item.MetadataLoaded += ImageItem_MetadataLoaded;
        return item;
    }
}
