using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace ComfyPromptViewer;

// The start screen. What it lists and does comes from StartMenuViewModel; this owns what needs a window: the
// folder picker, the About dialog, opening a folder in the file manager, and the recent-row gestures.
public partial class StartMenuView : UserControl
{
    public StartMenuView()
    {
        InitializeComponent();
    }

    internal StartMenuViewModel ViewModel => (StartMenuViewModel)DataContext!;

    internal void Initialize(StartMenuViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.OpenFolderInFileManager = OpenFolderInFileManagerAsync;
    }

    public void Open()
    {
        IsVisible = true;
        ViewModel.Open();
    }

    private async void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Open image folder") is { } folderPath)
        {
            ViewModel.RequestFolder(folderPath);
        }
    }

    private async void ThumbnailCacheLocationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Store thumbnails in") is { } folderPath)
        {
            await ViewModel.SetThumbnailCacheLocationAsync(folderPath);
        }
    }

    // Null when cancelled or when the picker failed. The picker is a platform service (a portal on Linux)
    // that can throw, and its callers are async void, so a failure is reported here rather than thrown.
    private async Task<string?> PickFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return null;
        }

        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

            return folders.FirstOrDefault()?.Path.LocalPath is { Length: > 0 } folderPath ? folderPath : null;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Folder picker failed: {ex}");
            ViewModel.ShowError($"Could not open the folder picker: {ex.Message}");
            return null;
        }
    }

    private async void AboutButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await new AboutWindow().ShowDialog(owner);
        }
    }

    private void RecentRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: RecentFolderViewModel folder } row &&
            e.GetCurrentPoint(row).Properties.IsLeftButtonPressed &&
            !IsInsideButton(e.Source as Visual, row))
        {
            folder.Open();
        }
    }

    private void RecentRow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is Border { DataContext: RecentFolderViewModel folder } row &&
            e.Source == row && e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            folder.Open();
        }
    }

    private static bool IsInsideButton(Visual? source, Visual row)
    {
        for (var visual = source; visual is not null && visual != row; visual = visual.GetVisualParent())
        {
            if (visual is Button)
            {
                return true;
            }
        }

        return false;
    }

    // ILauncher is the platform's own "hand this to the default handler" path, so there is no
    // explorer.exe / open / xdg-open branching to keep in sync, and no UseShellExecute behavior to
    // reason about under single-file publish.
    private async Task<bool> OpenFolderInFileManagerAsync(string folderPath)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null)
        {
            return false;
        }

        return await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Path.GetFullPath(folderPath)));
    }
}
