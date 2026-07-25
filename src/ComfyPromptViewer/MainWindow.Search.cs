using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public partial class MainWindow
{
    private void SearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        if (ClearSearchButton != null)
        {
            ClearSearchButton.IsVisible = hasText;
        }

        _hasSearchQueryActive = !string.IsNullOrWhiteSpace(SearchTextBox.Text);
        CancelSearchFilter();

        if (_searchDebounceTimer == null)
        {
            _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _searchDebounceTimer.Tick += (s, ev) =>
            {
                _searchDebounceTimer.Stop();
                ApplyFilter(resetScroll: true);
            };
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void SearchScopeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ApplyFilter(resetScroll: true);
    }

    private void ClearSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        SearchTextBox.Focus();
    }

    private void SearchHelpButton_Click(object? sender, RoutedEventArgs e)
    {
        SearchHelpPopup.IsOpen = !SearchHelpPopup.IsOpen;
        e.Handled = true;
    }

    private SearchScope GetSearchScope()
    {
        return SearchScopeComboBox.SelectedIndex switch
        {
            (int)SearchScope.PositivePrompt => SearchScope.PositivePrompt,
            (int)SearchScope.NegativePrompt => SearchScope.NegativePrompt,
            (int)SearchScope.Filename => SearchScope.Filename,
            _ => SearchScope.All
        };
    }

    private void ApplyFilter(bool resetScroll = false)
    {
        var query = SearchTextBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(query))
        {
            CancelSearchFilter();
            ApplyFilteredItems(_catalog.Items, resetScroll);
            return;
        }

        SearchEngine.ParseQuery(query, out var positiveTerms, out var negativeTerms);
        if (positiveTerms.Count == 0 && negativeTerms.Count == 0)
        {
            CancelSearchFilter();
            ApplyFilteredItems(_catalog.Items, resetScroll);
            return;
        }

        var searchScope = GetSearchScope();
        var candidates = new SearchCandidate[_catalog.Count];
        for (var index = 0; index < candidates.Length; index++)
        {
            var item = _catalog.Items[index];
            candidates[index] = new SearchCandidate(item, item.SearchProjection);
        }

        CancelAndDispose(ref _searchFilterCancellation);
        _searchFilterCancellation = new CancellationTokenSource();
        var token = _searchFilterCancellation.Token;
        var generation = ++_searchFilterGeneration;
        var dataGeneration = _searchDataGeneration;
        DebugLog.Observe(Task.Run(() =>
        {
            var matches = new List<ImageItem>(candidates.Length);
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (ProjectionMatchesSearch(
                    candidate.Projection,
                    positiveTerms,
                    negativeTerms,
                    searchScope))
                {
                    matches.Add(candidate.Item);
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (IsCurrentSearchResult(
                    generation,
                    _searchFilterGeneration,
                    dataGeneration,
                    _searchDataGeneration,
                    token.IsCancellationRequested))
                {
                    ApplyFilteredItems(matches, resetScroll);
                }
            }, DispatcherPriority.Background);
        }, token), "Gallery search filter");
    }

    private void ApplyFilteredItems(IReadOnlyList<ImageItem> filtered, bool resetScroll)
    {
        var hasChanges = _viewModel.Items.Count != filtered.Count;
        if (!hasChanges)
        {
            for (var index = 0; index < filtered.Count; index++)
            {
                if (!ReferenceEquals(_viewModel.Items[index], filtered[index]))
                {
                    hasChanges = true;
                    break;
                }
            }
        }

        if (hasChanges)
        {
            var scrollAnchor = resetScroll ? null : CaptureGalleryScrollAnchor();
            if (resetScroll)
            {
                _viewModel.Items.Clear();
                _viewModel.Items.AddRange(filtered);
            }
            else if (
                scrollAnchor is null &&
                _viewModel.Items.Count > 0 &&
                filtered.Count > 0 &&
                !ReferenceEquals(_viewModel.Items[0], filtered[0]))
            {
                _viewModel.Items.Clear();
                _viewModel.Items.AddRange(filtered);
            }
            else
            {
                SynchronizeGalleryItems(filtered);
            }
            GalleryItems.InvalidateMeasure();
            GalleryScrollViewer.InvalidateMeasure();
            RestoreGalleryScrollAnchor(scrollAnchor, filtered);
        }

        if (resetScroll)
        {
            _galleryScrollRestoreGeneration++;
            GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, 0);
        }

        UpdateCountText();
        QueueViewportThumbnailSchedule();

        bool showEmpty = filtered.Count == 0 && _catalog.Count > 0;
        ToggleGalleryEmptyState(showEmpty);
    }

    private void SynchronizeGalleryItems(IReadOnlyList<ImageItem> filtered)
    {
        if (!CanSynchronizeGalleryItemsIncrementally(_viewModel.Items, filtered, MaxIncrementalGalleryChanges))
        {
            _viewModel.Items.Clear();
            _viewModel.Items.AddRange(filtered);
            return;
        }

        var targetItems = new HashSet<ImageItem>(filtered);
        for (var index = 0; index < filtered.Count;)
        {
            var item = filtered[index];
            if (index == _viewModel.Items.Count)
            {
                _viewModel.Items.Insert(index, item);
                index++;
                continue;
            }

            if (ReferenceEquals(_viewModel.Items[index], item))
            {
                index++;
                continue;
            }

            if (!targetItems.Contains(_viewModel.Items[index]))
            {
                _viewModel.Items.RemoveAt(index);
                continue;
            }

            _viewModel.Items.Insert(index, item);
            index++;
        }

        while (_viewModel.Items.Count > filtered.Count)
        {
            _viewModel.Items.RemoveAt(_viewModel.Items.Count - 1);
        }
    }

    internal static bool CanSynchronizeGalleryItemsIncrementally(
        IReadOnlyList<ImageItem> currentItems,
        IReadOnlyList<ImageItem> targetItems,
        int maximumChanges)
    {
        if (maximumChanges < 0)
        {
            return false;
        }

        var targetSet = new HashSet<ImageItem>(targetItems);
        if (targetSet.Count != targetItems.Count)
        {
            return false;
        }

        var currentIndexes = new Dictionary<ImageItem, int>(currentItems.Count);
        var changeCount = 0;
        for (var index = 0; index < currentItems.Count; index++)
        {
            var item = currentItems[index];
            if (!currentIndexes.TryAdd(item, index))
            {
                return false;
            }

            if (!targetSet.Contains(item) && ++changeCount > maximumChanges)
            {
                return false;
            }
        }

        var lastCurrentIndex = -1;
        foreach (var item in targetItems)
        {
            if (currentIndexes.TryGetValue(item, out var currentIndex))
            {
                if (currentIndex < lastCurrentIndex)
                {
                    return false;
                }

                lastCurrentIndex = currentIndex;
            }
            else if (++changeCount > maximumChanges)
            {
                return false;
            }
        }

        // Small insert/delete batches keep realized cards stable. Larger changes use one reset.
        return true;
    }

    private GalleryScrollAnchor? CaptureGalleryScrollAnchor()
    {
        var offset = GalleryScrollViewer.Offset.Y;
        if (offset <= 0.5 || _viewModel.Items.Count == 0)
        {
            return null;
        }

        var columns = GetGalleryColumnCount();
        var firstVisibleRow = Math.Max(0, (int)Math.Floor(offset / Math.Max(1, _tileItemExtent)));
        var index = Math.Min(_viewModel.Items.Count - 1, firstVisibleRow * columns);
        return new GalleryScrollAnchor(_viewModel.Items[index], index, offset);
    }

    private void RestoreGalleryScrollAnchor(GalleryScrollAnchor? anchor, IReadOnlyList<ImageItem> filtered)
    {
        if (anchor is not { } value)
        {
            return;
        }

        var newIndex = -1;
        for (var index = 0; index < filtered.Count; index++)
        {
            if (ReferenceEquals(filtered[index], value.Item))
            {
                newIndex = index;
                break;
            }
        }
        var columns = GetGalleryColumnCount();
        var desiredOffset = CalculateAnchoredGalleryOffset(
            value.OldIndex,
            newIndex,
            columns,
            _tileItemExtent,
            value.Offset,
            double.PositiveInfinity);

        // Uniform fixed-height rows make a row anchor sufficient. Variable-height tiles would need realized-element anchoring.
        QueueGalleryScrollRestore(desiredOffset);
    }

    private void QueueGalleryScrollRestore(double desiredOffset)
    {
        var restoreGeneration = ++_galleryScrollRestoreGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            ApplyGalleryScrollRestore(restoreGeneration, desiredOffset);
        }, DispatcherPriority.Loaded);
    }

    private void ApplyGalleryScrollRestore(int restoreGeneration, double desiredOffset)
    {
        if (restoreGeneration != _galleryScrollRestoreGeneration)
        {
            return;
        }

        var viewportHeight = GalleryScrollViewer.Viewport.Height > 0
            ? GalleryScrollViewer.Viewport.Height
            : Math.Max(1, GalleryScrollViewer.Bounds.Height);
        var maxOffset = GalleryScrollViewer.Extent.Height > 0
            ? Math.Max(0, GalleryScrollViewer.Extent.Height - viewportHeight)
            : desiredOffset;
        var restoredOffset = Math.Clamp(desiredOffset, 0, maxOffset);
        GalleryScrollViewer.Offset = new Vector(GalleryScrollViewer.Offset.X, restoredOffset);
    }

    internal static double CalculateAnchoredGalleryOffset(
        int oldIndex,
        int newIndex,
        int columns,
        double itemExtent,
        double oldOffset,
        double maxOffset)
    {
        columns = Math.Max(1, columns);
        itemExtent = Math.Max(1, itemExtent);
        if (newIndex < 0)
        {
            return Math.Clamp(oldOffset, 0, maxOffset);
        }

        var offsetWithinRow = oldOffset - ((oldIndex / columns) * itemExtent);
        return Math.Clamp(((newIndex / columns) * itemExtent) + offsetWithinRow, 0, maxOffset);
    }

    internal static bool ItemMatchesSearch(
        ImageItem item,
        List<SearchTerm> positiveTerms,
        List<SearchTerm> negativeTerms,
        SearchScope searchScope)
    {
        return ProjectionMatchesSearch(item.SearchProjection, positiveTerms, negativeTerms, searchScope);
    }

    internal static bool ProjectionMatchesSearch(
        SearchProjection projection,
        List<SearchTerm> positiveTerms,
        List<SearchTerm> negativeTerms,
        SearchScope searchScope)
    {
        return searchScope switch
        {
            SearchScope.Filename => TextMatchesTerms(projection.FileName, positiveTerms, negativeTerms),
            SearchScope.PositivePrompt => projection.HasLoadedMetadata
                ? TextMatchesTerms(projection.Prompt, positiveTerms, negativeTerms)
                : true,
            SearchScope.NegativePrompt => projection.HasLoadedMetadata
                ? TextMatchesTerms(projection.NegativePrompt, positiveTerms, negativeTerms)
                : true,
            _ => ProjectionMatchesAllSearch(projection, positiveTerms, negativeTerms)
        };
    }

    private static bool ProjectionMatchesAllSearch(
        SearchProjection projection,
        List<SearchTerm> positiveTerms,
        List<SearchTerm> negativeTerms)
    {
        foreach (var term in negativeTerms)
        {
            if (ProjectionMatchesAnySearchableText(projection, term))
            {
                return false;
            }
        }

        foreach (var term in positiveTerms)
        {
            if (SearchEngine.IsMatch(projection.FileName, term))
            {
                continue;
            }

            if (!projection.HasLoadedMetadata)
            {
                return true;
            }

            if (!ProjectionMetadataMatchesTerm(projection, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ProjectionMatchesAnySearchableText(SearchProjection projection, SearchTerm term)
    {
        return SearchEngine.IsMatch(projection.FileName, term) ||
               projection.HasLoadedMetadata && ProjectionMetadataMatchesTerm(projection, term);
    }

    private static bool ProjectionMetadataMatchesTerm(SearchProjection projection, SearchTerm term)
    {
        return SearchEngine.IsMatch(projection.Prompt, projection.NegativePrompt, term) ||
               SearchEngine.IsMatch(
                   projection.NormalizedSettingsText,
                   term with { Text = term.NormalizedText });
    }

    private static bool TextMatchesTerms(
        string text,
        List<SearchTerm> positiveTerms,
        List<SearchTerm> negativeTerms)
    {
        foreach (var term in positiveTerms)
        {
            if (!SearchEngine.IsMatch(text, term))
            {
                return false;
            }
        }

        foreach (var term in negativeTerms)
        {
            if (SearchEngine.IsMatch(text, term))
            {
                return false;
            }
        }

        return true;
    }

    private void CancelSearchFilter()
    {
        CancelAndDispose(ref _searchFilterCancellation);
        _searchFilterGeneration++;
    }

    internal static bool IsCurrentSearchResult(
        int resultGeneration,
        int currentGeneration,
        int resultDataGeneration,
        int currentDataGeneration,
        bool isCancellationRequested)
    {
        return !isCancellationRequested &&
               resultGeneration == currentGeneration &&
               resultDataGeneration == currentDataGeneration;
    }

    private readonly record struct SearchCandidate(ImageItem Item, SearchProjection Projection);
}
