using System;
using Avalonia.Media.Imaging;

namespace ComfyPromptViewer;

// One image as the grid, sidebar and preview show it. It holds state only and does no I/O: metadata comes from
// MetadataScanCoordinator, the tile bitmap from ThumbnailLoadCoordinator (its lifetime is DecodedImageCache's),
// and the sidebar bitmap from SelectedPreviewLoader. Their setters are internal and called on the UI thread.
public sealed class ImageItem : ObservableObject
{
    private readonly object _thumbnailKeyLock = new();
    private Bitmap? _preview;
    private int _previewWidth;
    private Bitmap? _selectedPreview;
    private bool _selectedPreviewUnavailable;
    private ImageMetadata _metadata = ImageMetadata.Empty;
    private ThumbnailKey _thumbnailKey;
    private int _thumbnailKeyWidth = -1;
    private bool _isSelected;
    private bool _isMarkedSelected;
    private volatile bool _hasLoadedMetadata;
    private MetadataLoadStatus _metadataLoadStatus;
    private int _realizedCount;
    private double _tileSize;
    private double _renderScaling = 1.0;
    private bool _needsVlc;

    internal ImageItem(ImageFileEntry file, double tileSize, ThumbnailFolderScope? cacheScope)
    {
        Path = file.Path;
        FileName = System.IO.Path.GetFileName(file.Path);
        SourceFingerprint = file.Fingerprint;
        CreationDateText = file.CreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown";
        _tileSize = tileSize;
        CacheScope = cacheScope;
    }

    // Raised once, on the UI thread, when metadata has been applied or has finally failed to load.
    public event Action<ImageItem>? MetadataLoaded;

    public string Path { get; }
    public string FileName { get; }
    internal SourceFingerprint SourceFingerprint { get; }
    public string CreationDateText { get; }

    // The folder cache this item reads and writes. Captured at construction so that when the folder swaps,
    // a decode still in flight resolves against its own (retired) pack rather than the new folder's.
    internal ThumbnailFolderScope? CacheScope { get; }

    // Row in the catalog's SearchIndex, or -1 when not in a catalog. Only SearchIndex assigns this.
    internal int SearchSlot { get; set; } = -1;

    // Empty until metadata loads, and stays empty when it fails to.
    public ImageMetadata Metadata
    {
        get => _metadata;
        private set => Set(ref _metadata, value);
    }

    public bool HasLoadedMetadata => _hasLoadedMetadata;
    internal MetadataLoadStatus MetadataLoadStatus => _metadataLoadStatus;

    // While any tile shows this item, the decoded-image cache will not evict its bitmap.
    public bool IsRealized => _realizedCount > 0;

    public Bitmap? Preview
    {
        get => _preview;
        private set => Set(ref _preview, value, nameof(Preview), nameof(IsLoading), nameof(ThumbnailOpacity));
    }

    // The decode width drifts from the one Preview was built at when the tile size or DPI crosses a bucket.
    internal bool IsPreviewStale => Preview is not null && _previewWidth != GetThumbnailDecodeWidth();
    internal bool HasCurrentPreview => Preview is not null && !IsPreviewStale;

    public bool IsLoading => Preview == null;
    public double ThumbnailOpacity => Preview is null ? 0 : 1;

    public Bitmap? SelectedPreview
    {
        get => _selectedPreview;
        private set => Set(ref _selectedPreview, value);
    }

    // Separates "finished and produced nothing" from "still decoding", so a caller holding the previous
    // image can tell that no replacement is coming.
    public bool SelectedPreviewUnavailable
    {
        get => _selectedPreviewUnavailable;
        private set => Set(ref _selectedPreviewUnavailable, value);
    }

    public bool IsVideo => MediaFiles.IsVideo(Path);

    // Set only when the thumbnail failed because VLC is missing, not for a video that is merely corrupt.
    public bool NeedsVlc
    {
        get => _needsVlc;
        private set => Set(ref _needsVlc, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool IsMarkedSelected
    {
        get => _isMarkedSelected;
        set => Set(ref _isMarkedSelected, value);
    }

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

    // Render scaling rides along with tile size because both feed the same decode-width decision and both
    // are only worth re-aligning for items the window is about to show.
    public void SetTileSize(double tileSize, double renderScaling)
    {
        _tileSize = tileSize;
        _renderScaling = renderScaling;
    }

    internal int GetThumbnailDecodeWidth() => ThumbnailSizing.DecodeWidth(_tileSize, _renderScaling);

    // Memoized per width, so a scroll pass costs a comparison and a tile-size change invalidates it.
    internal ThumbnailKey GetThumbnailKey()
    {
        var width = GetThumbnailDecodeWidth();
        lock (_thumbnailKeyLock)
        {
            if (_thumbnailKeyWidth != width)
            {
                _thumbnailKey = ThumbnailPack.CreateKey(Path, SourceFingerprint.LastWriteTimeUtcTicks, width);
                _thumbnailKeyWidth = width;
            }

            return _thumbnailKey;
        }
    }

    // What the thumbnail service needs to decode or cache this item, without holding the item itself.
    internal ThumbnailSource ThumbnailSource => new(Path, CacheScope, GetThumbnailDecodeWidth());

    // Applies a load result once; later results are ignored. A failure still counts as loaded, so the item
    // stays visible and is not scanned again.
    internal void ApplyMetadataResult(MetadataLoadResult result)
    {
        ApplyMetadata(result.Status, result.Entry?.Metadata);
    }

    internal void ApplyMetadata(ImageMetadata metadata)
    {
        ApplyMetadata(MetadataLoadStatus.Success, metadata);
    }

    internal void MarkMetadataCancelled()
    {
        if (!_hasLoadedMetadata)
        {
            _metadataLoadStatus = MetadataLoadStatus.Cancelled;
        }
    }

    private void ApplyMetadata(MetadataLoadStatus status, ImageMetadata? metadata)
    {
        if (_hasLoadedMetadata)
        {
            return;
        }

        _metadataLoadStatus = status;
        if (metadata is not null)
        {
            Metadata = metadata;
        }

        _hasLoadedMetadata = true;
        MetadataLoaded?.Invoke(this);
    }

    // Swaps first so the tile never binds a disposed bitmap; the old one stays visible until now.
    internal void SetPreview(Bitmap bitmap, int decodeWidth)
    {
        var stale = Preview;
        Preview = bitmap;
        _previewWidth = decodeWidth;
        stale?.Dispose();
    }

    // Only DecodedImageCache calls this, so the cache's accounting and the bitmap go together.
    internal void ClearPreview()
    {
        var preview = Preview;
        Preview = null;
        preview?.Dispose();
    }

    internal void MarkNeedsVlc()
    {
        NeedsVlc = true;
    }

    internal void SetSelectedPreview(Bitmap bitmap)
    {
        SelectedPreview = bitmap;
        SelectedPreviewUnavailable = false;
    }

    internal void MarkSelectedPreviewUnavailable()
    {
        if (SelectedPreview is null)
        {
            SelectedPreviewUnavailable = true;
        }
    }

    internal void ResetSelectedPreviewUnavailable()
    {
        SelectedPreviewUnavailable = false;
    }

    public void ReleaseSelectedPreview()
    {
        var selectedPreview = SelectedPreview;
        SelectedPreview = null;
        SelectedPreviewUnavailable = false;
        selectedPreview?.Dispose();
    }
}
