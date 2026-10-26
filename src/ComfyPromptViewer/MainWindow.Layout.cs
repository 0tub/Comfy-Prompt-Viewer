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

// Window layout: the tile size setting, the sidebar width and splitter, and what a resize triggers.
public partial class MainWindow
{
    private void SetTileSize(double tileSize, bool persist)
    {
        var targetTileSize = Math.Clamp(RoundToTileStep(tileSize), MinTileSize, MaxTileSize);
        var tileSizeChanged = Math.Abs(_targetTileSize - targetTileSize) > 0.1;
        _targetTileSize = targetTileSize;

        _header.SyncTileSize(_targetTileSize);

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
        _session.TileSize = _targetTileSize;
        Gallery.SetTileSize(_targetTileSize);
    }

    private static double RoundToTileStep(double value)
    {
        return Math.Round(value / TileSizeStep) * TileSizeStep;
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

    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var windowHeight = e.NewSize.Height;

        ClampSidebarWidthToWindow(e.NewSize.Width);

        double targetImageHeight = Math.Clamp(
            windowHeight * SidebarPreviewHeightWindowRatio,
            MinSidebarPreviewHeight,
            MaxSidebarPreviewHeight);

        Sidebar.SetPreviewHeight(targetImageHeight);

        LargePreview.RefitIfFitted();

        Gallery.QueueViewportSchedule();
    }
}
