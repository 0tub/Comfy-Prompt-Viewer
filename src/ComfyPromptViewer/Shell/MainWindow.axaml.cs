using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public partial class MainWindow : Window
{
    private const double SidebarPreviewHeightWindowRatio = 0.4;
    private const double MinSidebarPreviewHeight = 180;
    private const double MaxSidebarPreviewHeight = 350;
    private static readonly TimeSpan TileSizeSaveInterval = TimeSpan.FromMilliseconds(250);
    private readonly GalleryViewModel _viewModel = new();
    private readonly UserPreferencesStore _preferences;
    private readonly DecodedImageCache _decodedImageCache;
    private readonly ThumbnailService _thumbnailService;
    private readonly ThumbnailLoadCoordinator _thumbnailLoads;
    private readonly MetadataRepository _metadataRepository;
    private readonly SelectedPreviewLoader _selectedPreviews = new();
    private readonly MetadataScanCoordinator _metadataScanner;
    private DispatcherTimer? _tileSizeSaveTimer;
    private TaskCompletionSource<bool>? _deleteConfirmationCompletion;
    private ImageItem? _queuedSelectedItemRefresh;
    // The folder the current search was typed for. Unlike the session's folder it survives Back, so reopening
    // the same folder keeps the search and only a different folder resets it.
    private string? _searchFolderKey;
    private double _targetTileSize;
    // Physical pixels per DIP for the display this window is on. Feeds the thumbnail decode bucket, so a
    // 120 DIP tile on a 200% monitor decodes 240px instead of a soft 180.
    private double _renderScaling = 1.0;
    private bool _isSidebarSplitterDragging;
    // Maximizing does not change Width/Height back to the restored size, so the last known normal bounds
    // are tracked separately; saving the maximized bounds would unmaximize into a full-screen-sized window.
    private Size _normalWindowSize;
    private PixelPoint _normalWindowPosition;

    public MainWindow()
    {
        _preferences = new UserPreferencesStore(AppPaths.LocalDataDirectory);
        _targetTileSize = TileSizeLimits.Normalize(_preferences.LoadTileSize() ?? TileSizeLimits.Default);
        _decodedImageCache = new DecodedImageCache();
        _thumbnailService = new ThumbnailService(AppPaths.ThumbnailCacheRoot(_preferences.LoadThumbnailCacheLocation()));
        _thumbnailLoads = new ThumbnailLoadCoordinator(_thumbnailService, _decodedImageCache);
        _metadataRepository = new MetadataRepository(AppPaths.LocalDataDirectory);
        _metadataScanner = new MetadataScanCoordinator(_metadataRepository);
        InitializeComponent();
        DataContext = _viewModel;
        _thumbnailLoads.VisibleWorkDrained = _thumbnailService.StartWriter;
        _thumbnailService.SetCacheWritePause(() => _thumbnailLoads.HasVisibleWork);

        InitializeLargePreview();
        InitializeSidebar();
        InitializeSession();
        InitializeStartMenu();
        InitializeHeader();
        InitializeGallery();

        SyncIncludeSubfoldersToggles();
        ApplyTileLayout();
        ThemeManager.Apply(_preferences.LoadThemeMode());

        SetSidebarWidth(SidebarWidthLimits.Normalize(_preferences.LoadSidebarWidth() ?? SidebarWidthLimits.Default));
        SidebarSplitter.DragStarted += SidebarSplitter_DragStarted;
        SidebarSplitter.DragCompleted += SidebarSplitter_DragCompleted;

        RestoreWindowPlacement();
        _renderScaling = GetCurrentRenderScaling();
        _session.RenderScaling = _renderScaling;
        Gallery.RenderScaling = _renderScaling;
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
        _session.RenderScaling = scaling;
        Gallery.RenderScaling = scaling;
        Gallery.QueueViewportSchedule(force: true);
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

    protected override void OnClosed(EventArgs e)
    {
        SaveWindowPlacement();
        CompleteDeleteConfirmation(false);
        Gallery.StopAutoScroll();
        LargePreview.StopPan();
        if (_tileSizeSaveTimer?.IsEnabled == true)
        {
            _tileSizeSaveTimer.Stop();
            _preferences.SaveTileSize(_targetTileSize);
        }
        _startMenu.Close();
        _header.Close();
        _session.Shutdown();
        _thumbnailService.Dispose();
        _metadataRepository.Dispose();
        base.OnClosed(e);
    }
}
