using Avalonia.Input;
using Avalonia.Interactivity;

namespace ComfyPromptViewer;

// Host side of the large preview: which item is selected and how to move the selection. Everything about
// drawing it lives in LargePreviewView and LargePreviewViewModel.
public partial class MainWindow
{
    private void InitializeLargePreview()
    {
        LargePreview.ContextMenuOpening += ImageContextMenu_Opening;
        LargePreview.ContextMenuItemClicked += ImageContextMenuItem_Click;
        LargePreview.ViewModel.CloseRequested += HideLargePreview;
        LargePreview.ViewModel.NavigateRequested += MoveLargePreviewSelection;
    }

    private void ShowLargePreview()
    {
        if (_viewModel.ActiveItem is null)
        {
            return;
        }

        var (canGoPrevious, canGoNext) = GetPreviewNavigation();
        LargePreview.Show(_viewModel.ActiveItem, canGoPrevious, canGoNext);
    }

    private void UpdateLargePreview(bool resetZoom)
    {
        if (_viewModel.ActiveItem is null)
        {
            return;
        }

        var (canGoPrevious, canGoNext) = GetPreviewNavigation();
        LargePreview.Update(_viewModel.ActiveItem, resetZoom, canGoPrevious, canGoNext);
    }

    private void HideLargePreview()
    {
        LargePreview.Hide();
    }

    private (bool CanGoPrevious, bool CanGoNext) GetPreviewNavigation()
    {
        var selectedIndex = _viewModel.ActiveItem is null ? -1 : _viewModel.Items.IndexOf(_viewModel.ActiveItem);
        return (selectedIndex > 0, selectedIndex >= 0 && selectedIndex < _viewModel.Items.Count - 1);
    }

    private void MoveLargePreviewSelection(int delta)
    {
        if (_viewModel.ActiveItem is null)
        {
            return;
        }

        var currentIndex = _viewModel.Items.IndexOf(_viewModel.ActiveItem);
        var nextIndex = currentIndex + delta;
        if (nextIndex < 0 || nextIndex >= _viewModel.Items.Count)
        {
            return;
        }

        SelectByIndex(nextIndex);
    }

    private bool MoveLargePreviewSelectionFromKey(Key key, int delta)
    {
        if (_viewModel.ActiveItem is null || _viewModel.Items.Count == 0)
        {
            return false;
        }

        return LargePreview.ConsumeNavigationKey(key, () => MoveSelection(delta));
    }
}
