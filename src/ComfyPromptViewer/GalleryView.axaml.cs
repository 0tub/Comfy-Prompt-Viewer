using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// The thumbnail grid: tile layout, scrolling, keeping your place when the list changes, which thumbnails to
// load for what is on screen, and the gestures on a card. Which images exist and which are selected is
// GalleryViewModel; this reports clicks to it and tells the window what the user opened.
public partial class GalleryView : UserControl
{
    private const double TileGap = 16;
    private const double WheelScrollRowsPerNotch = 1.7;
    private const double MinWheelScrollPixels = 180;
    private const double MaxWheelViewportRatio = 0.58;

    private readonly record struct ScrollAnchor(ImageItem Item, int OldIndex, double Offset);

    private readonly ViewportScheduler _scheduler = new();
    private readonly GalleryListSync _listSync = new();
    private readonly GenerationGate _scrollRestoreGate = new();
    private readonly GenerationGate _emptyStateGate = new();
    private GalleryViewModel _viewModel = null!;
    private ThumbnailLoadCoordinator _thumbnailLoads = null!;
    private FolderSession _session = null!;
    private AutoScroller? _autoScroller;
    private double _tileSize;
    private double _tileItemExtent;
    private bool _isViewportScheduleQueued;

    public GalleryView()
    {
        InitializeComponent();
    }

    // Physical pixels per DIP for the display the window is on. Feeds the thumbnail decode bucket, so a
    // 120 DIP tile on a 200% monitor decodes 240px instead of a soft 180.
    public double RenderScaling { get; set; } = 1.0;

    // Whether the large preview is up; Space and Enter on a card must leave it alone.
    internal Func<bool>? IsPreviewOpen { get; set; }

    // A card was double-clicked, or Space or Enter was pressed on it: the window opens the preview.
    internal event Action? PreviewRequested;
    // The items just changed (a filter or a file change landed), so the count line needs recomposing.
    internal event Action? ItemsApplied;
    // The context menu serves the window's selection and clipboard actions, so its events go to the window.
    internal event EventHandler<CancelEventArgs>? ContextMenuOpening;
    internal event EventHandler<RoutedEventArgs>? ContextMenuItemClicked;

    internal void Initialize(
        Window owner,
        GalleryViewModel viewModel,
        ThumbnailLoadCoordinator thumbnailLoads,
        FolderSession session,
        double tileSize)
    {
        _viewModel = viewModel;
        _thumbnailLoads = thumbnailLoads;
        _session = session;
        DataContext = viewModel;

        GalleryScrollViewer.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
            {
                UpdateWidth();
            }
        };
        _autoScroller = new AutoScroller(owner, GalleryScrollViewer);
        GalleryScrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, GalleryScrollViewer_PointerWheelChanged, RoutingStrategies.Tunnel, true);
        GalleryItems.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble, true);
        GalleryItems.ElementPrepared += GalleryItems_ElementPrepared;
        GalleryItems.ElementClearing += GalleryItems_ElementClearing;
        SetTileSize(tileSize);
    }

    public int ColumnCount
    {
        get
        {
            var availableWidth = GalleryItems.Bounds.Width > 0
                ? GalleryItems.Bounds.Width
                : (GalleryScrollViewer.Viewport.Width > 0 ? GalleryScrollViewer.Viewport.Width : Bounds.Width)
                  - GalleryItems.Margin.Left
                  - GalleryItems.Margin.Right;
            return Math.Max(1, (int)Math.Floor(Math.Max(1, availableWidth) / Math.Max(1, _tileItemExtent)));
        }
    }

    public void StopAutoScroll() => _autoScroller?.Stop();

    internal void SetTileSize(double tileSize)
    {
        _tileSize = tileSize;
        _tileItemExtent = tileSize + TileGap;

        if (GalleryItems.Layout is Avalonia.Layout.UniformGridLayout uniformLayout)
        {
            uniformLayout.MinItemWidth = _tileItemExtent;
            uniformLayout.MinItemHeight = _tileItemExtent;
        }

        if (Application.Current is { } application)
        {
            application.Resources["GalleryTileSize"] = tileSize;
        }

        UpdateWidth();
        QueueViewportSchedule();
    }

    // The new folder starts from the top with nothing scheduled.
    internal void Reset()
    {
        _scheduler.Reset();
        GalleryEmptyState.IsVisible = false;
        GalleryEmptyState.Opacity = 0;
    }

    // Shows `filtered` in the grid, keeping the user's place when the change is small.
    internal void ShowItems(IReadOnlyList<ImageItem> filtered, bool resetScroll, int catalogCount)
    {
        var items = _viewModel.Items;
        var hasChanges = items.Count != filtered.Count;
        if (!hasChanges)
        {
            for (var index = 0; index < filtered.Count; index++)
            {
                if (!ReferenceEquals(items[index], filtered[index]))
                {
                    hasChanges = true;
                    break;
                }
            }
        }

        if (hasChanges)
        {
            var scrollAnchor = resetScroll ? null : CaptureScrollAnchor();
            if (resetScroll)
            {
                items.Clear();
                items.AddRange(filtered);
            }
            else if (
                scrollAnchor is null &&
                items.Count > 0 &&
                filtered.Count > 0 &&
                !ReferenceEquals(items[0], filtered[0]))
            {
                items.Clear();
                items.AddRange(filtered);
            }
            else
            {
                _listSync.Synchronize(items, filtered);
            }

            GalleryItems.InvalidateMeasure();
            GalleryScrollViewer.InvalidateMeasure();
            RestoreScrollAnchor(scrollAnchor, filtered);
        }

        if (resetScroll)
        {
            _scrollRestoreGate.Invalidate();
            GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, 0);

        }

        ItemsApplied?.Invoke();
        QueueViewportSchedule();

        ToggleEmptyState(filtered.Count == 0 && catalogCount > 0);
    }

    // Brings a card into view and gives it keyboard focus.
    internal void Reveal(int index)
    {
        EnsureIndexVisible(index);

        if (GalleryItems.TryGetElement(index) is Control control)
        {
            control.Focus();
        }
    }

    internal void QueueViewportSchedule(bool force = false)
    {
        if (_isViewportScheduleQueued && !force)
        {
            return;
        }

        _isViewportScheduleQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _isViewportScheduleQueued = false;
                ScheduleViewportThumbnails();
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("ScheduleViewportThumbnails", ex);
                throw;
            }
        }, DispatcherPriority.Background);
    }

    internal void ScheduleViewportThumbnails()
    {
        if (_session.CurrentToken is not { IsCancellationRequested: false } token || _viewModel.Items.Count == 0)
        {
            return;
        }

        var itemExtent = Math.Max(1, _tileItemExtent);
        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0 ? GalleryScrollViewer.Viewport.Height : Bounds.Height;
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(GalleryScrollViewer.Offset.Y / itemExtent));
        var visibleRowCount = Math.Max(1, (int)Math.Ceiling(viewportHeight / itemExtent) + 1);

        var (first, last) = _scheduler.Plan(
            _viewModel.Items,
            firstVisibleRow,
            visibleRowCount,
            ColumnCount,
            _tileSize,
            RenderScaling);
        _thumbnailLoads.ScheduleViewport(_scheduler.Visible, _scheduler.Ahead, token);
        _session.FollowViewport(_viewModel.Items, first, last, _scheduler.Direction);
    }

    private void UpdateWidth()
    {
        var available = GalleryScrollViewer.Viewport.Width - GalleryItems.Margin.Left - GalleryItems.Margin.Right;
        if (available <= 0)
        {
            return;
        }

        GalleryItems.Width = GetWholeColumnWidth(available, _tileItemExtent);
    }

    // The widest run of whole tile columns that fits, never less than one column.
    internal static double GetWholeColumnWidth(double availableWidth, double itemExtent)
    {
        itemExtent = Math.Max(1, itemExtent);
        return Math.Max(1, Math.Floor(availableWidth / itemExtent)) * itemExtent;
    }

    private void GalleryItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control control && control.DataContext is ImageItem item)
        {
            var scrollOffset = GalleryScrollViewer.Offset.Y;
            var hasSelectionModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
            if (e.ClickCount >= 2)
            {
                if (hasSelectionModifier)
                {
                    _viewModel.AddToSelection(item);
                }
                else
                {
                    _viewModel.SelectItem(item);
                }
            }
            else if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                _viewModel.SelectRange(item);
            }
            else if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                _viewModel.ToggleSelected(item);
            }
            else
            {
                _viewModel.SelectItem(item);
            }

            var index = _viewModel.Items.IndexOf(item);
            if (index >= 0)
            {
                var row = index / ColumnCount;
                var rowTop = GalleryItems.Margin.Top + (row * _tileItemExtent);
                var rowBottom = rowTop + _tileItemExtent;
                var viewportHeight = GalleryScrollViewer.Viewport.Height > 0
                    ? GalleryScrollViewer.Viewport.Height
                    : Math.Max(1, GalleryScrollViewer.Bounds.Height);

                if (rowTop < scrollOffset || rowBottom > scrollOffset + viewportHeight)
                {
                    EnsureIndexVisible(index);
                }
                else
                {
                    QueueScrollRestore(scrollOffset);
                }
            }

            if (e.ClickCount >= 2 && !hasSelectionModifier)
            {
                PreviewRequested?.Invoke();
            }
        }
    }

    private void GalleryItem_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: ImageItem item })
        {
            return;
        }

        if (IsPreviewOpen?.Invoke() == true)
        {
            return;
        }

        if (e.Key is Key.Space or Key.Enter)
        {
            _viewModel.SelectItem(item);
            PreviewRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void ContextMenu_Opening(object? sender, CancelEventArgs e) => ContextMenuOpening?.Invoke(sender, e);

    private void ContextMenuItem_Click(object? sender, RoutedEventArgs e) => ContextMenuItemClicked?.Invoke(sender, e);

    private void EnsureIndexVisible(int index)
    {
        var row = index / ColumnCount;
        var rowTop = GalleryItems.Margin.Top + (row * _tileItemExtent);
        var rowBottom = rowTop + _tileItemExtent;
        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0
            ? GalleryScrollViewer.Viewport.Height
            : Math.Max(1, GalleryScrollViewer.Bounds.Height);
        var currentTop = GalleryScrollViewer.Offset.Y;
        var currentBottom = currentTop + viewportHeight;

        if (rowTop < currentTop)
        {
            GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, rowTop);
        }
        else if (rowBottom > currentBottom)
        {
            GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, rowBottom - viewportHeight);
        }
    }

    // ItemsRepeater raises exactly one ElementPrepared and one matching ElementClearing per realized
    // element, which is what keeps ImageItem's realized count balanced. The previous version inferred the
    // same thing from AttachedToVisualTree / DetachedFromVisualTree / DataContextChanged and had to stash
    // the previous item in control.Tag to tell recycling apart from a first realization.
    private void GalleryItems_ElementPrepared(object? sender, ItemsRepeaterElementPreparedEventArgs e)
    {
        if (e.Element.DataContext is not ImageItem item)
        {
            return;
        }

        // Realize unconditionally so every prepare has a matching clear. Only the thumbnail scheduling is
        // gated on the folder session, because a torn-down folder has no viewport worth scheduling.
        item.MarkRealized();

        if (_session.CurrentToken is { IsCancellationRequested: false })
        {
            QueueViewportSchedule();
        }
    }

    // The element still carries the outgoing item's DataContext here, which is why the unrealize can be
    // attributed without tracking it separately.
    private void GalleryItems_ElementClearing(object? sender, ItemsRepeaterElementClearingEventArgs e)
    {
        if (e.Element.DataContext is ImageItem item)
        {
            item.MarkUnrealized();
        }
    }

    private void GalleryScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        DebugLog.SetScrollState(
            GalleryScrollViewer.Offset.Y,
            GalleryScrollViewer.Viewport.Height,
            GalleryScrollViewer.Extent.Height,
            _viewModel.Items.Count,
            _tileItemExtent);

        // Background-priority coalescing is the throttle; do not add a second one.
        QueueViewportSchedule();
    }

    private void GalleryScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 || e.Delta.Y == 0)
        {
            return;
        }

        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0
            ? GalleryScrollViewer.Viewport.Height
            : Math.Max(1, GalleryScrollViewer.Bounds.Height);
        var maxOffset = Math.Max(0, GalleryScrollViewer.Extent.Height - viewportHeight);
        if (maxOffset <= 0)
        {
            return;
        }

        var rowBasedDistance = Math.Max(MinWheelScrollPixels, _tileItemExtent * WheelScrollRowsPerNotch);
        var viewportCap = Math.Max(MinWheelScrollPixels, viewportHeight * MaxWheelViewportRatio);
        var scrollDistance = Math.Min(rowBasedDistance, viewportCap);
        var nextOffset = Math.Clamp(GalleryScrollViewer.Offset.Y - (e.Delta.Y * scrollDistance), 0, maxOffset);

        GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, nextOffset);
        e.Handled = true;
    }

    private ScrollAnchor? CaptureScrollAnchor()
    {
        var offset = GalleryScrollViewer.Offset.Y;
        if (offset <= 0.5 || _viewModel.Items.Count == 0)
        {
            return null;
        }

        var firstVisibleRow = Math.Max(0, (int)Math.Floor(offset / Math.Max(1, _tileItemExtent)));
        var index = Math.Min(_viewModel.Items.Count - 1, firstVisibleRow * ColumnCount);
        return new ScrollAnchor(_viewModel.Items[index], index, offset);
    }

    private void RestoreScrollAnchor(ScrollAnchor? anchor, IReadOnlyList<ImageItem> filtered)
    {
        if (anchor is not { } value)
        {
            return;
        }

        var newIndex = -1;
        for (var index = 0; index < filtered.Count; index++)
        {
            if (ReferenceEquals(filtered[index], value.Item))
            {
                newIndex = index;
                break;
            }
        }

        var desiredOffset = GalleryListSync.CalculateAnchoredGalleryOffset(
            value.OldIndex,
            newIndex,
            ColumnCount,
            _tileItemExtent,
            value.Offset,
            double.PositiveInfinity);

        // Uniform fixed-height rows make a row anchor sufficient. Variable-height tiles would need realized-element anchoring.
        QueueScrollRestore(desiredOffset);
    }

    private void QueueScrollRestore(double desiredOffset)
    {
        var restore = _scrollRestoreGate.Begin();
        Dispatcher.UIThread.Post(() =>
        {
            ApplyScrollRestore(restore, desiredOffset);
        }, DispatcherPriority.Loaded);
    }

    private void ApplyScrollRestore(Generation restore, double desiredOffset)
    {
        if (restore.IsStale)
        {
            return;
        }

        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0
            ? GalleryScrollViewer.Viewport.Height
            : Math.Max(1, GalleryScrollViewer.Bounds.Height);
        var maxOffset = GalleryScrollViewer.Extent.Height > 0
            ? Math.Max(0, GalleryScrollViewer.Extent.Height - viewportHeight)
            : desiredOffset;
        var restoredOffset = Math.Clamp(desiredOffset, 0, maxOffset);
        GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, restoredOffset);
    }

    private async void ToggleEmptyState(bool show)
    {
        var transition = _emptyStateGate.Begin();
        if (show)
        {
            if (!GalleryEmptyState.IsVisible)
            {
                GalleryEmptyState.Opacity = 0;
                GalleryEmptyState.IsVisible = true;
            }

            await Task.Yield();
            if (transition.IsCurrent)
            {
                GalleryEmptyState.Opacity = 1;
            }
        }
        else
        {
            if (GalleryEmptyState.IsVisible)
            {
                GalleryEmptyState.Opacity = 0;
                await Task.Delay(120);
                if (transition.IsCurrent)
                {
                    GalleryEmptyState.IsVisible = false;
                }
            }
        }
    }
}
