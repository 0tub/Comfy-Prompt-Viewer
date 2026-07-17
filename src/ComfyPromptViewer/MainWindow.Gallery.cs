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
                UserPreferences.SaveTileSize(_targetTileSize);
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
        _allImagePaths.Sort(CompareImagePaths);

        if (_allImageItems.Count > 1)
        {
            var sortOrder = new Dictionary<string, int>(_allImagePaths.Count, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < _allImagePaths.Count; index++)
            {
                sortOrder[_allImagePaths[index]] = index;
            }

            _allImageItems.Sort((left, right) => sortOrder[left.Path].CompareTo(sortOrder[right.Path]));
        }
    }

    private int CompareImagePaths(string left, string right)
    {
        if (_sortMode == SortMode.Name)
        {
            return CompareImagePathNames(left, right);
        }

        var compare = _imageLastWriteTimes[left].CompareTo(_imageLastWriteTimes[right]);
        if (_sortMode == SortMode.NewestFirst)
        {
            compare = -compare;
        }

        return compare != 0
            ? compare
            : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    private static int CompareImagePathNames(string left, string right)
    {
        var fileNameCompare = StringComparer.CurrentCultureIgnoreCase.Compare(Path.GetFileName(left), Path.GetFileName(right));
        return fileNameCompare != 0
            ? fileNameCompare
            : StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
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

    private void GalleryItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control control && control.DataContext is ImageItem item)
        {
            var scrollOffset = GalleryScrollViewer.Offset.Y;
            SelectItem(item);
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
            if (e.ClickCount >= 2)
            {
                ShowLargePreview();
            }
        }
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
        var windowWidth = e.NewSize.Width;
        var windowHeight = e.NewSize.Height;

        double targetSidebarWidth = Math.Clamp(
            windowWidth * SidebarWidthWindowRatio,
            MinSidebarWidth,
            MaxSidebarWidth);
        
        if (MainGrid != null && MainGrid.ColumnDefinitions.Count > 1)
        {
            MainGrid.ColumnDefinitions[1].Width = new GridLength(targetSidebarWidth, GridUnitType.Pixel);
        }

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

        var currentOffset = GalleryScrollViewer.Offset.Y;
        var currentTimestamp = Environment.TickCount64;

        var dt = (currentTimestamp - _lastScrollTimestamp) / 1000.0;
        var dy = Math.Abs(currentOffset - _lastScrollOffsetY);
        var velocity = dt > 0.005 ? dy / dt : 0.0;

        _lastScrollOffsetY = currentOffset;
        _lastScrollTimestamp = currentTimestamp;
        _lastScrollEventTime = currentTimestamp;

        if (velocity > ScrollVelocityThreshold)
        {
            if (!IsFastScrolling)
            {
                _isFastScrollingStatic = true;
                _thumbnailLoads.Clear();
            }

            var now = Environment.TickCount64;
            if (now - _lastFastScrollScheduleTime >= 120)
            {
                _lastFastScrollScheduleTime = now;
                QueueViewportThumbnailSchedule();
            }

            if (_scrollMonitorTimer == null)
            {
                _scrollMonitorTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(50),
                    DispatcherPriority.Background,
                    ScrollMonitorTimer_Tick);
            }

            if (!_scrollMonitorTimer.IsEnabled)
            {
                _scrollMonitorTimer.Start();
            }
        }
        else
        {
            if (IsFastScrolling)
            {
                _isFastScrollingStatic = false;
                _scrollMonitorTimer?.Stop();
                QueueViewportThumbnailSchedule(force: true);
            }
            else
            {
                _scrollMonitorTimer?.Stop();
                QueueViewportThumbnailSchedule();
            }
        }
    }

    private void ScrollMonitorTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = Environment.TickCount64 - _lastScrollEventTime;
        if (elapsed >= 150)
        {
            _scrollMonitorTimer?.Stop();
            if (IsFastScrolling)
            {
                _isFastScrollingStatic = false;
                QueueViewportThumbnailSchedule(force: true);
            }
            else
            {
                QueueViewportThumbnailSchedule();
            }
        }
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

        var aheadStartRow = Math.Max(0, firstVisibleRow - 2);
        var aheadEndRow = firstVisibleRow + visibleRowCount + 4;
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
        var generation = ++_galleryEmptyStateGeneration;
        if (show)
        {
            if (!GalleryEmptyState.IsVisible)
            {
                GalleryEmptyState.Opacity = 0;
                GalleryEmptyState.IsVisible = true;
            }

            await Task.Yield();
            if (generation == _galleryEmptyStateGeneration)
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
                if (generation == _galleryEmptyStateGeneration)
                {
                    GalleryEmptyState.IsVisible = false;
                }
            }
        }
    }

    private void UpdateCountText()
    {
        int total = _allImageItems.Count;
        int filtered = _viewModel.Items.Count;
        int loadedMetadataCount = _allImageItems.Count(item => item.HasLoadedMetadata);

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
            if (total == filtered)
            {
                CountText.Text = $"{total:n0} images";
            }
            else
            {
                CountText.Text = $"{filtered:n0} of {total} images";
            }
        }

        Dispatcher.UIThread.Post(() => {
            CountText.Opacity = 1.0;
        }, DispatcherPriority.Render);
    }
    private void ClearImageItems()
    {
        _metadataCountUpdateTimer?.Stop();
        GalleryEmptyState.IsVisible = false;
        GalleryEmptyState.Opacity = 0;

        foreach (var item in _allImageItems)
        {
            item.MetadataLoaded -= ImageItem_MetadataLoaded;
        }

        _viewModel.Items.Clear();
        _allImagePaths.Clear();
        _allImageItems.Clear();
        _imageLastWriteTimes.Clear();
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
