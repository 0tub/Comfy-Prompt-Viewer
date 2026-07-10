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

    private readonly record struct PreviewZoomAnchor(double XRatio, double YRatio, double ViewportX, double ViewportY);

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

        if (_loadCancellation?.Token is { } token)
        {
            _selectedItem.EnsureSelectedPreviewLoaded(token);
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
        LargePreviewImage.Source = _selectedItem.SelectedPreview ?? _selectedItem.Preview;
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
        LargePreviewOverlay.IsVisible = false;
        LargePreviewOverlay.Opacity = 0;
        LargePreviewImage.Source = null;
        _largePreviewZoom = null;
        ResetLargePreviewPan();
        ApplyLargePreviewZoom(resetScroll: true);

        if (_selectedItem is not null)
        {
            var index = _viewModel.Items.IndexOf(_selectedItem);
            if (index >= 0 && GalleryItems.TryGetElement(index) is Control control)
            {
                control.Focus();
            }
        }
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

    private void PreviousPreviewButton_Click(object? sender, RoutedEventArgs e)
    {
        MoveLargePreviewSelection(-1);
    }

    private void NextPreviewButton_Click(object? sender, RoutedEventArgs e)
    {
        MoveLargePreviewSelection(1);
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

        LargePreviewImage.Stretch = Stretch.Fill;
        var contentSize = GetLargePreviewContentSize(bitmap);
        LargePreviewImage.Width = Math.Max(1, contentSize.Width);
        LargePreviewImage.Height = Math.Max(1, contentSize.Height);

        if (TryGetLargePreviewFitSize(out var viewportSize))
        {
            ApplyLargePreviewPlacement(contentSize, viewportSize);
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

    private Size GetLargePreviewContentSize(Bitmap bitmap)
    {
        var scale = _largePreviewZoom ?? (TryGetLargePreviewFitScale(bitmap, out var fitScale) ? fitScale : 1.0);
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
        Canvas.SetLeft(LargePreviewImage, Math.Round(_largePreviewPanX));
        Canvas.SetTop(LargePreviewImage, Math.Round(_largePreviewPanY));
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
