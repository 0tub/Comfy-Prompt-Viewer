using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private readonly record struct GalleryScrollAnchor(ImageItem Item, int OldIndex, double Offset);

    private void TileSizeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SetTileSize(RoundToTileStep(e.NewValue), persist: true);
    }



    private void SetTileSize(double tileSize, bool persist)
    {
        var targetTileSize = Math.Clamp(RoundToTileStep(tileSize), MinTileSize, MaxTileSize);
        var tileSizeChanged = Math.Abs(_targetTileSize - targetTileSize) > 0.1;
        _targetTileSize = targetTileSize;

        if (Math.Abs(TileSizeSlider.Value - _targetTileSize) > 0.1)
        {
            _isInitializing = true;
            TileSizeSlider.Value = _targetTileSize;
            _isInitializing = false;
        }

        if (tileSizeChanged)
        {
            ApplyTileLayout();
        }

        if (persist && tileSizeChanged)
        {
            QueueTileSizeSave();
        }
    }

    private void QueueTileSizeSave()
    {
        if (_tileSizeSaveTimer is null)
        {
            _tileSizeSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TileSizeSaveInterval
            };
            _tileSizeSaveTimer.Tick += (_, _) =>
            {
                _tileSizeSaveTimer.Stop();
                _preferences.SaveTileSize(_targetTileSize);
            };
        }

        _tileSizeSaveTimer.Stop();
        _tileSizeSaveTimer.Start();
    }

    private void ApplyTileLayout()
    {
        _tileSize = _targetTileSize;
        _tileItemExtent = _tileSize + TileGap;
        UpdateTileSizeText();

        if (GalleryItems.Layout is Avalonia.Layout.UniformGridLayout uniformLayout)
        {
            uniformLayout.MinItemWidth = _tileItemExtent;
            uniformLayout.MinItemHeight = _tileItemExtent;
        }

        if (Application.Current is { } application)
        {
            application.Resources["GalleryTileSize"] = _tileSize;
        }

        QueueViewportThumbnailSchedule();
    }

    private void UpdateTileSizeText()
    {
        TileSizeText.Text = $"{_tileSize:0}";
    }

    private static double RoundToTileStep(double value)
    {
        return Math.Round(value / TileSizeStep) * TileSizeStep;
    }

    private void ApplySort()
    {
        _catalog.Sort(CompareGalleryEntries);
    }

    private int CompareGalleryEntries(GalleryEntry left, GalleryEntry right)
    {
        return CompareSortKeys(left.Path, left.Fingerprint, right.Path, right.Fingerprint, _sortMode);
    }

    private static int CompareImageFileEntries(ImageFileEntry left, ImageFileEntry right, SortMode sortMode)
    {
        return CompareSortKeys(left.Path, left.Fingerprint, right.Path, right.Fingerprint, sortMode);
    }

    private static int CompareSortKeys(
        string leftPath,
        SourceFingerprint leftFingerprint,
        string rightPath,
        SourceFingerprint rightFingerprint,
        SortMode sortMode)
    {
        if (sortMode == SortMode.Name)
        {
            return CompareImagePathNames(leftPath, rightPath);
        }

        // Compare raw ticks so the sort inner loop does not construct a DateTime per comparison.
        var compare = leftFingerprint.LastWriteTimeUtcTicks.CompareTo(rightFingerprint.LastWriteTimeUtcTicks);
        if (sortMode == SortMode.NewestFirst)
        {
            compare = -compare;
        }

        return compare != 0
            ? compare
            : StringComparer.OrdinalIgnoreCase.Compare(leftPath, rightPath);
    }

    private static int CompareImagePathNames(string left, string right)
    {
        // Span slicing keeps the file-name comparison allocation-free across the whole n log n sort.
        var fileNameCompare = Path.GetFileName(left.AsSpan())
            .CompareTo(Path.GetFileName(right.AsSpan()), StringComparison.CurrentCultureIgnoreCase);
        return fileNameCompare != 0
            ? fileNameCompare
            : string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
    }

    internal static int FindSortedInsertIndex<T>(IReadOnlyList<T> sortedItems, T item, Comparison<T> comparison)
    {
        var low = 0;
        var high = sortedItems.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (comparison(sortedItems[middle], item) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    // A ColumnDefinition is not a Control, so XAML generates no field for it; the sidebar column is the
    // third definition on MainGrid (gallery, splitter, sidebar).
    private ColumnDefinition? SidebarColumn =>
        MainGrid?.ColumnDefinitions.Count > 2 ? MainGrid.ColumnDefinitions[2] : null;

    // The splitter owns the sidebar width now, but a window narrow enough to squeeze the gallery still has
    // to win. The column's own MinWidth/MaxWidth bound the drag; this only pulls the width back in when the
    // window shrinks under it.
    private void ClampSidebarWidthToWindow(double windowWidth)
    {
        if (SidebarColumn is null)
        {
            return;
        }

        var current = SidebarColumn.Width.IsAbsolute ? SidebarColumn.Width.Value : MaxSidebarWidth;
        var clamped = ComputeClampedSidebarWidth(current, windowWidth);

        if (Math.Abs(clamped - current) > 0.5)
        {
            SetSidebarWidth(clamped, persist: false);
        }
    }

    // The window ratio is a ceiling, never a target: a wide window leaves the dragged width alone, and only
    // a window too narrow to keep the gallery usable pulls it back down.
    internal static double ComputeClampedSidebarWidth(double currentWidth, double windowWidth)
    {
        var maxForWindow = Math.Max(MinSidebarWidth, windowWidth * MaxSidebarWidthWindowRatio);
        return Math.Clamp(currentWidth, MinSidebarWidth, Math.Min(MaxSidebarWidth, maxForWindow));
    }

    private void SetSidebarWidth(double width, bool persist)
    {
        if (SidebarColumn is null)
        {
            return;
        }

        var clamped = Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth);
        SidebarColumn.Width = new GridLength(clamped, GridUnitType.Pixel);

        if (persist)
        {
            _preferences.SaveSidebarWidth(clamped);
        }
    }

    private void SidebarSplitter_DragStarted(object? sender, VectorEventArgs e)
    {
        _isSidebarSplitterDragging = true;
    }

    // GridSplitter raises a DragCompleted while it normalizes column lengths during initial layout, which
    // would otherwise persist a width the user never chose and let it creep across runs. Only a drag that
    // actually started counts.
    private void SidebarSplitter_DragCompleted(object? sender, VectorEventArgs e)
    {
        if (!_isSidebarSplitterDragging)
        {
            return;
        }

        _isSidebarSplitterDragging = false;

        if (SidebarColumn is { Width.IsAbsolute: true } column)
        {
            _preferences.SaveSidebarWidth(Math.Clamp(column.Width.Value, MinSidebarWidth, MaxSidebarWidth));
        }
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
                    AddSelectedItem(item);
                    SetActiveItem(item);
                    UpdateCountText();
                }
                else
                {
                    SelectItem(item);
                }
            }
            else if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                SelectRange(item);
            }
            else if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                ToggleSelectedItem(item);
            }
            else
            {
                SelectItem(item);
            }

            var index = _viewModel.Items.IndexOf(item);
            if (index >= 0)
            {
                var row = index / GetGalleryColumnCount();
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
                    QueueGalleryScrollRestore(scrollOffset);
                }
            }
            if (e.ClickCount >= 2 && !hasSelectionModifier)
            {
                ShowLargePreview();
            }
        }
    }

    private void ToggleSelectedItem(ImageItem item)
    {
        _selectionAnchor = item;
        if (_selectedItems.Contains(item))
        {
            RemoveSelectedItem(item);
            if (_selectedItem == item)
            {
                SetActiveItem(_viewModel.Items.FirstOrDefault(_selectedItems.Contains));
            }
        }
        else
        {
            AddSelectedItem(item);
            SetActiveItem(item);
        }

        UpdateCountText();
    }

    private void SelectRange(ImageItem item)
    {
        var anchorIndex = _selectionAnchor is null ? -1 : _viewModel.Items.IndexOf(_selectionAnchor);
        var itemIndex = _viewModel.Items.IndexOf(item);
        if (anchorIndex < 0 || itemIndex < 0)
        {
            SelectItem(item);
            return;
        }

        ClearSelectedItems();

        var start = Math.Min(anchorIndex, itemIndex);
        var end = Math.Max(anchorIndex, itemIndex);
        for (var index = start; index <= end; index++)
        {
            AddSelectedItem(_viewModel.Items[index]);
        }

        SetActiveItem(item);
        UpdateCountText();
    }

    private void GalleryItem_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: ImageItem item })
        {
            return;
        }

        if (LargePreviewOverlay.IsVisible)
        {
            return;
        }

        if (e.Key is Key.Space or Key.Enter)
        {
            SelectItem(item);
            ShowLargePreview();
            e.Handled = true;
        }
    }

    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var windowHeight = e.NewSize.Height;

        ClampSidebarWidthToWindow(e.NewSize.Width);

        double targetImageHeight = Math.Clamp(
            windowHeight * SidebarPreviewHeightWindowRatio,
            MinSidebarPreviewHeight,
            MaxSidebarPreviewHeight);

        if (SidebarContent != null && SidebarContent.RowDefinitions.Count > 0)
        {
            SidebarContent.RowDefinitions[0].Height = new GridLength(targetImageHeight, GridUnitType.Pixel);
        }

        if (LargePreviewOverlay.IsVisible && _largePreviewZoom is null)
        {
            ApplyLargePreviewZoom(resetScroll: false);
        }

        QueueViewportThumbnailSchedule();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DeleteConfirmationOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                CompleteDeleteConfirmation(false);
                e.Handled = true;
            }

            base.OnKeyDown(e);
            return;
        }

        if (LargePreviewOverlay.IsVisible)
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                HideLargePreview();
                e.Handled = true;
                return;
            }

            var columns = GetGalleryColumnCount();
            var moved = e.Key switch
            {
                Key.Left => MoveLargePreviewSelectionFromKey(e.Key, -1),
                Key.Right => MoveLargePreviewSelectionFromKey(e.Key, 1),
                Key.Up => MoveLargePreviewSelectionFromKey(e.Key, -columns),
                Key.Down => MoveLargePreviewSelectionFromKey(e.Key, columns),
                Key.Home => SelectByIndex(0),
                Key.End => SelectByIndex(_viewModel.Items.Count - 1),
                _ => false
            };

            if (moved)
            {
                e.Handled = true;
                return;
            }
        }

        base.OnKeyDown(e);

        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.F5 || (e.Key == Key.R && (e.KeyModifiers & KeyModifiers.Control) != 0))
        {
            if (!string.IsNullOrEmpty(_currentFolderPath))
            {
                DebugLog.Observe(LoadFolderAsync(_currentFolderPath), "Keyboard folder reload");
                e.Handled = true;
            }
        }

        if (IsTextInputFocused(e.Source))
        {
            return;
        }

        var galleryColumns = GetGalleryColumnCount();
        var handled = e.Key switch
        {
            Key.Left => MoveSelection(-1),
            Key.Right => MoveSelection(1),
            Key.Up => MoveSelection(-galleryColumns),
            Key.Down => MoveSelection(galleryColumns),
            Key.Home => SelectByIndex(0),
            Key.End => SelectByIndex(_viewModel.Items.Count - 1),
            Key.Enter or Key.Space => ShowLargePreviewIfSelected(),
            _ => false
        };

        if (handled)
        {
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (LargePreviewOverlay.IsVisible && e.Key == _heldPreviewNavigationKey)
        {
            _heldPreviewNavigationKey = Key.None;
            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    private static bool IsTextInputFocused(object? source)
    {
        return source is TextBox;
    }

    private bool ShowLargePreviewIfSelected()
    {
        if (_selectedItem is null)
        {
            return false;
        }

        ShowLargePreview();
        return true;
    }

    private bool MoveSelection(int delta)
    {
        if (_viewModel.Items.Count == 0)
        {
            return false;
        }

        var currentIndex = _selectedItem is null ? -1 : _viewModel.Items.IndexOf(_selectedItem);
        var nextIndex = currentIndex < 0
            ? (delta < 0 ? _viewModel.Items.Count - 1 : 0)
            : Math.Clamp(currentIndex + delta, 0, _viewModel.Items.Count - 1);

        return SelectByIndex(nextIndex);
    }

    private bool SelectByIndex(int index)
    {
        if (index < 0 || index >= _viewModel.Items.Count)
        {
            return false;
        }

        SelectItem(_viewModel.Items[index]);
        EnsureIndexVisible(index);

        if (GalleryItems.TryGetElement(index) is Control control)
        {
            control.Focus();
        }

        return true;
    }

    private int GetGalleryColumnCount()
    {
        var availableWidth = GalleryItems.Bounds.Width > 0
            ? GalleryItems.Bounds.Width
            : (GalleryScrollViewer.Viewport.Width > 0 ? GalleryScrollViewer.Viewport.Width : Bounds.Width)
              - GalleryItems.Margin.Left
              - GalleryItems.Margin.Right;
        return Math.Max(1, (int)Math.Floor(Math.Max(1, availableWidth) / Math.Max(1, _tileItemExtent)));
    }

    private void EnsureIndexVisible(int index)
    {
        var columns = GetGalleryColumnCount();
        var row = index / columns;
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

    private void GalleryItem_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        EnsureItemLoadedFromControl(sender);
    }

    private void GalleryItem_DataContextChanged(object? sender, EventArgs e)
    {
        EnsureItemLoadedFromControl(sender);
    }

    private void EnsureItemLoadedFromControl(object? sender)
    {
        if (sender is Control control)
        {
            var newItem = control.DataContext as ImageItem;
            var oldItem = control.Tag as ImageItem;

            if (oldItem == newItem)
            {
                if (newItem is not null)
                {
                    QueueViewportThumbnailSchedule();
                }
                return;
            }

            if (oldItem is not null)
            {
                oldItem.MarkUnrealized();
            }

            if (newItem is not null && _folderLoader.CurrentToken is { IsCancellationRequested: false })
            {
                control.Tag = newItem;
                newItem.MarkRealized();
                QueueViewportThumbnailSchedule();
            }
            else
            {
                control.Tag = null;
            }
        }
    }

    private void GalleryItem_DetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
        {
            if (control.Tag is ImageItem item)
            {
                item.MarkUnrealized();
            }
            control.Tag = null;
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
        QueueViewportThumbnailSchedule();
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

    private void QueueViewportThumbnailSchedule(bool force = false)
    {
        if (_isViewportThumbnailScheduleQueued && !force)
        {
            return;
        }

        _isViewportThumbnailScheduleQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _isViewportThumbnailScheduleQueued = false;
                ScheduleViewportThumbnails();
            }
            catch (Exception ex)
            {
                DebugLog.WriteException("ScheduleViewportThumbnails", ex);
                throw;
            }
        }, DispatcherPriority.Background);
    }

    // About one screen of lookahead ahead of travel; a symmetric window wastes half on rows already passed.
    internal static (int RowsAbove, int RowsBelow) GetAheadRowWindow(int prefetchDirection)
    {
        return prefetchDirection < 0
            ? (AheadRowsInScrollDirection, AheadRowsAgainstScrollDirection)
            : (AheadRowsAgainstScrollDirection, AheadRowsInScrollDirection);
    }

    private void ScheduleViewportThumbnails()
    {
        if (_folderLoader.CurrentToken is not { IsCancellationRequested: false } token || _viewModel.Items.Count == 0)
        {
            return;
        }

        var itemExtent = Math.Max(1, _tileItemExtent);
        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0 ? GalleryScrollViewer.Viewport.Height : Bounds.Height;
        var columnCount = GetGalleryColumnCount();
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(GalleryScrollViewer.Offset.Y / itemExtent));
        var visibleRowCount = Math.Max(1, (int)Math.Ceiling(viewportHeight / itemExtent) + 1);
        var visibleItems = _visibleThumbnailScheduleItems;
        var aheadItems = _aheadThumbnailScheduleItems;
        visibleItems.Clear();
        aheadItems.Clear();
        if (_lastPrefetchFirstVisibleRow >= 0)
        {
            var rowDelta = firstVisibleRow.CompareTo(_lastPrefetchFirstVisibleRow);
            if (rowDelta != 0)
            {
                _prefetchDirection = rowDelta;
            }
        }
        _lastPrefetchFirstVisibleRow = firstVisibleRow;

        var firstVisibleIndex = firstVisibleRow * columnCount;
        var lastVisibleIndex = Math.Min(_viewModel.Items.Count - 1, ((firstVisibleRow + visibleRowCount) * columnCount) - 1);

        AddRange(visibleItems, firstVisibleIndex, lastVisibleIndex);

        var (rowsAbove, rowsBelow) = GetAheadRowWindow(_prefetchDirection);
        var aheadStartRow = Math.Max(0, firstVisibleRow - rowsAbove);
        var aheadEndRow = firstVisibleRow + visibleRowCount + rowsBelow;
        var aheadEndIndex = Math.Min(_viewModel.Items.Count - 1, (aheadEndRow * columnCount) - 1);

        if (_prefetchDirection < 0)
        {
            AddRowsDescending(aheadItems, firstVisibleRow - 1, aheadStartRow);
            AddRange(aheadItems, lastVisibleIndex + 1, aheadEndIndex);
        }
        else
        {
            AddRange(aheadItems, lastVisibleIndex + 1, aheadEndIndex);
            AddRowsDescending(aheadItems, firstVisibleRow - 1, aheadStartRow);
        }

        _thumbnailLoads.ScheduleViewport(visibleItems, aheadItems, token);

        void AddRange(List<ImageItem> target, int startIndex, int endIndex)
        {
            if (endIndex < startIndex)
            {
                return;
            }

            for (var index = Math.Max(0, startIndex); index <= endIndex; index++)
            {
                var item = _viewModel.Items[index];
                item.SetTileSize(_tileSize);
                target.Add(item);
            }
        }

        void AddRowsDescending(List<ImageItem> target, int startRow, int endRow)
        {
            if (startRow < endRow)
            {
                return;
            }

            for (var row = startRow; row >= endRow; row--)
            {
                AddRange(target, row * columnCount, Math.Min(_viewModel.Items.Count - 1, ((row + 1) * columnCount) - 1));
            }
        }
    }



    private async void ToggleGalleryEmptyState(bool show)
    {
        var transition = _galleryEmptyStateGate.Begin();
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

    private void UpdateCountText()
    {
        int total = _catalog.Count;
        int filtered = _viewModel.Items.Count;
        int loadedMetadataCount = _catalog.LoadedMetadataCount;

        bool isScanning = _metadataScanner.HasActiveSession &&
                         loadedMetadataCount < total;

        if (!isScanning)
        {
            _metadataCountUpdateTimer?.Stop();
        }

        CountText.Opacity = 0.2;

        if (isScanning)
        {
            if (total == filtered)
            {
                CountText.Text = $"{total:n0} images (Scanning prompts {loadedMetadataCount}/{total})";
            }
            else
            {
                CountText.Text = $"{filtered:n0} of {total} images (Scanning prompts {loadedMetadataCount}/{total})";
            }
        }
        else
        {
            CountText.Text = total == filtered
                ? $"{total:n0} images"
                : $"{filtered:n0} of {total} images";
        }

        // Counted against thumbnails that were actually missing, not against the folder: a fully cached
        // folder has no pass to report on.
        if (!isScanning && _prewarmRemaining > 0 && _prewarmTotal > 0)
        {
            CountText.Text += $" (Caching thumbnails {_prewarmTotal - _prewarmRemaining:n0}/{_prewarmTotal:n0})";
        }

        if (_selectedItems.Count > 1)
        {
            CountText.Text += $" | {_selectedItems.Count:n0} selected";
        }

        Dispatcher.UIThread.Post(() => {
            CountText.Opacity = 1.0;
        }, DispatcherPriority.Render);
    }

    private void ClearImageItems()
    {
        CancelSearchFilter();
        _prewarmRemaining = 0;
        _prewarmTotal = 0;
        _metadataCountUpdateTimer?.Stop();
        GalleryEmptyState.IsVisible = false;
        GalleryEmptyState.Opacity = 0;
        ClearSelectedItems();
        _selectionAnchor = null;

        foreach (var item in _catalog.Items)
        {
            item.MetadataLoaded -= ImageItem_MetadataLoaded;
        }

        _viewModel.Items.Clear();
        _catalog.Clear();
        _visibleThumbnailScheduleItems.Clear();
        _aheadThumbnailScheduleItems.Clear();
        _lastPrefetchFirstVisibleRow = -1;
        _prefetchDirection = 1;
    }

    private void SortComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || SortComboBox == null) return;
        var selectedIndex = SortComboBox.SelectedIndex;
        if (selectedIndex < 0) return;

        var newSortMode = selectedIndex switch
        {
            0 => SortMode.NewestFirst,
            1 => SortMode.OldestFirst,
            _ => SortMode.Name
        };

        if (_sortMode != newSortMode)
        {
            _sortMode = newSortMode;
            ApplySort();
            ApplyFilter(resetScroll: true);
        }
    }
}
