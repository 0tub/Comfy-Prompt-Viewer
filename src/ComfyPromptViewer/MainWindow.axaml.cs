using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
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
    // The sidebar width is dragged by the user and persisted; the ratio is only the ceiling that keeps a
    // narrow window from handing the whole client area to the sidebar.
    private const double MaxSidebarWidthWindowRatio = 0.45;
    private const double DefaultSidebarWidth = 380;
    private const double MinSidebarWidth = 260;
    private const double MaxSidebarWidth = 560;
    private const double SidebarPreviewHeightWindowRatio = 0.4;
    private const double MinSidebarPreviewHeight = 180;
    private const double MaxSidebarPreviewHeight = 350;
    private const double CollapsedPositivePromptMaxHeight = 168;
    private const int LongPositivePromptCharacterThreshold = 500;
    private const int LongPositivePromptLineThreshold = 7;
    private static readonly TimeSpan InitialMetadataScannerPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MetadataCountUpdateInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TileSizeSaveInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TransientStatusDuration = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan TransientStatusFadeDuration = TimeSpan.FromMilliseconds(120);
    private const int InitialMetadataScannerMaxPolls = 15;
    // How long a metadata scan has to run before its progress is worth showing. Warm folders finish first.
    private const long ScanProgressVisibleDelayMs = 250;
    private const int MaxIncrementalGalleryChanges = 32;
    private const int AheadRowsInScrollDirection = 8;
    private const int AheadRowsAgainstScrollDirection = 2;
    private const int PrewarmQueueHighWaterMark = 256;
    private const int PrewarmProgressBatch = 32;
    private static readonly TimeSpan PrewarmQueuePollInterval = TimeSpan.FromMilliseconds(250);
    private readonly GalleryViewModel _viewModel = new();
    private readonly UserPreferencesStore _preferences;
    private readonly DecodedImageCache _decodedImageCache;
    private readonly ThumbnailService _thumbnailService;
    private readonly ThumbnailLoadCoordinator _thumbnailLoads;
    private readonly MetadataRepository _metadataRepository;
    private readonly ImageMetadataService _metadataService;
    private readonly MetadataScanCoordinator _metadataScanner;
    private readonly SessionGate _folderLoader = new();
    private readonly GalleryCatalog _catalog = new();
    private readonly HashSet<ImageItem> _selectedItems = [];
    private readonly List<ImageItem> _visibleThumbnailScheduleItems = [];
    private readonly List<ImageItem> _aheadThumbnailScheduleItems = [];
    // Staleness lives in the two primitives from Staleness.cs. Folder-load, metadata-scan, and
    // thumbnail-load staleness belong to their coordinators; these four are the UI-side gates. Do not add
    // a bare int counter or a hand-managed CancellationTokenSource.
    private readonly SessionGate _advancedMaintenanceStatusGate = new();
    private readonly SessionGate _folderCacheStatusGate = new();
    private readonly SessionGate _searchFilterGate = new();
    private readonly GenerationGate _galleryScrollRestoreGate = new();
    private readonly GenerationGate _galleryEmptyStateGate = new();
    private DispatcherTimer? _searchDebounceTimer;
    private DispatcherTimer? _metadataCountUpdateTimer;
    private DispatcherTimer? _tileSizeSaveTimer;
    private ImageItem? _selectedItem;
    private ImageItem? _sidebarPreviewOwner;
    private ImageItem? _selectionAnchor;
    private TaskCompletionSource<bool>? _deleteConfirmationCompletion;
    private ImageItem? _queuedSelectedItemRefresh;
    private SortMode _sortMode = SortMode.NewestFirst;
    private ThemeMode _themeMode;
    private string? _currentFolderPath;
    // The thumbnail cache namespace for the open folder. Every ImageItem created while it is set - initial
    // load and watcher additions alike - captures it.
    private ThumbnailFolderScope? _folderCacheScope;
    private bool _includeSubfolders;
    private bool _prewarmThumbnails;
    private int _prewarmRemaining;
    private int _prewarmTotal;
    private long _metadataScanStartedAt;
    private double _targetTileSize;
    private double _tileSize;
    private double _tileItemExtent;
    // Physical pixels per DIP for the display this window is on. Feeds the thumbnail decode bucket, so a
    // 120 DIP tile on a 200% monitor decodes 240px instead of a soft 180.
    private double _renderScaling = 1.0;
    private bool _isInitializing = true;
    private bool _isViewportThumbnailScheduleQueued;
    private bool _thumbnailCacheClearInProgress;
    private int _lastPrefetchFirstVisibleRow = -1;
    private int _prefetchDirection = 1;
    private volatile bool _hasSearchQueryActive;
    private SelectableTextBlock? _activeContextMenuPromptText;
    private bool _isPositivePromptExpanded;
    private bool _isNegativePromptExpanded;
    private bool _isSidebarSplitterDragging;
    // Maximizing does not change Width/Height back to the restored size, so the last known normal bounds
    // are tracked separately; saving the maximized bounds would unmaximize into a full-screen-sized window.
    private Size _normalWindowSize;
    private PixelPoint _normalWindowPosition;

    public MainWindow()
    {
        _preferences = new UserPreferencesStore(AppPaths.LocalDataDirectory);
        _themeMode = _preferences.LoadThemeMode();
        _includeSubfolders = _preferences.LoadIncludeSubfolders();
        _prewarmThumbnails = _preferences.LoadPrewarmThumbnails();
        _targetTileSize = _preferences.LoadTileSize(DefaultTileSize, MinTileSize, MaxTileSize);
        _decodedImageCache = new DecodedImageCache();
        _thumbnailService = new ThumbnailService(AppPaths.LocalDataDirectory);
        _thumbnailLoads = new ThumbnailLoadCoordinator(_decodedImageCache);
        _metadataRepository = new MetadataRepository(AppPaths.LocalDataDirectory);
        _metadataService = new ImageMetadataService(_metadataRepository);
        _metadataScanner = new MetadataScanCoordinator(_metadataRepository);
        InitializeComponent();
        DataContext = _viewModel;
        _thumbnailLoads.VisibleWorkDrained = _thumbnailService.ResumeDeferredWrites;
        _thumbnailService.SetCacheWritePause(() => _thumbnailLoads.HasVisibleWork);

        GalleryScrollViewer.AddHandler(InputElement.PointerPressedEvent, GalleryScrollViewer_PointerPressed, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerMovedEvent, GalleryScrollViewer_PointerMoved, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerReleasedEvent, GalleryScrollViewer_PointerReleased, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerCaptureLostEvent, GalleryScrollViewer_PointerCaptureLost, RoutingStrategies.Bubble, true);
        GalleryScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, GalleryScrollViewer_PointerWheelChanged, RoutingStrategies.Tunnel, true);
        GalleryItems.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble, true);
        GalleryItems.ElementPrepared += GalleryItems_ElementPrepared;
        GalleryItems.ElementClearing += GalleryItems_ElementClearing;

        _tileSize = _targetTileSize;
        _tileItemExtent = _tileSize + TileGap;
        TileSizeSlider.Minimum = MinTileSize;
        TileSizeSlider.Maximum = MaxTileSize;
        TileSizeSlider.TickFrequency = TileSizeStep;
        TileSizeSlider.Value = _targetTileSize;
        SyncIncludeSubfoldersToggles();
        PrewarmThumbnailsToggle.IsChecked = _prewarmThumbnails;
        ApplyTileLayout();
        SortComboBox.SelectedIndex = (int)_sortMode;
        SearchScopeComboBox.SelectedIndex = (int)SearchScope.All;
        SearchHelpPopup.PlacementTarget = SearchHelpButton;
        ThemeManager.Apply(_themeMode);
        ThemeComboBox.SelectedIndex = (int)_themeMode;

        SetSidebarWidth(
            _preferences.LoadSidebarWidth(DefaultSidebarWidth, MinSidebarWidth, MaxSidebarWidth),
            persist: false);
        SidebarSplitter.DragStarted += SidebarSplitter_DragStarted;
        SidebarSplitter.DragCompleted += SidebarSplitter_DragCompleted;

        _isInitializing = false;
        RestoreWindowPlacement();
        _renderScaling = GetCurrentRenderScaling();
        this.ScalingChanged += Window_ScalingChanged;
        this.SizeChanged += Window_SizeChanged;
        this.SizeChanged += Window_SizeChangedTrackPlacement;
        this.PositionChanged += Window_PositionChanged;
        this.Opened += MainWindow_Opened;
    }

    private void RestoreWindowPlacement()
    {
        _normalWindowSize = new Size(Width, Height);

        if (_preferences.LoadWindowPlacement() is not { } placement)
        {
            return;
        }

        var width = Math.Max(MinWidth, placement.Width);
        var height = Math.Max(MinHeight, placement.Height);
        var position = new PixelPoint(placement.X, placement.Y);

        // A window restored onto a monitor that is no longer connected is invisible and unrecoverable
        // without editing the preference file, so an off-screen position falls back to the default.
        if (IsPlacementOnAScreen(position, width, height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = position;
            _normalWindowPosition = position;
        }

        Width = width;
        Height = height;
        _normalWindowSize = new Size(width, height);

        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private bool IsPlacementOnAScreen(PixelPoint position, double width, double height)
    {
        try
        {
            var screens = Screens;
            if (screens is null || screens.ScreenCount == 0)
            {
                return false;
            }

            // Scaling turns the DIP size into the pixel units Position and Screen.Bounds use.
            var scaling = screens.ScreenFromPoint(position)?.Scaling ?? 1.0;
            var windowBounds = new PixelRect(
                position,
                new PixelSize(
                    Math.Max(1, (int)Math.Round(width * scaling)),
                    Math.Max(1, (int)Math.Round(height * scaling))));

            foreach (var screen in screens.All)
            {
                if (screen.Bounds.Intersects(windowBounds))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to validate saved window placement: {ex.Message}");
        }

        return false;
    }

    private void Window_PositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _normalWindowPosition = e.Point;
        }
    }

    private double GetCurrentRenderScaling()
    {
        var scaling = RenderScaling;
        return double.IsFinite(scaling) && scaling > 0 ? scaling : 1.0;
    }

    // Dragging the window to a monitor with different DPI changes how many real pixels a tile occupies, so
    // the decode buckets have to be re-aligned and the viewport rescheduled at the new width.
    private void Window_ScalingChanged(object? sender, EventArgs e)
    {
        var scaling = GetCurrentRenderScaling();
        if (Math.Abs(_renderScaling - scaling) < 0.01)
        {
            return;
        }

        _renderScaling = scaling;
        QueueViewportThumbnailSchedule(force: true);
    }

    private void Window_SizeChangedTrackPlacement(object? sender, SizeChangedEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _normalWindowSize = e.NewSize;
        }
    }

    private void SaveWindowPlacement()
    {
        _preferences.SaveWindowPlacement(new WindowPlacement(
            _normalWindowSize.Width,
            _normalWindowSize.Height,
            _normalWindowPosition.X,
            _normalWindowPosition.Y,
            WindowState == WindowState.Maximized));
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
        _preferences.SaveThemeMode(_themeMode);
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowPlacement();
        CompleteDeleteConfirmation(false);
        StopAutoScroll();
        StopLargePreviewPan(releaseCapture: true);
        StopFolderWatcher();
        _searchDebounceTimer?.Stop();
        CancelSearchFilter();
        if (_tileSizeSaveTimer?.IsEnabled == true)
        {
            _tileSizeSaveTimer.Stop();
            _preferences.SaveTileSize(_targetTileSize);
        }
        _metadataScanner.Cancel();
        _folderLoader.Cancel();
        _advancedMaintenanceStatusGate.Cancel();
        _folderCacheStatusGate.Cancel();
        _thumbnailLoads.Clear();
        _thumbnailService.ClearDeferredWrites();
        _thumbnailService.SetCacheWritePause(null);
        SelectItem(null);
        ClearImageItems();
        _decodedImageCache.ClearAndReleaseAll();
        _thumbnailService.Dispose();
        _metadataRepository.Dispose();
        base.OnClosed(e);
    }
}
