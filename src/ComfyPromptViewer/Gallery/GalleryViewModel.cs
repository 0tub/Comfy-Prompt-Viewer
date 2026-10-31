using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Collections;

namespace ComfyPromptViewer;

// What the grid shows and what is selected in it. Items is the filtered, sorted list the grid binds to. The
// active item is the one the sidebar and the preview follow; the selection is the set a click, Ctrl+click or
// Shift+click builds, which the context menus act on. The window reacts to both through the events.
public sealed class GalleryViewModel
{
    private ImageItem? _activeItem;

    public AvaloniaList<ImageItem> Items { get; } = [];

    public ImageItem? ActiveItem => _activeItem;

    internal GallerySelection Selection { get; } = new();

    // Previous and current active item. Raised after ActiveItem has changed.
    internal event Action<ImageItem?, ImageItem?>? ActiveItemChanged;

    // The selected set changed; the count line shows how many are selected.
    internal event Action? SelectionChanged;

    // A plain click: exactly this item, which also becomes the range anchor. Null clears the selection.
    internal void SelectItem(ImageItem? item)
    {
        Selection.SelectOnly(item);
        SetActiveItem(item);
        SelectionChanged?.Invoke();
    }

    // Moves the active item without touching the selected set, e.g. when a menu acts on one item of a selection.
    internal void SetActiveItem(ImageItem? item)
    {
        if (_activeItem == item)
        {
            return;
        }

        var previous = _activeItem;
        if (previous is not null)
        {
            previous.IsSelected = false;
        }

        _activeItem = item;
        if (item is not null)
        {
            item.IsSelected = true;
        }

        ActiveItemChanged?.Invoke(previous, item);
    }

    // Ctrl+double-click: adds the item without moving the range anchor.
    internal void AddToSelection(ImageItem item)
    {
        Selection.Add(item);
        SetActiveItem(item);
        SelectionChanged?.Invoke();
    }

    internal void ToggleSelected(ImageItem item)
    {
        if (Selection.Toggle(item))
        {
            SetActiveItem(item);
        }
        else if (_activeItem == item)
        {
            SetActiveItem(Selection.FirstIn(Items));
        }

        SelectionChanged?.Invoke();
    }

    internal void SelectRange(ImageItem item)
    {
        if (!Selection.SelectRange(Items, item))
        {
            SelectItem(item);
            return;
        }

        SetActiveItem(item);
        SelectionChanged?.Invoke();
    }

    // Arrow keys: one item or one row from the active item. With nothing active, up and left start from the
    // last item and the rest from the first. Returns the index selected, or -1 when there is nothing to move to.
    internal int MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return -1;
        }

        var currentIndex = _activeItem is null ? -1 : Items.IndexOf(_activeItem);
        var nextIndex = currentIndex < 0
            ? (delta < 0 ? Items.Count - 1 : 0)
            : Math.Clamp(currentIndex + delta, 0, Items.Count - 1);

        return SelectByIndex(nextIndex);
    }

    internal int SelectByIndex(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return -1;
        }

        SelectItem(Items[index]);
        return index;
    }

    // An image left the folder. It can no longer be selected, and if it was the active one the sidebar falls
    // back to another selected image that is not also going away.
    internal void Remove(ImageItem item, IReadOnlySet<string> deletedPaths)
    {
        Selection.Remove(item);

        if (_activeItem == item)
        {
            SetActiveItem(Items.FirstOrDefault(candidate =>
                Selection.Contains(candidate) && !deletedPaths.Contains(candidate.Path)));
        }
    }

    // A changed file is a new item: the selection and the active image carry over to it.
    internal void Replace(ImageItem previous, ImageItem replacement)
    {
        Selection.Replace(previous, replacement);
        if (_activeItem == previous)
        {
            SetActiveItem(replacement);
        }
    }
}
