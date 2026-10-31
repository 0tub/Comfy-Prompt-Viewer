using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// Which image is active (shown in the sidebar and the preview) and how the sidebar is fed. What the sidebar
// shows is SidebarViewModel's business; this only decides when to tell it.
public partial class MainWindow
{
    private void InitializeSidebar()
    {
        Sidebar.ViewModel.CopyText = CopyTextAsync;
        Sidebar.PreviewOpenRequested += ShowLargePreview;
        Sidebar.SearchRequested += Header.AppendSearchTerm;
        // The overlay has to let go of the outgoing bitmap before the sidebar disposes its shared owner.
        Sidebar.ViewModel.PreviewSwapped += () =>
        {
            if (LargePreview.IsOpen)
            {
                UpdateLargePreview(resetZoom: false);
            }
        };
        Sidebar.ViewModel.PreviewReleasing += LargePreview.ClearImage;
        _viewModel.ActiveItemChanged += OnActiveItemChanged;
        _viewModel.SelectionChanged += UpdateCountText;
    }

    private void InitializeGallery()
    {
        Gallery.Initialize(this, _viewModel, _thumbnailLoads, _session, _targetTileSize);
        Gallery.IsPreviewOpen = () => LargePreview.IsOpen;
        Gallery.PreviewRequested += ShowLargePreview;
        Gallery.ItemsApplied += UpdateCountText;
        Gallery.ContextMenuOpening += ImageContextMenu_Opening;
        Gallery.ContextMenuActionInvoked += ImageContextMenu_ActionInvoked;
    }

    // The active image changed: the sidebar, the preview and the metadata and preview loads follow it.
    private void OnActiveItemChanged(ImageItem? previous, ImageItem? current)
    {
        _session.SelectedPath = current?.Path;
        if (previous is not null)
        {
            previous.PropertyChanged -= SelectedItem_PropertyChanged;

            // Released when its replacement is shown, not here; releasing now blanks the sidebar.
            if (!ReferenceEquals(previous, Sidebar.ViewModel.PreviewOwner))
            {
                previous.ReleaseSelectedPreview();
            }
        }

        if (current is null)
        {
            // A folder close clears the selection without hiding the overlay; a playing video must not keep going.
            LargePreview.StopVideo();
            Sidebar.ViewModel.ReleasePreview();
            Sidebar.HideContent();
            Sidebar.ViewModel.Clear();
            return;
        }

        current.PropertyChanged += SelectedItem_PropertyChanged;
        if (_session.CurrentToken is { IsCancellationRequested: false } token)
        {
            _thumbnailLoads.EnqueueVisible(current, token);
            DebugLog.Observe(_metadataScanner.EnsureLoadedAsync(current, token), "Selected metadata load for", current.Path);
            _selectedPreviews.EnsureLoaded(current, token);
        }

        Sidebar.ViewModel.Show(current, _header.SearchText, _header.SearchScope);

        if (LargePreview.IsOpen)
        {
            UpdateLargePreview(resetZoom: true);
        }
    }

    private void SelectedItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ImageItem item || item != _viewModel.ActiveItem || item == _queuedSelectedItemRefresh)
        {
            return;
        }

        _queuedSelectedItemRefresh = item;
        Dispatcher.UIThread.Post(() =>
        {
            if (_queuedSelectedItemRefresh == item)
            {
                _queuedSelectedItemRefresh = null;
                if (_viewModel.ActiveItem == item)
                {
                    Sidebar.ViewModel.Refresh(item, _header.SearchText, _header.SearchScope);
                    if (LargePreview.IsOpen)
                    {
                        UpdateLargePreview(resetZoom: false);
                    }
                }
            }
        }, DispatcherPriority.Background);
    }

    private void RefreshPromptHighlightsIfQueryChanged()
    {
        if (_viewModel.ActiveItem is not null)
        {
            Sidebar.ViewModel.RefreshHighlights(_header.SearchText, _header.SearchScope);
        }
    }
}
