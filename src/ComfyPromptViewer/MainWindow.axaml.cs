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
    private const double SidebarWidthWindowRatio = 0.3;
    private const double MinSidebarWidth = 260;
    private const double MaxSidebarWidth = 380;
    private const double SidebarPreviewHeightWindowRatio = 0.4;
    private const double MinSidebarPreviewHeight = 180;
    private const double MaxSidebarPreviewHeight = 350;
    private const double CollapsedPositivePromptMaxHeight = 168;
    private const int LongPositivePromptCharacterThreshold = 500;
    private const int LongPositivePromptLineThreshold = 7;
    private static readonly TimeSpan InitialMetadataScannerPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MetadataScannerSearchRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MetadataCountUpdateInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TileSizeSaveInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AdvancedMaintenanceStatusDuration = TimeSpan.FromSeconds(2.5);
    private const int InitialMetadataScannerMaxPolls = 15;
    private const int MetadataScannerMaxDegreeOfParallelism = 2;
    private const int WarmMetadataUiBatchSize = 64;
    private const int MaxIncrementalGalleryChanges = 32;
    private readonly GalleryViewModel _viewModel = new();
    private readonly ThumbnailLoadCoordinator _thumbnailLoads = new();
    private readonly List<string> _allImagePaths = [];
    private readonly List<ImageItem> _allImageItems = [];
    private readonly Dictionary<string, DateTime> _imageLastWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ImageItem> _visibleThumbnailScheduleItems = [];
    private readonly List<ImageItem> _aheadThumbnailScheduleItems = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _scannerCancellation;
    private CancellationTokenSource? _advancedMaintenanceStatusCancellation;
    private DispatcherTimer? _searchDebounceTimer;
    private DispatcherTimer? _metadataCountUpdateTimer;
    private DispatcherTimer? _tileSizeSaveTimer;
    private ImageItem? _selectedItem;
    private ImageItem? _queuedSelectedItemRefresh;
    private SortMode _sortMode = SortMode.NewestFirst;
    private ThemeMode _themeMode = UserPreferences.LoadThemeMode();
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
    private int _galleryScrollRestoreGeneration;
    private int _lastPrefetchFirstVisibleRow = -1;
    private int _prefetchDirection = 1;
    private volatile bool _hasSearchQueryActive;
    private TextBox? _activeContextMenuTextBox;
    private bool _isPositivePromptExpanded;
    private bool _isNegativePromptExpanded;
    private double _lastScrollOffsetY;
    private long _lastScrollTimestamp;
    private long _lastScrollEventTime;
    private long _lastFastScrollScheduleTime;
    private static volatile bool _isFastScrollingStatic;
    public static bool IsFastScrolling => _isFastScrollingStatic;
    private DispatcherTimer? _scrollMonitorTimer;
    private const double ScrollVelocityThreshold = 1200.0;

    internal enum SearchScope
    {
        All,
        PositivePrompt,
        NegativePrompt,
        Filename
    }

    private sealed record ThemePalette(
        string BackgroundBase,
        string SurfaceCard,
        string SurfaceSidebar,
        string SurfaceElevated,
        string SurfaceInput,
        string BorderSubtle,
        string ToolbarBorderSubtle,
        string BorderAccent,
        string CardHoverBorder,
        string TextPrimary,
        string TextSecondary,
        string TextMuted,
        string TextAccent,
        string EmptyStateSubtext);

    private static readonly (string Key, Func<ThemePalette, string> Color)[] ThemeBrushResources =
    [
        ("BackgroundBase", p => p.BackgroundBase),
        ("SurfaceBase", p => p.BackgroundBase),
        ("LargePreviewOverlayBackground", p => p.BackgroundBase),
        ("SurfaceCard", p => p.SurfaceCard),
        ("SurfaceSidebar", p => p.SurfaceSidebar),
        ("SurfaceElevated", p => p.SurfaceElevated),
        ("SurfaceInput", p => p.SurfaceInput),
        ("BorderSubtle", p => p.BorderSubtle),
        ("ToolbarBorderSubtle", p => p.ToolbarBorderSubtle),
        ("BorderAccent", p => p.BorderAccent),
        ("CardHoverBorder", p => p.CardHoverBorder),
        ("TextPrimary", p => p.TextPrimary),
        ("TextSecondary", p => p.TextSecondary),
        ("TextMuted", p => p.TextMuted),
        ("TextAccent", p => p.TextAccent),
        ("PromptText", p => p.TextSecondary),
        ("EmptyStateSubtext", p => p.EmptyStateSubtext),

        ("SystemControlHighlightAccentBrush", p => p.BorderAccent),
        ("AccentFillColorDefaultBrush", p => p.BorderAccent),
        ("AccentFillColorSecondaryBrush", p => p.BorderAccent),
        ("AccentFillColorTertiaryBrush", p => p.BorderAccent),

        ("SliderThumbBackground", p => p.BorderAccent),
        ("SliderThumbBackgroundPointerOver", p => p.TextAccent),
        ("SliderThumbBackgroundPressed", p => p.BorderAccent),
        ("SliderThumbBackgroundDisabled", p => p.BorderSubtle),
        ("SliderTrackFill", p => p.BorderSubtle),
        ("SliderTrackFillPointerOver", p => p.BorderSubtle),
        ("SliderTrackFillPressed", p => p.BorderSubtle),
        ("SliderTrackFillDisabled", p => p.SurfaceInput),
        ("SliderTrackValueFill", p => p.BorderAccent),
        ("SliderTrackValueFillPointerOver", p => p.BorderAccent),
        ("SliderTrackValueFillPressed", p => p.BorderAccent),
        ("SliderTrackValueFillDisabled", p => p.BorderSubtle),

        ("ComboBoxBackground", p => p.SurfaceInput),
        ("ComboBoxBackgroundPointerOver", p => p.SurfaceElevated),
        ("ComboBoxBackgroundPressed", p => p.SurfaceInput),
        ("ComboBoxBackgroundDisabled", p => p.SurfaceSidebar),
        ("ComboBoxBorderBrush", p => p.BorderSubtle),
        ("ComboBoxBorderBrushPointerOver", p => p.BorderAccent),
        ("ComboBoxBorderBrushPressed", p => p.BorderAccent),
        ("ComboBoxBorderBrushDisabled", p => p.BorderSubtle),
        ("ComboBoxDropdownBackground", p => p.SurfaceCard),
        ("ComboBoxDropDownBackground", p => p.SurfaceCard),
        ("ComboBoxDropdownBorderBrush", p => p.BorderSubtle),
        ("ComboBoxDropDownBorderBrush", p => p.BorderSubtle),
        ("ComboBoxForeground", p => p.TextPrimary),
        ("ComboBoxForegroundPointerOver", p => p.TextPrimary),
        ("ComboBoxForegroundPressed", p => p.TextPrimary),
        ("ComboBoxForegroundDisabled", p => p.TextMuted),
        ("ComboBoxItemBackgroundPointerOver", p => p.SurfaceInput),
        ("ComboBoxItemBackgroundPressed", p => p.SurfaceInput),
        ("ComboBoxItemBackgroundSelected", p => p.BorderAccent),
        ("ComboBoxItemBackgroundSelectedPointerOver", p => p.TextAccent),
        ("ComboBoxItemBackgroundSelectedPressed", p => p.BorderAccent),
        ("ComboBoxItemForeground", p => p.TextSecondary),
        ("ComboBoxItemForegroundPointerOver", p => p.TextPrimary),
        ("ComboBoxItemForegroundPressed", p => p.TextPrimary),
        ("ComboBoxItemForegroundSelected", p => p.TextPrimary),
        ("ComboBoxItemForegroundSelectedPointerOver", p => p.TextPrimary),
        ("ComboBoxItemForegroundSelectedPressed", p => p.TextPrimary),
        ("ComboBoxItemForegroundDisabled", p => p.TextMuted),

        ("MenuFlyoutPresenterBackground", p => p.BackgroundBase),
        ("MenuFlyoutPresenterBorderBrush", p => p.BorderSubtle),
        ("MenuFlyoutItemBackgroundPointerOver", p => p.SurfaceCard),
        ("MenuFlyoutItemBackgroundPressed", p => p.SurfaceInput),
        ("MenuFlyoutItemForeground", p => p.TextSecondary),
        ("MenuFlyoutItemForegroundPointerOver", p => p.TextPrimary),
        ("MenuFlyoutItemForegroundPressed", p => p.TextPrimary),
        ("MenuFlyoutItemForegroundDisabled", p => p.TextMuted)
    ];

    private readonly record struct GalleryScrollAnchor(ImageItem Item, int OldIndex, double Offset);
    private readonly record struct ImageFileEntry(string Path, DateTime LastWriteTimeUtc);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _thumbnailLoads.VisibleWorkDrained = ImageItem.ResumeDeferredThumbnailCacheWrites;
        ImageItem.SetDeferredThumbnailCacheWritePause(() => _thumbnailLoads.HasVisibleWork);

        GalleryScrollViewer.AddHandler(InputElement.PointerPressedEvent, GalleryScrollViewer_PointerPressed, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerMovedEvent, GalleryScrollViewer_PointerMoved, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerReleasedEvent, GalleryScrollViewer_PointerReleased, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerCaptureLostEvent, GalleryScrollViewer_PointerCaptureLost, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, GalleryScrollViewer_PointerWheelChanged, RoutingStrategies.Tunnel, true);
        GalleryItems.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble, true);
        
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
        SearchHelpPopup.PlacementTarget = SearchHelpButton;
        ApplyTheme(_themeMode);
        ThemeComboBox.SelectedIndex = (int)_themeMode;

        _isInitializing = false;
        this.SizeChanged += Window_SizeChanged;
        this.Opened += MainWindow_Opened;
    }

    private void ThemeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var selectedIndex = ThemeComboBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex > (int)ThemeMode.Plum)
        {
            return;
        }

        _themeMode = (ThemeMode)selectedIndex;
        ApplyTheme(_themeMode);
        UserPreferences.SaveThemeMode(_themeMode);
    }

    private static void ApplyTheme(ThemeMode themeMode)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var palette = GetThemePalette(themeMode);

        SetColor(resources, "SystemAccentColor", palette.BorderAccent);
        foreach (var (key, color) in ThemeBrushResources)
        {
            SetBrush(resources, key, color(palette));
        }
    }

    private static ThemePalette GetThemePalette(ThemeMode themeMode)
    {
        return themeMode switch
        {
            ThemeMode.DarkGray => new ThemePalette(
                "#111315", "#202326", "#171A1D", "#1D2023", "#24282C", "#343A40", "#434B52",
                "#6E7681", "#464D55", "#F1F4F6", "#C4CCD4", "#828B95", "#A8B2BD", "#69737D"),
            ThemeMode.DarkBlue => new ThemePalette(
                "#0D1320", "#182236", "#111A2B", "#162033", "#1B2940", "#2A3A56", "#394D70",
                "#3D6EA8", "#344A68", "#EEF5FF", "#B9CBE2", "#7186A0", "#7EA7D8", "#5E7188"),
            ThemeMode.DarkGreen => new ThemePalette(
                "#0D1712", "#18251D", "#111D16", "#16231B", "#1C2B22", "#2C3E32", "#3B5142",
                "#4F7D5E", "#3A4E40", "#EFF7F0", "#BFD3C3", "#758B7A", "#83B88E", "#617467"),
            ThemeMode.Plum => new ThemePalette(
                "#17111B", "#251B2B", "#1C1421", "#231928", "#2B2032", "#3B2D45", "#4D3A5A",
                "#7B4F8C", "#4A3855", "#F5EDF7", "#CFB9D7", "#8B7594", "#B985C8", "#735F7B"),
            _ => new ThemePalette(
                "#17120D", "#272016", "#1C1610", "#261E16", "#2C2318", "#3A2E22", "#4A3A2A",
                "#8B3A2E", "#4A3D30", "#F5EDE0", "#C4AE92", "#8C7660", "#D4795A", "#74604E")
        };
    }

    private static void SetBrush(IResourceDictionary resources, string key, string color)
    {
        var parsed = Color.Parse(color);
        if (resources[key] is SolidColorBrush brush)
        {
            brush.Color = parsed;
        }
        else
        {
            resources[key] = new SolidColorBrush(parsed);
        }
    }

    private static void SetColor(IResourceDictionary resources, string key, string color)
    {
        resources[key] = Color.Parse(color);
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
        _scrollMonitorTimer?.Stop();
        _isFastScrollingStatic = false;
        _lastFastScrollScheduleTime = 0;
        StopAutoScroll();
        StopLargePreviewPan(releaseCapture: true);
        StopFolderWatcher();
        _searchDebounceTimer?.Stop();
        if (_tileSizeSaveTimer?.IsEnabled == true)
        {
            _tileSizeSaveTimer.Stop();
            UserPreferences.SaveTileSize(_targetTileSize);
        }
        CancelAndDispose(ref _scannerCancellation);
        _scannerGeneration++;
        CancelAndDispose(ref _loadCancellation);
        CancelAndDispose(ref _advancedMaintenanceStatusCancellation);
        _loadGeneration++;
        _thumbnailLoads.Clear();
        ImageItem.ClearDeferredThumbnailCacheWrites();
        ImageItem.SetDeferredThumbnailCacheWritePause(null);
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
        _metadataCountUpdateTimer?.Stop();
        GalleryEmptyState.IsVisible = false;
        GalleryEmptyState.Opacity = 0;

        foreach (var item in _allImageItems)
        {
            item.MetadataLoaded -= ImageItem_MetadataLoaded;
        }

        _viewModel.Items.Clear();
        _allImagePaths.Clear();
        _allImageItems.Clear();
        _imageLastWriteTimes.Clear();
        _visibleThumbnailScheduleItems.Clear();
        _aheadThumbnailScheduleItems.Clear();
        _lastPrefetchFirstVisibleRow = -1;
        _prefetchDirection = 1;
    }

    private void AdvancedToggle_Click(object? sender, RoutedEventArgs e)
    {
        AdvancedPanel.IsVisible = AdvancedToggle.IsChecked == true;
        if (!AdvancedPanel.IsVisible)
        {
            ClearAdvancedMaintenanceStatus();
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
        var loadGeneration = ++_loadGeneration;

        CancelAndDispose(ref _loadCancellation);
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;

        CancelAndDispose(ref _scannerCancellation);
        _scannerGeneration++;
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
        _loadedMetadataCount = 0;
        FolderText.Text = TruncatePath(folderPath);
        CopyPathButton.IsVisible = true;
        _currentFolderPath = folderPath;
        CountText.Text = "Scanning...";
        MainMenu.IsVisible = false;
        HeaderBorder.IsVisible = true;

        try
        {
            var imageFiles = await Task.Run(
                () => ReadImageFileEntries(EnumerateImagePaths(folderPath, includeSubfolders, token), token),
                token);

            if (token.IsCancellationRequested || loadGeneration != _loadGeneration)
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
            _ = Task.Run(() => MetadataIndex.PruneMissing(imageFiles.Select(file => file.Path), includeSubfolders));
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

    private static List<ImageFileEntry> ReadImageFileEntries(IEnumerable<string> paths, CancellationToken token)
    {
        var entries = new List<ImageFileEntry>();
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (!ImageFileReader.IsSupportedImage(path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    entries.Add(new ImageFileEntry(path, File.GetLastWriteTimeUtc(path)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DebugLog.Write($"Skipped image file {path}: {ex.Message}");
            }
        }

        return entries;
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
            QueueTileSizeSave();
        }
    }

    private void QueueTileSizeSave()
    {
        if (_tileSizeSaveTimer is null)
        {
            _tileSizeSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TileSizeSaveInterval
            };
            _tileSizeSaveTimer.Tick += (_, _) =>
            {
                _tileSizeSaveTimer.Stop();
                UserPreferences.SaveTileSize(_targetTileSize);
            };
        }

        _tileSizeSaveTimer.Stop();
        _tileSizeSaveTimer.Start();
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

        if (Application.Current is { } application)
        {
            application.Resources["GalleryTileSize"] = _tileSize;
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
        _allImagePaths.Sort(CompareImagePaths);

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

    private int CompareImagePaths(string left, string right)
    {
        if (_sortMode == SortMode.Name)
        {
            return CompareImagePathNames(left, right);
        }

        var compare = _imageLastWriteTimes[left].CompareTo(_imageLastWriteTimes[right]);
        if (_sortMode == SortMode.NewestFirst)
        {
            compare = -compare;
        }

        return compare != 0
            ? compare
            : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static int CompareImagePathNames(string left, string right)
    {
        var fileNameCompare = StringComparer.CurrentCultureIgnoreCase.Compare(Path.GetFileName(left), Path.GetFileName(right));
        return fileNameCompare != 0
            ? fileNameCompare
            : StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
    }

    internal static int FindSortedInsertIndex<T>(IReadOnlyList<T> sortedItems, T item, Comparison<T> comparison)
    {
        var low = 0;
        var high = sortedItems.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (comparison(sortedItems[middle], item) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private void GalleryItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control control && control.DataContext is ImageItem item)
        {
            var scrollOffset = GalleryScrollViewer.Offset.Y;
            SelectItem(item);
            var index = _viewModel.Items.IndexOf(item);
            if (index >= 0)
            {
                var row = index / GetGalleryColumnCount();
                var rowTop = GalleryItems.Margin.Top + (row * _tileItemExtent);
                var rowBottom = rowTop + _tileItemExtent;
                var viewportHeight = GalleryScrollViewer.Viewport.Height > 0
                    ? GalleryScrollViewer.Viewport.Height
                    : Math.Max(1, GalleryScrollViewer.Bounds.Height);

                if (rowTop < scrollOffset || rowBottom > scrollOffset + viewportHeight)
                {
                    EnsureIndexVisible(index);
                }
                else
                {
                    QueueGalleryScrollRestore(scrollOffset);
                }
            }
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

        double targetSidebarWidth = Math.Clamp(
            windowWidth * SidebarWidthWindowRatio,
            MinSidebarWidth,
            MaxSidebarWidth);
        
        if (MainGrid != null && MainGrid.ColumnDefinitions.Count > 1)
        {
            MainGrid.ColumnDefinitions[1].Width = new GridLength(targetSidebarWidth, GridUnitType.Pixel);
        }

        double targetImageHeight = Math.Clamp(
            windowHeight * SidebarPreviewHeightWindowRatio,
            MinSidebarPreviewHeight,
            MaxSidebarPreviewHeight);

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
        var availableWidth = GalleryItems.Bounds.Width > 0
            ? GalleryItems.Bounds.Width
            : (GalleryScrollViewer.Viewport.Width > 0 ? GalleryScrollViewer.Viewport.Width : Bounds.Width)
              - GalleryItems.Margin.Left
              - GalleryItems.Margin.Right;
        return Math.Max(1, (int)Math.Floor(Math.Max(1, availableWidth) / Math.Max(1, _tileItemExtent)));
    }

    private void EnsureIndexVisible(int index)
    {
        var columns = GetGalleryColumnCount();
        var row = index / columns;
        var rowTop = GalleryItems.Margin.Top + (row * _tileItemExtent);
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

        var currentOffset = GalleryScrollViewer.Offset.Y;
        var currentTimestamp = Environment.TickCount64;

        var dt = (currentTimestamp - _lastScrollTimestamp) / 1000.0;
        var dy = Math.Abs(currentOffset - _lastScrollOffsetY);
        var velocity = dt > 0.005 ? dy / dt : 0.0;

        _lastScrollOffsetY = currentOffset;
        _lastScrollTimestamp = currentTimestamp;
        _lastScrollEventTime = currentTimestamp;

        if (velocity > ScrollVelocityThreshold)
        {
            if (!IsFastScrolling)
            {
                _isFastScrollingStatic = true;
                _thumbnailLoads.Clear();
            }

            var now = Environment.TickCount64;
            if (now - _lastFastScrollScheduleTime >= 120)
            {
                _lastFastScrollScheduleTime = now;
                QueueViewportThumbnailSchedule();
            }

            if (_scrollMonitorTimer == null)
            {
                _scrollMonitorTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(50),
                    DispatcherPriority.Background,
                    ScrollMonitorTimer_Tick);
            }

            if (!_scrollMonitorTimer.IsEnabled)
            {
                _scrollMonitorTimer.Start();
            }
        }
        else
        {
            if (IsFastScrolling)
            {
                _isFastScrollingStatic = false;
                _scrollMonitorTimer?.Stop();
                QueueViewportThumbnailSchedule(force: true);
            }
            else
            {
                _scrollMonitorTimer?.Stop();
                QueueViewportThumbnailSchedule();
            }
        }
    }

    private void ScrollMonitorTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = Environment.TickCount64 - _lastScrollEventTime;
        if (elapsed >= 150)
        {
            _scrollMonitorTimer?.Stop();
            if (IsFastScrolling)
            {
                _isFastScrollingStatic = false;
                QueueViewportThumbnailSchedule(force: true);
            }
            else
            {
                QueueViewportThumbnailSchedule();
            }
        }
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
        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0 ? GalleryScrollViewer.Viewport.Height : Bounds.Height;
        var columnCount = GetGalleryColumnCount();
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(GalleryScrollViewer.Offset.Y / itemExtent));
        var visibleRowCount = Math.Max(1, (int)Math.Ceiling(viewportHeight / itemExtent) + 1);
        var visibleItems = _visibleThumbnailScheduleItems;
        var aheadItems = _aheadThumbnailScheduleItems;
        visibleItems.Clear();
        aheadItems.Clear();
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
                var item = _viewModel.Items[index];
                item.SetTileSize(_tileSize);
                target.Add(item);
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
            ApplyPositivePromptPresentation("");
            SidebarNegativePrompt.Text = "";
            SidebarNegativePromptContainer.IsVisible = false;
            CollapseNegativePrompt();
            ApplyNegativePromptPresentation("");

            SidebarHeaderTitle.Text = "Metadata";
            ToolTip.SetTip(SidebarHeaderTitle, null);
            SidebarHeaderDesc.IsVisible = true;
            return;
        }

        CollapseNegativePrompt();
        _isPositivePromptExpanded = false;
        _isNegativePromptExpanded = false;

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
        bool isVisible = !SidebarNegativePromptTextContainer.IsVisible;
        SidebarNegativePromptTextContainer.IsVisible = isVisible;
        SidebarCopyNegativePromptButton.IsVisible = isVisible;
        SidebarNegativePromptArrow.Text = isVisible ? "▾" : "▸";
        SidebarNegativePromptHeader.IsChecked = isVisible;
        ApplyNegativePromptPresentation(SidebarNegativePrompt.Text ?? "");
    }

    private void CollapseNegativePrompt()
    {
        SidebarNegativePromptTextContainer.IsVisible = false;
        SidebarCopyNegativePromptButton.IsVisible = false;
        SidebarNegativePromptFade.IsVisible = false;
        SidebarNegativePromptExpandButton.IsVisible = false;
        SidebarNegativePromptArrow.Text = "▸";
        SidebarNegativePromptHeader.IsChecked = false;
        _isNegativePromptExpanded = false;
    }

    private void SidebarPromptExpandButton_Click(object? sender, RoutedEventArgs e)
    {
        _isPositivePromptExpanded = !_isPositivePromptExpanded;
        ApplyPositivePromptPresentation(SidebarPrompt.Text ?? "");
        e.Handled = true;
    }

    private void SidebarNegativePromptExpandButton_Click(object? sender, RoutedEventArgs e)
    {
        _isNegativePromptExpanded = !_isNegativePromptExpanded;
        ApplyNegativePromptPresentation(SidebarNegativePrompt.Text ?? "");
        e.Handled = true;
    }

    private void ApplyPositivePromptPresentation(string prompt)
    {
        // Intentional heuristic: avoids a text layout pass; use measured rendered height if sidebar widths become less predictable.
        bool isLong = prompt.Length >= LongPositivePromptCharacterThreshold ||
                      prompt.Count(character => character == '\n') >= LongPositivePromptLineThreshold;
        if (!isLong)
        {
            _isPositivePromptExpanded = false;
        }

        bool isCollapsed = isLong && !_isPositivePromptExpanded;
        SidebarPrompt.MaxHeight = isCollapsed ? CollapsedPositivePromptMaxHeight : double.PositiveInfinity;
        SidebarPromptFade.IsVisible = isCollapsed;
        SidebarPromptExpandButton.IsVisible = isLong;
        SidebarPromptExpandButton.IsChecked = _isPositivePromptExpanded;
        if (SidebarPromptExpandButton.Content is TextBlock label)
        {
            label.Text = _isPositivePromptExpanded ? "Show less" : "Show more";
        }
    }

    private void ApplyNegativePromptPresentation(string prompt)
    {
        // Intentional heuristic: mirrors positive prompt expansion without an extra text measurement pass.
        bool isLong = prompt.Length >= LongPositivePromptCharacterThreshold ||
                      prompt.Count(character => character == '\n') >= LongPositivePromptLineThreshold;
        if (!isLong)
        {
            _isNegativePromptExpanded = false;
        }

        bool isCollapsed = isLong && !_isNegativePromptExpanded;
        bool isVisible = SidebarNegativePromptTextContainer.IsVisible;
        SidebarNegativePrompt.MaxHeight = isCollapsed ? CollapsedPositivePromptMaxHeight : double.PositiveInfinity;
        SidebarNegativePromptFade.IsVisible = isVisible && isCollapsed;
        SidebarNegativePromptExpandButton.IsVisible = isVisible && isLong;
        SidebarNegativePromptExpandButton.IsChecked = _isNegativePromptExpanded;
        if (SidebarNegativePromptExpandButton.Content is TextBlock label)
        {
            label.Text = _isNegativePromptExpanded ? "Show less" : "Show more";
        }
    }

    private void SelectedItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ImageItem item || item != _selectedItem || item == _queuedSelectedItemRefresh)
        {
            return;
        }

        _queuedSelectedItemRefresh = item;
        Dispatcher.UIThread.Post(() =>
        {
            if (_queuedSelectedItemRefresh == item)
            {
                _queuedSelectedItemRefresh = null;
                if (_selectedItem == item)
                {
                    RefreshSidebar(item);
                    if (LargePreviewOverlay.IsVisible)
                    {
                        UpdateLargePreview(resetZoom: false);
                    }
                }
            }
        }, DispatcherPriority.Background);
    }

    private void RefreshSidebar(ImageItem item)
    {
        SidebarHeaderTitle.Text = item.FileName;
        ToolTip.SetTip(SidebarHeaderTitle, item.Path);
        SidebarHeaderDesc.IsVisible = false;
        SidebarDimensions.Text = item.DimensionsText;

        SidebarTool.Text = item.Tool;
        SidebarToolRow.IsVisible = !string.IsNullOrWhiteSpace(item.Tool);

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

        SidebarResources.Text = item.Resources;
        SidebarResourcesRow.IsVisible = !string.IsNullOrWhiteSpace(item.Resources);

        SidebarDate.Text = item.CreationDateText;
        SidebarPrompt.Text = item.HasPrompt ? item.Prompt : "";
        SidebarPrompt.SelectionStart = 0;
        SidebarPrompt.SelectionEnd = 0;
        SidebarCopyPromptButton.IsVisible = item.HasPrompt;
        SidebarCopyPromptButton.IsEnabled = item.HasPrompt;
        ApplyPositivePromptPresentation(SidebarPrompt.Text);

        SidebarNegativePrompt.Text = item.HasNegativePrompt ? item.NegativePrompt : "";
        SidebarNegativePrompt.SelectionStart = 0;
        SidebarNegativePrompt.SelectionEnd = 0;
        SidebarNegativePromptContainer.IsVisible = item.HasNegativePrompt;
        ApplyNegativePromptPresentation(SidebarNegativePrompt.Text);

        SidebarImage.Source = item.SelectedPreview ?? item.Preview;
    }

    private async void SidebarCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field } button || _selectedItem is not { } item)
        {
            return;
        }

        var text = field switch
        {
            "positive" when item.HasPrompt => item.Prompt,
            "negative" when item.HasNegativePrompt => item.NegativePrompt,
            "seed" => item.Seed,
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            await CopyWithButtonFeedbackAsync(button, text);
        }
    }

    private async Task CopyWithButtonFeedbackAsync(Button button, string text)
    {
        if (!await CopyTextAsync(text))
        {
            return;
        }

        var originalContent = button.Content;
        button.Content = "Copied!";
        button.IsEnabled = false;
        await Task.Delay(1000);
        button.Content = originalContent;
        button.IsEnabled = button == SidebarCopyPromptButton
            ? _selectedItem?.HasPrompt == true
            : button == SidebarCopyNegativePromptButton
                ? _selectedItem?.HasNegativePrompt == true
                : _selectedItem is not null && !string.IsNullOrWhiteSpace(_selectedItem.Seed);
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

    private async void ImageContextMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string action } menuItem ||
            (menuItem.DataContext as ImageItem ?? _selectedItem) is not { } item)
        {
            return;
        }

        SelectItem(item);
        switch (action)
        {
            case "open":
                OpenInExplorer(item.Path);
                break;
            case "prompt":
                if (item.HasPrompt && await CopyTextAsync(item.Prompt))
                {
                    ShowCopiedToast(SidebarPrompt);
                }
                break;
            case "negative":
                if (item.HasNegativePrompt && await CopyTextAsync(item.NegativePrompt))
                {
                    ShowCopiedToast(SidebarNegativePrompt);
                }
                break;
            case "path":
                await CopyTextAsync(item.Path);
                break;
            case "delete":
                HideLargePreview();
                DeleteImage(item);
                break;
        }
    }

    private void DeleteImage(ImageItem item)
    {
        try
        {
            var path = item.Path;
            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows())
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    File.Delete(path);
                }
            }

            ProcessWatcherChanges([], [], [path]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CountText.Text = $"Could not delete image: {ex.Message}";
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
                catch (Exception ex)
                {
                    DebugLog.Write($"Failed to select image in Explorer shell API for {filePath}: {ex.Message}");
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
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to open file manager for {filePath}: {ex.Message}");
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
                var cachedEntries = MetadataIndex.LoadMany(itemsSnapshot.Select(item => item.Path), token);

                if (cachedEntries.Count > 0 && !token.IsCancellationRequested && scannerGeneration == _scannerGeneration)
                {
                    if (!await ApplyWarmMetadataEntriesAsync(itemsSnapshot, cachedEntries, token, scannerGeneration))
                    {
                        return;
                    }
                }

                var uncachedItems = itemsSnapshot
                    .Where(item => !item.HasLoadedMetadata)
                    .ToList();

                await Parallel.ForEachAsync(uncachedItems, new ParallelOptions
                {
                    MaxDegreeOfParallelism = MetadataScannerMaxDegreeOfParallelism,
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
                                if (now - lastRefreshTime > MetadataScannerSearchRefreshInterval)
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
                    catch (Exception ex)
                    {
                        DebugLog.Write($"Metadata scanner worker failed: {ex.Message}");
                    }
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

    private async Task<bool> ApplyWarmMetadataEntriesAsync(
        List<ImageItem> items,
        Dictionary<string, MetadataIndexEntry> cachedEntries,
        CancellationToken token,
        int scannerGeneration)
    {
        for (var startIndex = 0; startIndex < items.Count; startIndex += WarmMetadataUiBatchSize)
        {
            token.ThrowIfCancellationRequested();
            var batchStartIndex = startIndex;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || scannerGeneration != _scannerGeneration)
                {
                    return;
                }

                var endIndex = Math.Min(items.Count, batchStartIndex + WarmMetadataUiBatchSize);
                for (var index = batchStartIndex; index < endIndex; index++)
                {
                    var item = items[index];
                    if (!item.HasLoadedMetadata && cachedEntries.TryGetValue(item.Path, out var entry))
                    {
                        item.ApplyMetadataEntry(entry);
                    }
                }
            }, DispatcherPriority.Background);

            if (token.IsCancellationRequested || scannerGeneration != _scannerGeneration)
            {
                return false;
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!token.IsCancellationRequested && scannerGeneration == _scannerGeneration && HasSearchQueryActive())
            {
                ApplyFilter();
            }
        }, DispatcherPriority.Background);

        return !token.IsCancellationRequested && scannerGeneration == _scannerGeneration;
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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && loadGeneration == _loadGeneration)
                {
                    ScheduleViewportThumbnails();
                }
            }, DispatcherPriority.Background);

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
        QueueMetadataCountTextUpdate();
    }

    private void QueueMetadataCountTextUpdate()
    {
        if (_metadataCountUpdateTimer is null)
        {
            _metadataCountUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = MetadataCountUpdateInterval
            };
            _metadataCountUpdateTimer.Tick += (s, e) =>
            {
                _metadataCountUpdateTimer.Stop();
                UpdateCountText();
            };
        }

        if (!_metadataCountUpdateTimer.IsEnabled)
        {
            _metadataCountUpdateTimer.Start();
        }
    }

    private void SearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        if (ClearSearchButton != null)
        {
            ClearSearchButton.IsVisible = hasText;
        }

        _hasSearchQueryActive = !string.IsNullOrWhiteSpace(SearchTextBox.Text);

        if (_searchDebounceTimer == null)
        {
            _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _searchDebounceTimer.Tick += (s, ev) =>
            {
                _searchDebounceTimer.Stop();
                ApplyFilter(resetScroll: true);
            };
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
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

    private void SearchHelpButton_Click(object? sender, RoutedEventArgs e)
    {
        SearchHelpPopup.IsOpen = !SearchHelpPopup.IsOpen;
        e.Handled = true;
    }

    private SearchScope GetSearchScope()
    {
        return SearchScopeComboBox.SelectedIndex switch
        {
            (int)SearchScope.PositivePrompt => SearchScope.PositivePrompt,
            (int)SearchScope.NegativePrompt => SearchScope.NegativePrompt,
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
            var scrollAnchor = resetScroll ? null : CaptureGalleryScrollAnchor();
            SynchronizeGalleryItems(filtered);
            GalleryItems.InvalidateMeasure();
            RestoreGalleryScrollAnchor(scrollAnchor, filtered);
        }

        if (resetScroll)
        {
            _galleryScrollRestoreGeneration++;
            GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, 0);
        }

        UpdateCountText();
        QueueViewportThumbnailSchedule();

        bool showEmpty = filtered.Count == 0 && _allImageItems.Count > 0;
        ToggleGalleryEmptyState(showEmpty);
    }

    private void SynchronizeGalleryItems(List<ImageItem> filtered)
    {
        if (!CanSynchronizeGalleryItemsIncrementally(_viewModel.Items, filtered, MaxIncrementalGalleryChanges))
        {
            _viewModel.Items.Clear();
            _viewModel.Items.AddRange(filtered);
            return;
        }

        var targetItems = new HashSet<ImageItem>(filtered);
        for (var index = 0; index < filtered.Count;)
        {
            var item = filtered[index];
            if (index == _viewModel.Items.Count)
            {
                _viewModel.Items.Insert(index, item);
                index++;
                continue;
            }

            if (ReferenceEquals(_viewModel.Items[index], item))
            {
                index++;
                continue;
            }

            if (!targetItems.Contains(_viewModel.Items[index]))
            {
                _viewModel.Items.RemoveAt(index);
                continue;
            }

            _viewModel.Items.Insert(index, item);
            index++;
        }

        while (_viewModel.Items.Count > filtered.Count)
        {
            _viewModel.Items.RemoveAt(_viewModel.Items.Count - 1);
        }
    }

    internal static bool CanSynchronizeGalleryItemsIncrementally(
        IReadOnlyList<ImageItem> currentItems,
        IReadOnlyList<ImageItem> targetItems,
        int maximumChanges)
    {
        if (maximumChanges < 0)
        {
            return false;
        }

        var targetSet = new HashSet<ImageItem>(targetItems);
        if (targetSet.Count != targetItems.Count)
        {
            return false;
        }

        var currentIndexes = new Dictionary<ImageItem, int>(currentItems.Count);
        var changeCount = 0;
        for (var index = 0; index < currentItems.Count; index++)
        {
            var item = currentItems[index];
            if (!currentIndexes.TryAdd(item, index))
            {
                return false;
            }

            if (!targetSet.Contains(item) && ++changeCount > maximumChanges)
            {
                return false;
            }
        }

        var lastCurrentIndex = -1;
        foreach (var item in targetItems)
        {
            if (currentIndexes.TryGetValue(item, out var currentIndex))
            {
                if (currentIndex < lastCurrentIndex)
                {
                    return false;
                }

                lastCurrentIndex = currentIndex;
            }
            else if (++changeCount > maximumChanges)
            {
                return false;
            }
        }

        // Small insert/delete batches keep realized cards stable. Larger changes use one reset.
        return true;
    }

    private GalleryScrollAnchor? CaptureGalleryScrollAnchor()
    {
        var offset = GalleryScrollViewer.Offset.Y;
        if (offset <= 0.5 || _viewModel.Items.Count == 0)
        {
            return null;
        }

        var columns = GetGalleryColumnCount();
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(offset / Math.Max(1, _tileItemExtent)));
        var index = Math.Min(_viewModel.Items.Count - 1, firstVisibleRow * columns);
        return new GalleryScrollAnchor(_viewModel.Items[index], index, offset);
    }

    private void RestoreGalleryScrollAnchor(GalleryScrollAnchor? anchor, List<ImageItem> filtered)
    {
        if (anchor is not { } value)
        {
            return;
        }

        var newIndex = filtered.IndexOf(value.Item);
        var columns = GetGalleryColumnCount();
        var desiredOffset = CalculateAnchoredGalleryOffset(
            value.OldIndex,
            newIndex,
            columns,
            _tileItemExtent,
            value.Offset,
            double.PositiveInfinity);

        // Uniform fixed-height rows make a row anchor sufficient. Variable-height tiles would need realized-element anchoring.
        QueueGalleryScrollRestore(desiredOffset);
    }

    private void QueueGalleryScrollRestore(double desiredOffset)
    {
        var restoreGeneration = ++_galleryScrollRestoreGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            ApplyGalleryScrollRestore(restoreGeneration, desiredOffset);
        }, DispatcherPriority.Loaded);
    }

    private void ApplyGalleryScrollRestore(int restoreGeneration, double desiredOffset)
    {
        if (restoreGeneration != _galleryScrollRestoreGeneration)
        {
            return;
        }

        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0
            ? GalleryScrollViewer.Viewport.Height
            : Math.Max(1, GalleryScrollViewer.Bounds.Height);
        var maxOffset = GalleryScrollViewer.Extent.Height > 0
            ? Math.Max(0, GalleryScrollViewer.Extent.Height - viewportHeight)
            : desiredOffset;
        var restoredOffset = Math.Clamp(desiredOffset, 0, maxOffset);
        GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, restoredOffset);
    }

    internal static double CalculateAnchoredGalleryOffset(
        int oldIndex,
        int newIndex,
        int columns,
        double itemExtent,
        double oldOffset,
        double maxOffset)
    {
        columns = Math.Max(1, columns);
        itemExtent = Math.Max(1, itemExtent);
        if (newIndex < 0)
        {
            return Math.Clamp(oldOffset, 0, maxOffset);
        }

        var offsetWithinRow = oldOffset - ((oldIndex / columns) * itemExtent);
        return Math.Clamp(((newIndex / columns) * itemExtent) + offsetWithinRow, 0, maxOffset);
    }

    internal static bool ItemMatchesSearch(
        ImageItem item,
        List<SearchTerm> positiveTerms,
        List<SearchTerm> negativeTerms,
        SearchScope searchScope)
    {
        return searchScope switch
        {
            SearchScope.Filename => TextMatchesTerms(item.FileName, positiveTerms, negativeTerms),
            SearchScope.PositivePrompt => item.HasLoadedMetadata
                ? TextMatchesTerms(item.Prompt, positiveTerms, negativeTerms)
                : true,
            SearchScope.NegativePrompt => item.HasLoadedMetadata
                ? TextMatchesTerms(item.NegativePrompt, positiveTerms, negativeTerms)
                : true,
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
            if (ItemMatchesAnySearchableText(item, term))
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

            if (!ItemMetadataMatchesTerm(item, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ItemMatchesAnySearchableText(ImageItem item, SearchTerm term)
    {
        return SearchEngine.IsMatch(item.FileName, term) ||
               item.HasLoadedMetadata && ItemMetadataMatchesTerm(item, term);
    }

    private static bool ItemMetadataMatchesTerm(ImageItem item, SearchTerm term)
    {
        return SearchEngine.IsMatch(item.Prompt, item.NegativePrompt, term) ||
               SearchEngine.IsSeparatorInsensitiveMatch(item.Tool, term) ||
               SearchEngine.IsSeparatorInsensitiveMatch(item.Model, term) ||
               SearchEngine.IsSeparatorInsensitiveMatch(item.Sampler, term) ||
               SearchEngine.IsSeparatorInsensitiveMatch(item.Seed, term) ||
               SearchEngine.IsSeparatorInsensitiveMatch(item.Settings, term) ||
               SearchEngine.IsSeparatorInsensitiveMatch(item.Lora, term) ||
               SearchEngine.IsSeparatorInsensitiveMatch(item.Resources, term);
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

        if (!isScanning)
        {
            _metadataCountUpdateTimer?.Stop();
        }

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

    private void ClearCacheButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _thumbnailLoads.Clear();
            ImageItem.ClearDeferredThumbnailCacheWrites();
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
        CancelAndDispose(ref _scannerCancellation);
        _scannerGeneration++;
        CancelAndDispose(ref _loadCancellation);
        _loadGeneration++;
        _thumbnailLoads.Clear();
        ImageItem.ClearDeferredThumbnailCacheWrites();
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


}
