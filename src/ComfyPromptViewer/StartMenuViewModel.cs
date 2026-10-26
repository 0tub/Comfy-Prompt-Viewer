using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// The start screen: open a folder, recent folders, theme, and the maintenance panel. Opening a folder is the
// host's job, so it is a request; everything else on this screen is handled here.
public sealed class StartMenuViewModel : INotifyPropertyChanged
{
    private readonly UserPreferencesStore _preferences;
    private readonly CacheMaintenance _maintenance;
    private readonly Action<ThemeMode> _applyTheme;
    private readonly GenerationGate _recentFoldersGate = new();
    private ThemeMode _themeMode;
    private bool _includeSubfolders;
    private bool _prewarmThumbnails;
    private bool _softwareRendering;
    private bool _isAdvancedOpen;
    private string _errorMessage = "";
    private bool _isErrorVisible;
    private double _errorOpacity;

    internal StartMenuViewModel(
        UserPreferencesStore preferences,
        CacheMaintenance maintenance,
        Action<ThemeMode> applyTheme)
    {
        _preferences = preferences;
        _maintenance = maintenance;
        _applyTheme = applyTheme;
        _themeMode = preferences.LoadThemeMode();
        _prewarmThumbnails = preferences.LoadPrewarmThumbnails();
        _softwareRendering = preferences.LoadSoftwareRendering();

        maintenance.BusyChanged += () => Raise(nameof(IsMaintenanceEnabled));
        ClearAllCachesCommand = new RelayCommand(() => DebugLog.Observe(ClearAllCachesAsync(), "Clear all thumbnail caches"));
        ClearMetadataCacheCommand = new RelayCommand(ClearMetadataCache);
        OpenAppDataCommand = new RelayCommand(() => DebugLog.Observe(OpenAppDataAsync(), "Open app data folder"));
        DismissErrorCommand = new RelayCommand(() => DebugLog.Observe(DismissErrorAsync(), "Dismiss start menu error"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Opening a folder and flipping Include subfolders change what the window loads, so the host acts on them.
    internal event Action<string>? FolderOpenRequested;
    internal event Action<bool>? IncludeSubfoldersRequested;
    internal event Action<bool>? PrewarmThumbnailsChanged;

    // Set by the view; opening a folder in the file manager needs a window.
    public Func<string, Task<bool>>? OpenFolderInFileManager { get; set; }

    public ICommand ClearAllCachesCommand { get; }
    public ICommand ClearMetadataCacheCommand { get; }
    public ICommand OpenAppDataCommand { get; }
    public ICommand DismissErrorCommand { get; }

    public TransientStatus AdvancedStatus { get; } = new();
    public ObservableCollection<RecentFolderViewModel> RecentFolders { get; } = [];
    public bool HasRecentFolders => RecentFolders.Count > 0;
    public bool IsMaintenanceEnabled => !_maintenance.IsBusy;
    public string ThumbnailCacheRoot => _maintenance.ThumbnailCacheRoot;
    public string ErrorMessage => _errorMessage;
    public bool IsErrorVisible => _isErrorVisible;
    public double ErrorOpacity => _errorOpacity;

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

    // The window validates the change and may put it back, so it reports the real value through this.
    internal void SyncIncludeSubfolders(bool value)
    {
        if (_includeSubfolders != value)
        {
            _includeSubfolders = value;
            Raise(nameof(IncludeSubfolders));
        }
    }

    public int ThemeIndex
    {
        get => (int)_themeMode;
        set
        {
            if (value < 0 || value > (int)ThemeMode.Plum || value == (int)_themeMode)
            {
                return;
            }

            _themeMode = (ThemeMode)value;
            Raise();
            _applyTheme(_themeMode);
            _preferences.SaveThemeMode(_themeMode);
        }
    }

    public bool PrewarmThumbnails
    {
        get => _prewarmThumbnails;
        set
        {
            if (_prewarmThumbnails == value)
            {
                return;
            }

            _prewarmThumbnails = value;
            Raise();
            _preferences.SavePrewarmThumbnails(value);
            AdvancedStatus.Show(value ? "Background thumbnail caching on." : "Background thumbnail caching off.");
            PrewarmThumbnailsChanged?.Invoke(value);
        }
    }

    public bool SoftwareRendering
    {
        get => _softwareRendering;
        set
        {
            if (_softwareRendering == value)
            {
                return;
            }

            _softwareRendering = value;
            Raise();
            _preferences.SaveSoftwareRendering(value);
            AdvancedStatus.Show(value
                ? "Low-memory rendering on. Restart to apply."
                : "Low-memory rendering off. Restart to apply.");
        }
    }

    public bool IsAdvancedOpen
    {
        get => _isAdvancedOpen;
        set
        {
            if (_isAdvancedOpen == value)
            {
                return;
            }

            _isAdvancedOpen = value;
            Raise();
            if (!value)
            {
                AdvancedStatus.Clear();
            }
        }
    }

    // Shown each time the window returns to this screen.
    internal void Open()
    {
        _errorOpacity = 0;
        _isErrorVisible = false;
        Raise(nameof(ErrorOpacity));
        Raise(nameof(IsErrorVisible));
        RefreshRecentFolders();
    }

    internal async void ShowError(string message)
    {
        _errorMessage = message;
        _errorOpacity = 0;
        _isErrorVisible = true;
        Raise(nameof(ErrorMessage));
        Raise(nameof(ErrorOpacity));
        Raise(nameof(IsErrorVisible));
        await Task.Yield();
        _errorOpacity = 1;
        Raise(nameof(ErrorOpacity));
    }

    private async Task DismissErrorAsync()
    {
        if (!_isErrorVisible)
        {
            return;
        }

        _errorOpacity = 0;
        Raise(nameof(ErrorOpacity));
        await Task.Delay(120);
        if (_errorOpacity == 0)
        {
            _isErrorVisible = false;
            Raise(nameof(IsErrorVisible));
        }
    }

    internal void RequestFolder(string path) => FolderOpenRequested?.Invoke(path);

    internal void Close() => AdvancedStatus.Clear();

    internal void RefreshRecentFolders()
    {
        var existenceCheck = _recentFoldersGate.Begin();
        RecentFolders.Clear();
        Raise(nameof(HasRecentFolders));
        var saved = _preferences.LoadRecentFolders();
        if (saved.Count == 0)
        {
            return;
        }

        // Pinned first, each group keeping most-recent-first order.
        var recent = new List<RecentFolder>(saved.Count);
        recent.AddRange(saved.Where(folder => folder.IsPinned));
        recent.AddRange(saved.Where(folder => !folder.IsPinned));
        var displayNames = BuildRecentFolderDisplayNames(recent.Select(folder => folder.Path).ToList());

        for (var index = 0; index < recent.Count; index++)
        {
            RecentFolders.Add(new RecentFolderViewModel(this, recent[index], displayNames[index]));
        }

        Raise(nameof(HasRecentFolders));

        // Off the UI thread: Directory.Exists on an unreachable network mount can block for seconds.
        var rows = RecentFolders.ToArray();
        DebugLog.Observe(Task.Run(() =>
        {
            var exists = rows.Select(row => Directory.Exists(row.Path)).ToArray();
            Dispatcher.UIThread.Post(() =>
            {
                if (existenceCheck.IsStale)
                {
                    return;
                }

                for (var index = 0; index < rows.Length; index++)
                {
                    if (!exists[index])
                    {
                        rows[index].MarkMissing();
                    }
                }
            });
        }), "Recent folder existence check");
    }

    internal void SetPinned(RecentFolderViewModel folder, bool pinned)
    {
        _preferences.SetRecentFolderPinned(folder.Path, pinned);
        RefreshRecentFolders();
    }

    internal void RemoveRecent(RecentFolderViewModel folder)
    {
        var recent = _preferences.LoadRecentFolders();
        recent.RemoveAll(x => string.Equals(x.Path, folder.Path, StringComparison.OrdinalIgnoreCase));
        _preferences.SaveRecentFolders(recent);
        RefreshRecentFolders();
    }

    private async Task ClearAllCachesAsync()
    {
        if (_maintenance.IsBusy)
        {
            return;
        }

        var failure = await _maintenance.ClearAllAsync();
        if (failure is null)
        {
            AdvancedStatus.Show("All thumbnail caches cleared.");
        }
        else
        {
            AdvancedStatus.Clear();
            ShowError($"Could not clear cache: {failure.Message}");
        }
    }

    internal async Task SetThumbnailCacheLocationAsync(string location)
    {
        var root = AppPaths.ThumbnailCacheRoot(location);
        try
        {
            await _maintenance.SetThumbnailCacheRootAsync(root);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to move thumbnail cache to {root}: {ex}");
            AdvancedStatus.Clear();
            ShowError($"Could not use that folder for thumbnails: {ex.Message}");
            return;
        }

        _preferences.SaveThumbnailCacheLocation(location);
        Raise(nameof(ThumbnailCacheRoot));
        AdvancedStatus.Show($"Thumbnails now cached in {root}");
    }

    private void ClearMetadataCache()
    {
        var failure = _maintenance.ClearMetadata();
        if (failure is null)
        {
            AdvancedStatus.Show("Metadata cache cleared.");
        }
        else
        {
            AdvancedStatus.Clear();
            ShowError($"Could not clear metadata cache: {failure.Message}");
        }
    }

    private async Task OpenAppDataAsync()
    {
        try
        {
            Directory.CreateDirectory(_maintenance.ThumbnailCacheRoot);
            if (OpenFolderInFileManager is { } open && await open(AppPaths.LocalDataDirectory))
            {
                AdvancedStatus.Show("App data folder opened.");
            }
            else
            {
                AdvancedStatus.Clear();
                ShowError("Could not open the app data folder.");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to open app data folder: {ex}");
            AdvancedStatus.Clear();
            ShowError($"Could not open app data: {ex.Message}");
        }
    }

    // The leaf name alone is ambiguous when two recent folders share it (every ComfyUI install has an
    // "output"), so colliding names get their parent folder as a prefix.
    internal static string[] BuildRecentFolderDisplayNames(IReadOnlyList<string> paths)
    {
        var leaves = new string[paths.Count];
        var leafCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < paths.Count; index++)
        {
            var trimmed = paths[index].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            leaves[index] = string.IsNullOrEmpty(leaf) ? paths[index] : leaf;
            leafCounts[leaves[index]] = leafCounts.GetValueOrDefault(leaves[index]) + 1;
        }

        var names = new string[paths.Count];
        for (var index = 0; index < paths.Count; index++)
        {
            names[index] = leaves[index];
            if (leafCounts[leaves[index]] < 2)
            {
                continue;
            }

            var trimmed = paths[index].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetFileName(Path.GetDirectoryName(trimmed));
            if (!string.IsNullOrEmpty(parent))
            {
                names[index] = $"{parent}{Path.DirectorySeparatorChar}{leaves[index]}";
            }
        }

        return names;
    }

    internal static string GetRelativeTime(DateTime utcTime)
    {
        if (utcTime == DateTime.MinValue) return string.Empty;

        var span = DateTime.UtcNow - utcTime;
        if (span.TotalSeconds < 0) return "just now";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        return utcTime.ToLocalTime().ToString("MMM d, yyyy");
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

// One row of the recent list.
public sealed class RecentFolderViewModel : INotifyPropertyChanged
{
    private readonly StartMenuViewModel _menu;
    private string _metaText;
    private bool _isMissing;

    internal RecentFolderViewModel(StartMenuViewModel menu, RecentFolder folder, string displayName)
    {
        _menu = menu;
        Path = folder.Path;
        DisplayName = displayName;
        IsPinned = folder.IsPinned;

        var parts = new List<string>();
        if (folder.ImageCount >= 0)
        {
            parts.Add($"{folder.ImageCount:n0} image" + (folder.ImageCount == 1 ? "" : "s"));
        }

        var relativeTime = StartMenuViewModel.GetRelativeTime(folder.LastOpened);
        if (!string.IsNullOrEmpty(relativeTime))
        {
            parts.Add(relativeTime);
        }

        _metaText = string.Join(" • ", parts);
        TogglePinCommand = new RelayCommand(() => _menu.SetPinned(this, !IsPinned));
        RemoveCommand = new RelayCommand(() => _menu.RemoveRecent(this));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string DisplayName { get; }
    public bool IsPinned { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand RemoveCommand { get; }
    public string MetaText => _metaText;
    public bool IsMissing => _isMissing;
    public string OpenAutomationName => $"Open recent folder {DisplayName}";
    public string PinAutomationName => IsPinned ? $"Unpin {DisplayName}" : $"Pin {DisplayName}";
    public string PinToolTip => IsPinned ? "Unpin" : "Pin to top";
    public string RemoveAutomationName => $"Remove {DisplayName} from Recent";

    public void Open() => _menu.RequestFolder(Path);

    internal void MarkMissing()
    {
        _isMissing = true;
        _metaText = "Folder not found";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMissing)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetaText)));
    }
}
