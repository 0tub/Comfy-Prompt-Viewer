using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ComfyPromptViewer;

public sealed class ImageItem : INotifyPropertyChanged
{
    public static readonly string ThumbnailCacheRootDir = System.IO.Path.Combine(UserPreferences.AppDataDir, "thumbnails");

    static ImageItem()
    {
        try
        {
            Directory.CreateDirectory(ThumbnailCacheRootDir);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to create thumbnail cache root {ThumbnailCacheRootDir}: {ex.Message}");
        }
    }

    private string BuildThumbnailCachePath()
    {
        try
        {
            var parentDirectory = Directory.GetParent(Path);
            var parentPath = parentDirectory?.FullName ?? "";
            var parentName = parentDirectory?.Name;
            if (string.IsNullOrWhiteSpace(parentName))
            {
                parentName = "root";
            }

            var folderHash = HashText(parentPath).Substring(0, ThumbnailFolderHashLength);
            var safeParentName = MakeSafePathSegment(parentName);
            var cacheDir = System.IO.Path.Combine(ThumbnailCacheRootDir, $"{safeParentName}_{folderHash}");
            Directory.CreateDirectory(cacheDir);

            var lastWriteTime = File.GetLastWriteTimeUtc(Path).Ticks;
            var thumbnailWidth = GetThumbnailDecodeWidth();
            var input = $"{Path}_{lastWriteTime}";
            var hash = HashText(input);
            return System.IO.Path.Combine(cacheDir, $"w{thumbnailWidth}_{hash}.jpg");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to build thumbnail cache path for {Path}: {ex.Message}");
            return "";
        }
    }

    private static string HashText(string value)
    {
        return Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string MakeSafePathSegment(string value)
    {
        var safeValue = value.Trim();
        if (safeValue.Length == 0)
        {
            return "folder";
        }

        var firstInvalidIndex = safeValue.IndexOfAny(InvalidFileNameChars);
        if (firstInvalidIndex < 0)
        {
            return safeValue;
        }

        var chars = safeValue.ToCharArray();
        for (var index = firstInvalidIndex; index < chars.Length; index++)
        {
            if (Array.IndexOf(InvalidFileNameChars, chars[index]) >= 0)
            {
                chars[index] = '_';
            }
        }

        return new string(chars);
    }

    private const int SmallThumbnailWidth = 180;
    private const int MediumThumbnailWidth = 240;
    private const int LargeThumbnailWidth = 320;
    private const int ThumbnailJpegQuality = 82;
    private const int SelectedPreviewMaxWidth = 1200;
    private const int ThumbnailFolderHashLength = 8;
    private const double ThumbnailDecodeScale = 1.5;
    private static readonly char[] InvalidFileNameChars = System.IO.Path.GetInvalidFileNameChars();
    private static readonly SemaphoreSlim ThumbnailCacheWriteLimiter = new(1, 1);
    private static readonly SemaphoreSlim SelectedPreviewLoadLimiter = new(1);
    private static readonly object PendingThumbnailCacheWritesLock = new();
    private static readonly HashSet<string> PendingThumbnailCacheWrites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<DeferredThumbnailCacheWrite> DeferredThumbnailCacheWrites = new();
    private static Func<bool>? DeferredThumbnailCacheWritesPaused;

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
    private readonly object _thumbnailCacheStateLock = new();
    private string _thumbnailCachePath = "";
    private bool _thumbnailCacheExists;
    private bool _hasThumbnailCacheState;
    private bool _isSelected;
    private bool _hasLoadedMetadata;
    private int _realizedCount;
    private double _tileSize;
    private Task? _selectedPreviewLoadTask;
    private string? _creationDateText;
    private int _activeSavesCount;
    private Bitmap? _disposalPendingBitmap;
    private bool _hasLoggedThumbnailError;

    public ImageItem(string path, double tileSize)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        _tileSize = tileSize;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<ImageItem>? MetadataLoaded;

    public string Path { get; }
    public string FileName { get; }

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

    public double TileSize => _tileSize;
    public double CardHeight => _tileSize;

    public void SetTileSize(double tileSize)
    {
        if (Math.Abs(_tileSize - tileSize) < 0.1)
        {
            return;
        }

        _tileSize = tileSize;
        InvalidateThumbnailCacheState();
        OnPropertyChanged(nameof(TileSize));
        OnPropertyChanged(nameof(CardHeight));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            SetField(ref _isSelected, value);
        }
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
        if (_hasLoadedMetadata) return;

        try
        {
            var entry = await Task.Run(() =>
            {
                if (MetadataIndex.TryLoad(Path, out var cached))
                {
                    return cached;
                }

                var result = ImageFileReader.Read(Path);
                var extracted = PromptExtractor.ExtractAll(result.TextMetadata);
                MetadataIndex.Save(Path, result, extracted);

                return new MetadataIndexEntry
                {
                    Width = result.Width,
                    Height = result.Height,
                    Prompt = extracted.Prompt,
                    NegativePrompt = extracted.NegativePrompt,
                    Model = extracted.GenerationSettings.Model,
                    Sampler = extracted.GenerationSettings.Sampler,
                    Seed = extracted.GenerationSettings.Seed,
                    Settings = extracted.GenerationSettings.Settings,
                    Lora = extracted.GenerationSettings.Lora
                };
            }, token);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                _width = entry.Width;
                _height = entry.Height;
                Prompt = entry.Prompt;
                NegativePrompt = entry.NegativePrompt;
                Model = entry.Model;
                Sampler = entry.Sampler;
                Seed = entry.Seed;
                Settings = entry.Settings;
                Lora = entry.Lora;
                MarkMetadataLoaded();
                OnPropertyChanged(nameof(DimensionsText));
                OnPropertyChanged(nameof(IsLoading));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load metadata for {Path}: {ex}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                MarkMetadataLoaded();
            });
        }
    }

    internal void ApplyMetadataEntry(MetadataIndexEntry entry)
    {
        if (_hasLoadedMetadata)
        {
            return;
        }

        _width = entry.Width;
        _height = entry.Height;
        Prompt = entry.Prompt;
        NegativePrompt = entry.NegativePrompt;
        Model = entry.Model;
        Sampler = entry.Sampler;
        Seed = entry.Seed;
        Settings = entry.Settings;
        Lora = entry.Lora;
        MarkMetadataLoaded();
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

    public void ReleasePreview(bool skipIfCached = false)
    {
        if (skipIfCached && CacheNode is not null)
        {
            return;
        }

        ImageCache.Remove(this);

        if (Preview is not null)
        {
            if (_activeSavesCount > 0)
            {
                _disposalPendingBitmap = Preview;
            }
            else
            {
                Preview.Dispose();
            }
            Preview = null;
        }
    }

    private void DecrementActiveSaves()
    {
        _activeSavesCount--;
        if (_activeSavesCount == 0 && _disposalPendingBitmap is not null)
        {
            _disposalPendingBitmap.Dispose();
            _disposalPendingBitmap = null;
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
            await SelectedPreviewLoadLimiter.WaitAsync(token);
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(Path);
                    var decodeWidth = _width > 0 ? Math.Min(_width, SelectedPreviewMaxWidth) : SelectedPreviewMaxWidth;
                    return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality);
                }, token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !IsSelected)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    SelectedPreview = bitmap;
                });
            }
            finally
            {
                SelectedPreviewLoadLimiter.Release();
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

    public async Task LoadThumbnailAsync(CancellationToken token)
    {
        if (Preview is not null)
        {
            ImageCache.Touch(this);
            return;
        }

        try
        {
            var bitmap = await Task.Run(() =>
            {
                var (cachePath, hasCachedThumbnail) = GetThumbnailCacheState();
                if (hasCachedThumbnail)
                {
                    try
                    {
                        return new Bitmap(cachePath);
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

                using var stream = File.OpenRead(Path);
                var decoded = Bitmap.DecodeToWidth(stream, GetThumbnailDecodeWidth(), BitmapInterpolationMode.MediumQuality);
                if (!string.IsNullOrEmpty(cachePath))
                {
                    QueueThumbnailCacheWrite(this, decoded, cachePath);
                }
                return decoded;
            }, token);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    if (_activeSavesCount > 0)
                    {
                        _disposalPendingBitmap = bitmap;
                    }
                    else
                    {
                        bitmap.Dispose();
                    }
                    return;
                }

                Preview = bitmap;
                ImageCache.Touch(this);
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
        MetadataLoaded?.Invoke(this);
    }

    private static void QueueThumbnailCacheWrite(ImageItem item, Bitmap thumbnail, string cachePath)
    {
        if (string.IsNullOrEmpty(cachePath))
        {
            return;
        }

        if (File.Exists(cachePath))
        {
            item.SetThumbnailCacheState(cachePath, exists: true);
            return;
        }

        if (!TryBeginThumbnailCacheWrite(cachePath))
        {
            TryQueueDeferredThumbnailCacheWrite(item, cachePath);
            return;
        }

        Interlocked.Increment(ref item._activeSavesCount);

        _ = Task.Run(async () =>
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    item.SetThumbnailCacheState(cachePath, exists: true);
                    return;
                }

                thumbnail.Save(cachePath, ThumbnailJpegQuality);
                item.SetThumbnailCacheState(cachePath, exists: true);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to write thumbnail cache for {item.Path} to {cachePath}: {ex.Message}");
            }
            finally
            {
                EndThumbnailCacheWrite(cachePath);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.DecrementActiveSaves();
                });
            }
        });
    }

    internal static bool TryQueueDeferredThumbnailCacheWrite(ImageItem item, string cachePath)
    {
        if (string.IsNullOrEmpty(cachePath))
        {
            return false;
        }

        if (File.Exists(cachePath))
        {
            item.SetThumbnailCacheState(cachePath, exists: true);
            return false;
        }

        lock (PendingThumbnailCacheWritesLock)
        {
            if (!PendingThumbnailCacheWrites.Add(cachePath))
            {
                return false;
            }

            DeferredThumbnailCacheWrites.Enqueue(new DeferredThumbnailCacheWrite(
                item,
                cachePath,
                item.GetThumbnailDecodeWidth()));
        }

        StartDeferredThumbnailCacheWriter();
        return true;
    }

    internal static void SetDeferredThumbnailCacheWritePause(Func<bool>? isPaused)
    {
        DeferredThumbnailCacheWritesPaused = isPaused;
    }

    internal static void ResumeDeferredThumbnailCacheWrites()
    {
        StartDeferredThumbnailCacheWriter();
    }

    internal static bool TryBeginThumbnailCacheWrite(string cachePath)
    {
        // Intentional simplification: only one writer runs at a time. Busy misses
        // are deferred by path so decoded bitmaps are not retained while waiting.
        if (!ThumbnailCacheWriteLimiter.Wait(0))
        {
            return false;
        }

        lock (PendingThumbnailCacheWritesLock)
        {
            if (PendingThumbnailCacheWrites.Add(cachePath))
            {
                return true;
            }
        }

        ThumbnailCacheWriteLimiter.Release();
        return false;
    }

    internal static void EndThumbnailCacheWrite(string cachePath)
    {
        lock (PendingThumbnailCacheWritesLock)
        {
            PendingThumbnailCacheWrites.Remove(cachePath);
        }

        ThumbnailCacheWriteLimiter.Release();
        StartDeferredThumbnailCacheWriter();
    }

    internal static void ClearDeferredThumbnailCacheWrites()
    {
        lock (PendingThumbnailCacheWritesLock)
        {
            while (DeferredThumbnailCacheWrites.TryDequeue(out var write))
            {
                PendingThumbnailCacheWrites.Remove(write.CachePath);
            }
        }
    }

    private static void StartDeferredThumbnailCacheWriter()
    {
        if (ShouldPauseDeferredThumbnailCacheWrites())
        {
            return;
        }

        if (!ThumbnailCacheWriteLimiter.Wait(0))
        {
            return;
        }

        if (!TryDequeueDeferredThumbnailCacheWrite(out var write))
        {
            ThumbnailCacheWriteLimiter.Release();
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (File.Exists(write.CachePath))
                {
                    write.Item.SetThumbnailCacheState(write.CachePath, exists: true);
                    return;
                }

                using var stream = File.OpenRead(write.Item.Path);
                using var thumbnail = Bitmap.DecodeToWidth(
                    stream,
                    write.ThumbnailWidth,
                    BitmapInterpolationMode.MediumQuality);
                thumbnail.Save(write.CachePath, ThumbnailJpegQuality);
                write.Item.SetThumbnailCacheState(write.CachePath, exists: true);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Failed to write deferred thumbnail cache for {write.Item.Path} to {write.CachePath}: {ex.Message}");
            }
            finally
            {
                EndThumbnailCacheWrite(write.CachePath);
            }
        });
    }

    private static bool ShouldPauseDeferredThumbnailCacheWrites()
    {
        try
        {
            return DeferredThumbnailCacheWritesPaused?.Invoke() == true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Deferred thumbnail cache pause callback failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryDequeueDeferredThumbnailCacheWrite(out DeferredThumbnailCacheWrite write)
    {
        lock (PendingThumbnailCacheWritesLock)
        {
            return DeferredThumbnailCacheWrites.TryDequeue(out write);
        }
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
        var cachePath = BuildThumbnailCachePath();
        var exists = !string.IsNullOrEmpty(cachePath) && File.Exists(cachePath);
        SetThumbnailCacheState(cachePath, exists);
    }

    private int GetThumbnailDecodeWidth()
    {
        var targetWidth = (int)Math.Ceiling(_tileSize * ThumbnailDecodeScale);
        if (targetWidth <= SmallThumbnailWidth)
        {
            return SmallThumbnailWidth;
        }

        return targetWidth <= MediumThumbnailWidth ? MediumThumbnailWidth : LargeThumbnailWidth;
    }

    private void InvalidateThumbnailCacheState()
    {
        lock (_thumbnailCacheStateLock)
        {
            _hasThumbnailCacheState = false;
        }
    }

    private void SetThumbnailCacheState(string cachePath, bool exists)
    {
        lock (_thumbnailCacheStateLock)
        {
            _thumbnailCachePath = cachePath;
            _thumbnailCacheExists = exists;
            _hasThumbnailCacheState = true;
        }
    }

    internal readonly record struct DeferredThumbnailCacheWrite(
        ImageItem Item,
        string CachePath,
        int ThumbnailWidth);


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
