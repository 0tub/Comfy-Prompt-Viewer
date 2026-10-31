using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ComfyPromptViewer;

// What the large preview shows: titles, navigation state and video playback. The view owns zoom, pan and
// gestures, and tells this model nothing about them except the zoom text; this model tells the view when the
// video bitmap it should draw appears or goes away.
public sealed class LargePreviewViewModel : ObservableObject
{
    private static readonly TimeSpan LoadingIndicatorDelay = TimeSpan.FromMilliseconds(400);
    private const string StillHint = "Arrows navigate  ·  Scroll to zoom  ·  Esc / Space / Enter close";
    private const string VideoHint = "Arrows navigate  ·  Space pause  ·  M mute  ·  Esc / Enter close";
    private const string NoVlcHint = "VLC not found  ·  install VLC to play videos (see README)  ·  Esc / Enter close";

    private readonly GenerationGate _videoGate = new();
    private readonly DispatcherTimer _videoTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private VideoPlayback.Preview? _video;
    private string? _videoPath;
    private bool _videoMuted;
    private int _videoVolume = 100;

    private ImageItem? _item;
    private bool _isLoading;
    private bool _showLoading;
    private DispatcherTimer? _loadingTimer;
    private string _zoomText = "Fit";
    private string _hint = StillHint;
    private bool _canGoPrevious;
    private bool _canGoNext;
    private bool _isVideoControlsVisible;
    private bool _isPaused;
    private string _timeText = "";
    private double _position;

    public LargePreviewViewModel()
    {
        _videoTimer.Tick += (_, _) => UpdateVideoControls();
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
        PreviousCommand = new RelayCommand(() => NavigateRequested?.Invoke(-1));
        NextCommand = new RelayCommand(() => NavigateRequested?.Invoke(1));
        TogglePauseCommand = new RelayCommand(TogglePause);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
    }

    public event Action? CloseRequested;
    public event Action<int>? NavigateRequested;

    // Raised once the video is open, before the controls update, so the view can swap its still for the
    // video bitmap. VideoClosing is the reverse, raised before the bitmap is disposed.
    internal event Action<Bitmap>? VideoOpened;
    internal event Action<Bitmap>? VideoClosing;
    internal event Action? VideoFrameShown;

    public ICommand CloseCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand ToggleMuteCommand { get; }

    // Set by the view while the user drags the position slider, so the timer does not fight the drag.
    public bool IsScrubbing { get; set; }
    public bool IsUpdatingPosition { get; private set; }

    internal Bitmap? VideoBitmap => _video?.Bitmap;

    public string Title => _item?.FileName ?? "";

    // While the next image decodes, the new size shows at once and "Loading…" only appears if it takes a while;
    // flashing it for every quick load read as the line flickering when stepping through images.
    public string Meta => _item is null
        ? ""
        : _isLoading
            ? (_showLoading ? "Loading…" : _item.Metadata.DimensionsText)
            : $"{_item.Metadata.DimensionsText} - {_zoomText}";

    public string Hint => _hint;
    public bool CanGoPrevious => _canGoPrevious;
    public bool CanGoNext => _canGoNext;
    public bool IsVideoControlsVisible => _isVideoControlsVisible;
    public bool IsPaused => _isPaused;
    public bool IsMuted => _videoMuted;
    public string MuteTip => _videoMuted ? "Unmute (M)" : "Mute (M)";
    public string TimeText => _timeText;
    public double Position => _position;
    public int Volume => _videoVolume;

    // The view reports the zoom it applied; it is only ever shown, never read back.
    public void SetZoomText(string zoomText)
    {
        Set(ref _zoomText, zoomText, nameof(Meta));
    }

    public void SetNavigation(bool canGoPrevious, bool canGoNext)
    {
        Set(ref _canGoPrevious, canGoPrevious, nameof(CanGoPrevious));
        Set(ref _canGoNext, canGoNext, nameof(CanGoNext));
    }

    // Returns true while the item's still preview is still decoding, in which case nothing else changes and the
    // outgoing image stays on screen.
    internal bool Present(ImageItem item)
    {
        _item = item;
        Raise(nameof(Title));
        _isLoading = item.SelectedPreview is null && !item.SelectedPreviewUnavailable;
        _showLoading = false;
        _loadingTimer?.Stop();
        Raise(nameof(Meta));
        if (_isLoading)
        {
            StartLoadingTimer();
            return true;
        }

        RefreshHint();
        SyncVideo(item);
        return false;
    }

    private void StartLoadingTimer()
    {
        if (_loadingTimer is null)
        {
            _loadingTimer = new DispatcherTimer { Interval = LoadingIndicatorDelay };
            _loadingTimer.Tick += (_, _) =>
            {
                _loadingTimer.Stop();
                if (_isLoading)
                {
                    _showLoading = true;
                    Raise(nameof(Meta));
                }
            };
        }

        _loadingTimer.Start();
    }

    private void RefreshHint()
    {
        var hint = _item is not { IsVideo: true }
            ? StillHint
            : VideoPlayback.IsKnownUnavailable ? NoVlcHint : VideoHint;
        Set(ref _hint, hint, nameof(Hint));
    }

    // Called only once the item's still preview is showing, so the outgoing video keeps playing while the
    // next item loads instead of blanking.
    private void SyncVideo(ImageItem item)
    {
        if (_videoPath == item.Path)
        {
            return;
        }

        StopVideo();
        if (item.IsVideo)
        {
            _videoPath = item.Path;
            DebugLog.Observe(OpenVideoAsync(item.Path, _videoGate.Begin()), "Video preview open for", item.Path);
        }
    }

    private async Task OpenVideoAsync(string path, Generation generation)
    {
        var muted = _videoMuted;
        var volume = _videoVolume;
        var opened = await Task.Run(() => VideoPlayback.Preview.TryOpen(path, muted, volume));
        if (opened is null)
        {
            // The first open is what finds out whether VLC loads.
            RefreshHint();
            return;
        }

        if (generation.IsStale)
        {
            opened.Dispose();
            return;
        }

        _video = opened;
        // Only when M was pressed while it opened: every libvlc mute call reaches the audio output plugin.
        if (_videoMuted != muted)
        {
            opened.IsMuted = _videoMuted;
        }

        if (_videoVolume != volume)
        {
            opened.Volume = _videoVolume;
        }

        // Shown first, so the resize it causes places the video once, in the area that remains.
        SetVideoControlsVisible(true);
        VideoOpened?.Invoke(opened.Bitmap);
        UpdateVideoControls();
        _videoTimer.Start();
        opened.Start(() =>
        {
            if (ReferenceEquals(_video, opened))
            {
                VideoFrameShown?.Invoke();
            }
        });
    }

    internal void StopVideo()
    {
        _videoGate.Invalidate();
        _videoPath = null;
        _videoTimer.Stop();
        SetVideoControlsVisible(false);
        if (_video is not { } video)
        {
            return;
        }

        _video = null;
        VideoClosing?.Invoke(video.Bitmap);
        video.Dispose();
    }

    // Returns false when the key is not a video key or no video is selected, so the caller can handle it.
    internal bool HandleVideoKey(Key key, ImageItem? item)
    {
        if (item is not { IsVideo: true })
        {
            return false;
        }

        switch (key)
        {
            case Key.Space:
                // Swallowed while the player is still opening, so the press does not close the preview instead.
                TogglePause();
                return true;
            case Key.M:
                ToggleMute();
                return true;
            default:
                return false;
        }
    }

    private void TogglePause()
    {
        _video?.TogglePause();
        UpdateVideoControls();
    }

    private void ToggleMute()
    {
        _videoMuted = !_videoMuted;
        if (_video is { } video)
        {
            video.IsMuted = _videoMuted;
        }

        Raise(nameof(IsMuted));
        Raise(nameof(MuteTip));
    }

    public void SetVolume(int volume)
    {
        _videoVolume = volume;
        _video?.Volume = _videoVolume;
        // Turning the volume up while muted means "I want to hear it", as in other players.
        if (_videoMuted && _videoVolume > 0)
        {
            ToggleMute();
        }
    }

    public void Seek(double position)
    {
        if (!IsUpdatingPosition && _video is { } video)
        {
            video.Position = position;
        }
    }

    private void SetVideoControlsVisible(bool visible)
    {
        Set(ref _isVideoControlsVisible, visible, nameof(IsVideoControlsVisible));
    }

    private void UpdateVideoControls()
    {
        if (_video is not { } video)
        {
            return;
        }

        Set(ref _isPaused, video.IsPaused, nameof(IsPaused));

        var time = $"{FormatTime(video.Time)} / {FormatTime(video.Length)}";
        Set(ref _timeText, time, nameof(TimeText));

        if (!IsScrubbing)
        {
            IsUpdatingPosition = true;
            _position = video.Position;
            Raise(nameof(Position));
            IsUpdatingPosition = false;
        }
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}
