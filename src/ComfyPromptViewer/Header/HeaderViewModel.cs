using System;
using System.Windows.Input;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// The folder header: where you are, the count, sort, subfolders, search and tile size. It holds the values the
// user can change and tells the window when they change; what the window does about it (loading a folder,
// filtering the grid, resizing tiles) is the window's business.
public sealed class HeaderViewModel : ObservableObject
{
    public const double DefaultTileSize = 120;
    public const double MinTileSize = 80;
    public const double MaxTileSize = 320;
    public const double TileSizeStep = 10;

    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(150);

    private readonly CacheMaintenance _maintenance;
    private DispatcherTimer? _searchTimer;
    private string _folderTitle = "Comfy Prompt Viewer";
    private string _folderPath = "Open an image folder to start scrolling.";
    private bool _isCopyPathVisible;
    private string _countText = "0 images";
    private int _sortIndex = (int)SortMode.NewestFirst;
    private bool _includeSubfolders;
    private string _searchText = "";
    private int _searchScopeIndex = (int)SearchScope.All;
    private bool _isSearchHelpOpen;
    private double _tileSize;

    internal HeaderViewModel(CacheMaintenance maintenance, double tileSize)
    {
        _maintenance = maintenance;
        _tileSize = tileSize;
        maintenance.BusyChanged += () => Raise(nameof(IsClearCacheEnabled));
        BackCommand = new RelayCommand(() => BackRequested?.Invoke());
        CopyPathCommand = new RelayCommand(() => CopyPathRequested?.Invoke());
        ClearFolderCacheCommand = new RelayCommand(() => ClearFolderCacheRequested?.Invoke());
    }

    public event Action? BackRequested;
    public event Action? CopyPathRequested;
    public event Action? ClearFolderCacheRequested;
    internal event Action<SortMode>? SortChanged;
    internal event Action<bool>? IncludeSubfoldersRequested;
    // Raised on every edit, before the debounce: the window cancels a filter that is still running.
    internal event Action? SearchEdited;
    // Raised once the user pauses typing, or at once when the scope changes.
    internal event Action? FilterRequested;
    internal event Action<double>? TileSizeChanged;

    public ICommand BackCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand ClearFolderCacheCommand { get; }

    public TransientStatus FolderCacheStatus { get; } = new();

    public string FolderTitle => _folderTitle;
    public string FolderPath => _folderPath;
    public bool IsCopyPathVisible => _isCopyPathVisible;
    public string CountText => _countText;
    public bool IsClearCacheEnabled => !_maintenance.IsBusy;
    public bool HasSearchText => _searchText.Length > 0;
    public double TileSizeMin => MinTileSize;
    public double TileSizeMax => MaxTileSize;
    public double TileSizeTick => TileSizeStep;
    public string TileSizeText => $"{_tileSize:0}";

    // The combo boxes list the enum members in declaration order, so an index is the enum value. The setters
    // validate it, which keeps these casts total.
    internal SortMode SortMode => (SortMode)_sortIndex;
    internal SearchScope SearchScope => (SearchScope)_searchScopeIndex;

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            // -1 is an unselected combo, not a request.
            if (!Enum.IsDefined((SortMode)value) || value == _sortIndex)
            {
                return;
            }

            _sortIndex = value;
            Raise();
            SortChanged?.Invoke(SortMode);
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (_includeSubfolders != value)
            {
                _includeSubfolders = value;
                Raise();
                IncludeSubfoldersRequested?.Invoke(value);
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= "";
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            Raise();
            Raise(nameof(HasSearchText));
            SearchEdited?.Invoke();
            RestartSearchTimer();
        }
    }

    public int SearchScopeIndex
    {
        get => _searchScopeIndex;
        set
        {
            if (value < 0)
            {
                return;
            }

            value = Enum.IsDefined((SearchScope)value) ? value : (int)SearchScope.All;
            if (value == _searchScopeIndex)
            {
                return;
            }

            _searchScopeIndex = value;
            Raise();
            FilterRequested?.Invoke();
        }
    }

    public bool IsSearchHelpOpen
    {
        get => _isSearchHelpOpen;
        set
        {
            if (_isSearchHelpOpen != value)
            {
                _isSearchHelpOpen = value;
                Raise();
            }
        }
    }

    public double TileSize
    {
        get => _tileSize;
        set
        {
            value = NormalizeTileSize(value);
            if (Math.Abs(_tileSize - value) < 0.1)
            {
                return;
            }

            _tileSize = value;
            Raise();
            Raise(nameof(TileSizeText));
            TileSizeChanged?.Invoke(value);
        }
    }

    // The window reports a value it settled on (a clamped tile size, the validated subfolders flag) without it
    // counting as a request.
    internal void SyncTileSize(double value)
    {
        if (Math.Abs(_tileSize - value) >= 0.1)
        {
            _tileSize = value;
            Raise(nameof(TileSize));
            Raise(nameof(TileSizeText));
        }
    }

    internal void SyncIncludeSubfolders(bool value)
    {
        if (_includeSubfolders != value)
        {
            _includeSubfolders = value;
            Raise(nameof(IncludeSubfolders));
        }
    }

    // Every tile size that arrives (saved file, slider) goes through this once, so nothing downstream
    // re-clamps or re-rounds it.
    internal static double NormalizeTileSize(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(Math.Round(value / TileSizeStep) * TileSizeStep, MinTileSize, MaxTileSize)
            : DefaultTileSize;
    }

    // The count line: how many images, how far the prompt scan and thumbnail caching are, and how many are
    // selected. Pure, so the wording can be checked without a window.
    internal static string FormatCount(
        int total,
        int filtered,
        int loadedMetadata,
        bool showScanProgress,
        bool isScanning,
        int prewarmRemaining,
        int prewarmTotal,
        int selected)
    {
        var counts = total == filtered
            ? $"{total:n0} images"
            : $"{filtered:n0} of {total} images";
        var text = showScanProgress
            ? $"{counts} (Scanning prompts {loadedMetadata}/{total})"
            : counts;

        // Counted against thumbnails that were actually missing, not against the folder: a fully cached
        // folder has no pass to report on.
        if (!isScanning && prewarmRemaining > 0 && prewarmTotal > 0)
        {
            text += $" (Caching thumbnails {prewarmTotal - prewarmRemaining:n0}/{prewarmTotal:n0})";
        }

        if (selected > 1)
        {
            text += $" | {selected:n0} selected";
        }

        return text;
    }

    internal void SetFolder(string title, string path)
    {
        Set(ref _folderTitle, title, nameof(FolderTitle));
        Set(ref _folderPath, path, nameof(FolderPath));
        Set(ref _isCopyPathVisible, true, nameof(IsCopyPathVisible));
    }

    internal void SetCountText(string text)
    {
        Set(ref _countText, text, nameof(CountText));
    }

    // A search typed for one folder rarely means anything in another.
    internal void ResetSearch()
    {
        SearchText = "";
        SearchScopeIndex = (int)SearchScope.All;
    }

    internal void Close()
    {
        _searchTimer?.Stop();
        FolderCacheStatus.Clear();
    }

    private void RestartSearchTimer()
    {
        if (_searchTimer is null)
        {
            _searchTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = SearchDebounce };
            _searchTimer.Tick += (_, _) =>
            {
                _searchTimer.Stop();
                FilterRequested?.Invoke();
            };
        }

        _searchTimer.Stop();
        _searchTimer.Start();
    }
}
