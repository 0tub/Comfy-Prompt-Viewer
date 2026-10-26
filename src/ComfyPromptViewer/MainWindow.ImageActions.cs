using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tmds.DBus.Protocol;

namespace ComfyPromptViewer;

// Actions on images from the context menus: clipboard, delete, reveal in the file manager. The selection
// they apply to lives in MainWindow.Selection.cs.
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

    private async void ImageContextMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string action } menuItem ||
            (menuItem.DataContext as ImageItem ?? _viewModel.ActiveItem) is not { } item)
        {
            return;
        }

        ImageItem[] actionItems;
        if (_viewModel.Selection.Contains(item))
        {
            actionItems = _viewModel.Selection.InOrder(_catalog.Items);
            _viewModel.SetActiveItem(item);
        }
        else
        {
            _viewModel.SelectItem(item);
            actionItems = [item];
        }

        switch (action)
        {
            case "open":
                OpenInExplorer(item.Path);
                break;
            case "prompt":
                var prompts = actionItems
                    .Where(selectedItem => selectedItem.HasPrompt)
                    .Select(selectedItem => selectedItem.Prompt);
                var promptText = string.Join(Environment.NewLine + Environment.NewLine, prompts);
                if (!string.IsNullOrEmpty(promptText) && await CopyTextAsync(promptText))
                {
                    Sidebar.ViewModel.FlashCopied(negative: false);
                }
                break;
            case "negative":
                if (item.HasNegativePrompt && await CopyTextAsync(item.NegativePrompt))
                {
                    Sidebar.ViewModel.FlashCopied(negative: true);
                }
                break;
            case "path":
                await CopyTextAsync(item.Path);
                break;
            case "delete":
                HideLargePreview();
                // Only the Windows Recycle Bin makes an unconfirmed single delete recoverable.
                if ((actionItems.Length == 1 && OperatingSystem.IsWindows()) || await ConfirmBulkDeleteAsync(actionItems.Length))
                {
                    DeleteImages(actionItems);
                }
                break;
        }
    }

    private void ImageContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu contextMenu ||
            (contextMenu.DataContext as ImageItem ?? _viewModel.ActiveItem) is not { } item)
        {
            return;
        }

        var selectedCount = _viewModel.Selection.Contains(item) ? _viewModel.Selection.Count : 1;
        foreach (var menuItem in contextMenu.Items.OfType<MenuItem>())
        {
            switch (menuItem.Tag)
            {
                case "open":
                case "negative":
                case "path":
                    menuItem.IsVisible = selectedCount == 1;
                    break;
                case "prompt":
                    menuItem.Header = selectedCount > 1 ? "Copy Selected Prompts" : "Copy Prompt";
                    menuItem.IsEnabled = selectedCount > 1
                        ? _viewModel.Selection.Any(selectedItem => selectedItem.HasPrompt)
                        : item.HasPrompt;
                    break;
                case "delete":
                    menuItem.Header = selectedCount > 1 ? $"Delete {selectedCount:n0} Images" : "Delete Image";
                    break;
            }
        }
    }

    private async Task<bool> ConfirmBulkDeleteAsync(int count)
    {
        CompleteDeleteConfirmation(false);
        _deleteConfirmationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var noun = count == 1 ? "image" : "images";
        DeleteConfirmationTitle.Text = $"Delete {count:n0} {noun}?";
        DeleteConfirmationText.Text = OperatingSystem.IsWindows()
            ? $"The selected {noun} will be moved to the Recycle Bin."
            : $"The selected {noun} will be permanently deleted.";
        DeleteConfirmationOverlay.IsVisible = true;
        Dispatcher.UIThread.Post(() => ConfirmDeleteButton.Focus(), DispatcherPriority.Input);
        return await _deleteConfirmationCompletion.Task;
    }

    private void DeleteConfirmationButton_Click(object? sender, RoutedEventArgs e)
    {
        CompleteDeleteConfirmation(sender is Button { Tag: "delete" });
    }

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
        var deletedPaths = new System.Collections.Generic.List<string>(items.Length);
        var failedCount = 0;
        foreach (var item in items)
        {
            try
            {
                var path = item.Path;
                if (File.Exists(path))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }

                deletedPaths.Add(path);
            }
            // Cancelling the Windows shell error dialog surfaces as OperationCanceledException.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or OperationCanceledException)
            {
                failedCount++;
                DebugLog.Write($"Could not delete image '{item.Path}': {ex.Message}");
            }
        }

        if (deletedPaths.Count > 0)
        {
            _session.ApplyChanges([], [], deletedPaths);
        }

        if (failedCount > 0)
        {
            _header.SetCountText(failedCount == 1
                ? "Could not delete 1 image"
                : $"Could not delete {failedCount:n0} images");
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        IntPtr apidl,
        uint dwFlags);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    private void OpenInExplorer(string filePath)
    {
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                var windowsPath = Path.GetFullPath(filePath);
                bool success = false;
                try
                {
                    int hr = SHParseDisplayName(windowsPath, IntPtr.Zero, out IntPtr pidl, 0, out _);
                    if (hr == 0 && pidl != IntPtr.Zero)
                    {
                        try
                        {
                            int selectHr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
                            if (selectHr == 0)
                            {
                                success = true;
                            }
                        }
                        finally
                        {
                            ILFree(pidl);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"Failed to select image in Explorer shell API for {filePath}: {ex.Message}");
                }

                if (!success)
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{windowsPath}\"");
                }
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-R \"{filePath}\"",
                    UseShellExecute = true
                });
            }
            else
            {
                DebugLog.Observe(RevealOnFreedesktopAsync(filePath), $"Reveal {filePath}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to open file manager for {filePath}: {ex.Message}");
        }
    }

    // org.freedesktop.FileManager1.ShowItems opens the folder with the file selected; Dolphin, Nautilus, Nemo,
    // Thunar and Caja implement it. Without that service, open the containing folder instead.
    private async Task RevealOnFreedesktopAsync(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        try
        {
            using var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus"));
            await connection.ConnectAsync();
            MessageBuffer message;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader("org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                    "org.freedesktop.FileManager1", "ShowItems", "ass", MessageFlags.None);
                writer.WriteArray(new[] { new Uri(filePath).AbsoluteUri });
                writer.WriteString(""); // startup id
                message = writer.CreateMessage();
            }
            await connection.CallMethodAsync(message);
            return;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"FileManager1.ShowItems failed for {filePath}, opening the folder: {ex.Message}");
        }

        var dir = Path.GetDirectoryName(filePath);
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (dir is not null && launcher is not null)
        {
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir));
        }
    }
}
