using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public sealed class ImageItem : INotifyPropertyChanged
{
    private const int SmallThumbnailWidth = 180;
    private const int MediumThumbnailWidth = 240;
    private const int LargeThumbnailWidth = 320;
    private const int SelectedPreviewMaxWidth = 1200;
    private const double ThumbnailDecodeScale = 1.5;

    private Bitmap? _preview;
    private Bitmap? _selectedPreview;
    private int _width;
    private int _height;
    private string _prompt = "";
    private string _model = "";
    private string _sampler = "";
    private string _seed = "";
    private string _settings = "";
    private string _negativePrompt = "";
    private string _lora = "";
    private string _tool = "";
    private string _resources = "";
    private readonly object _thumbnailCacheStateLock = new();
    private string _thumbnailCachePath = "";
    private bool _thumbnailCacheExists;
    private bool _hasThumbnailCacheState;
    private bool _isSelected;
    private bool _isMarkedSelected;
    private volatile bool _hasLoadedMetadata;
    private MetadataLoadStatus _metadataLoadStatus;
    private readonly object _metadataLoadLock = new();
    private readonly ImageMetadataService _metadataService;
    private readonly DecodedImageCache _decodedImageCache;
    private readonly ThumbnailService _thumbnailService;
    private Task<MetadataLoadResult>? _metadataLoadTask;
    private int _realizedCount;
    private double _tileSize;
    private Task? _selectedPreviewLoadTask;
    private string? _creationDateText;
    private bool _hasLoggedThumbnailError;
    private SearchProjection _searchProjection;

    internal ImageItem(
        string path,
        SourceFingerprint sourceFingerprint,
        double tileSize,
        ImageMetadataService metadataService,
        DecodedImageCache decodedImageCache,
        ThumbnailService thumbnailService)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        SourceFingerprint = sourceFingerprint;
        _searchProjection = new SearchProjection(FileName, "", "", "", HasLoadedMetadata: false);
        _tileSize = tileSize;
        _metadataService = metadataService;
        _decodedImageCache = decodedImageCache;
        _thumbnailService = thumbnailService;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ImageItem>? MetadataLoaded;

    public string Path { get; }
    public string FileName { get; }
    internal SourceFingerprint SourceFingerprint { get; }
    internal SearchProjection SearchProjection => Volatile.Read(ref _searchProjection);

    public string CreationDateText
    {
        get
        {
            if (_creationDateText == null)
            {
                try
                {
                    var dt = File.GetCreationTime(Path);
                    _creationDateText = dt.ToString("yyyy-MM-dd HH:mm:ss");
                }
                catch (Exception ex)
                {
                    DebugLog.Write($"Failed to read creation time for {Path}: {ex.Message}");
                    _creationDateText = "Unknown";
                }
            }
            return _creationDateText;
        }
    }

    public System.Collections.Generic.LinkedListNode<ImageItem>? CacheNode { get; set; }
    internal long CachedPreviewBytes { get; set; }
    public bool HasLoadedMetadata => _hasLoadedMetadata;
    internal MetadataLoadStatus MetadataLoadStatus => _metadataLoadStatus;
    public bool IsRealized => _realizedCount > 0;
    internal long EstimatedPreviewBytes
    {
        get
        {
            var preview = Preview;
            return preview is null
                ? 0
                : Math.Max(1, preview.PixelSize.Width) * (long)Math.Max(1, preview.PixelSize.Height) * 4;
        }
    }

    public Bitmap? Preview
    {
        get => _preview;
        private set
        {
            if (SetField(ref _preview, value))
            {
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(ThumbnailOpacity));
            }
        }
    }

    public bool IsLoading => Preview == null;
    public double ThumbnailOpacity => Preview is null ? 0 : 1;

    public Bitmap? SelectedPreview
    {
        get => _selectedPreview;
        private set => SetField(ref _selectedPreview, value);
    }

    public string Prompt
    {
        get => _prompt;
        private set
        {
            if (SetField(ref _prompt, value))
            {
                OnPropertyChanged(nameof(HasPrompt));
            }
        }
    }

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);

    public string NegativePrompt
    {
        get => _negativePrompt;
        private set
        {
            if (SetField(ref _negativePrompt, value))
            {
                OnPropertyChanged(nameof(HasNegativePrompt));
            }
        }
    }

    public bool HasNegativePrompt => !string.IsNullOrWhiteSpace(NegativePrompt);

    public string Tool
    {
        get => _tool;
        private set => SetField(ref _tool, value);
    }

    public string Model
    {
        get => _model;
        private set
        {
            if (SetField(ref _model, value))
            {
                OnPropertyChanged(nameof(HasGenerationSettings));
            }
        }
    }

    public string Sampler
    {
        get => _sampler;
        private set
        {
            if (SetField(ref _sampler, value))
            {
                OnPropertyChanged(nameof(HasGenerationSettings));
            }
        }
    }

    public string Seed
    {
        get => _seed;
        private set
        {
            if (SetField(ref _seed, value))
            {
                OnPropertyChanged(nameof(HasGenerationSettings));
            }
        }
    }

    public string Settings
    {
        get => _settings;
        private set
        {
            if (SetField(ref _settings, value))
            {
                OnPropertyChanged(nameof(HasGenerationSettings));
            }
        }
    }

    public bool HasGenerationSettings => !string.IsNullOrWhiteSpace(Model) ||
                                         !string.IsNullOrWhiteSpace(Sampler) ||
                                         !string.IsNullOrWhiteSpace(Seed) ||
                                         !string.IsNullOrWhiteSpace(Settings);

    public string Lora
    {
        get => _lora;
        private set
        {
            if (SetField(ref _lora, value))
            {
                OnPropertyChanged(nameof(HasLora));
            }
        }
    }

    public bool HasLora => !string.IsNullOrWhiteSpace(Lora);

    public string Resources
    {
        get => _resources;
        private set => SetField(ref _resources, value);
    }

    public void SetTileSize(double tileSize)
    {
        if (Math.Abs(_tileSize - tileSize) < 0.1)
        {
            return;
        }

        _tileSize = tileSize;
        InvalidateThumbnailCacheState();
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            SetField(ref _isSelected, value);
        }
    }

    public bool IsMarkedSelected
    {
        get => _isMarkedSelected;
        set => SetField(ref _isMarkedSelected, value);
    }

    public string DimensionsText => _width > 0 && _height > 0 ? $"{_width} x {_height}" : "Unknown size";

    public int Width => _width;
    public int Height => _height;

    public void MarkRealized()
    {
        _realizedCount++;
    }

    public void MarkUnrealized()
    {
        if (_realizedCount > 0)
        {
            _realizedCount--;
        }
    }

    public async Task EnsureMetadataLoadedAsync(CancellationToken token)
    {
        if (_hasLoadedMetadata)
        {
            return;
        }

        try
        {
            var result = await GetMetadataLoadResultAsync(
                skipCacheLookup: false,
                persistResult: true,
                token);
            if (result.NeedsSave)
            {
                await Task.Run(() => _metadataService.Save(result), token);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    ApplyMetadataResult(result);
                }
            });
        }
        catch (OperationCanceledException)
        {
            _metadataLoadStatus = MetadataLoadStatus.Cancelled;
        }
        catch (Exception ex)
        {
            if (ex is not InvalidDataException { Message: "Invalid PNG signature." })
            {
                DebugLog.Write($"Failed to load metadata for {Path}: {ex}");
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MarkMetadataLoaded();
            });
        }
    }

    internal Task<MetadataLoadResult> GetMetadataLoadResultAsync(
        bool skipCacheLookup,
        bool persistResult,
        CancellationToken token)
    {
        lock (_metadataLoadLock)
        {
            if (_metadataLoadTask is null || _metadataLoadTask.IsCanceled || _metadataLoadTask.IsFaulted)
            {
                _metadataLoadTask = _metadataService.LoadAsync(
                    Path,
                    SourceFingerprint,
                    skipCacheLookup,
                    persistResult,
                    token);
            }

            return _metadataLoadTask.WaitAsync(token);
        }
    }

    internal void ApplyMetadataResult(MetadataLoadResult result)
    {
        if (_hasLoadedMetadata)
        {
            return;
        }

        _metadataLoadStatus = result.Status;
        if (result.Entry is { } entry)
        {
            ApplyMetadataValues(entry);
        }
        MarkMetadataLoaded();

        if (result.Exception is { } exception &&
            exception is not InvalidDataException { Message: "Invalid PNG signature." })
        {
            DebugLog.Write($"Failed to load metadata for {Path}: {exception}");
        }
    }

    internal void ApplyMetadataEntry(MetadataIndexEntry entry)
    {
        if (_hasLoadedMetadata)
        {
            return;
        }

        _metadataLoadStatus = MetadataLoadStatus.Success;
        ApplyMetadataValues(entry);
        MarkMetadataLoaded();
    }

    private void ApplyMetadataValues(MetadataIndexEntry entry)
    {
        _width = entry.Width;
        _height = entry.Height;
        Prompt = entry.Prompt;
        NegativePrompt = entry.NegativePrompt;
        Tool = entry.Tool;
        Model = entry.Model;
        Sampler = entry.Sampler;
        Seed = entry.Seed;
        Settings = entry.Settings;
        Lora = entry.Lora;
        Resources = entry.Resources;
        OnPropertyChanged(nameof(DimensionsText));
        OnPropertyChanged(nameof(IsLoading));
    }

    public void EnsureSelectedPreviewLoaded(CancellationToken token)
    {
        if (_selectedPreviewLoadTask is { IsCompleted: false } || SelectedPreview is not null)
        {
            return;
        }

        _selectedPreviewLoadTask = LoadSelectedPreviewAsync(token);
    }

    public void LoadSelectedPreviewSync()
    {
        if (SelectedPreview is not null || _selectedPreviewLoadTask is { IsCompleted: false })
        {
            return;
        }

        try
        {
            var decodeWidth = _width > 0 ? Math.Min(_width, SelectedPreviewMaxWidth) : SelectedPreviewMaxWidth;
            SelectedPreview = _thumbnailService.DecodeSelectedPreview(Path, decodeWidth);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load selected preview synchronously: {ex}");
        }
    }

    public void ReleasePreview(bool skipIfCached = false)
    {
        if (skipIfCached && CacheNode is not null)
        {
            return;
        }

        _decodedImageCache.Remove(this);

        if (Preview is not null)
        {
            Preview.Dispose();
            Preview = null;
        }
    }

    public void ReleaseSelectedPreview()
    {
        if (SelectedPreview is IDisposable disposable)
        {
            disposable.Dispose();
        }

        SelectedPreview = null;
    }

    private async Task LoadSelectedPreviewAsync(CancellationToken token)
    {
        try
        {
            await _thumbnailService.SelectedPreviewLoadLimiter.WaitAsync(token);
            try
            {
                if (!IsSelected)
                {
                    return;
                }

                var bitmap = await Task.Run(() =>
                {
                    var decodeWidth = _width > 0 ? Math.Min(_width, SelectedPreviewMaxWidth) : SelectedPreviewMaxWidth;
                    return _thumbnailService.DecodeSelectedPreview(Path, decodeWidth);
                }, token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !IsSelected)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    if (SelectedPreview is not null)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    SelectedPreview = bitmap;
                });
            }
            finally
            {
                _thumbnailService.SelectedPreviewLoadLimiter.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load selected preview for {Path}: {ex}");
        }
    }

    public async Task LoadThumbnailAsync(CancellationToken token, Func<bool>? isCurrent = null)
    {
        if (Preview is not null || token.IsCancellationRequested || isCurrent?.Invoke() == false)
        {
            if (Preview is not null)
            {
                _decodedImageCache.Touch(this);
            }
            return;
        }

        try
        {
            var bitmap = await Task.Run(() =>
            {
                if (token.IsCancellationRequested || isCurrent?.Invoke() == false)
                {
                    return null;
                }

                var (cachePath, hasCachedThumbnail) = GetThumbnailCacheState();
                if (hasCachedThumbnail)
                {
                    try
                    {
                        return _thumbnailService.LoadCachedThumbnail(cachePath);
                    }
                    catch (Exception ex)
                    {
                        if (!_hasLoggedThumbnailError)
                        {
                            _hasLoggedThumbnailError = true;
                            DebugLog.Write($"Failed to load cached thumbnail for {Path} at {cachePath}: {ex.Message}. Re-decoding...");
                        }
                        try
                        {
                            File.Delete(cachePath);
                        }
                        catch (Exception deleteEx)
                        {
                            DebugLog.Write($"Failed to delete corrupt cached thumbnail {cachePath}: {deleteEx.Message}");
                        }
                        SetThumbnailCacheState(cachePath, exists: false);
                    }
                }

                if (token.IsCancellationRequested || isCurrent?.Invoke() == false || MainWindow.IsFastScrolling)
                {
                    return null;
                }

                var decoded = _thumbnailService.DecodeThumbnail(Path, GetThumbnailDecodeWidth());
                if (!string.IsNullOrEmpty(cachePath) &&
                    isCurrent?.Invoke() != false &&
                    !MainWindow.IsFastScrolling)
                {
                    _thumbnailService.TryQueueCacheWrite(this, cachePath);
                }
                return decoded;
            }, token);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || isCurrent?.Invoke() == false)
                {
                    bitmap?.Dispose();
                    return;
                }

                if (bitmap is null)
                {
                    return;
                }

                if (Preview is not null)
                {
                    bitmap.Dispose();
                    _decodedImageCache.Touch(this);
                    return;
                }

                Preview = bitmap;
                _decodedImageCache.Touch(this);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_hasLoggedThumbnailError)
            {
                _hasLoggedThumbnailError = true;
                DebugLog.Write($"Failed to load thumbnail for {Path}: {ex.Message}");
            }
        }
    }

    private void MarkMetadataLoaded()
    {
        if (_hasLoadedMetadata)
        {
            return;
        }

        _hasLoadedMetadata = true;
        Volatile.Write(ref _searchProjection, new SearchProjection(
            FileName,
            Prompt,
            NegativePrompt,
            SearchEngine.NormalizeSeparators(string.Join(
                '\0',
                Tool,
                Model,
                Sampler,
                Seed,
                Settings,
                Lora,
                Resources)),
            HasLoadedMetadata: true));
        MetadataLoaded?.Invoke(this);
    }

    private (string CachePath, bool Exists) GetThumbnailCacheState()
    {
        lock (_thumbnailCacheStateLock)
        {
            if (_hasThumbnailCacheState)
            {
                return (_thumbnailCachePath, _thumbnailCacheExists);
            }
        }

        RefreshThumbnailCacheState();

        lock (_thumbnailCacheStateLock)
        {
            return (_thumbnailCachePath, _thumbnailCacheExists);
        }
    }

    private void RefreshThumbnailCacheState()
    {
        var cachePath = _thumbnailService.BuildCachePath(Path, GetThumbnailDecodeWidth());
        var exists = !string.IsNullOrEmpty(cachePath) && File.Exists(cachePath);
        SetThumbnailCacheState(cachePath, exists);
    }

    internal int GetThumbnailDecodeWidth()
    {
        var targetWidth = (int)Math.Ceiling(_tileSize * ThumbnailDecodeScale);
        if (targetWidth <= SmallThumbnailWidth)
        {
            return SmallThumbnailWidth;
        }

        return targetWidth <= MediumThumbnailWidth ? MediumThumbnailWidth : LargeThumbnailWidth;
    }

    internal void InvalidateThumbnailCacheState()
    {
        lock (_thumbnailCacheStateLock)
        {
            _hasThumbnailCacheState = false;
        }
    }

    internal void SetThumbnailCacheState(string cachePath, bool exists)
    {
        lock (_thumbnailCacheStateLock)
        {
            _thumbnailCachePath = cachePath;
            _thumbnailCacheExists = exists;
            _hasThumbnailCacheState = true;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed record SearchProjection(
    string FileName,
    string Prompt,
    string NegativePrompt,
    string NormalizedSettingsText,
    bool HasLoadedMetadata);
