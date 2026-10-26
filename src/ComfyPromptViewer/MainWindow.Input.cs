using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace ComfyPromptViewer;

// Window-level keys: the preview and delete overlays take them first, then arrow-key navigation through the
// grid, Enter and Space to open the preview, and F5 to reload.
public partial class MainWindow
{
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

        if (LargePreview.IsOpen)
        {
            if (!IsTextInputFocused(e.Source) && LargePreview.HandleVideoKey(e.Key, _viewModel.ActiveItem))
            {
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                HideLargePreview();
                e.Handled = true;
                return;
            }

            var columns = Gallery.ColumnCount;
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
            if (_session.CurrentFolderPath is { Length: > 0 } folderPath)
            {
                OpenFolder(folderPath);
                e.Handled = true;
            }
        }

        if (IsTextInputFocused(e.Source))
        {
            return;
        }

        var galleryColumns = Gallery.ColumnCount;
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
        if (LargePreview.ReleaseNavigationKey(e.Key))
        {
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
        if (_viewModel.ActiveItem is null)
        {
            return false;
        }

        ShowLargePreview();
        return true;
    }

    private bool MoveSelection(int delta) => ShowSelected(_viewModel.MoveSelection(delta));

    private bool SelectByIndex(int index) => ShowSelected(_viewModel.SelectByIndex(index));

    // Brings the newly selected card into view and gives it keyboard focus.
    private bool ShowSelected(int index)
    {
        if (index < 0)
        {
            return false;
        }

        Gallery.Reveal(index);
        return true;
    }
}
