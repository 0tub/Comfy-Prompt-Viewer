using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private void SelectItem(ImageItem? item)
    {
        ClearSelectedItems();
        if (item is not null)
        {
            AddSelectedItem(item);
        }

        _selectionAnchor = item;
        SetActiveItem(item);
        UpdateCountText();
    }

    private void SetActiveItem(ImageItem? item)
    {
        if (_selectedItem == item)
        {
            return;
        }

        if (_selectedItem is not null)
        {
            _selectedItem.PropertyChanged -= SelectedItem_PropertyChanged;
            _selectedItem.IsSelected = false;

            // Released when its replacement is shown, not here; releasing now blanks the sidebar.
            if (!ReferenceEquals(_selectedItem, _sidebarPreviewOwner))
            {
                _selectedItem.ReleaseSelectedPreview();
            }
        }

        _selectedItem = item;
        if (item is null)
        {
            ReleaseSidebarPreview();
            SidebarEmpty.IsVisible = true;
            SidebarContent.IsVisible = false;
            SidebarMetadataPanel.Opacity = 0;
            SidebarImage.Source = null;
            SidebarPrompt.Text = "";
            SidebarPrompt.SelectionStart = 0;
            SidebarPrompt.SelectionEnd = 0;
            ApplyPositivePromptPresentation("");
            SidebarNegativePrompt.Text = "";
            SidebarNegativePromptContainer.IsVisible = false;
            CollapseNegativePrompt();
            ApplyNegativePromptPresentation("");

            SidebarHeaderTitle.Text = "Metadata";
            ToolTip.SetTip(SidebarHeaderTitle, null);
            SidebarHeaderDesc.IsVisible = true;
            return;
        }

        CollapseNegativePrompt();
        _isPositivePromptExpanded = false;
        _isNegativePromptExpanded = false;

        item.IsSelected = true;
        item.PropertyChanged += SelectedItem_PropertyChanged;
        if (_folderLoader.CurrentToken is { IsCancellationRequested: false } token)
        {
            _thumbnailLoads.EnqueueVisible(item, token);
            DebugLog.Observe(item.EnsureMetadataLoadedAsync(token), $"Selected metadata load for {item.Path}");
            if (!LargePreviewOverlay.IsVisible)
            {
                item.EnsureSelectedPreviewLoaded(token);
            }
        }

        SidebarEmpty.IsVisible = false;
        SidebarMetadataPanel.Opacity = 0;
        SidebarContent.IsVisible = true;
        RefreshSidebar(item);
        FadeInSidebarContent();

        if (LargePreviewOverlay.IsVisible)
        {
            UpdateLargePreview(resetZoom: true);
        }
    }

    private void AddSelectedItem(ImageItem item)
    {
        if (_selectedItems.Add(item))
        {
            item.IsMarkedSelected = true;
        }
    }

    private void RemoveSelectedItem(ImageItem item)
    {
        if (_selectedItems.Remove(item))
        {
            item.IsMarkedSelected = false;
        }
    }

    private void ClearSelectedItems()
    {
        foreach (var selectedItem in _selectedItems)
        {
            selectedItem.IsMarkedSelected = false;
        }

        _selectedItems.Clear();
    }

    private void FadeInSidebarContent()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (SidebarContent.IsVisible)
            {
                SidebarMetadataPanel.Opacity = 1;
            }
        }, DispatcherPriority.Render);
    }

    private void SidebarNegativePromptHeader_Click(object? sender, RoutedEventArgs e)
    {
        ToggleNegativePrompt();
        e.Handled = true;
    }

    private void ToggleNegativePrompt()
    {
        bool isVisible = !SidebarNegativePromptTextContainer.IsVisible;
        SidebarNegativePromptTextContainer.IsVisible = isVisible;
        SidebarCopyNegativePromptButton.IsVisible = isVisible;
        SidebarNegativePromptArrow.Text = isVisible ? "▾" : "▸";
        SidebarNegativePromptHeader.IsChecked = isVisible;
        ApplyNegativePromptPresentation(SidebarNegativePrompt.Text ?? "");
    }

    private void CollapseNegativePrompt()
    {
        SidebarNegativePromptTextContainer.IsVisible = false;
        SidebarCopyNegativePromptButton.IsVisible = false;
        SidebarNegativePromptFade.IsVisible = false;
        SidebarNegativePromptExpandButton.IsVisible = false;
        SidebarNegativePromptArrow.Text = "▸";
        SidebarNegativePromptHeader.IsChecked = false;
        _isNegativePromptExpanded = false;
    }

    private void SidebarPromptExpandButton_Click(object? sender, RoutedEventArgs e)
    {
        _isPositivePromptExpanded = !_isPositivePromptExpanded;
        ApplyPositivePromptPresentation(SidebarPrompt.Text ?? "");
        e.Handled = true;
    }

    private void SidebarNegativePromptExpandButton_Click(object? sender, RoutedEventArgs e)
    {
        _isNegativePromptExpanded = !_isNegativePromptExpanded;
        ApplyNegativePromptPresentation(SidebarNegativePrompt.Text ?? "");
        e.Handled = true;
    }

    private void ApplyPositivePromptPresentation(string prompt)
    {
        // Intentional heuristic: avoids a text layout pass; use measured rendered height if sidebar widths become less predictable.
        bool isLong = prompt.Length >= LongPositivePromptCharacterThreshold ||
                      prompt.AsSpan().Count('\n') >= LongPositivePromptLineThreshold;
        if (!isLong)
        {
            _isPositivePromptExpanded = false;
        }

        bool isCollapsed = isLong && !_isPositivePromptExpanded;
        SidebarPrompt.MaxHeight = isCollapsed ? CollapsedPositivePromptMaxHeight : double.PositiveInfinity;
        // Stands in for the TextBox watermark SelectableTextBlock does not have. Text stays empty so the
        // copy commands still see "no prompt".
        SidebarPromptPlaceholder.IsVisible = prompt.Length == 0;
        SidebarPromptFade.IsVisible = isCollapsed;
        SidebarPromptExpandButton.IsVisible = isLong;
        SidebarPromptExpandButton.IsChecked = _isPositivePromptExpanded;
        if (SidebarPromptExpandButton.Content is TextBlock label)
        {
            label.Text = _isPositivePromptExpanded ? "Show less" : "Show more";
        }
    }

    private void ApplyNegativePromptPresentation(string prompt)
    {
        // Intentional heuristic: mirrors positive prompt expansion without an extra text measurement pass.
        bool isLong = prompt.Length >= LongPositivePromptCharacterThreshold ||
                      prompt.AsSpan().Count('\n') >= LongPositivePromptLineThreshold;
        if (!isLong)
        {
            _isNegativePromptExpanded = false;
        }

        bool isCollapsed = isLong && !_isNegativePromptExpanded;
        bool isVisible = SidebarNegativePromptTextContainer.IsVisible;
        SidebarNegativePrompt.MaxHeight = isCollapsed ? CollapsedPositivePromptMaxHeight : double.PositiveInfinity;
        SidebarNegativePromptPlaceholder.IsVisible = isVisible && prompt.Length == 0;
        SidebarNegativePromptFade.IsVisible = isVisible && isCollapsed;
        SidebarNegativePromptExpandButton.IsVisible = isVisible && isLong;
        SidebarNegativePromptExpandButton.IsChecked = _isNegativePromptExpanded;
        if (SidebarNegativePromptExpandButton.Content is TextBlock label)
        {
            label.Text = _isNegativePromptExpanded ? "Show less" : "Show more";
        }
    }

    private void SelectedItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ImageItem item || item != _selectedItem || item == _queuedSelectedItemRefresh)
        {
            return;
        }

        _queuedSelectedItemRefresh = item;
        Dispatcher.UIThread.Post(() =>
        {
            if (_queuedSelectedItemRefresh == item)
            {
                _queuedSelectedItemRefresh = null;
                if (_selectedItem == item)
                {
                    RefreshSidebar(item);
                    if (LargePreviewOverlay.IsVisible)
                    {
                        UpdateLargePreview(resetZoom: false);
                    }
                }
            }
        }, DispatcherPriority.Background);
    }

    private void RefreshSidebar(ImageItem item)
    {
        SidebarHeaderTitle.Text = item.FileName;
        ToolTip.SetTip(SidebarHeaderTitle, item.Path);
        SidebarHeaderDesc.IsVisible = false;
        SidebarDimensions.Text = item.DimensionsText;

        SidebarTool.Text = item.Tool;
        SidebarToolRow.IsVisible = !string.IsNullOrWhiteSpace(item.Tool);

        SidebarModel.Text = item.Model;
        SidebarModelRow.IsVisible = !string.IsNullOrWhiteSpace(item.Model);

        SidebarSampler.Text = item.Sampler;
        SidebarSamplerRow.IsVisible = !string.IsNullOrWhiteSpace(item.Sampler);

        SidebarSeed.Text = item.Seed;
        SidebarSeedRow.IsVisible = !string.IsNullOrWhiteSpace(item.Seed);

        SidebarSettings.Text = item.Settings;
        SidebarSettingsRow.IsVisible = !string.IsNullOrWhiteSpace(item.Settings);

        SidebarLora.Text = item.Lora;
        SidebarLoraRow.IsVisible = !string.IsNullOrWhiteSpace(item.Lora);

        SidebarResources.Text = item.Resources;
        SidebarResourcesRow.IsVisible = !string.IsNullOrWhiteSpace(item.Resources);

        SidebarDate.Text = item.CreationDateText;
        SidebarPrompt.Text = item.HasPrompt ? item.Prompt : "";
        SidebarPrompt.SelectionStart = 0;
        SidebarPrompt.SelectionEnd = 0;
        SidebarCopyPromptButton.IsVisible = item.HasPrompt;
        SidebarCopyPromptButton.IsEnabled = item.HasPrompt;
        ApplyPositivePromptPresentation(SidebarPrompt.Text);

        SidebarNegativePrompt.Text = item.HasNegativePrompt ? item.NegativePrompt : "";
        SidebarNegativePrompt.SelectionStart = 0;
        SidebarNegativePrompt.SelectionEnd = 0;
        SidebarNegativePromptContainer.IsVisible = item.HasNegativePrompt;
        ApplyNegativePromptPresentation(SidebarNegativePrompt.Text);

        // One source, so there is no second state to transition from; binding the gallery thumbnail here
        // too is what produced the soft-then-sharp swap. The previous image holds until the new one lands.
        if (item.SelectedPreview is { } selectedPreview)
        {
            SidebarImage.Source = selectedPreview;
            var previousOwner = _sidebarPreviewOwner;
            _sidebarPreviewOwner = item;
            if (previousOwner is not null && !ReferenceEquals(previousOwner, item))
            {
                previousOwner.ReleaseSelectedPreview();
            }
        }
        else if (item.SelectedPreviewUnavailable)
        {
            // No replacement is coming, so holding on would show the wrong picture, not a lagging one.
            ReleaseSidebarPreview();
        }
    }

    private void ReleaseSidebarPreview()
    {
        var owner = _sidebarPreviewOwner;
        _sidebarPreviewOwner = null;
        SidebarImage.Source = null;
        owner?.ReleaseSelectedPreview();
    }

    private async void SidebarCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field } button || _selectedItem is not { } item)
        {
            return;
        }

        var text = field switch
        {
            "positive" when item.HasPrompt => item.Prompt,
            "negative" when item.HasNegativePrompt => item.NegativePrompt,
            "seed" => item.Seed,
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            await CopyWithButtonFeedbackAsync(button, text);
        }
    }

    private async Task CopyWithButtonFeedbackAsync(Button button, string text)
    {
        if (!await CopyTextAsync(text))
        {
            return;
        }

        var originalContent = button.Content;
        button.Content = "Copied!";
        button.IsEnabled = false;
        await Task.Delay(1000);
        button.Content = originalContent;
        button.IsEnabled = button == SidebarCopyPromptButton
            ? _selectedItem?.HasPrompt == true
            : button == SidebarCopyNegativePromptButton
                ? _selectedItem?.HasNegativePrompt == true
                : _selectedItem is not null && !string.IsNullOrWhiteSpace(_selectedItem.Seed);
    }

    private async Task<bool> CopyTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
            return true;
        }

        return false;
    }

    private void ShowCopiedToast(SelectableTextBlock promptText)
    {
        Border? badge = null;
        if (promptText == SidebarPrompt)
        {
            badge = PositiveCopiedBadge;
        }
        else if (promptText == SidebarNegativePrompt)
        {
            badge = NegativeCopiedBadge;
        }

        if (badge is not null)
        {
            if (badge.Tag is DispatcherTimer existingTimer)
            {
                existingTimer.Stop();
            }

            badge.Opacity = 1;

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.2)
            };
            timer.Tick += (s, e) =>
            {
                badge.Opacity = 0;
                timer.Stop();
                badge.Tag = null;
            };
            badge.Tag = timer;
            timer.Start();
        }
    }

    // SelectableTextBlock has no caret and no focus adorner, so the old post-copy focus reset is gone; all
    // that is still wanted is dropping the highlight and confirming the copy.
    private void AfterPromptCopy(SelectableTextBlock promptText)
    {
        promptText.ClearSelection();
        ShowCopiedToast(promptText);
    }

    // SelectableTextBlock copies on Ctrl+C itself but raises no event for it, so the confirmation toast is
    // driven from the key press. TextBox.CopyingToClipboardEvent used to cover this.
    private async void SidebarPromptText_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not SelectableTextBlock promptText ||
            e.Key != Key.C ||
            (e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            return;
        }

        var selection = promptText.SelectedText;
        if (string.IsNullOrEmpty(selection))
        {
            return;
        }

        e.Handled = true;
        if (await CopyTextAsync(selection))
        {
            AfterPromptCopy(promptText);
        }
    }

    private SelectableTextBlock? GetTargetPromptText(object? sender)
    {
        if (sender is MenuItem item)
        {
            if (item.Name == "MenuCopyNegativeSelection" ||
                item.Name == "MenuCopyNegativeFullPrompt" ||
                item.Name == "MenuSelectNegativeAll")
            {
                return SidebarNegativePrompt;
            }
            if (item.Name == "MenuCopySelection" ||
                item.Name == "MenuCopyFullPrompt" ||
                item.Name == "MenuSelectAll")
            {
                return SidebarPrompt;
            }
        }
        if (_activeContextMenuPromptText is not null)
        {
            return _activeContextMenuPromptText;
        }
        if (sender is MenuItem item2 && item2.Parent is ContextMenu menu)
        {
            return menu.PlacementTarget as SelectableTextBlock;
        }
        return null;
    }

    private void ContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            var promptText = menu.PlacementTarget as SelectableTextBlock;
            if (promptText is not null)
            {
                _activeContextMenuPromptText = promptText;
                var copyItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Name == "MenuCopySelection" || i.Name == "MenuCopyNegativeSelection");
                if (copyItem is not null)
                {
                    copyItem.IsEnabled = !string.IsNullOrEmpty(promptText.SelectedText);
                }
            }
        }
    }

    private async void MenuCopySelection_Click(object? sender, RoutedEventArgs e)
    {
        var promptText = GetTargetPromptText(sender) ?? SidebarPrompt;
        var selection = promptText.SelectedText;
        if (!string.IsNullOrEmpty(selection))
        {
            if (await CopyTextAsync(selection))
            {
                AfterPromptCopy(promptText);
            }
        }
    }

    private async void MenuCopyFullPrompt_Click(object? sender, RoutedEventArgs e)
    {
        var promptText = GetTargetPromptText(sender) ?? SidebarPrompt;
        if (!string.IsNullOrEmpty(promptText.Text))
        {
            if (await CopyTextAsync(promptText.Text))
            {
                AfterPromptCopy(promptText);
            }
        }
    }

    private void MenuSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        var promptText = GetTargetPromptText(sender) ?? SidebarPrompt;
        promptText.SelectAll();
    }

    private async void ImageContextMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string action } menuItem ||
            (menuItem.DataContext as ImageItem ?? _selectedItem) is not { } item)
        {
            return;
        }

        ImageItem[] actionItems;
        if (_selectedItems.Contains(item))
        {
            actionItems = _catalog.Items.Where(_selectedItems.Contains).ToArray();
            SetActiveItem(item);
        }
        else
        {
            SelectItem(item);
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
                    ShowCopiedToast(SidebarPrompt);
                }
                break;
            case "negative":
                if (item.HasNegativePrompt && await CopyTextAsync(item.NegativePrompt))
                {
                    ShowCopiedToast(SidebarNegativePrompt);
                }
                break;
            case "path":
                await CopyTextAsync(item.Path);
                break;
            case "delete":
                HideLargePreview();
                if (actionItems.Length == 1 || await ConfirmBulkDeleteAsync(actionItems.Length))
                {
                    DeleteImages(actionItems);
                }
                break;
        }
    }

    private void ImageContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu ||
            (contextMenu.DataContext as ImageItem ?? _selectedItem) is not { } item)
        {
            return;
        }

        var selectedCount = _selectedItems.Contains(item) ? _selectedItems.Count : 1;
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
                        ? _selectedItems.Any(selectedItem => selectedItem.HasPrompt)
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
        DeleteConfirmationTitle.Text = $"Delete {count:n0} images?";
        DeleteConfirmationText.Text = OperatingSystem.IsWindows()
            ? "The selected images will be moved to the Recycle Bin."
            : "The selected images will be permanently deleted.";
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                failedCount++;
                DebugLog.Write($"Could not delete image '{item.Path}': {ex.Message}");
            }
        }

        if (deletedPaths.Count > 0)
        {
            ProcessWatcherChanges([], [], deletedPaths);
        }

        if (failedCount > 0)
        {
            CountText.Text = failedCount == 1
                ? "Could not delete 1 image"
                : $"Could not delete {failedCount:n0} images";
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
                // Windows and macOS above reveal-and-select, which no cross-platform API exposes. Everywhere
                // else the best available behavior is opening the containing folder, and that is exactly
                // what ILauncher does without shelling out to xdg-open.
                var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                var launcher = TopLevel.GetTopLevel(this)?.Launcher;
                if (dir is not null && launcher is not null)
                {
                    DebugLog.Observe(
                        launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dir)),
                        $"Open containing folder for {filePath}");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to open file manager for {filePath}: {ex.Message}");
        }
    }
}
