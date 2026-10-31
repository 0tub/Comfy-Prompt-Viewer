using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// The metadata sidebar. What it shows comes from SidebarViewModel; this owns what needs controls: the
// highlighted prompt runs and their selection, the LoRA links, the prompt context menus and Ctrl+C.
public partial class SidebarView : UserControl
{
    private readonly SidebarViewModel _viewModel = new();
    private SelectableTextBlock? _activeContextMenuPromptText;

    public SidebarView()
    {
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.PromptTextsChanged += OnPromptTextsChanged;
        PromptScroller.ScrollChanged += (_, _) => UpdateFade(PromptScroller, PromptFade);
        NegativeScroller.ScrollChanged += (_, _) => UpdateFade(NegativeScroller, NegativeFade);
        _viewModel.ItemShown += FadeInContent;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public SidebarViewModel ViewModel => _viewModel;

    // The preview image and a clicked value are requests the host acts on: it owns the large preview and the
    // search box.
    public event Action? PreviewOpenRequested;
    public event Action<string>? SearchRequested;

    public void SetPreviewHeight(double height)
    {
        if (ContentGrid.RowDefinitions.Count > 0)
        {
            ContentGrid.RowDefinitions[0].Height = new GridLength(height, GridUnitType.Pixel);
        }
    }

    public void HideContent()
    {
        MetadataPanel.Opacity = 0;
    }

    private void FadeInContent()
    {
        MetadataPanel.Opacity = 0;
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel.HasItem)
            {
                MetadataPanel.Opacity = 1;
            }
        }, DispatcherPriority.Render);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // The toggles flip their own checked state on click; this keeps them in step when the model resets.
            case nameof(SidebarViewModel.IsPromptExpanded):
                PromptExpandButton.IsChecked = _viewModel.IsPromptExpanded;
                break;
            case nameof(SidebarViewModel.IsNegativeOpen):
                NegativeHeaderButton.IsChecked = _viewModel.IsNegativeOpen;
                break;
            case nameof(SidebarViewModel.IsNegativeExpanded):
                NegativeExpandButton.IsChecked = _viewModel.IsNegativeExpanded;
                break;
            case nameof(SidebarViewModel.LoraEntries):
                PopulateLoraList(_viewModel.LoraEntries);
                break;
        }
    }

    private void OnPromptTextsChanged(bool resetSelection)
    {
        SetHighlightedText(PromptBlock, _viewModel.Prompt, _viewModel.PromptTerms);
        SetHighlightedText(NegativeBlock, _viewModel.NegativePrompt, _viewModel.NegativeTerms);
        if (resetSelection)
        {
            PromptScroller.Offset = default;
            NegativeScroller.Offset = default;
            PromptBlock.SelectionStart = 0;
            PromptBlock.SelectionEnd = 0;
            NegativeBlock.SelectionStart = 0;
            NegativeBlock.SelectionEnd = 0;
        }
    }

    // Scrolled to the end, nothing is hidden below, so the fade would only cover the last lines.
    private static void UpdateFade(ScrollViewer scroller, Control fade) =>
        fade.Opacity = scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 1 ? 0 : 1;

    // Text always holds the plain prompt, which is what the copy commands and the collapse heuristic read.
    // Assigning a fresh InlineCollection renders the highlighted runs without touching Text; editing the
    // existing collection in place would clear it.
    private static void SetHighlightedText(SelectableTextBlock block, string text, IReadOnlyList<SearchTerm> terms)
    {
        block.Inlines = null;
        block.Text = text;

        var ranges = terms.Count == 0 ? [] : SearchEngine.FindMatchRanges(text, terms);
        if (ranges.Count == 0)
        {
            return;
        }

        var inlines = new InlineCollection();
        var cursor = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > cursor)
            {
                inlines.Add(new Run(text[cursor..start]));
            }

            var hit = new Run(text.Substring(start, length));
            hit.Classes.Add("search-hit");
            inlines.Add(hit);
            cursor = start + length;
        }

        if (cursor < text.Length)
        {
            inlines.Add(new Run(text[cursor..]));
        }

        block.Inlines = inlines;
    }

    private void PopulateLoraList(IReadOnlyList<(string Display, string Name)> entries)
    {
        LoraList.Children.Clear();
        for (var index = 0; index < entries.Count; index++)
        {
            var (display, name) = entries[index];
            var link = new TextBlock { Text = display, Tag = name, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            link.Classes.Add("sidebar-value");
            link.Classes.Add("search-link");
            ToolTip.SetTip(link, "Find images with this LoRA");
            link.PointerPressed += SearchValue_PointerPressed;

            // The comma sits outside the link so the underline covers only the name.
            var entry = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
            entry.Children.Add(link);
            if (index < entries.Count - 1)
            {
                var comma = new TextBlock { Text = "," };
                comma.Classes.Add("sidebar-value");
                entry.Children.Add(comma);
            }

            LoraList.Children.Add(entry);
        }
    }

    private void Image_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            PreviewOpenRequested?.Invoke();
        }
    }

    // Model, sampler, and seed are whole values, searched as exact terms. A LoRA link carries its bare name
    // in Tag and is searched as a plain term; SearchEngine.BuildValueQuery explains why.
    private void SearchValue_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock block || !e.GetCurrentPoint(block).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var query = block.Tag is string loraName
            ? SearchEngine.BuildValueQuery(loraName, exact: false)
            : SearchEngine.BuildValueQuery(block.Text ?? "", exact: true);
        if (query is null)
        {
            return;
        }

        e.Handled = true;
        SearchRequested?.Invoke(query);
    }

    // SelectableTextBlock copies on Ctrl+C itself but raises no event for it, so the confirmation toast is
    // driven from the key press. TextBox.CopyingToClipboardEvent used to cover this.
    private async void PromptText_KeyDown(object? sender, KeyEventArgs e)
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
        await CopyAndConfirmAsync(promptText, selection);
    }

    private SelectableTextBlock? GetTargetPromptText(object? sender)
    {
        if (sender is MenuItem item)
        {
            if (item.Name is "MenuCopyNegativeSelection" or "MenuCopyNegativeFullPrompt" or "MenuSelectNegativeAll")
            {
                return NegativeBlock;
            }

            if (item.Name is "MenuCopySelection" or "MenuCopyFullPrompt" or "MenuSelectAll")
            {
                return PromptBlock;
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

    private void PromptContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.PlacementTarget is SelectableTextBlock promptText)
        {
            _activeContextMenuPromptText = promptText;
            var copyItem = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Name is "MenuCopySelection" or "MenuCopyNegativeSelection");
            if (copyItem is not null)
            {
                copyItem.IsEnabled = !string.IsNullOrEmpty(promptText.SelectedText);
            }
        }
    }

    private async void MenuCopySelection_Click(object? sender, RoutedEventArgs e)
    {
        var promptText = GetTargetPromptText(sender) ?? PromptBlock;
        var selection = promptText.SelectedText;
        if (!string.IsNullOrEmpty(selection))
        {
            await CopyAndConfirmAsync(promptText, selection);
        }
    }

    private async void MenuCopyFullPrompt_Click(object? sender, RoutedEventArgs e)
    {
        var promptText = GetTargetPromptText(sender) ?? PromptBlock;
        if (!string.IsNullOrEmpty(promptText.Text))
        {
            await CopyAndConfirmAsync(promptText, promptText.Text);
        }
    }

    private void MenuSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        var promptText = GetTargetPromptText(sender) ?? PromptBlock;
        promptText.SelectAll();
    }

    // SelectableTextBlock has no caret and no focus adorner, so the old post-copy focus reset is gone; all
    // that is still wanted is dropping the highlight and confirming the copy.
    private async System.Threading.Tasks.Task CopyAndConfirmAsync(SelectableTextBlock promptText, string text)
    {
        if (_viewModel.CopyText is { } copy && await copy(text))
        {
            promptText.ClearSelection();
            _viewModel.FlashCopied(negative: promptText == NegativeBlock);
        }
    }
}
