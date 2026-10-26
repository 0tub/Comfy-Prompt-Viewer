using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// What the metadata sidebar shows for the selected image: the fields, the prompt collapse state, the copy
// buttons' feedback, and the preview bitmap's lifetime. The view owns what needs controls (highlighted runs,
// selection, the LoRA links); the host owns which image is selected and feeds it in.
public sealed class SidebarViewModel : INotifyPropertyChanged
{
    public const double CollapsedPromptMaxHeight = 168;
    // Intentional heuristics: they avoid a text layout pass. Use the measured rendered height if sidebar widths
    // become less predictable.
    private const int LongPromptCharacterThreshold = 500;
    private const int LongPromptLineThreshold = 7;
    private const string CopyLabel = "Copy";
    private const string CopiedLabel = "Copied!";

    private ImageItem? _item;
    private ImageItem? _previewOwner;
    private Bitmap? _previewImage;
    private bool _isEmpty = true;
    private string _title = "Metadata";
    private string? _titleTip;
    private bool _isHeaderDescVisible = true;
    private string _dimensions = "";
    private string _tool = "";
    private string _model = "";
    private string _sampler = "";
    private string _seed = "";
    private string _settings = "";
    private string _resources = "";
    private string _date = "";
    private string _lora = "";
    private IReadOnlyList<(string Display, string Name)> _loraEntries = [];
    private string _prompt = "";
    private string _negativePrompt = "";
    private bool _hasNegativePrompt;
    private bool _isPromptExpanded;
    private bool _isNegativeOpen;
    private bool _isNegativeExpanded;
    private IReadOnlyList<SearchTerm> _promptTerms = [];
    private IReadOnlyList<SearchTerm> _negativeTerms = [];
    private (ImageItem? Item, string Query, SearchScope Scope) _highlightKey;
    private string _promptCopyLabel = CopyLabel;
    private string _negativeCopyLabel = CopyLabel;
    private string _seedCopyLabel = CopyLabel;
    private bool _canCopyPrompt;
    private bool _canCopyNegative = true;
    private bool _canCopySeed = true;
    private double _promptCopiedOpacity;
    private double _negativeCopiedOpacity;
    private DispatcherTimer? _promptCopiedTimer;
    private DispatcherTimer? _negativeCopiedTimer;

    public SidebarViewModel()
    {
        CopyPromptCommand = new RelayCommand(() => DebugLog.Observe(CopyWithFeedbackAsync(CopyTarget.Prompt), "Copy prompt"));
        CopyNegativeCommand = new RelayCommand(() => DebugLog.Observe(CopyWithFeedbackAsync(CopyTarget.Negative), "Copy negative prompt"));
        CopySeedCommand = new RelayCommand(() => DebugLog.Observe(CopyWithFeedbackAsync(CopyTarget.Seed), "Copy seed"));
        TogglePromptExpandedCommand = new RelayCommand(() =>
        {
            _isPromptExpanded = !_isPromptExpanded;
            UpdatePromptPresentation();
        });
        ToggleNegativeOpenCommand = new RelayCommand(() =>
        {
            _isNegativeOpen = !_isNegativeOpen;
            UpdateNegativePresentation();
        });
        ToggleNegativeExpandedCommand = new RelayCommand(() =>
        {
            _isNegativeExpanded = !_isNegativeExpanded;
            UpdateNegativePresentation();
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // The prompt blocks hold highlighted runs, which only the view can build. The flag is true when the text
    // itself changed, as opposed to only the highlighted terms, so a selection the user is making survives a
    // query-only refresh.
    internal event Action<bool>? PromptTextsChanged;
    internal event Action? ItemShown;
    internal event Action? PreviewSwapped;
    internal event Action? PreviewReleasing;

    // Set by the host; the clipboard needs a window.
    public Func<string, Task<bool>>? CopyText { get; set; }

    public ICommand CopyPromptCommand { get; }
    public ICommand CopyNegativeCommand { get; }
    public ICommand CopySeedCommand { get; }
    public ICommand TogglePromptExpandedCommand { get; }
    public ICommand ToggleNegativeOpenCommand { get; }
    public ICommand ToggleNegativeExpandedCommand { get; }

    internal ImageItem? PreviewOwner => _previewOwner;
    internal IReadOnlyList<(string Display, string Name)> LoraEntries => _loraEntries;
    internal string Prompt => _prompt;
    internal string NegativePrompt => _negativePrompt;
    internal IReadOnlyList<SearchTerm> PromptTerms => _promptTerms;
    internal IReadOnlyList<SearchTerm> NegativeTerms => _negativeTerms;

    public Bitmap? PreviewImage => _previewImage;
    public bool IsEmpty => _isEmpty;
    public bool HasItem => !_isEmpty;
    public string Title => _title;
    public string? TitleTip => _titleTip;
    public bool IsHeaderDescVisible => _isHeaderDescVisible;
    public string Dimensions => _dimensions;
    public string Tool => _tool;
    public bool HasTool => !string.IsNullOrWhiteSpace(_tool);
    public string Model => _model;
    public bool HasModel => !string.IsNullOrWhiteSpace(_model);
    public string Sampler => _sampler;
    public bool HasSampler => !string.IsNullOrWhiteSpace(_sampler);
    public string Seed => _seed;
    public bool HasSeed => !string.IsNullOrWhiteSpace(_seed);
    public string Settings => _settings;
    public bool HasSettings => !string.IsNullOrWhiteSpace(_settings);
    public string Resources => _resources;
    public bool HasResources => !string.IsNullOrWhiteSpace(_resources);
    public bool HasLora => !string.IsNullOrWhiteSpace(_lora);
    public string Date => _date;

    public bool HasPrompt => _item?.HasPrompt == true;
    public bool IsNegativeSectionVisible => _hasNegativePrompt;
    public string PromptCopyLabel => _promptCopyLabel;
    public string NegativeCopyLabel => _negativeCopyLabel;
    public string SeedCopyLabel => _seedCopyLabel;
    public bool CanCopyPrompt => _canCopyPrompt;
    public bool CanCopyNegative => _canCopyNegative;
    public bool CanCopySeed => _canCopySeed;
    public double PromptCopiedOpacity => _promptCopiedOpacity;
    public double NegativeCopiedOpacity => _negativeCopiedOpacity;

    public bool IsPromptExpanded => _isPromptExpanded;
    public bool IsPromptLong => IsLong(_prompt);
    public bool IsPromptCollapsed => IsPromptLong && !_isPromptExpanded;
    public double PromptMaxHeight => IsPromptCollapsed ? CollapsedPromptMaxHeight : double.PositiveInfinity;
    public bool IsPromptPlaceholderVisible => _prompt.Length == 0;
    public string PromptExpandLabel => _isPromptExpanded ? "Show less" : "Show more";

    public bool IsNegativeOpen => _isNegativeOpen;
    public bool IsNegativeExpanded => _isNegativeExpanded;
    public bool IsNegativeLong => IsLong(_negativePrompt);
    public bool IsNegativeCollapsed => IsNegativeLong && !_isNegativeExpanded;
    public double NegativeMaxHeight => IsNegativeCollapsed ? CollapsedPromptMaxHeight : double.PositiveInfinity;
    public bool IsNegativePlaceholderVisible => _isNegativeOpen && _negativePrompt.Length == 0;
    public bool IsNegativeFadeVisible => _isNegativeOpen && IsNegativeCollapsed;
    public bool IsNegativeExpandVisible => _isNegativeOpen && IsNegativeLong;
    public string NegativeExpandLabel => _isNegativeExpanded ? "Show less" : "Show more";
    public string NegativeArrow => _isNegativeOpen ? "▾" : "▸";
    public Thickness SettingsMargin => new(0, _isNegativeOpen ? 20 : 8, 0, 0);

    // A new image: the negative prompt starts collapsed and both prompts start unexpanded.
    internal void Show(ImageItem item, string query, SearchScope scope)
    {
        _isNegativeOpen = false;
        _isPromptExpanded = false;
        _isNegativeExpanded = false;
        SetEmpty(false);
        Refresh(item, query, scope);
        ItemShown?.Invoke();
    }

    internal void Refresh(ImageItem item, string query, SearchScope scope)
    {
        _item = item;
        Set(ref _title, item.FileName, nameof(Title));
        Set(ref _titleTip, item.Path, nameof(TitleTip));
        Set(ref _isHeaderDescVisible, false, nameof(IsHeaderDescVisible));
        Set(ref _dimensions, item.DimensionsText, nameof(Dimensions));
        Set(ref _tool, item.Tool, nameof(Tool), nameof(HasTool));
        Set(ref _model, item.Model, nameof(Model), nameof(HasModel));
        Set(ref _sampler, item.Sampler, nameof(Sampler), nameof(HasSampler));
        Set(ref _seed, item.Seed, nameof(Seed), nameof(HasSeed));
        Set(ref _settings, item.Settings, nameof(Settings), nameof(HasSettings));
        Set(ref _resources, item.Resources, nameof(Resources), nameof(HasResources));
        Set(ref _date, item.CreationDateText, nameof(Date));
        _lora = item.Lora;
        _loraEntries = PromptExtractor.SplitLoraEntries(item.Lora);
        Raise(nameof(LoraEntries));
        Raise(nameof(HasLora));

        _prompt = item.HasPrompt ? item.Prompt : "";
        _negativePrompt = item.HasNegativePrompt ? item.NegativePrompt : "";
        _hasNegativePrompt = item.HasNegativePrompt;
        Raise(nameof(HasPrompt));
        Raise(nameof(IsNegativeSectionVisible));
        Set(ref _canCopyPrompt, item.HasPrompt, nameof(CanCopyPrompt));
        BuildTerms(item, query, scope);
        UpdatePromptPresentation();
        UpdateNegativePresentation();
        PromptTextsChanged?.Invoke(true);
        RefreshPreview(item);
    }

    public void Clear()
    {
        _item = null;
        SetEmpty(true);
        Set(ref _title, "Metadata", nameof(Title));
        Set<string?>(ref _titleTip, null, nameof(TitleTip));
        Set(ref _isHeaderDescVisible, true, nameof(IsHeaderDescVisible));
        _prompt = "";
        _negativePrompt = "";
        _hasNegativePrompt = false;
        _isNegativeOpen = false;
        _isPromptExpanded = false;
        _isNegativeExpanded = false;
        _promptTerms = [];
        _negativeTerms = [];
        Raise(nameof(HasPrompt));
        Raise(nameof(IsNegativeSectionVisible));
        UpdatePromptPresentation();
        UpdateNegativePresentation();
        PromptTextsChanged?.Invoke(true);
    }

    // A scanner-driven filter refresh with the same query does not rebuild the runs and wipe a selection.
    internal void RefreshHighlights(string query, SearchScope scope)
    {
        if (_item is not { } item || _highlightKey == (item, query, scope))
        {
            return;
        }

        BuildTerms(item, query, scope);
        PromptTextsChanged?.Invoke(false);
    }

    // One source, so there is no second state to transition from; binding the gallery thumbnail here too is
    // what produced the soft-then-sharp swap. The previous image holds until the new one lands.
    private void RefreshPreview(ImageItem item)
    {
        if (item.SelectedPreview is { } selectedPreview)
        {
            Set<Bitmap?>(ref _previewImage, selectedPreview, nameof(PreviewImage));
            var previousOwner = _previewOwner;
            _previewOwner = item;
            // The host detaches the overlay from the outgoing bitmap before its shared owner is disposed.
            PreviewSwapped?.Invoke();
            if (previousOwner is not null && !ReferenceEquals(previousOwner, item))
            {
                previousOwner.ReleaseSelectedPreview();
            }
        }
        else if (item.SelectedPreviewUnavailable)
        {
            // No replacement is coming, so holding on would show the wrong picture, not a lagging one.
            ReleasePreview();
        }
    }

    public void ReleasePreview()
    {
        var owner = _previewOwner;
        _previewOwner = null;
        Set<Bitmap?>(ref _previewImage, null, nameof(PreviewImage));
        PreviewReleasing?.Invoke();
        owner?.ReleaseSelectedPreview();
    }

    // Shown briefly next to a prompt after any route copies it (button, menu, Ctrl+C or the image menu).
    public void FlashCopied(bool negative)
    {
        ref var timer = ref (negative ? ref _negativeCopiedTimer : ref _promptCopiedTimer);
        timer?.Stop();
        SetCopiedOpacity(negative, 1);

        var next = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        next.Tick += (_, _) =>
        {
            next.Stop();
            SetCopiedOpacity(negative, 0);
        };
        timer = next;
        next.Start();
    }

    private void SetCopiedOpacity(bool negative, double value)
    {
        if (negative)
        {
            Set(ref _negativeCopiedOpacity, value, nameof(NegativeCopiedOpacity));
        }
        else
        {
            Set(ref _promptCopiedOpacity, value, nameof(PromptCopiedOpacity));
        }
    }

    private enum CopyTarget { Prompt, Negative, Seed }

    private async Task CopyWithFeedbackAsync(CopyTarget target)
    {
        if (_item is not { } item || CopyText is null)
        {
            return;
        }

        var text = target switch
        {
            CopyTarget.Prompt when item.HasPrompt => item.Prompt,
            CopyTarget.Negative when item.HasNegativePrompt => item.NegativePrompt,
            CopyTarget.Seed => item.Seed,
            _ => ""
        };
        if (string.IsNullOrWhiteSpace(text) || !await CopyText(text))
        {
            return;
        }

        SetCopyState(target, CopiedLabel, false);
        await Task.Delay(1000);
        SetCopyState(target, CopyLabel, target switch
        {
            CopyTarget.Prompt => _item?.HasPrompt == true,
            CopyTarget.Negative => _item?.HasNegativePrompt == true,
            _ => _item is not null && !string.IsNullOrWhiteSpace(_item.Seed)
        });
    }

    private void SetCopyState(CopyTarget target, string label, bool enabled)
    {
        switch (target)
        {
            case CopyTarget.Prompt:
                Set(ref _promptCopyLabel, label, nameof(PromptCopyLabel));
                Set(ref _canCopyPrompt, enabled, nameof(CanCopyPrompt));
                break;
            case CopyTarget.Negative:
                Set(ref _negativeCopyLabel, label, nameof(NegativeCopyLabel));
                Set(ref _canCopyNegative, enabled, nameof(CanCopyNegative));
                break;
            default:
                Set(ref _seedCopyLabel, label, nameof(SeedCopyLabel));
                Set(ref _canCopySeed, enabled, nameof(CanCopySeed));
                break;
        }
    }

    private void BuildTerms(ImageItem item, string query, SearchScope scope)
    {
        _highlightKey = (item, query, scope);
        var terms = new List<SearchTerm>();
        if (scope != SearchScope.Filename && !string.IsNullOrWhiteSpace(query))
        {
            SearchEngine.ParseQuery(query, out terms, out _);
        }

        _promptTerms = scope is SearchScope.All or SearchScope.PositivePrompt ? terms : [];
        _negativeTerms = scope is SearchScope.All or SearchScope.NegativePrompt ? terms : [];
    }

    private void UpdatePromptPresentation()
    {
        if (!IsPromptLong)
        {
            _isPromptExpanded = false;
        }

        Raise(nameof(IsPromptExpanded));
        Raise(nameof(IsPromptLong));
        Raise(nameof(IsPromptCollapsed));
        Raise(nameof(PromptMaxHeight));
        Raise(nameof(IsPromptPlaceholderVisible));
        Raise(nameof(PromptExpandLabel));
    }

    private void UpdateNegativePresentation()
    {
        if (!IsNegativeLong)
        {
            _isNegativeExpanded = false;
        }

        Raise(nameof(IsNegativeOpen));
        Raise(nameof(IsNegativeExpanded));
        Raise(nameof(IsNegativeLong));
        Raise(nameof(IsNegativeCollapsed));
        Raise(nameof(NegativeMaxHeight));
        Raise(nameof(IsNegativePlaceholderVisible));
        Raise(nameof(IsNegativeFadeVisible));
        Raise(nameof(IsNegativeExpandVisible));
        Raise(nameof(NegativeExpandLabel));
        Raise(nameof(NegativeArrow));
        Raise(nameof(SettingsMargin));
    }

    private void SetEmpty(bool empty)
    {
        if (_isEmpty != empty)
        {
            _isEmpty = empty;
            Raise(nameof(IsEmpty));
            Raise(nameof(HasItem));
        }
    }

    private static bool IsLong(string prompt)
    {
        return prompt.Length >= LongPromptCharacterThreshold ||
               prompt.AsSpan().Count('\n') >= LongPromptLineThreshold;
    }

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        foreach (var name in names)
        {
            Raise(name);
        }
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
