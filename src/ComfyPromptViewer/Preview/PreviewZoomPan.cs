using System;
using Avalonia;

namespace ComfyPromptViewer;

// Zoom and pan math for the large preview. Sizes come in as arguments, so none of this touches a control.
internal sealed class PreviewZoomPan
{
    public const double MinZoom = 0.10;
    public const double MaxZoom = 4.0;
    public const double WheelZoomFactor = 1.10;

    // Null means fit to the viewport.
    public double? Zoom { get; set; }
    public double PanX { get; set; }
    public double PanY { get; set; }
    public double? PinchStartZoom { get; set; }

    public readonly record struct Anchor(double XRatio, double YRatio, double ViewportX, double ViewportY);

    public void ResetPan()
    {
        PanX = 0;
        PanY = 0;
    }

    public static bool TryGetFitScale(PixelSize image, Size? viewport, out double scale)
    {
        if (image.Width <= 0 || image.Height <= 0 || viewport is not { } fit)
        {
            scale = 1.0;
            return false;
        }

        scale = Math.Min(fit.Width / image.Width, fit.Height / image.Height);
        return scale > 0 && !double.IsNaN(scale) && !double.IsInfinity(scale);
    }

    public double GetScale(PixelSize image, Size? viewport)
    {
        return Zoom ?? (TryGetFitScale(image, viewport, out var fitScale) ? fitScale : 1.0);
    }

    public Size GetContentSize(PixelSize image, Size? viewport)
    {
        var scale = GetScale(image, viewport);
        return new Size(image.Width * scale, image.Height * scale);
    }

    public bool TryGetCurrentZoom(PixelSize? image, Size? viewport, out double zoom)
    {
        if (Zoom is { } explicitZoom)
        {
            zoom = explicitZoom;
            return true;
        }

        if (image is { } size && TryGetFitScale(size, viewport, out var fitScale))
        {
            zoom = fitScale;
            return true;
        }

        zoom = 1.0;
        return false;
    }

    public static double GetMinimumZoom(PixelSize? image, Size? viewport)
    {
        if (image is { } size && TryGetFitScale(size, viewport, out var fitScale))
        {
            return Math.Clamp(Math.Min(fitScale, 1.0), MinZoom, MaxZoom);
        }

        return MinZoom;
    }

    public static double NextWheelZoom(double currentZoom, double wheelDelta)
    {
        var clampedDelta = Math.Clamp(wheelDelta, -3.0, 3.0);
        return currentZoom * Math.Pow(WheelZoomFactor, clampedDelta);
    }

    // "Fit 62%" while fitted, "100%" once zoomed; relative to the source's own pixels, not the decoded preview's.
    public string DescribeZoom(PixelSize? image, Size? viewport, int sourceWidth)
    {
        if (image is not { } size)
        {
            return "Fit";
        }

        var scale = Zoom ?? (TryGetFitScale(size, viewport, out var fitScale) ? fitScale : 1.0);
        var actualZoom = scale;
        if (sourceWidth > 0 && size.Width > 0)
        {
            actualZoom = scale * (size.Width / (double)sourceWidth);
        }

        var text = $"{Math.Round(actualZoom * 100):0}%";
        return Zoom is null ? $"Fit {text}" : text;
    }

    public Anchor? CaptureAnchor(Point? viewportPoint, PixelSize? image, Size? viewport)
    {
        if (image is not { } size || viewport is not { } view)
        {
            return null;
        }

        var contentSize = GetContentSize(size, view);
        var point = viewportPoint ?? new Point(view.Width / 2, view.Height / 2);
        var xRatio = contentSize.Width <= 0 ? 0.5 : (point.X - PanX) / contentSize.Width;
        var yRatio = contentSize.Height <= 0 ? 0.5 : (point.Y - PanY) / contentSize.Height;
        return new Anchor(
            Math.Clamp(xRatio, 0, 1),
            Math.Clamp(yRatio, 0, 1),
            Math.Clamp(point.X, 0, view.Width),
            Math.Clamp(point.Y, 0, view.Height));
    }

    public void RestoreAnchor(Anchor anchor, Size contentSize)
    {
        PanX = anchor.ViewportX - contentSize.Width * anchor.XRatio;
        PanY = anchor.ViewportY - contentSize.Height * anchor.YRatio;
    }

    public void ClampPan(Size contentSize, Size viewportSize)
    {
        PanX = ClampAxis(PanX, contentSize.Width, viewportSize.Width);
        PanY = ClampAxis(PanY, contentSize.Height, viewportSize.Height);
    }

    private static double ClampAxis(double pan, double contentLength, double viewportLength)
    {
        if (contentLength <= viewportLength)
        {
            return (viewportLength - contentLength) / 2;
        }

        return Math.Clamp(pan, viewportLength - contentLength, 0);
    }
}
