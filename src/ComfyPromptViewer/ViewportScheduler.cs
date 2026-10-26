using System;
using System.Collections.Generic;

namespace ComfyPromptViewer;

// Decides which thumbnails to ask for: the rows on screen, then a window of rows ahead. The window leans
// the way the user is scrolling, and it is the rows' order that matters, because the coordinator works
// through the lists front to back. It reads no control; the grid view hands it the geometry.
internal sealed class ViewportScheduler
{
    public const int AheadRowsInScrollDirection = 8;
    public const int AheadRowsAgainstScrollDirection = 2;

    private int _lastFirstVisibleRow = -1;

    public IReadOnlyList<ImageItem> Visible => VisibleItems;
    public IReadOnlyList<ImageItem> Ahead => AheadItems;

    // 1 while scrolling toward the end of the list, -1 toward the start.
    public int Direction { get; private set; } = 1;

    private List<ImageItem> VisibleItems { get; } = [];
    private List<ImageItem> AheadItems { get; } = [];

    // About one screen of lookahead ahead of travel; a symmetric window wastes half on rows already passed.
    public static (int RowsAbove, int RowsBelow) GetAheadRowWindow(int prefetchDirection)
    {
        return prefetchDirection < 0
            ? (AheadRowsInScrollDirection, AheadRowsAgainstScrollDirection)
            : (AheadRowsAgainstScrollDirection, AheadRowsInScrollDirection);
    }

    public void Reset()
    {
        VisibleItems.Clear();
        AheadItems.Clear();
        _lastFirstVisibleRow = -1;
        Direction = 1;
    }

    // Fills Visible and Ahead. Returns the index range on screen, which the prewarm pass follows.
    public (int FirstVisibleIndex, int LastVisibleIndex) Plan(
        IReadOnlyList<ImageItem> items,
        int firstVisibleRow,
        int visibleRowCount,
        int columnCount,
        double tileSize,
        double renderScaling)
    {
        VisibleItems.Clear();
        AheadItems.Clear();
        if (_lastFirstVisibleRow >= 0)
        {
            var rowDelta = firstVisibleRow.CompareTo(_lastFirstVisibleRow);
            if (rowDelta != 0)
            {
                Direction = rowDelta;
            }
        }

        _lastFirstVisibleRow = firstVisibleRow;

        var firstVisibleIndex = firstVisibleRow * columnCount;
        var lastVisibleIndex = Math.Min(items.Count - 1, ((firstVisibleRow + visibleRowCount) * columnCount) - 1);

        AddRange(VisibleItems, firstVisibleIndex, lastVisibleIndex);

        var (rowsAbove, rowsBelow) = GetAheadRowWindow(Direction);
        var aheadStartRow = Math.Max(0, firstVisibleRow - rowsAbove);
        var aheadEndRow = firstVisibleRow + visibleRowCount + rowsBelow;
        var aheadEndIndex = Math.Min(items.Count - 1, (aheadEndRow * columnCount) - 1);

        if (Direction < 0)
        {
            AddRowsDescending(AheadItems, firstVisibleRow - 1, aheadStartRow);
            AddRange(AheadItems, lastVisibleIndex + 1, aheadEndIndex);
        }
        else
        {
            AddRange(AheadItems, lastVisibleIndex + 1, aheadEndIndex);
            AddRowsDescending(AheadItems, firstVisibleRow - 1, aheadStartRow);
        }

        return (firstVisibleIndex, lastVisibleIndex);

        void AddRange(List<ImageItem> target, int startIndex, int endIndex)
        {
            if (endIndex < startIndex)
            {
                return;
            }

            for (var index = Math.Max(0, startIndex); index <= endIndex; index++)
            {
                var item = items[index];
                item.SetTileSize(tileSize, renderScaling);
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
                AddRange(target, row * columnCount, Math.Min(items.Count - 1, ((row + 1) * columnCount) - 1));
            }
        }
    }
}
