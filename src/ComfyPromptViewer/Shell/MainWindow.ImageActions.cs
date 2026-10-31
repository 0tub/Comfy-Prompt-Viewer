using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// What the image menus do: clipboard, delete, reveal in the file manager. The file work itself is in
// FileOperations; the selection they apply to is GalleryViewModel's.
public partial class MainWindow
{
    private async Task<bool> CopyTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard; every caller is async void, so this must not escape.
            DebugLog.Write($"Failed to copy to clipboard: {ex.Message}");
            return false;
        }
    }

    // The card a menu opened on, or for the preview's menu the active image.
    private ImageItem? GetMenuTarget(ImageContextMenu menu) => menu.DataContext as ImageItem ?? _viewModel.ActiveItem;

    private void ImageContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (sender is not ImageContextMenu menu || GetMenuTarget(menu) is not { } item)
        {
            return;
        }

        var inSelection = _viewModel.Selection.Contains(item);
        menu.Configure(
            inSelection ? _viewModel.Selection.Count : 1,
            inSelection && _viewModel.Selection.Count > 1
                ? _viewModel.Selection.Any(selected => selected.Metadata.HasPrompt)
                : item.Metadata.HasPrompt,
            item.Metadata.HasNegativePrompt);
    }

    private async void ImageContextMenu_ActionInvoked(ImageContextMenu menu, ImageAction action)
    {
        if (GetMenuTarget(menu) is not { } item)
        {
            return;
        }

        ImageItem[] actionItems;
        if (_viewModel.Selection.Contains(item))
        {
            actionItems = _viewModel.Selection.InOrder(_session.Catalog.Items);
            _viewModel.SetActiveItem(item);
        }
        else
        {
            _viewModel.SelectItem(item);
            actionItems = [item];
        }

        switch (action)
        {
            case ImageAction.Open:
                FileOperations.Reveal(item.Path, TopLevel.GetTopLevel(this)?.Launcher);
                break;
            case ImageAction.CopyPrompt:
                var promptText = string.Join(
                    Environment.NewLine + Environment.NewLine,
                    actionItems.Where(selected => selected.Metadata.HasPrompt).Select(selected => selected.Metadata.Prompt));
                if (promptText.Length > 0 && await CopyTextAsync(promptText))
                {
                    Sidebar.ViewModel.FlashCopied(negative: false);
                }
                break;
            case ImageAction.CopyNegativePrompt:
                if (item.Metadata.HasNegativePrompt && await CopyTextAsync(item.Metadata.NegativePrompt))
                {
                    Sidebar.ViewModel.FlashCopied(negative: true);
                }
                break;
            case ImageAction.CopyPath:
                await CopyTextAsync(item.Path);
                break;
            case ImageAction.Delete:
                LargePreview.Hide();
                // Only a recoverable single delete goes ahead unconfirmed.
                if ((actionItems.Length == 1 && FileOperations.DeleteIsRecoverable) || await ConfirmDeleteAsync(actionItems.Length))
                {
                    DeleteImages(actionItems);
                }
                break;
        }
    }

    private async Task<bool> ConfirmDeleteAsync(int count)
    {
        CompleteDeleteConfirmation(false);
        _deleteConfirmationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var noun = count == 1 ? "image" : "images";
        DeleteConfirmationTitle.Text = $"Delete {count:n0} {noun}?";
        DeleteConfirmationText.Text = FileOperations.DeleteIsRecoverable
            ? $"The selected {noun} will be moved to the Recycle Bin."
            : $"The selected {noun} will be permanently deleted.";
        DeleteConfirmationOverlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => ConfirmDeleteButton.Focus(), DispatcherPriority.Input);
        return await _deleteConfirmationCompletion.Task;
    }

    private void ConfirmDeleteButton_Click(object? sender, RoutedEventArgs e) => CompleteDeleteConfirmation(true);

    private void CancelDeleteButton_Click(object? sender, RoutedEventArgs e) => CompleteDeleteConfirmation(false);

    private void CompleteDeleteConfirmation(bool confirmed)
    {
        if (_deleteConfirmationCompletion is not { } completion)
        {
            return;
        }

        _deleteConfirmationCompletion = null;
        DeleteConfirmationOverlay.IsVisible = false;
        completion.TrySetResult(confirmed);
    }

    private void DeleteImages(ImageItem[] items)
    {
        var (deleted, failedCount) = FileOperations.Delete(items.Select(item => item.Path).ToArray());
        if (deleted.Count > 0)
        {
            _session.ApplyChanges([], [], deleted);
        }

        if (failedCount > 0)
        {
            _header.SetCountText(failedCount == 1
                ? "Could not delete 1 image"
                : $"Could not delete {failedCount:n0} images");
        }
    }
}
