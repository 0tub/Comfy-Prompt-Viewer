using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ComfyPromptViewer;

// Middle-click autoscroll: click to anchor, then the pointer's distance from the anchor sets the speed.
internal sealed class AutoScroller
{
    // Cursor owns a native handle and is IDisposable, so it is created once instead of per use.
    private static readonly Cursor ScrollCursor = new(StandardCursorType.SizeNorthSouth);

    private readonly Window _window;
    private readonly ScrollViewer _viewer;
    private readonly double[] _dtHistory = new double[6];
    private bool _isScrolling;
    private bool _hasDragged;
    private Point _anchor;
    private Point _pointerPosition;
    private IPointer? _capturedPointer;
    private TimeSpan _lastFrameTime;
    private bool _isFirstFrame;
    private double _smoothedVelocity;
    private int _dtHistoryIndex;

    public AutoScroller(Window window, ScrollViewer viewer)
    {
        _window = window;
        _viewer = viewer;
        viewer.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, true);
        viewer.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, true);
        viewer.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, true);
        viewer.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => Stop(), RoutingStrategies.Bubble, true);
    }

    public void Stop()
    {
        if (!_isScrolling)
        {
            return;
        }

        _isScrolling = false;
        _capturedPointer?.Capture(null);
        _capturedPointer = null;
        _window.Cursor = null;
        _smoothedVelocity = 0;
        Array.Clear(_dtHistory, 0, _dtHistory.Length);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isScrolling)
        {
            Stop();
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(_window).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed)
        {
            return;
        }

        _isScrolling = true;
        _hasDragged = false;
        _anchor = e.GetPosition(_window);
        _pointerPosition = _anchor;
        _capturedPointer = e.Pointer;
        _capturedPointer.Capture(_viewer);
        _window.Cursor = ScrollCursor;
        _isFirstFrame = true;
        RequestNextFrame();
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isScrolling)
        {
            return;
        }

        _pointerPosition = e.GetPosition(_window);
        var delta = _pointerPosition - _anchor;
        if (Math.Abs(delta.X) > 6 || Math.Abs(delta.Y) > 6)
        {
            _hasDragged = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isScrolling ||
            e.GetCurrentPoint(_window).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased)
        {
            return;
        }

        // A click without movement leaves autoscroll running until the next click; a drag ends it on release.
        if (_hasDragged)
        {
            Stop();
        }

        e.Handled = true;
    }

    private void RequestNextFrame()
    {
        if (!_isScrolling)
        {
            return;
        }

        TopLevel.GetTopLevel(_window)?.RequestAnimationFrame(time =>
        {
            if (!_isScrolling)
            {
                return;
            }

            ProcessFrame(time);
            RequestNextFrame();
        });
    }

    private void ProcessFrame(TimeSpan time)
    {
        if (_isFirstFrame)
        {
            _lastFrameTime = time;
            _isFirstFrame = false;
            _smoothedVelocity = 0;
            Array.Clear(_dtHistory, 0, _dtHistory.Length);
            return;
        }

        var rawDt = (time - _lastFrameTime).TotalSeconds;
        _lastFrameTime = time;

        if (rawDt <= 0 || rawDt > 0.1)
        {
            return;
        }

        var dt = GetSmoothedDt(rawDt);

        var deltaY = _pointerPosition.Y - _anchor.Y;
        var absDeltaY = Math.Abs(deltaY);

        double targetVelocity = 0;
        if (absDeltaY >= 12)
        {
            targetVelocity = Math.Sign(deltaY) * Math.Pow(absDeltaY - 12, 1.5) * 4.0;
        }

        var easeAmount = 1.0 - Math.Exp(-12.0 * dt);
        _smoothedVelocity += (targetVelocity - _smoothedVelocity) * easeAmount;

        if (targetVelocity == 0 && Math.Abs(_smoothedVelocity) < 0.5)
        {
            _smoothedVelocity = 0;
        }

        if (_smoothedVelocity == 0)
        {
            return;
        }

        var maxOffset = Math.Max(0, _viewer.Extent.Height - _viewer.Viewport.Height);
        var nextOffset = Math.Clamp(_viewer.Offset.Y + _smoothedVelocity * dt, 0, maxOffset);
        _viewer.Offset = new Vector(_viewer.Offset.X, nextOffset);
    }

    // A short moving average of frame times keeps the scroll speed steady when frames arrive unevenly.
    private double GetSmoothedDt(double dt)
    {
        _dtHistory[_dtHistoryIndex] = dt;
        _dtHistoryIndex = (_dtHistoryIndex + 1) % _dtHistory.Length;

        double sum = 0;
        var count = 0;
        foreach (var value in _dtHistory)
        {
            if (value > 0)
            {
                sum += value;
                count++;
            }
        }

        return count > 0 ? sum / count : dt;
    }
}
