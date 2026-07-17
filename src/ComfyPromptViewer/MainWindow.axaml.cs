using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private static readonly TimeSpan MetadataCountUpdateInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TileSizeSaveInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AdvancedMaintenanceStatusDuration = TimeSpan.FromSeconds(2.5);
    private const int InitialMetadataScannerMaxPolls = 15;
    private const int MaxIncrementalGalleryChanges = 32;
    private readonly GalleryViewModel _viewModel = new();
    private readonly ThumbnailLoadCoordinator _thumbnailLoads = new();
    private readonly MetadataScanCoordinator _metadataScanner = new();
    private readonly FolderLoadCoordinator _folderLoader = new();
    private readonly List<string> _allImagePaths = [];
    private readonly List<ImageItem> _allImageItems = [];
    private readonly Dictionary<string, DateTime> _imageLastWriteTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ImageItem> _visibleThumbnailScheduleItems = [];
    private readonly List<ImageItem> _aheadThumbnailScheduleItems = [];
    private CancellationTokenSource? _advancedMaintenanceStatusCancellation;
    private DispatcherTimer? _searchDebounceTimer;
    private DispatcherTimer? _metadataCountUpdateTimer;
    private DispatcherTimer? _tileSizeSaveTimer;
    private ImageItem? _selectedItem;
    private ImageItem? _queuedSelectedItemRefresh;
    private SortMode _sortMode = SortMode.NewestFirst;
    private ThemeMode _themeMode = UserPreferences.LoadThemeMode();
    private string? _currentFolderPath;
    private bool _includeSubfolders = UserPreferences.LoadIncludeSubfolders();
    private double _targetTileSize = UserPreferences.LoadTileSize(DefaultTileSize, MinTileSize, MaxTileSize);
    private double _tileSize;
    private double _tileItemExtent;
    private bool _isInitializing = true;
    private bool _isViewportThumbnailScheduleQueued;
    private bool _thumbnailCacheClearInProgress;
    private int _galleryScrollRestoreGeneration;
    private int _galleryEmptyStateGeneration;
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
        ThemeManager.Apply(_themeMode);
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
        ThemeManager.Apply(_themeMode);
        UserPreferences.SaveThemeMode(_themeMode);
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
        _metadataScanner.Cancel();
        _folderLoader.Cancel();
        CancelAndDispose(ref _advancedMaintenanceStatusCancellation);
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
}
