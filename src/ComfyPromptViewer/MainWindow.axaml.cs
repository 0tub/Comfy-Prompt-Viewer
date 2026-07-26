using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    private readonly FolderLoadCoordinator _folderLoader = new();
    private readonly GalleryCatalog _catalog = new();
    private readonly HashSet<ImageItem> _selectedItems = [];
    private readonly List<ImageItem> _visibleThumbnailScheduleItems = [];
    private readonly List<ImageItem> _aheadThumbnailScheduleItems = [];
    // Staleness lives in the two primitives from Staleness.cs. Folder-load, metadata-scan, and
    // thumbnail-load staleness belong to their coordinators; these four are the UI-side gates. Do not add
    // a bare int counter or a hand-managed CancellationTokenSource.
    private readonly SessionGate _advancedMaintenanceStatusGate = new();
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
    private bool _includeSubfolders;
    private bool _prewarmThumbnails;
    private int _prewarmRemaining;
    private int _prewarmTotal;
    private double _targetTileSize;
    private double _tileSize;
    private double _tileItemExtent;
    private bool _isInitializing = true;
    private bool _isViewportThumbnailScheduleQueued;
    private bool _thumbnailCacheClearInProgress;
    private int _lastPrefetchFirstVisibleRow = -1;
    private int _prefetchDirection = 1;
    private volatile bool _hasSearchQueryActive;
    private TextBox? _activeContextMenuTextBox;
    private bool _isPositivePromptExpanded;
    private bool _isNegativePromptExpanded;

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
        _metadataScanner = new MetadataScanCoordinator(_metadataRepository, new AvaloniaUiScheduler());
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
        
        SidebarPrompt.AddHandler(TextBox.CopyingToClipboardEvent, TextBox_CopyingToClipboard, RoutingStrategies.Bubble, true);
        SidebarNegativePrompt.AddHandler(TextBox.CopyingToClipboardEvent, TextBox_CopyingToClipboard, RoutingStrategies.Bubble, true);

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
        _preferences.SaveThemeMode(_themeMode);
    }

    protected override void OnClosed(EventArgs e)
    {
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
