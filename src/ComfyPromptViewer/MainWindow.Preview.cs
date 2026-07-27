using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private const double PreviewMinZoom = 0.10;
    private const double PreviewMaxZoom = 4.0;
    private const double PreviewWheelZoomFactor = 1.10;
    private double? _largePreviewZoom;
    private double _largePreviewPanX;
    private double _largePreviewPanY;
    private bool _isLargePreviewPanning;
    private Point _largePreviewPanStartPoint;
    private double _largePreviewPanStartX;
    private double _largePreviewPanStartY;
    private IPointer? _largePreviewPanPointer;
    private Key _heldPreviewNavigationKey = Key.None;
    private double? _pinchStartZoom;

    // Zoom and pan are a render transform rather than Width/Height plus Canvas.Left/Top. Resizing the Image
    // per wheel notch forced a layout pass and a fresh Skia resample of a multi-megapixel bitmap; a
    // transform is composited. Both transforms are created once and mutated, so a pointer-move drag does
    // not allocate.
    private readonly ScaleTransform _largePreviewScaleTransform = new(1, 1);
    private readonly TranslateTransform _largePreviewTranslateTransform = new();
    private bool _isLargePreviewTransformAttached;

    private readonly record struct PreviewZoomAnchor(double XRatio, double YRatio, double ViewportX, double ViewportY);

    private void EnsureLargePreviewTransform()
    {
        if (_isLargePreviewTransformAttached)
        {
            return;
        }

        // Scale before translate, so the pan stays in viewport pixels instead of image pixels, and the
        // top-left origin keeps the existing pan clamping math valid.
        LargePreviewImage.RenderTransformOrigin = RelativePoint.TopLeft;
        LargePreviewImage.RenderTransform = new TransformGroup
        {
            Children = { _largePreviewScaleTransform, _largePreviewTranslateTransform }
        };
        Canvas.SetLeft(LargePreviewImage, 0);
        Canvas.SetTop(LargePreviewImage, 0);
        _isLargePreviewTransformAttached = true;
    }

    private void SidebarImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ShowLargePreview();
        }
    }

    private void ShowLargePreview()
    {
        if (_selectedItem is null)
        {
            return;
        }

        LargePreviewOverlay.Opacity = 1;
        LargePreviewOverlay.IsVisible = true;
        UpdateLargePreview(resetZoom: true);
    }

    private void UpdateLargePreview(bool resetZoom)
    {
        if (_selectedItem is null)
        {
            return;
        }

        _selectedItem.LoadSelectedPreviewSync();

        LargePreviewTitle.Text = _selectedItem.FileName;
        LargePreviewMeta.Text = _selectedItem.DimensionsText;
        var preview = _selectedItem.SelectedPreview;
        if (!ReferenceEquals(LargePreviewImage.Source, preview))
        {
            LargePreviewImage.Source = preview;
        }
        PreviewCopyNegativePromptMenuItem.IsEnabled = _selectedItem.HasNegativePrompt;
        UpdateLargePreviewNavigationButtons();

        if (resetZoom)
        {
            _largePreviewZoom = null;
        }

        ApplyLargePreviewZoom(resetScroll: resetZoom);
        Dispatcher.UIThread.Post(() => ApplyLargePreviewZoom(resetScroll: resetZoom), DispatcherPriority.Loaded);
    }

    private void LargePreviewOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == LargePreviewOverlay)
        {
            HideLargePreview();
        }
    }

    private void ClosePreviewButton_Click(object? sender, RoutedEventArgs e)
    {
        HideLargePreview();
    }

    private void HideLargePreview()
    {
        StopLargePreviewPan(releaseCapture: true);
        _heldPreviewNavigationKey = Key.None;
        LargePreviewOverlay.IsVisible = false;
        LargePreviewOverlay.Opacity = 0;
        LargePreviewImage.Source = null;
        _largePreviewZoom = null;
        ResetLargePreviewPan();
        ApplyLargePreviewZoom(resetScroll: true);

        // Keep the current gallery offset; focusing the selected card scrolls it back into view.
    }

    private void PreviewFitButton_Click(object? sender, RoutedEventArgs e)
    {
        _largePreviewZoom = null;
        ResetLargePreviewPan();
        ApplyLargePreviewZoom(resetScroll: true);
    }

    private void PreviewActualSizeButton_Click(object? sender, RoutedEventArgs e)
    {
        SetLargePreviewZoom(1.0);
    }

    private void PreviewNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        MoveLargePreviewSelection(sender == PreviousPreviewButton ? -1 : 1);
    }

    private void MoveLargePreviewSelection(int delta)
    {
        if (_selectedItem is null)
        {
            return;
        }

        var currentIndex = _viewModel.Items.IndexOf(_selectedItem);
        var nextIndex = currentIndex + delta;
        if (nextIndex < 0 || nextIndex >= _viewModel.Items.Count)
        {
            return;
        }

        SelectByIndex(nextIndex);
    }

    private bool MoveLargePreviewSelectionFromKey(Key key, int delta)
    {
        if (_selectedItem is null || _viewModel.Items.Count == 0)
        {
            return false;
        }

        if (_heldPreviewNavigationKey == Key.None)
        {
            _heldPreviewNavigationKey = key;
            return MoveSelection(delta);
        }

        return true;
    }

    private void UpdateLargePreviewNavigationButtons()
    {
        var selectedIndex = _selectedItem is null ? -1 : _viewModel.Items.IndexOf(_selectedItem);
        PreviousPreviewButton.IsEnabled = selectedIndex > 0;
        NextPreviewButton.IsEnabled = selectedIndex >= 0 && selectedIndex < _viewModel.Items.Count - 1;
    }

    private void LargePreviewCanvas_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!LargePreviewOverlay.IsVisible ||
            e.Delta.Y == 0 ||
            !TryGetLargePreviewFitSize(out _))
        {
            e.Handled = true;
            return;
        }

        ApplyLargePreviewWheelZoom(e.Delta.Y, e.GetPosition(LargePreviewCanvas));
        e.Handled = true;
    }

    // Trackpad and touchscreen pinch. Scale is cumulative from the start of the gesture, so the zoom is
    // rebased against the zoom in force when the gesture began rather than compounded per event.
    private void LargePreviewCanvas_Pinch(object? sender, PinchEventArgs e)
    {
        if (!LargePreviewOverlay.IsVisible ||
            LargePreviewImage.Source is not Bitmap bitmap ||
            !TryGetLargePreviewFitSize(out var viewportSize))
        {
            return;
        }

        _pinchStartZoom ??= GetLargePreviewScale(bitmap);

        var anchor = new Point(
            e.ScaleOrigin.X * viewportSize.Width,
            e.ScaleOrigin.Y * viewportSize.Height);

        SetLargePreviewZoom(_pinchStartZoom.Value * e.Scale, anchor);
        e.Handled = true;
    }

    private void LargePreviewCanvas_PinchEnded(object? sender, PinchEndedEventArgs e)
    {
        _pinchStartZoom = null;
        e.Handled = true;
    }

    private void LargePreviewCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(LargePreviewCanvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isLargePreviewPanning = true;
        _largePreviewPanStartPoint = e.GetPosition(LargePreviewCanvas);
        _largePreviewPanStartX = _largePreviewPanX;
        _largePreviewPanStartY = _largePreviewPanY;
        _largePreviewPanPointer = e.Pointer;
        _largePreviewPanPointer.Capture(LargePreviewCanvas);
        e.Handled = true;
    }

    private void LargePreviewCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isLargePreviewPanning)
        {
            return;
        }

        var currentPoint = e.GetPosition(LargePreviewCanvas);
        _largePreviewPanX = _largePreviewPanStartX + currentPoint.X - _largePreviewPanStartPoint.X;
        _largePreviewPanY = _largePreviewPanStartY + currentPoint.Y - _largePreviewPanStartPoint.Y;
        ApplyLargePreviewPlacementFromCurrentState();
        e.Handled = true;
    }

    private void LargePreviewCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer == _largePreviewPanPointer)
        {
            StopLargePreviewPan(releaseCapture: true);
            e.Handled = true;
        }
    }

    private void LargePreviewCanvas_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Pointer == _largePreviewPanPointer)
        {
            StopLargePreviewPan(releaseCapture: false);
        }
    }

    private void LargePreviewImageHost_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var contentWidth = e.NewSize.Width - LargePreviewImageHost.Padding.Left - LargePreviewImageHost.Padding.Right;
        var contentHeight = e.NewSize.Height - LargePreviewImageHost.Padding.Top - LargePreviewImageHost.Padding.Bottom;
        LargePreviewCanvas.Width = Math.Max(0, contentWidth);
        LargePreviewCanvas.Height = Math.Max(0, contentHeight);

        if (LargePreviewOverlay.IsVisible)
        {
            ApplyLargePreviewZoom(resetScroll: false);
        }
    }

    private void ApplyLargePreviewWheelZoom(double wheelDelta, Point viewportPoint)
    {
        if (!TryGetCurrentLargePreviewZoom(out var currentZoom))
        {
            return;
        }

        var clampedDelta = Math.Clamp(wheelDelta, -3.0, 3.0);
        var nextZoom = currentZoom * Math.Pow(PreviewWheelZoomFactor, clampedDelta);
        SetLargePreviewZoom(nextZoom, viewportPoint);
    }

    private void SetLargePreviewZoom(double zoom, Point? viewportPoint = null)
    {
        var minimumZoom = GetLargePreviewMinimumZoom();
        var clampedZoom = Math.Clamp(zoom, minimumZoom, PreviewMaxZoom);

        if (LargePreviewImage.Source is Bitmap bitmap &&
            TryGetLargePreviewFitScale(bitmap, out var fitScale) &&
            fitScale <= 1.0 &&
            clampedZoom <= fitScale + 0.001)
        {
            _largePreviewZoom = null;
            ResetLargePreviewPan();
            ApplyLargePreviewZoom(resetScroll: false);
            return;
        }

        var anchor = CaptureLargePreviewZoomAnchor(viewportPoint);
        _largePreviewZoom = clampedZoom;
        ApplyLargePreviewZoom(resetScroll: false);
        RestoreLargePreviewZoomAnchor(anchor);
    }

    private void ApplyLargePreviewZoom(bool resetScroll)
    {
        if (resetScroll)
        {
            ResetLargePreviewPan();
        }

        if (LargePreviewImage.Source is not Bitmap bitmap)
        {
            UpdateLargePreviewMeta();
            return;
        }

        EnsureLargePreviewTransform();

        // The Image keeps its natural pixel size and never re-layouts; the scale transform does the zoom.
        LargePreviewImage.Stretch = Stretch.Fill;
        LargePreviewImage.Width = Math.Max(1, bitmap.PixelSize.Width);
        LargePreviewImage.Height = Math.Max(1, bitmap.PixelSize.Height);

        var scale = GetLargePreviewScale(bitmap);
        _largePreviewScaleTransform.ScaleX = scale;
        _largePreviewScaleTransform.ScaleY = scale;

        if (TryGetLargePreviewFitSize(out var viewportSize))
        {
            ApplyLargePreviewPlacement(GetLargePreviewContentSize(bitmap), viewportSize);
        }

        UpdateLargePreviewMeta();
    }

    private void UpdateLargePreviewMeta()
    {
        if (_selectedItem is null)
        {
            LargePreviewMeta.Text = "";
            return;
        }

        string zoomText = "Fit";
        if (LargePreviewImage.Source is Bitmap bitmap)
        {
            var scale = _largePreviewZoom ?? (TryGetLargePreviewFitScale(bitmap, out var fitScale) ? fitScale : 1.0);
            
            double actualZoom = scale;
            if (_selectedItem.Width > 0 && bitmap.PixelSize.Width > 0)
            {
                actualZoom = scale * (bitmap.PixelSize.Width / (double)_selectedItem.Width);
            }

            zoomText = _largePreviewZoom is null
                ? $"Fit {FormatPreviewZoom(actualZoom)}"
                : FormatPreviewZoom(actualZoom);
        }

        LargePreviewMeta.Text = $"{_selectedItem.DimensionsText} - {zoomText}";
    }

    private bool TryGetCurrentLargePreviewZoom(out double zoom)
    {
        if (_largePreviewZoom is { } explicitZoom)
        {
            zoom = explicitZoom;
            return true;
        }

        if (LargePreviewImage.Source is Bitmap bitmap && TryGetLargePreviewFitScale(bitmap, out var fitScale))
        {
            zoom = fitScale;
            return true;
        }

        zoom = 1.0;
        return false;
    }

    private double GetLargePreviewMinimumZoom()
    {
        if (LargePreviewImage.Source is Bitmap bitmap &&
            TryGetLargePreviewFitScale(bitmap, out var fitScale))
        {
            return Math.Clamp(Math.Min(fitScale, 1.0), PreviewMinZoom, PreviewMaxZoom);
        }

        return PreviewMinZoom;
    }

    private static string FormatPreviewZoom(double zoom)
    {
        return $"{Math.Round(zoom * 100):0}%";
    }

    private PreviewZoomAnchor? CaptureLargePreviewZoomAnchor(Point? viewportPoint = null)
    {
        if (!TryGetLargePreviewContentSize(out var contentSize) ||
            !TryGetLargePreviewFitSize(out var viewportSize))
        {
            return null;
        }

        var point = viewportPoint ?? new Point(viewportSize.Width / 2, viewportSize.Height / 2);
        var contentX = point.X - _largePreviewPanX;
        var contentY = point.Y - _largePreviewPanY;
        var xRatio = contentSize.Width <= 0 ? 0.5 : contentX / contentSize.Width;
        var yRatio = contentSize.Height <= 0 ? 0.5 : contentY / contentSize.Height;
        return new PreviewZoomAnchor(
            Math.Clamp(xRatio, 0, 1),
            Math.Clamp(yRatio, 0, 1),
            Math.Clamp(point.X, 0, viewportSize.Width),
            Math.Clamp(point.Y, 0, viewportSize.Height));
    }

    private void RestoreLargePreviewZoomAnchor(PreviewZoomAnchor? anchor)
    {
        if (anchor is null)
        {
            return;
        }

        if (!LargePreviewOverlay.IsVisible ||
            !TryGetLargePreviewContentSize(out var contentSize) ||
            !TryGetLargePreviewFitSize(out var viewportSize))
        {
            return;
        }

        _largePreviewPanX = anchor.Value.ViewportX - contentSize.Width * anchor.Value.XRatio;
        _largePreviewPanY = anchor.Value.ViewportY - contentSize.Height * anchor.Value.YRatio;
        ApplyLargePreviewPlacement(contentSize, viewportSize);
    }

    private bool TryGetLargePreviewContentSize(out Size contentSize)
    {
        if (LargePreviewImage.Source is not Bitmap bitmap)
        {
            contentSize = default;
            return false;
        }

        contentSize = GetLargePreviewContentSize(bitmap);
        return true;
    }

    private double GetLargePreviewScale(Bitmap bitmap)
    {
        return _largePreviewZoom ?? (TryGetLargePreviewFitScale(bitmap, out var fitScale) ? fitScale : 1.0);
    }

    private Size GetLargePreviewContentSize(Bitmap bitmap)
    {
        var scale = GetLargePreviewScale(bitmap);
        return new Size(bitmap.PixelSize.Width * scale, bitmap.PixelSize.Height * scale);
    }

    private void ApplyLargePreviewPlacementFromCurrentState()
    {
        if (TryGetLargePreviewContentSize(out var contentSize) &&
            TryGetLargePreviewFitSize(out var viewportSize))
        {
            ApplyLargePreviewPlacement(contentSize, viewportSize);
        }
    }

    private void ApplyLargePreviewPlacement(Size contentSize, Size viewportSize)
    {
        ClampLargePreviewPan(contentSize, viewportSize);
        _largePreviewTranslateTransform.X = Math.Round(_largePreviewPanX);
        _largePreviewTranslateTransform.Y = Math.Round(_largePreviewPanY);
    }

    private void ClampLargePreviewPan(Size contentSize, Size viewportSize)
    {
        _largePreviewPanX = ClampPreviewAxis(_largePreviewPanX, contentSize.Width, viewportSize.Width);
        _largePreviewPanY = ClampPreviewAxis(_largePreviewPanY, contentSize.Height, viewportSize.Height);
    }

    private static double ClampPreviewAxis(double pan, double contentLength, double viewportLength)
    {
        if (contentLength <= viewportLength)
        {
            return (viewportLength - contentLength) / 2;
        }

        return Math.Clamp(pan, viewportLength - contentLength, 0);
    }

    private void ResetLargePreviewPan()
    {
        _largePreviewPanX = 0;
        _largePreviewPanY = 0;
    }

    private void StopLargePreviewPan(bool releaseCapture)
    {
        _isLargePreviewPanning = false;

        if (releaseCapture)
        {
            _largePreviewPanPointer?.Capture(null);
        }

        _largePreviewPanPointer = null;
    }

    private bool TryGetLargePreviewFitScale(Bitmap bitmap, out double scale)
    {
        if (bitmap.PixelSize.Width <= 0 ||
            bitmap.PixelSize.Height <= 0 ||
            !TryGetLargePreviewFitSize(out var fitSize))
        {
            scale = 1.0;
            return false;
        }

        scale = Math.Min(fitSize.Width / bitmap.PixelSize.Width, fitSize.Height / bitmap.PixelSize.Height);
        return scale > 0 && !double.IsNaN(scale) && !double.IsInfinity(scale);
    }

    private bool TryGetLargePreviewFitSize(out Size fitSize)
    {
        var width = FirstUsableSize(
            LargePreviewCanvas.Bounds.Width,
            LargePreviewImageHost.Bounds.Width - LargePreviewImageHost.Padding.Left - LargePreviewImageHost.Padding.Right);

        var height = FirstUsableSize(
            LargePreviewCanvas.Bounds.Height,
            LargePreviewImageHost.Bounds.Height - LargePreviewImageHost.Padding.Top - LargePreviewImageHost.Padding.Bottom);

        if (width <= 0 || height <= 0)
        {
            fitSize = default;
            return false;
        }

        fitSize = new Size(Math.Floor(width), Math.Floor(height));
        return true;
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
