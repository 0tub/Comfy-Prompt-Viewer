using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private FolderChangeMonitor? _folderChangeMonitor;

    private void StartFolderWatcher(string folderPath, bool includeSubfolders)
    {
        StopFolderWatcher();
        var loadGeneration = _folderLoader.Generation;
        FolderChangeMonitor? monitor = null;
        try
        {
            monitor = new FolderChangeMonitor(
                folderPath,
                includeSubfolders,
                batch => Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(monitor, _folderChangeMonitor) &&
                        _folderLoader.IsCurrent(loadGeneration))
                    {
                        ProcessWatcherChanges(
                            batch.AddedFiles,
                            batch.ChangedFiles,
                            batch.DeletedPaths);
                    }
                }),
                exception => Dispatcher.UIThread.Post(() =>
                {
                    DebugLog.Write($"Folder watcher failed: {exception}");
                    if (ReferenceEquals(monitor, _folderChangeMonitor) &&
                        _folderLoader.IsCurrent(loadGeneration) &&
                        string.Equals(folderPath, _currentFolderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        CountText.Text = "Folder changed rapidly; refreshing...";
                        DebugLog.Observe(LoadFolderAsync(folderPath), "Folder watcher recovery reload");
                    }
                }, DispatcherPriority.Background));
            _folderChangeMonitor = monitor;
        }
        catch (Exception ex)
        {
            monitor?.Dispose();
            DebugLog.Write($"Failed to start folder watcher for {folderPath}: {ex.Message}");
        }
    }

    private void StopFolderWatcher()
    {
        var monitor = _folderChangeMonitor;
        _folderChangeMonitor = null;
        monitor?.Dispose();
    }

    private void ProcessWatcherChanges(
        IReadOnlyList<ImageFileEntry> addedFiles,
        IReadOnlyList<ImageFileEntry> changedFiles,
        IReadOnlyList<string> deletedPaths)
    {
        bool changed = false;
        bool needsSort = false;
        var itemsToScan = new List<ImageItem>();

        if (deletedPaths.Count > 0)
        {
            DebugLog.Observe(
                Task.Run(() => _metadataRepository.DeletePaths(deletedPaths)),
                "MetadataRepository.DeletePaths");
            var deletedSet = new HashSet<string>(deletedPaths, StringComparer.OrdinalIgnoreCase);
            var removedEntries = _catalog.RemovePaths(deletedSet);
            if (removedEntries.Count > 0)
            {
                changed = true;
            }

            foreach (var entry in removedEntries)
            {
                var item = entry.Item;
                item.MetadataLoaded -= ImageItem_MetadataLoaded;
                RemoveSelectedItem(item);
                if (_selectionAnchor == item)
                {
                    _selectionAnchor = null;
                }

                if (_selectedItem == item)
                {
                    SetActiveItem(_viewModel.Items.FirstOrDefault(candidate =>
                        _selectedItems.Contains(candidate) && !deletedSet.Contains(candidate.Path)));
                }
            }
        }

        if (addedFiles.Count > 0 || changedFiles.Count > 0)
        {
            var newFiles = new List<ImageFileEntry>(addedFiles.Count + changedFiles.Count);
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ProcessChangedFile(ImageFileEntry imageFile)
            {
                if (!processedPaths.Add(imageFile.Path))
                {
                    return;
                }

                if (!_catalog.TryGet(imageFile.Path, out var existingEntry))
                {
                    newFiles.Add(imageFile);
                    return;
                }

                var previousItem = existingEntry.Item;
                var replacementItem = CreateImageItem(imageFile.Path, imageFile.Fingerprint);
                _catalog.Replace(
                    imageFile.Path,
                    new GalleryEntry(imageFile.Path, imageFile.Fingerprint, replacementItem),
                    out _);
                previousItem.MetadataLoaded -= ImageItem_MetadataLoaded;
                previousItem.ReleasePreview();
                if (_selectedItems.Contains(previousItem))
                {
                    RemoveSelectedItem(previousItem);
                    AddSelectedItem(replacementItem);
                }
                if (_selectionAnchor == previousItem)
                {
                    _selectionAnchor = replacementItem;
                }
                if (_selectedItem == previousItem)
                {
                    SetActiveItem(replacementItem);
                }

                itemsToScan.Add(replacementItem);
                changed = true;
                needsSort = true;
            }

            foreach (var imageFile in changedFiles)
            {
                ProcessChangedFile(imageFile);
            }

            foreach (var imageFile in addedFiles)
            {
                ProcessChangedFile(imageFile);
            }

            var useSortedInsertion = !needsSort && newFiles.Count <= MaxIncrementalGalleryChanges;
            foreach (var addedFile in newFiles)
            {
                var path = addedFile.Path;
                var item = CreateImageItem(path, addedFile.Fingerprint);
                var entry = new GalleryEntry(path, addedFile.Fingerprint, item);
                if (useSortedInsertion)
                {
                    var insertIndex = _catalog.FindSortedInsertIndex(entry, CompareGalleryEntries);
                    _catalog.Insert(insertIndex, entry);
                }
                else
                {
                    _catalog.Add(entry);
                }

                itemsToScan.Add(item);
                changed = true;
            }

            if ((!useSortedInsertion && newFiles.Count > 0) || needsSort)
            {
                ApplySort();
            }
        }

        if (changed)
        {
            if (itemsToScan.Count > 0)
            {
                _metadataScanner.ScanAdded(
                    itemsToScan,
                    HasSearchQueryActive,
                    () => ApplyFilter(resetScroll: false));
            }

            ApplyFilter(resetScroll: false);
        }
    }

}
