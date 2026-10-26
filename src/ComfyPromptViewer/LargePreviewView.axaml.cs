using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// The zoomable preview overlay. It owns everything that depends on the on-screen size: zoom, pan, gestures and
// the render transform. What to show comes from LargePreviewViewModel; which image is selected stays with the
// host window, which feeds it in through Show and Update.
public partial class LargePreviewView : UserControl
{
    private readonly LargePreviewViewModel _viewModel = new();
    private readonly PreviewZoomPan _zoom = new();
    private ImageItem? _item;
    private bool _needsFit;
    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartX;
    private double _panStartY;
    private IPointer? _panPointer;
    private Key _heldNavigationKey = Key.None;

    // Zoom and pan are a render transform rather than Width/Height plus Canvas.Left/Top. Resizing the Image
    // per wheel notch forced a layout pass and a fresh Skia resample of a multi-megapixel bitmap; a
    // transform is composited. Both transforms are created once and mutated, so a pointer-move drag does
    // not allocate.
    private readonly ScaleTransform _scaleTransform = new(1, 1);
    private readonly TranslateTransform _translateTransform = new();
    private bool _isTransformAttached;

    public LargePreviewView()
    {
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.VideoOpened += OnVideoOpened;
        _viewModel.VideoClosing += video =>
        {
            if (ReferenceEquals(PreviewImage.Source, video))
            {
                PreviewImage.Source = null;
            }
        };
        _viewModel.VideoFrameShown += () => PreviewImage.InvalidateVisual();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // The slider's thumb handles these itself, so listen for already-handled ones too.
        PositionSlider.AddHandler(PointerPressedEvent, (_, _) => _viewModel.IsScrubbing = true, RoutingStrategies.Tunnel, true);
        PositionSlider.AddHandler(PointerReleasedEvent, (_, _) => _viewModel.IsScrubbing = false, RoutingStrategies.Tunnel, true);
        PositionSlider.AddHandler(PointerCaptureLostEvent, (_, _) => _viewModel.IsScrubbing = false, RoutingStrategies.Tunnel, true);
    }

    public LargePreviewViewModel ViewModel => _viewModel;

    public bool IsOpen => IsVisible;

    // The context menu serves the host's selection and clipboard actions, so its events go to the host.
    public event EventHandler<CancelEventArgs>? ContextMenuOpening;
    public event EventHandler<RoutedEventArgs>? ContextMenuItemClicked;

    public void Show(ImageItem item, bool canGoPrevious, bool canGoNext)
    {
        Overlay.Opacity = 1;
        IsVisible = true;
        Update(item, resetZoom: true, canGoPrevious, canGoNext);
    }

    public void Update(ImageItem item, bool resetZoom, bool canGoPrevious, bool canGoNext)
    {
        _item = item;
        _needsFit |= resetZoom;
        _viewModel.SetNavigation(canGoPrevious, canGoNext);
        var loading = _viewModel.Present(item);
        if (ImageCanvas.ContextMenu is { } menu)
        {
            menu.IsEnabled = !loading;
            if (loading)
            {
                menu.Close();
            }
        }

        if (loading)
        {
            // Keep the outgoing full-quality image visible while the shared asynchronous decode runs.
            return;
        }

        var preview = _viewModel.VideoBitmap ?? item.SelectedPreview;
        if (!ReferenceEquals(PreviewImage.Source, preview))
        {
            PreviewImage.Source = preview;
        }

        if (_needsFit)
        {
            _zoom.Zoom = null;
        }

        var fit = _needsFit;
        _needsFit = false;
        ApplyZoom(resetScroll: fit);
        Dispatcher.UIThread.Post(() => ApplyZoom(resetScroll: fit), DispatcherPriority.Loaded);
    }

    public void Hide()
    {
        StopPan(releaseCapture: true);
        _heldNavigationKey = Key.None;
        IsVisible = false;
        Overlay.Opacity = 0;
        PreviewImage.Source = null;
        _viewModel.StopVideo();
        _zoom.Zoom = null;
        _zoom.ResetPan();
        ApplyZoom(resetScroll: true);

        // Keep the current gallery offset; focusing the selected card scrolls it back into view.
    }

    // The selection went away (a folder closed) while the overlay was up; a playing video must not keep going.
    public void StopVideo() => _viewModel.StopVideo();

    // Detaches the overlay from a bitmap that is about to be disposed.
    public void ClearImage()
    {
        PreviewImage.Source = null;
    }

    public void StopPan() => StopPan(releaseCapture: true);

    // Window resizes while fitted keep the image fitted.
    public void RefitIfFitted()
    {
        if (IsVisible && _zoom.Zoom is null)
        {
            ApplyZoom(resetScroll: false);
        }
    }

    internal bool HandleVideoKey(Key key, ImageItem? item) => _viewModel.HandleVideoKey(key, item);

    // The first press of a navigation key moves; the auto-repeat that follows is swallowed until it is released.
    public bool ConsumeNavigationKey(Key key, Func<bool> navigate)
    {
        if (_heldNavigationKey != Key.None)
        {
            return true;
        }

        _heldNavigationKey = key;
        return navigate();
    }

    public bool ReleaseNavigationKey(Key key)
    {
        if (IsVisible && key == _heldNavigationKey)
        {
            _heldNavigationKey = Key.None;
            return true;
        }

        return false;
    }

    private void OnVideoOpened(Bitmap video)
    {
        // The still preview may have been decoded at a different size; keep an explicit zoom's on-screen size.
        if (PreviewImage.Source is Bitmap still && _zoom.Zoom is { } zoom)
        {
            _zoom.Zoom = zoom * still.PixelSize.Width / video.PixelSize.Width;
        }

        PreviewImage.Source = video;
        ApplyZoom(resetScroll: false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LargePreviewViewModel.Position):
                PositionSlider.Value = _viewModel.Position;
                break;
            case nameof(LargePreviewViewModel.CanCopyNegativePrompt):
                CopyNegativeMenuItem.IsEnabled = _viewModel.CanCopyNegativePrompt;
                break;
        }
    }

    private void Overlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == Overlay)
        {
            _viewModel.CloseCommand.Execute(null);
        }
    }

    private void FitButton_Click(object? sender, RoutedEventArgs e)
    {
        _zoom.Zoom = null;
        _zoom.ResetPan();
        ApplyZoom(resetScroll: true);
    }

    private void ActualSizeButton_Click(object? sender, RoutedEventArgs e)
    {
        SetZoom(1.0);
    }

    private void ContextMenu_Opening(object? sender, CancelEventArgs e) => ContextMenuOpening?.Invoke(sender, e);

    private void ContextMenuItem_Click(object? sender, RoutedEventArgs e) => ContextMenuItemClicked?.Invoke(sender, e);

    private void PositionSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _viewModel.Seek(e.NewValue);
    }

    private void VolumeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _viewModel.SetVolume((int)Math.Round(e.NewValue));
    }

    private void Canvas_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!IsVisible || e.Delta.Y == 0 || GetFitSize() is null)
        {
            e.Handled = true;
            return;
        }

        ApplyWheelZoom(e.Delta.Y, e.GetPosition(ImageCanvas));
        e.Handled = true;
    }

    // Trackpad and touchscreen pinch. Scale is cumulative from the start of the gesture, so the zoom is
    // rebased against the zoom in force when the gesture began rather than compounded per event.
    private void Canvas_Pinch(object? sender, PinchEventArgs e)
    {
        if (!IsVisible || PreviewImage.Source is not Bitmap bitmap || GetFitSize() is not { } viewport)
        {
            return;
        }

        _zoom.PinchStartZoom ??= _zoom.GetScale(bitmap.PixelSize, viewport);

        var anchor = new Point(
            e.ScaleOrigin.X * viewport.Width,
            e.ScaleOrigin.Y * viewport.Height);

        SetZoom(_zoom.PinchStartZoom.Value * e.Scale, anchor);
        e.Handled = true;
    }

    private void Canvas_PinchEnded(object? sender, PinchEndedEventArgs e)
    {
        _zoom.PinchStartZoom = null;
        e.Handled = true;
    }

    private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ImageCanvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panStartPoint = e.GetPosition(ImageCanvas);
        _panStartX = _zoom.PanX;
        _panStartY = _zoom.PanY;
        _panPointer = e.Pointer;
        _panPointer.Capture(ImageCanvas);
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        var currentPoint = e.GetPosition(ImageCanvas);
        _zoom.PanX = _panStartX + currentPoint.X - _panStartPoint.X;
        _zoom.PanY = _panStartY + currentPoint.Y - _panStartPoint.Y;
        if (GetImageSize() is { } size && GetFitSize() is { } viewport)
        {
            ApplyPlacement(_zoom.GetContentSize(size, viewport), viewport);
        }

        e.Handled = true;
    }

    private void Canvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer == _panPointer)
        {
            StopPan(releaseCapture: true);
            e.Handled = true;
        }
    }

    private void Canvas_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Pointer == _panPointer)
        {
            StopPan(releaseCapture: false);
        }
    }

    private void ImageHost_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var contentWidth = e.NewSize.Width - ImageHost.Padding.Left - ImageHost.Padding.Right;
        var contentHeight = e.NewSize.Height - ImageHost.Padding.Top - ImageHost.Padding.Bottom;
        ImageCanvas.Width = Math.Max(0, contentWidth);
        ImageCanvas.Height = Math.Max(0, contentHeight);

        if (IsVisible)
        {
            ApplyZoom(resetScroll: false);
        }
    }

    private void ApplyWheelZoom(double wheelDelta, Point viewportPoint)
    {
        if (_zoom.TryGetCurrentZoom(GetImageSize(), GetFitSize(), out var currentZoom))
        {
            SetZoom(PreviewZoomPan.NextWheelZoom(currentZoom, wheelDelta), viewportPoint);
        }
    }

    private void SetZoom(double zoom, Point? viewportPoint = null)
    {
        var imageSize = GetImageSize();
        var fitSize = GetFitSize();
        var minimumZoom = PreviewZoomPan.GetMinimumZoom(imageSize, fitSize);
        var clampedZoom = Math.Clamp(zoom, minimumZoom, PreviewZoomPan.MaxZoom);

        if (imageSize is { } size &&
            PreviewZoomPan.TryGetFitScale(size, fitSize, out var fitScale) &&
            fitScale <= 1.0 &&
            clampedZoom <= fitScale + 0.001)
        {
            _zoom.Zoom = null;
            _zoom.ResetPan();
            ApplyZoom(resetScroll: false);
            return;
        }

        var anchor = _zoom.CaptureAnchor(viewportPoint, imageSize, fitSize);
        _zoom.Zoom = clampedZoom;
        ApplyZoom(resetScroll: false);
        RestoreAnchor(anchor);
    }

    private void RestoreAnchor(PreviewZoomPan.Anchor? anchor)
    {
        if (anchor is not { } value || !IsVisible || GetImageSize() is not { } size || GetFitSize() is not { } viewport)
        {
            return;
        }

        var contentSize = _zoom.GetContentSize(size, viewport);
        _zoom.RestoreAnchor(value, contentSize);
        ApplyPlacement(contentSize, viewport);
    }

    private void ApplyZoom(bool resetScroll)
    {
        if (resetScroll)
        {
            _zoom.ResetPan();
        }

        if (PreviewImage.Source is not Bitmap bitmap)
        {
            UpdateZoomText();
            return;
        }

        EnsureTransform();

        // The Image keeps its natural pixel size and never re-layouts; the scale transform does the zoom.
        PreviewImage.Stretch = Stretch.Fill;
        PreviewImage.Width = Math.Max(1, bitmap.PixelSize.Width);
        PreviewImage.Height = Math.Max(1, bitmap.PixelSize.Height);

        var viewport = GetFitSize();
        var scale = _zoom.GetScale(bitmap.PixelSize, viewport);
        _scaleTransform.ScaleX = scale;
        _scaleTransform.ScaleY = scale;

        if (viewport is { } view)
        {
            ApplyPlacement(_zoom.GetContentSize(bitmap.PixelSize, view), view);
        }

        UpdateZoomText();
    }

    private void UpdateZoomText()
    {
        _viewModel.SetZoomText(_zoom.DescribeZoom(GetImageSize(), GetFitSize(), _item?.Width ?? 0));
    }

    private void ApplyPlacement(Size contentSize, Size viewportSize)
    {
        _zoom.ClampPan(contentSize, viewportSize);
        _translateTransform.X = Math.Round(_zoom.PanX);
        _translateTransform.Y = Math.Round(_zoom.PanY);
    }

    private void EnsureTransform()
    {
        if (_isTransformAttached)
        {
            return;
        }

        // Scale before translate, so the pan stays in viewport pixels instead of image pixels, and the
        // top-left origin keeps the existing pan clamping math valid.
        PreviewImage.RenderTransformOrigin = RelativePoint.TopLeft;
        PreviewImage.RenderTransform = new TransformGroup
        {
            Children = { _scaleTransform, _translateTransform }
        };
        Canvas.SetLeft(PreviewImage, 0);
        Canvas.SetTop(PreviewImage, 0);
        _isTransformAttached = true;
    }

    private void StopPan(bool releaseCapture)
    {
        _isPanning = false;

        if (releaseCapture)
        {
            _panPointer?.Capture(null);
        }

        _panPointer = null;
    }

    private PixelSize? GetImageSize() => PreviewImage.Source is Bitmap bitmap ? bitmap.PixelSize : null;

    // The size ImageHost_SizeChanged assigned comes first: Bounds still hold the previous layout while
    // that handler runs, and fitting to them left the video sized for the area before its control bar appeared.
    private Size? GetFitSize()
    {
        var width = FirstUsableSize(
            ImageCanvas.Width,
            ImageCanvas.Bounds.Width,
            ImageHost.Bounds.Width - ImageHost.Padding.Left - ImageHost.Padding.Right);

        var height = FirstUsableSize(
            ImageCanvas.Height,
            ImageCanvas.Bounds.Height,
            ImageHost.Bounds.Height - ImageHost.Padding.Top - ImageHost.Padding.Bottom);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new Size(Math.Floor(width), Math.Floor(height));
    }

    private static double FirstUsableSize(params double[] values)
    {
        foreach (var value in values)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 8)
            {
                return value;
            }
        }

        return 0;
    }
}
