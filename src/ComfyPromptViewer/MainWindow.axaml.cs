using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public partial class MainWindow : Window
{
    private const double DefaultTileSize = 120;
    private const double MinTileSize = 80;
    private const double MaxTileSize = 320;
    private const double TileSizeStep = 10;
    private const double TileGap = 16;
    private const double WheelScrollRowsPerNotch = 1.7;
    private const double MinWheelScrollPixels = 180;
    private const double MaxWheelViewportRatio = 0.58;
    private static readonly TimeSpan InitialMetadataScannerPollInterval = TimeSpan.FromMilliseconds(100);
    private const int InitialMetadataScannerMaxPolls = 15;
    private readonly GalleryViewModel _viewModel = new();
    private readonly ThumbnailLoadCoordinator _thumbnailLoads = new();
    private readonly List<string> _allImagePaths = [];
    private readonly List<ImageItem> _allImageItems = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _scannerCancellation;
    private CancellationTokenSource? _searchDebounceCancellation;
    private ImageItem? _selectedItem;
    private SortMode _sortMode = SortMode.NewestFirst;
    private string? _currentFolderPath;
    private int _loadedMetadataCount;
    private int _loadGeneration;
    private int _scannerGeneration;
    private bool _includeSubfolders = UserPreferences.LoadIncludeSubfolders();
    private double _targetTileSize = UserPreferences.LoadTileSize(DefaultTileSize, MinTileSize, MaxTileSize);
    private double _tileSize;
    private double _tileItemExtent;
    private bool _isInitializing = true;
    private bool _isViewportThumbnailScheduleQueued;
    private int _lastPrefetchFirstVisibleRow = -1;
    private int _prefetchDirection = 1;
    private volatile bool _hasSearchQueryActive;
    private TextBox? _activeContextMenuTextBox;

    private enum SearchScope
    {
        All,
        Prompts,
        Filename
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        GalleryScrollViewer.AddHandler(InputElement.PointerPressedEvent, GalleryScrollViewer_PointerPressed, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerMovedEvent, GalleryScrollViewer_PointerMoved, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerReleasedEvent, GalleryScrollViewer_PointerReleased, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerCaptureLostEvent, GalleryScrollViewer_PointerCaptureLost, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, GalleryScrollViewer_PointerWheelChanged, RoutingStrategies.Tunnel, true);
        
        SidebarPrompt.AddHandler(TextBox.CopyingToClipboardEvent, TextBox_CopyingToClipboard, RoutingStrategies.Bubble, true);
        SidebarNegativePrompt.AddHandler(TextBox.CopyingToClipboardEvent, TextBox_CopyingToClipboard, RoutingStrategies.Bubble, true);

        _tileSize = _targetTileSize;
        _tileItemExtent = _tileSize + TileGap;
        TileSizeSlider.Minimum = MinTileSize;
        TileSizeSlider.Maximum = MaxTileSize;
        TileSizeSlider.TickFrequency = TileSizeStep;
        TileSizeSlider.Value = _targetTileSize;
        SyncIncludeSubfoldersToggles();
        ApplyTileLayout();
        SortComboBox.SelectedIndex = (int)_sortMode;
        SearchScopeComboBox.SelectedIndex = (int)SearchScope.All;

        _isInitializing = false;
        this.SizeChanged += Window_SizeChanged;
        this.Opened += MainWindow_Opened;
    }

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

    protected override void OnClosed(EventArgs e)
    {
        StopAutoScroll();
        StopLargePreviewPan(releaseCapture: true);
        StopFolderWatcher();
        CancelAndDispose(ref _searchDebounceCancellation);
        CancelAndDispose(ref _scannerCancellation);
        _scannerGeneration++;
        CancelAndDispose(ref _loadCancellation);
        _loadGeneration++;
        _thumbnailLoads.Clear();
        SelectItem(null);
        ClearImageItems();
        ImageCache.ClearAndReleaseAll();
        base.OnClosed(e);
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancellation.Dispose();
        cancellation = null;
    }

    private void ClearImageItems()
    {
        GalleryEmptyState.IsVisible = false;
        GalleryEmptyState.Opacity = 0;

        foreach (var item in _allImageItems)
        {
            item.MetadataLoaded -= ImageItem_MetadataLoaded;
        }

        _viewModel.Items.Clear();
        _allImagePaths.Clear();
        _allImageItems.Clear();
        _lastPrefetchFirstVisibleRow = -1;
        _prefetchDirection = 1;
    }

    private void AdvancedToggle_Click(object? sender, RoutedEventArgs e)
    {
        AdvancedPanel.IsVisible = AdvancedToggle.IsChecked == true;
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
        var loadGeneration = ++_loadGeneration;

        CancelAndDispose(ref _loadCancellation);
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;

        CancelAndDispose(ref _scannerCancellation);
        _scannerGeneration++;
        _thumbnailLoads.Clear();

        ImageCache.ClearAndReleaseAll();
        SelectItem(null);
        ClearImageItems();
        _loadedMetadataCount = 0;
        FolderText.Text = TruncatePath(folderPath);
        CopyPathButton.IsVisible = true;
        _currentFolderPath = folderPath;
        CountText.Text = "Scanning...";
        MainMenu.IsVisible = false;
        HeaderBorder.IsVisible = true;

        try
        {
            var imagePaths = await Task.Run(() => EnumerateImagePaths(folderPath, includeSubfolders, token)
                .Where(ImageFileReader.IsSupportedImage)
                .ToList(), token);

            if (token.IsCancellationRequested || loadGeneration != _loadGeneration)
            {
                return;
            }

            _allImagePaths.AddRange(imagePaths);
            UserPreferences.SaveLastFolderPath(folderPath);
            UserPreferences.AddRecentFolder(folderPath, imagePaths.Count);
            ApplySort();
            CountText.Text = $"{_allImagePaths.Count:n0} images";

            if (_allImagePaths.Count == 0)
            {
                ShowMainMenu();
                var hasNestedImages = !includeSubfolders && await FolderHasImagesAsync(folderPath, includeSubfolders: true);
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
            QueueInitialMetadataScanner(loadGeneration);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write($"Failed to load folder '{folderPath}': {ex}");
            if (token.IsCancellationRequested || loadGeneration != _loadGeneration)
            {
                return;
            }

            ShowMainMenu();
            ShowMenuError(ex.Message);
        }
    }

    private void SortComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || SortComboBox == null) return;
        var selectedIndex = SortComboBox.SelectedIndex;
        if (selectedIndex < 0) return;

        var newSortMode = selectedIndex switch
        {
            0 => SortMode.NewestFirst,
            1 => SortMode.OldestFirst,
            _ => SortMode.Name
        };

        if (_sortMode != newSortMode)
        {
            _sortMode = newSortMode;
            ApplySort();
            ApplyFilter(resetScroll: true);
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
            !await FolderHasImagesAsync(currentFolderPath, includeSubfolders))
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

    private static Task<bool> FolderHasImagesAsync(string folderPath, bool includeSubfolders)
    {
        return Task.Run(() =>
        {
            try
            {
                return EnumerateImagePaths(folderPath, includeSubfolders, CancellationToken.None)
                    .Any(ImageFileReader.IsSupportedImage);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugLog.Write($"Failed to scan folder '{folderPath}' for images: {ex.Message}");
                return false;
            }
        });
    }

    private static IEnumerable<string> EnumerateImagePaths(string folderPath, bool includeSubfolders, CancellationToken token)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            IgnoreInaccessible = includeSubfolders
        };

        foreach (var path in Directory.EnumerateFiles(folderPath, "*", options))
        {
            token.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    private ImageItem CreateImageItem(string path)
    {
        var item = new ImageItem(path, _tileSize);
        item.MetadataLoaded += ImageItem_MetadataLoaded;
        return item;
    }

    private void TileSizeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SetTileSize(RoundToTileStep(e.NewValue), persist: true);
    }



    private void SetTileSize(double tileSize, bool persist)
    {
        var targetTileSize = Math.Clamp(RoundToTileStep(tileSize), MinTileSize, MaxTileSize);
        var tileSizeChanged = Math.Abs(_targetTileSize - targetTileSize) > 0.1;
        _targetTileSize = targetTileSize;

        if (Math.Abs(TileSizeSlider.Value - _targetTileSize) > 0.1)
        {
            _isInitializing = true;
            TileSizeSlider.Value = _targetTileSize;
            _isInitializing = false;
        }

        if (tileSizeChanged)
        {
            ApplyTileLayout();
        }

        if (persist && tileSizeChanged)
        {
            UserPreferences.SaveTileSize(_targetTileSize);
        }
    }

    private void ApplyTileLayout()
    {
        _tileSize = _targetTileSize;
        _tileItemExtent = _tileSize + TileGap;
        UpdateTileSizeText();

        if (GalleryItems.Layout is Avalonia.Layout.UniformGridLayout uniformLayout)
        {
            uniformLayout.MinItemWidth = _tileItemExtent;
            uniformLayout.MinItemHeight = _tileItemExtent;
        }

        foreach (var item in _allImageItems)
        {
            item.SetTileSize(_tileSize);
        }

        QueueViewportThumbnailSchedule();
    }

    private void UpdateTileSizeText()
    {
        TileSizeText.Text = $"{_tileSize:0}";
    }

    private static double RoundToTileStep(double value)
    {
        return Math.Round(value / TileSizeStep) * TileSizeStep;
    }

    private void ApplySort()
    {
        if (_sortMode == SortMode.Name)
        {
            _allImagePaths.Sort(CompareImagePathNames);
        }
        else
        {
            var lastWriteTimes = new Dictionary<string, DateTime>(_allImagePaths.Count, StringComparer.OrdinalIgnoreCase);
            var originalOrder = new Dictionary<string, int>(_allImagePaths.Count, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < _allImagePaths.Count; index++)
            {
                var path = _allImagePaths[index];
                lastWriteTimes[path] = File.GetLastWriteTimeUtc(path);
                originalOrder[path] = index;
            }

            var descending = _sortMode == SortMode.NewestFirst;
            _allImagePaths.Sort((left, right) => CompareImagePathDates(left, right, lastWriteTimes, originalOrder, descending));
        }

        if (_allImageItems.Count > 1)
        {
            var sortOrder = new Dictionary<string, int>(_allImagePaths.Count, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < _allImagePaths.Count; index++)
            {
                sortOrder[_allImagePaths[index]] = index;
            }

            _allImageItems.Sort((left, right) => sortOrder[left.Path].CompareTo(sortOrder[right.Path]));
        }
    }

    private static int CompareImagePathNames(string left, string right)
    {
        var fileNameCompare = StringComparer.CurrentCultureIgnoreCase.Compare(Path.GetFileName(left), Path.GetFileName(right));
        return fileNameCompare != 0
            ? fileNameCompare
            : StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
    }

    private static int CompareImagePathDates(
        string left,
        string right,
        Dictionary<string, DateTime> lastWriteTimes,
        Dictionary<string, int> originalOrder,
        bool descending)
    {
        var compare = lastWriteTimes[left].CompareTo(lastWriteTimes[right]);
        if (descending)
        {
            compare = -compare;
        }

        return compare != 0
            ? compare
            : originalOrder[left].CompareTo(originalOrder[right]);
    }

    private void GalleryItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control control && control.DataContext is ImageItem item)
        {
            control.Focus();
            SelectItem(item);
            if (e.ClickCount >= 2)
            {
                ShowLargePreview();
            }
        }
    }

    private void GalleryItem_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: ImageItem item })
        {
            return;
        }

        if (LargePreviewOverlay.IsVisible)
        {
            return;
        }

        if (e.Key is Key.Space or Key.Enter)
        {
            SelectItem(item);
            ShowLargePreview();
            e.Handled = true;
        }
    }

    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var windowWidth = e.NewSize.Width;
        var windowHeight = e.NewSize.Height;

        double targetSidebarWidth = Math.Clamp(windowWidth * 0.3, 260, 380);
        
        if (MainGrid != null && MainGrid.ColumnDefinitions.Count > 1)
        {
            MainGrid.ColumnDefinitions[1].Width = new GridLength(targetSidebarWidth, GridUnitType.Pixel);
        }

        double targetImageHeight = Math.Clamp(windowHeight * 0.4, 180, 350);

        if (SidebarContent != null && SidebarContent.RowDefinitions.Count > 0)
        {
            SidebarContent.RowDefinitions[0].Height = new GridLength(targetImageHeight, GridUnitType.Pixel);
        }

        if (LargePreviewOverlay.IsVisible && _largePreviewZoom is null)
        {
            ApplyLargePreviewZoom(resetScroll: false);
        }

        QueueViewportThumbnailSchedule();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (LargePreviewOverlay.IsVisible)
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                HideLargePreview();
                e.Handled = true;
                return;
            }

            var columns = GetGalleryColumnCount();
            var moved = e.Key switch
            {
                Key.Left => MoveSelection(-1),
                Key.Right => MoveSelection(1),
                Key.Up => MoveSelection(-columns),
                Key.Down => MoveSelection(columns),
                Key.Home => SelectByIndex(0),
                Key.End => SelectByIndex(_viewModel.Items.Count - 1),
                _ => false
            };

            if (moved)
            {
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);

        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.F5 || (e.Key == Key.R && (e.KeyModifiers & KeyModifiers.Control) != 0))
        {
            if (!string.IsNullOrEmpty(_currentFolderPath))
            {
                _ = LoadFolderAsync(_currentFolderPath);
                e.Handled = true;
            }
        }

        if (IsTextInputFocused(e.Source))
        {
            return;
        }

        var galleryColumns = GetGalleryColumnCount();
        var handled = e.Key switch
        {
            Key.Left => MoveSelection(-1),
            Key.Right => MoveSelection(1),
            Key.Up => MoveSelection(-galleryColumns),
            Key.Down => MoveSelection(galleryColumns),
            Key.Home => SelectByIndex(0),
            Key.End => SelectByIndex(_viewModel.Items.Count - 1),
            Key.Enter or Key.Space => ShowLargePreviewIfSelected(),
            _ => false
        };

        if (handled)
        {
            e.Handled = true;
        }
    }

    private static bool IsTextInputFocused(object? source)
    {
        return source is TextBox;
    }

    private static bool IsGalleryNavigationKey(Key key)
    {
        return key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    }

    private bool ShowLargePreviewIfSelected()
    {
        if (_selectedItem is null)
        {
            return false;
        }

        ShowLargePreview();
        return true;
    }

    private bool MoveSelection(int delta)
    {
        if (_viewModel.Items.Count == 0)
        {
            return false;
        }

        var currentIndex = _selectedItem is null ? -1 : _viewModel.Items.IndexOf(_selectedItem);
        var nextIndex = currentIndex < 0
            ? (delta < 0 ? _viewModel.Items.Count - 1 : 0)
            : Math.Clamp(currentIndex + delta, 0, _viewModel.Items.Count - 1);

        return SelectByIndex(nextIndex);
    }

    private bool SelectByIndex(int index)
    {
        if (index < 0 || index >= _viewModel.Items.Count)
        {
            return false;
        }

        SelectItem(_viewModel.Items[index]);
        EnsureIndexVisible(index);

        if (GalleryItems.TryGetElement(index) is Control control)
        {
            control.Focus();
        }

        return true;
    }

    private int GetGalleryColumnCount()
    {
        var viewportWidth = GalleryScrollViewer.Viewport.Width > 0 ? GalleryScrollViewer.Viewport.Width : Bounds.Width;
        return Math.Max(1, (int)Math.Floor(viewportWidth / Math.Max(1, _tileItemExtent)));
    }

    private void EnsureIndexVisible(int index)
    {
        var columns = GetGalleryColumnCount();
        var row = index / columns;
        var rowTop = row * _tileItemExtent;
        var rowBottom = rowTop + _tileItemExtent;
        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0
            ? GalleryScrollViewer.Viewport.Height
            : Math.Max(1, GalleryScrollViewer.Bounds.Height);
        var currentTop = GalleryScrollViewer.Offset.Y;
        var currentBottom = currentTop + viewportHeight;

        if (rowTop < currentTop)
        {
            GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, rowTop);
        }
        else if (rowBottom > currentBottom)
        {
            GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, rowBottom - viewportHeight);
        }
    }

    private void GalleryItem_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        EnsureItemLoadedFromControl(sender);
    }

    private void GalleryItem_DataContextChanged(object? sender, EventArgs e)
    {
        EnsureItemLoadedFromControl(sender);
    }

    private void EnsureItemLoadedFromControl(object? sender)
    {
        if (sender is Control control)
        {
            var newItem = control.DataContext as ImageItem;
            var oldItem = control.Tag as ImageItem;

            if (oldItem == newItem)
            {
                if (newItem is not null)
                {
                    QueueViewportThumbnailSchedule();
                }
                return;
            }

            if (oldItem is not null)
            {
                oldItem.MarkUnrealized();
            }

            if (newItem is not null && _loadCancellation?.Token is { IsCancellationRequested: false })
            {
                control.Tag = newItem;
                newItem.MarkRealized();
                QueueViewportThumbnailSchedule();
            }
            else
            {
                control.Tag = null;
            }
        }
    }

    private void GalleryItem_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
        {
            if (control.Tag is ImageItem item)
            {
                item.MarkUnrealized();
            }
            control.Tag = null;
        }
    }

    private void GalleryScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        DebugLog.SetScrollState(
            GalleryScrollViewer.Offset.Y,
            GalleryScrollViewer.Viewport.Height,
            GalleryScrollViewer.Extent.Height,
            _viewModel.Items.Count,
            _tileItemExtent);
        QueueViewportThumbnailSchedule();
    }

    private void GalleryScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 || e.Delta.Y == 0)
        {
            return;
        }

        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0
            ? GalleryScrollViewer.Viewport.Height
            : Math.Max(1, GalleryScrollViewer.Bounds.Height);
        var maxOffset = Math.Max(0, GalleryScrollViewer.Extent.Height - viewportHeight);
        if (maxOffset <= 0)
        {
            return;
        }

        var rowBasedDistance = Math.Max(MinWheelScrollPixels, _tileItemExtent * WheelScrollRowsPerNotch);
        var viewportCap = Math.Max(MinWheelScrollPixels, viewportHeight * MaxWheelViewportRatio);
        var scrollDistance = Math.Min(rowBasedDistance, viewportCap);
        var nextOffset = Math.Clamp(GalleryScrollViewer.Offset.Y - (e.Delta.Y * scrollDistance), 0, maxOffset);

        GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, nextOffset);
        e.Handled = true;
    }

    private void QueueViewportThumbnailSchedule(bool force = false)
    {
        if (_isViewportThumbnailScheduleQueued && !force)
        {
            return;
        }

        _isViewportThumbnailScheduleQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _isViewportThumbnailScheduleQueued = false;
                ScheduleViewportThumbnails();
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("ScheduleViewportThumbnails", ex);
                throw;
            }
        }, DispatcherPriority.Background);
    }

    private void ScheduleViewportThumbnails()
    {
        if (_loadCancellation?.Token is not { } token || token.IsCancellationRequested || _viewModel.Items.Count == 0)
        {
            return;
        }

        var itemExtent = Math.Max(1, _tileItemExtent);
        var viewportWidth = GalleryScrollViewer.Viewport.Width > 0 ? GalleryScrollViewer.Viewport.Width : Bounds.Width;
        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0 ? GalleryScrollViewer.Viewport.Height : Bounds.Height;
        var columnCount = Math.Max(1, (int)Math.Floor(Math.Max(itemExtent, viewportWidth) / itemExtent));
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(GalleryScrollViewer.Offset.Y / itemExtent));
        var visibleRowCount = Math.Max(1, (int)Math.Ceiling(viewportHeight / itemExtent) + 1);
        var visibleItems = new List<ImageItem>(visibleRowCount * columnCount);
        var aheadItems = new List<ImageItem>(Math.Max(0, 6 * columnCount));
        if (_lastPrefetchFirstVisibleRow >= 0)
        {
            var rowDelta = firstVisibleRow.CompareTo(_lastPrefetchFirstVisibleRow);
            if (rowDelta != 0)
            {
                _prefetchDirection = rowDelta;
            }
        }
        _lastPrefetchFirstVisibleRow = firstVisibleRow;

        var firstVisibleIndex = firstVisibleRow * columnCount;
        var lastVisibleIndex = Math.Min(_viewModel.Items.Count - 1, ((firstVisibleRow + visibleRowCount) * columnCount) - 1);

        AddRange(visibleItems, firstVisibleIndex, lastVisibleIndex);

        var aheadStartRow = Math.Max(0, firstVisibleRow - 2);
        var aheadEndRow = firstVisibleRow + visibleRowCount + 4;
        var aheadEndIndex = Math.Min(_viewModel.Items.Count - 1, (aheadEndRow * columnCount) - 1);

        if (_prefetchDirection < 0)
        {
            AddRowsDescending(aheadItems, firstVisibleRow - 1, aheadStartRow);
            AddRange(aheadItems, lastVisibleIndex + 1, aheadEndIndex);
        }
        else
        {
            AddRange(aheadItems, lastVisibleIndex + 1, aheadEndIndex);
            AddRowsDescending(aheadItems, firstVisibleRow - 1, aheadStartRow);
        }

        _thumbnailLoads.ScheduleViewport(visibleItems, aheadItems, token);

        void AddRange(List<ImageItem> target, int startIndex, int endIndex)
        {
            if (endIndex < startIndex)
            {
                return;
            }

            for (var index = Math.Max(0, startIndex); index <= endIndex; index++)
            {
                target.Add(_viewModel.Items[index]);
            }
        }

        void AddRowsDescending(List<ImageItem> target, int startRow, int endRow)
        {
            if (startRow < endRow)
            {
                return;
            }

            for (var row = startRow; row >= endRow; row--)
            {
                AddRange(target, row * columnCount, Math.Min(_viewModel.Items.Count - 1, ((row + 1) * columnCount) - 1));
            }
        }
    }

    private void SelectItem(ImageItem? item)
    {
        if (_selectedItem == item)
        {
            return;
        }

        if (_selectedItem is not null)
        {
            _selectedItem.PropertyChanged -= SelectedItem_PropertyChanged;
            _selectedItem.IsSelected = false;
            _selectedItem.ReleaseSelectedPreview();
        }

        _selectedItem = item;
        if (item is null)
        {
            SidebarEmpty.IsVisible = true;
            SidebarContent.IsVisible = false;
            SidebarMetadataPanel.Opacity = 0;
            SidebarImage.Source = null;
            SidebarPrompt.Text = "";
            SidebarPrompt.SelectionStart = 0;
            SidebarPrompt.SelectionEnd = 0;
            SidebarNegativePrompt.Text = "";
            SidebarNegativePromptContainer.IsVisible = false;
            CollapseNegativePrompt();

            SidebarHeaderTitle.Text = "Metadata";
            ToolTip.SetTip(SidebarHeaderTitle, null);
            SidebarHeaderDesc.IsVisible = true;
            return;
        }

        CollapseNegativePrompt();

        item.IsSelected = true;
        item.PropertyChanged += SelectedItem_PropertyChanged;
        if (_loadCancellation?.Token is { } token)
        {
            _thumbnailLoads.EnqueueVisible(item, token);
            _ = item.EnsureMetadataLoadedAsync(token);
            item.EnsureSelectedPreviewLoaded(token);
        }

        SidebarEmpty.IsVisible = false;
        SidebarMetadataPanel.Opacity = 0;
        SidebarContent.IsVisible = true;
        RefreshSidebar(item);
        FadeInSidebarContent();

        if (LargePreviewOverlay.IsVisible)
        {
            UpdateLargePreview(resetZoom: true);
        }
    }

    private void FadeInSidebarContent()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (SidebarContent.IsVisible)
            {
                SidebarMetadataPanel.Opacity = 1;
            }
        }, DispatcherPriority.Render);
    }

    private void SidebarNegativePromptHeader_Click(object? sender, RoutedEventArgs e)
    {
        ToggleNegativePrompt();
        e.Handled = true;
    }

    private void ToggleNegativePrompt()
    {
        bool isVisible = !SidebarNegativePrompt.IsVisible;
        SidebarNegativePrompt.IsVisible = isVisible;
        SidebarNegativePromptArrow.Text = isVisible ? "▾" : "▸";
        SidebarNegativePromptHeader.IsChecked = isVisible;
    }

    private void CollapseNegativePrompt()
    {
        SidebarNegativePrompt.IsVisible = false;
        SidebarNegativePromptArrow.Text = "▸";
        SidebarNegativePromptHeader.IsChecked = false;
    }


    private void SelectedItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == _selectedItem)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_selectedItem is not null)
                {
                    RefreshSidebar(_selectedItem);
                    if (LargePreviewOverlay.IsVisible)
                    {
                        UpdateLargePreview(resetZoom: false);
                    }
                }
            });
        }
    }

    private void RefreshSidebar(ImageItem item)
    {
        SidebarHeaderTitle.Text = item.FileName;
        ToolTip.SetTip(SidebarHeaderTitle, item.Path);
        SidebarHeaderDesc.IsVisible = false;
        SidebarDimensions.Text = item.DimensionsText;

        SidebarModel.Text = item.Model;
        SidebarModelRow.IsVisible = !string.IsNullOrWhiteSpace(item.Model);

        SidebarSampler.Text = item.Sampler;
        SidebarSamplerRow.IsVisible = !string.IsNullOrWhiteSpace(item.Sampler);

        SidebarSeed.Text = item.Seed;
        SidebarSeedRow.IsVisible = !string.IsNullOrWhiteSpace(item.Seed);

        SidebarSettings.Text = item.Settings;
        SidebarSettingsRow.IsVisible = !string.IsNullOrWhiteSpace(item.Settings);

        SidebarLora.Text = item.Lora;
        SidebarLoraRow.IsVisible = !string.IsNullOrWhiteSpace(item.Lora);

        SidebarDate.Text = item.CreationDateText;
        SidebarPrompt.Text = item.HasPrompt ? item.Prompt : "";
        SidebarPrompt.SelectionStart = 0;
        SidebarPrompt.SelectionEnd = 0;

        SidebarNegativePrompt.Text = item.HasNegativePrompt ? item.NegativePrompt : "";
        SidebarNegativePrompt.SelectionStart = 0;
        SidebarNegativePrompt.SelectionEnd = 0;
        SidebarNegativePromptContainer.IsVisible = item.HasNegativePrompt;

        SidebarImage.Source = item.SelectedPreview ?? item.Preview;
    }

    private async void CopySeedButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedItem is not null && !string.IsNullOrWhiteSpace(_selectedItem.Seed))
        {
            await CopyTextAsync(_selectedItem.Seed);
            if (sender is Button button)
            {
                var originalContent = button.Content;
                button.Content = "Copied!";
                button.IsEnabled = false;
                await Task.Delay(1000);
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }
    }

    private async Task<bool> CopyTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
            return true;
        }

        return false;
    }

    private void ShowCopiedToast(TextBox textBox)
    {
        Border? badge = null;
        if (textBox == SidebarPrompt)
        {
            badge = PositiveCopiedBadge;
        }
        else if (textBox == SidebarNegativePrompt)
        {
            badge = NegativeCopiedBadge;
        }

        if (badge is not null)
        {
            if (badge.Tag is DispatcherTimer existingTimer)
            {
                existingTimer.Stop();
            }

            badge.Opacity = 1;

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.2)
            };
            timer.Tick += (s, e) =>
            {
                badge.Opacity = 0;
                timer.Stop();
                badge.Tag = null;
            };
            badge.Tag = timer;
            timer.Start();
        }
    }

    private void ClearTextBoxFocusAfterCopy(TextBox textBox)
    {
        textBox.ClearSelection();
        ShowCopiedToast(textBox);
        
        Dispatcher.UIThread.Post(() =>
        {
            var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
            if (focusManager != null && textBox.IsKeyboardFocusWithin)
            {
                focusManager.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None);
            }
        }, DispatcherPriority.Background);
    }

    private void TextBox_CopyingToClipboard(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            Dispatcher.UIThread.Post(() => ClearTextBoxFocusAfterCopy(textBox));
        }
    }

    private TextBox? GetTargetTextBox(object? sender)
    {
        if (sender is MenuItem item)
        {
            if (item.Name == "MenuCopyNegativeSelection" || 
                item.Name == "MenuCopyNegativeFullPrompt" || 
                item.Name == "MenuSelectNegativeAll")
            {
                return SidebarNegativePrompt;
            }
            if (item.Name == "MenuCopySelection" || 
                item.Name == "MenuCopyFullPrompt" || 
                item.Name == "MenuSelectAll")
            {
                return SidebarPrompt;
            }
        }
        if (_activeContextMenuTextBox is not null)
        {
            return _activeContextMenuTextBox;
        }
        if (sender is MenuItem item2 && item2.Parent is ContextMenu menu)
        {
            return menu.PlacementTarget as TextBox;
        }
        return null;
    }

    private void ContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            var textBox = menu.PlacementTarget as TextBox;
            if (textBox is not null)
            {
                _activeContextMenuTextBox = textBox;
                var copyItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Name == "MenuCopySelection" || i.Name == "MenuCopyNegativeSelection");
                if (copyItem is not null)
                {
                    copyItem.IsEnabled = !string.IsNullOrEmpty(textBox.SelectedText);
                }
            }
        }
    }

    private async void MenuCopySelection_Click(object? sender, RoutedEventArgs e)
    {
        var textBox = GetTargetTextBox(sender) ?? SidebarPrompt;
        var selection = textBox.SelectedText;
        if (!string.IsNullOrEmpty(selection))
        {
            if (await CopyTextAsync(selection))
            {
                ClearTextBoxFocusAfterCopy(textBox);
            }
        }
    }

    private async void MenuCopyFullPrompt_Click(object? sender, RoutedEventArgs e)
    {
        var textBox = GetTargetTextBox(sender) ?? SidebarPrompt;
        if (!string.IsNullOrEmpty(textBox.Text))
        {
            if (await CopyTextAsync(textBox.Text))
            {
                ClearTextBoxFocusAfterCopy(textBox);
            }
        }
    }

    private void MenuSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        var textBox = GetTargetTextBox(sender) ?? SidebarPrompt;
        textBox.SelectAll();
    }

    private void MenuOpenInExplorer_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ImageItem item })
        {
            SelectItem(item);
            OpenInExplorer(item.Path);
        }
    }

    private async void MenuCardCopyPrompt_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ImageItem item } && item.HasPrompt)
        {
            SelectItem(item);
            await CopyTextAsync(item.Prompt);
            ShowCopiedToast(SidebarPrompt);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        IntPtr apidl,
        uint dwFlags);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    private void OpenInExplorer(string filePath)
    {
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                var windowsPath = Path.GetFullPath(filePath);
                bool success = false;
                try
                {
                    int hr = SHParseDisplayName(windowsPath, IntPtr.Zero, out IntPtr pidl, 0, out _);
                    if (hr == 0 && pidl != IntPtr.Zero)
                    {
                        try
                        {
                            int selectHr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
                            if (selectHr == 0)
                            {
                                success = true;
                            }
                        }
                        finally
                        {
                            ILFree(pidl);
                        }
                    }
                }
                catch
                {
                }

                if (!success)
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{windowsPath}\"");
                }
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-R \"{filePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                var dir = Path.GetDirectoryName(filePath);
                if (dir is not null)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private void StartMetadataScanner(List<ImageItem> items)
    {
        CancelAndDispose(ref _scannerCancellation);
        _scannerCancellation = new CancellationTokenSource();
        var token = _scannerCancellation.Token;
        var scannerGeneration = ++_scannerGeneration;

        _ = Task.Run(async () =>
        {
            try
            {
                var lastRefreshTime = DateTime.UtcNow;
                var itemsSnapshot = items.ToList();

                await Parallel.ForEachAsync(itemsSnapshot, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 2,
                    CancellationToken = token
                },
                async (item, cancellationToken) =>
                {
                    try
                    {
                        await item.EnsureMetadataLoadedAsync(cancellationToken);

                        if (HasSearchQueryActive())
                        {
                            var now = DateTime.UtcNow;
                            bool shouldRefresh = false;
                            lock (itemsSnapshot)
                            {
                                if ((now - lastRefreshTime).TotalMilliseconds > 1000)
                                {
                                    lastRefreshTime = now;
                                    shouldRefresh = true;
                                }
                            }
                            if (shouldRefresh)
                            {
                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (!token.IsCancellationRequested && scannerGeneration == _scannerGeneration)
                                    {
                                        ApplyFilter();
                                    }
                                });
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                });

                if (!token.IsCancellationRequested && scannerGeneration == _scannerGeneration)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested && scannerGeneration == _scannerGeneration)
                        {
                            ApplyFilter();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void QueueInitialMetadataScanner(int loadGeneration)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_loadCancellation?.Token is not { IsCancellationRequested: false } token ||
                loadGeneration != _loadGeneration ||
                _allImageItems.Count == 0)
            {
                return;
            }

            QueueViewportThumbnailSchedule(force: true);
            Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested && loadGeneration == _loadGeneration)
                {
                    _ = StartInitialMetadataScannerWhenReadyAsync(loadGeneration, token);
                }
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Render);
    }

    private async Task StartInitialMetadataScannerWhenReadyAsync(int loadGeneration, CancellationToken token)
    {
        try
        {
            for (var poll = 0; poll < InitialMetadataScannerMaxPolls; poll++)
            {
                if (token.IsCancellationRequested || loadGeneration != _loadGeneration)
                {
                    return;
                }

                if (!_thumbnailLoads.HasVisibleWork)
                {
                    break;
                }

                await Task.Delay(InitialMetadataScannerPollInterval, token);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || loadGeneration != _loadGeneration)
                {
                    return;
                }

                if (_allImageItems.Count == 0)
                {
                    return;
                }

                StartMetadataScanner(_allImageItems);
                UpdateCountText();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool HasSearchQueryActive()
    {
        return _hasSearchQueryActive;
    }

    private void ImageItem_MetadataLoaded(ImageItem item)
    {
        _loadedMetadataCount++;
        UpdateCountText();
    }

    private void SearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        if (ClearSearchButton != null)
        {
            ClearSearchButton.IsVisible = hasText;
        }

        _hasSearchQueryActive = !string.IsNullOrWhiteSpace(SearchTextBox.Text);
        CancelAndDispose(ref _searchDebounceCancellation);
        _searchDebounceCancellation = new CancellationTokenSource();
        var token = _searchDebounceCancellation.Token;

        Task.Delay(150, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
            {
                Dispatcher.UIThread.Post(() => ApplyFilter(resetScroll: true));
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SearchScopeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ApplyFilter(resetScroll: true);
    }

    private void ClearSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        SearchTextBox.Focus();
    }

    private SearchScope GetSearchScope()
    {
        return SearchScopeComboBox.SelectedIndex switch
        {
            (int)SearchScope.Prompts => SearchScope.Prompts,
            (int)SearchScope.Filename => SearchScope.Filename,
            _ => SearchScope.All
        };
    }

    private void ApplyFilter(bool resetScroll = false)
    {
        var query = SearchTextBox.Text?.Trim();

        List<ImageItem> filtered;
        if (string.IsNullOrWhiteSpace(query))
        {
            filtered = _allImageItems;
        }
        else
        {
            SearchEngine.ParseQuery(query, out var positiveTerms, out var negativeTerms);
            if (positiveTerms.Count == 0 && negativeTerms.Count == 0)
            {
                filtered = _allImageItems;
            }
            else
            {
                var searchScope = GetSearchScope();
                filtered = new List<ImageItem>(_allImageItems.Count);
                foreach (var item in _allImageItems)
                {
                    if (ItemMatchesSearch(item, positiveTerms, negativeTerms, searchScope))
                    {
                        filtered.Add(item);
                    }
                }
            }
        }

        var hasChanges = _viewModel.Items.Count != filtered.Count;
        if (!hasChanges)
        {
            for (var index = 0; index < filtered.Count; index++)
            {
                if (!ReferenceEquals(_viewModel.Items[index], filtered[index]))
                {
                    hasChanges = true;
                    break;
                }
            }
        }

        if (hasChanges)
        {
            _viewModel.Items.Clear();
            _viewModel.Items.AddRange(filtered);
        }

        if (resetScroll)
        {
            GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, 0);
        }

        UpdateCountText();
        QueueViewportThumbnailSchedule();

        bool showEmpty = filtered.Count == 0 && _allImageItems.Count > 0;
        ToggleGalleryEmptyState(showEmpty);
    }

    private static bool ItemMatchesSearch(
        ImageItem item,
        List<SearchTerm> positiveTerms,
        List<SearchTerm> negativeTerms,
        SearchScope searchScope)
    {
        return searchScope switch
        {
            SearchScope.Filename => TextMatchesTerms(item.FileName, positiveTerms, negativeTerms),
            SearchScope.Prompts => item.HasLoadedMetadata &&
                                   PromptMatchesTerms(item, positiveTerms, negativeTerms) ||
                                   !item.HasLoadedMetadata,
            _ => ItemMatchesAllSearch(item, positiveTerms, negativeTerms)
        };
    }

    private static bool ItemMatchesAllSearch(
        ImageItem item,
        List<SearchTerm> positiveTerms,
        List<SearchTerm> negativeTerms)
    {
        foreach (var term in negativeTerms)
        {
            if (SearchEngine.IsMatch(item.FileName, term) ||
                item.HasLoadedMetadata && SearchEngine.IsMatch(item.Prompt, item.NegativePrompt, term))
            {
                return false;
            }
        }

        foreach (var term in positiveTerms)
        {
            if (SearchEngine.IsMatch(item.FileName, term))
            {
                continue;
            }

            if (!item.HasLoadedMetadata)
            {
                return true;
            }

            if (!SearchEngine.IsMatch(item.Prompt, item.NegativePrompt, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PromptMatchesTerms(
        ImageItem item,
        List<SearchTerm> positiveTerms,
        List<SearchTerm> negativeTerms)
    {
        foreach (var term in positiveTerms)
        {
            if (!SearchEngine.IsMatch(item.Prompt, item.NegativePrompt, term))
            {
                return false;
            }
        }

        foreach (var term in negativeTerms)
        {
            if (SearchEngine.IsMatch(item.Prompt, item.NegativePrompt, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TextMatchesTerms(
        string text,
        List<SearchTerm> positiveTerms,
        List<SearchTerm> negativeTerms)
    {
        foreach (var term in positiveTerms)
        {
            if (!SearchEngine.IsMatch(text, term))
            {
                return false;
            }
        }

        foreach (var term in negativeTerms)
        {
            if (SearchEngine.IsMatch(text, term))
            {
                return false;
            }
        }

        return true;
    }

    private async void ToggleGalleryEmptyState(bool show)
    {
        if (show)
        {
            if (!GalleryEmptyState.IsVisible)
            {
                GalleryEmptyState.Opacity = 0;
                GalleryEmptyState.IsVisible = true;
                await Task.Yield();
                GalleryEmptyState.Opacity = 1;
            }
        }
        else
        {
            if (GalleryEmptyState.IsVisible)
            {
                GalleryEmptyState.Opacity = 0;
                await Task.Delay(120);
                if (GalleryEmptyState.Opacity == 0)
                {
                    GalleryEmptyState.IsVisible = false;
                }
            }
        }
    }

    private void UpdateCountText()
    {
        int total = _allImageItems.Count;
        int filtered = _viewModel.Items.Count;

        bool isScanning = _scannerCancellation != null &&
                         !_scannerCancellation.IsCancellationRequested &&
                         _loadedMetadataCount < total;

        CountText.Opacity = 0.2;

        if (isScanning)
        {
            if (total == filtered)
            {
                CountText.Text = $"{total:n0} images (Scanning prompts {_loadedMetadataCount}/{total})";
            }
            else
            {
                CountText.Text = $"{filtered:n0} of {total} images (Scanning prompts {_loadedMetadataCount}/{total})";
            }
        }
        else
        {
            if (total == filtered)
            {
                CountText.Text = $"{total:n0} images";
            }
            else
            {
                CountText.Text = $"{filtered:n0} of {total} images";
            }
        }

        Dispatcher.UIThread.Post(() => {
            CountText.Opacity = 1.0;
        }, DispatcherPriority.Render);
    }

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
            return "Open a folder of ComfyUI images to start scrolling.";
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
        catch
        {
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

    private void ClearCacheButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _thumbnailLoads.Clear();
            ImageCache.ClearAndReleaseAll();
            if (Directory.Exists(ImageItem.ThumbnailCacheRootDir))
            {
                Directory.Delete(ImageItem.ThumbnailCacheRootDir, recursive: true);
            }

            Directory.CreateDirectory(ImageItem.ThumbnailCacheRootDir);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to clear thumbnail cache: {ex}");
            ShowMenuError($"Could not clear cache: {ex.Message}");
        }
    }

    private void OpenAppDataButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ImageItem.ThumbnailCacheRootDir);
            OpenFolderInFileManager(UserPreferences.AppDataDir);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to open app data folder: {ex}");
            ShowMenuError($"Could not open app data: {ex.Message}");
        }
    }

    private void ShowMainMenu()
    {
        StopFolderWatcher();
        CancelAndDispose(ref _scannerCancellation);
        _scannerGeneration++;
        CancelAndDispose(ref _loadCancellation);
        _loadGeneration++;
        _thumbnailLoads.Clear();
        ImageCache.ClearAndReleaseAll();
        SelectItem(null);
        ClearImageItems();
        _loadedMetadataCount = 0;
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
            closeButton.SetValue(AutomationProperties.NameProperty, $"Remove recent folder {folderName}");
            ToolTip.SetTip(closeButton, "Remove recent folder");
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


}
