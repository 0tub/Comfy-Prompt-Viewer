using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private bool _isAutoScrolling;
    private bool _hasDragged;
    private Point _autoScrollAnchor;
    private Point _currentPointerPosition;
    private IPointer? _capturedPointer;
    private TimeSpan _lastFrameTime;
    private bool _isFirstFrame;
    private double _smoothedVelocity;
    private readonly double[] _dtHistory = new double[6];
    private int _dtHistoryIndex;

    private void GalleryScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;

        if (_isAutoScrolling)
        {
            StopAutoScroll();
            e.Handled = true;
            return;
        }

        if (properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            _isAutoScrolling = true;
            _hasDragged = false;
            _autoScrollAnchor = e.GetPosition(this);
            _currentPointerPosition = _autoScrollAnchor;
            _capturedPointer = e.Pointer;
            _capturedPointer.Capture(GalleryScrollViewer);

            this.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

            _isFirstFrame = true;
            RequestNextScrollFrame();

            e.Handled = true;
        }
    }

    private void RequestNextScrollFrame()
    {
        if (!_isAutoScrolling) return;

        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(time =>
        {
            if (!_isAutoScrolling) return;

            ProcessAutoScrollFrame(time);
            RequestNextScrollFrame();
        });
    }

    private void ProcessAutoScrollFrame(TimeSpan time)
    {
        if (_isFirstFrame)
        {
            _lastFrameTime = time;
            _isFirstFrame = false;
            _smoothedVelocity = 0;
            Array.Clear(_dtHistory, 0, _dtHistory.Length);
            return;
        }

        double rawDt = (time - _lastFrameTime).TotalSeconds;
        _lastFrameTime = time;

        if (rawDt <= 0 || rawDt > 0.1)
        {
            return;
        }

        double dt = GetSmoothedDt(rawDt);

        double deltaY = _currentPointerPosition.Y - _autoScrollAnchor.Y;
        double absDeltaY = Math.Abs(deltaY);

        double targetVelocity = 0;
        if (absDeltaY >= 12)
        {
            double distance = absDeltaY - 12;
            targetVelocity = Math.Sign(deltaY) * Math.Pow(distance, 1.5) * 4.0;
        }

        double easeAmount = 1.0 - Math.Exp(-12.0 * dt);
        _smoothedVelocity = Lerp(_smoothedVelocity, targetVelocity, easeAmount);

        if (targetVelocity == 0 && Math.Abs(_smoothedVelocity) < 0.5)
        {
            _smoothedVelocity = 0;
        }

        if (_smoothedVelocity == 0)
        {
            return;
        }

        double currentOffset = GalleryScrollViewer.Offset.Y;
        double maxOffset = Math.Max(0, GalleryScrollViewer.Extent.Height - GalleryScrollViewer.Viewport.Height);
        double nextOffset = Math.Clamp(currentOffset + (_smoothedVelocity * dt), 0, maxOffset);

        GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, nextOffset);
    }

    private double GetSmoothedDt(double dt)
    {
        _dtHistory[_dtHistoryIndex] = dt;
        _dtHistoryIndex = (_dtHistoryIndex + 1) % _dtHistory.Length;

        double sum = 0;
        int count = 0;
        foreach (var val in _dtHistory)
        {
            if (val > 0)
            {
                sum += val;
                count++;
            }
        }
        return count > 0 ? sum / count : dt;
    }

    private double Lerp(double start, double end, double amount)
    {
        return start + (end - start) * amount;
    }

    private void GalleryScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isAutoScrolling)
        {
            _currentPointerPosition = e.GetPosition(this);
            var delta = _currentPointerPosition - _autoScrollAnchor;
            if (Math.Abs(delta.X) > 6 || Math.Abs(delta.Y) > 6)
            {
                _hasDragged = true;
            }
        }
    }

    private void GalleryScrollViewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isAutoScrolling)
        {
            var properties = e.GetCurrentPoint(this).Properties;
            if (properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
            {
                if (_hasDragged)
                {
                    StopAutoScroll();
                }
                e.Handled = true;
            }
        }
    }

    private void GalleryScrollViewer_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        StopAutoScroll();
    }

    private void StopAutoScroll()
    {
        if (_isAutoScrolling)
        {
            _isAutoScrolling = false;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;

            this.Cursor = null;

            _smoothedVelocity = 0;
            Array.Clear(_dtHistory, 0, _dtHistory.Length);
        }
    }
}
