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
    private const int TinyThumbnailWidth = 120;
    private const int SmallThumbnailWidth = 180;
    private const int MediumThumbnailWidth = 240;
    private const int LargeThumbnailWidth = 320;
    private const double ThumbnailDecodeScale = 1.5;

    private Bitmap? _preview;
    private Bitmap? _selectedPreview;
    private bool _selectedPreviewUnavailable;
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
    private readonly object _thumbnailKeyLock = new();
    private ThumbnailKey _thumbnailKey;
    private int _thumbnailKeyWidth = -1;
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

    // Row in the catalog's SearchIndex, or -1 when not in a catalog. Only SearchIndex assigns this.
    internal int SearchSlot { get; set; } = -1;

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
        private set
        {
            if (SetField(ref _selectedPreview, value) && value is not null)
            {
                SelectedPreviewUnavailable = false;
            }
        }
    }

    // Separates "finished and produced nothing" from "still decoding", so a caller holding the previous
    // image can tell that no replacement is coming.
    public bool SelectedPreviewUnavailable
    {
        get => _selectedPreviewUnavailable;
        private set => SetField(ref _selectedPreviewUnavailable, value);
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

        SelectedPreviewUnavailable = false;
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
            SelectedPreview = _thumbnailService.DecodeSelectedPreview(Path, _width, _height);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load selected preview synchronously: {ex}");
            if (SelectedPreview is null)
            {
                SelectedPreviewUnavailable = true;
            }
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
        SelectedPreviewUnavailable = false;
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
                    return _thumbnailService.DecodeSelectedPreview(Path, _width, _height);
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
            Dispatcher.UIThread.Post(() =>
            {
                if (IsSelected && SelectedPreview is null)
                {
                    SelectedPreviewUnavailable = true;
                }
            });
        }
    }

    // isStillWanted gates only the cold path: a pack miss followed by a full-resolution source decode.
    public async Task LoadThumbnailAsync(
        CancellationToken token,
        Func<bool>? isCurrent = null,
        Func<bool>? isStillWanted = null)
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

                var cacheKey = GetThumbnailKey();
                try
                {
                    if (_thumbnailService.TryLoadCachedThumbnail(cacheKey) is { } cached)
                    {
                        return cached;
                    }
                }
                catch (Exception ex)
                {
                    // The pack already dropped the unreadable entry; fall through and re-encode it.
                    if (!_hasLoggedThumbnailError)
                    {
                        _hasLoggedThumbnailError = true;
                        DebugLog.Write($"Failed to load cached thumbnail for {Path}: {ex.Message}. Re-decoding...");
                    }
                }

                if (token.IsCancellationRequested || isCurrent?.Invoke() == false || isStillWanted?.Invoke() == false)
                {
                    return null;
                }

                var decoded = _thumbnailService.DecodeThumbnail(Path, GetThumbnailDecodeWidth());
                if (isCurrent?.Invoke() != false && isStillWanted?.Invoke() != false)
                {
                    _thumbnailService.TryQueueCacheWrite(this, cacheKey);
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

        // The completed task holds a second copy of the entry for as long as the folder stays open. The
        // values it carried are on this item now, and every caller checks HasLoadedMetadata before asking
        // again, so nothing re-reads it.
        lock (_metadataLoadLock)
        {
            _metadataLoadTask = null;
        }

        MetadataLoaded?.Invoke(this);
    }

    // Memoized per width, so a scroll pass costs a comparison and a tile-size change invalidates it.
    internal ThumbnailKey GetThumbnailKey()
    {
        var width = GetThumbnailDecodeWidth();
        lock (_thumbnailKeyLock)
        {
            if (_thumbnailKeyWidth != width)
            {
                _thumbnailKey = ThumbnailPack.CreateKey(
                    Path,
                    SourceFingerprint.LastWriteTimeUtcTicks,
                    width);
                _thumbnailKeyWidth = width;
            }

            return _thumbnailKey;
        }
    }

    internal int GetThumbnailDecodeWidth()
    {
        // Buckets track ThumbnailDecodeScale closely so a tile never decodes far wider than it renders.
        // Without the tiny bucket the smallest tiles decoded at 180px, over twice their display width,
        // which is what pushes the decoded cache past its byte budget when many tiles are visible.
        var targetWidth = (int)Math.Ceiling(_tileSize * ThumbnailDecodeScale);
        if (targetWidth <= TinyThumbnailWidth)
        {
            return TinyThumbnailWidth;
        }

        if (targetWidth <= SmallThumbnailWidth)
        {
            return SmallThumbnailWidth;
        }

        return targetWidth <= MediumThumbnailWidth ? MediumThumbnailWidth : LargeThumbnailWidth;
    }

    // One pack-index lookup, so there is no cached existence state to keep fresh.
    internal bool HasCachedThumbnail()
    {
        return _thumbnailService.HasCachedThumbnail(GetThumbnailKey());
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
