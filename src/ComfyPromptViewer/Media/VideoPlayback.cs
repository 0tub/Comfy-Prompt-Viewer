using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using SkiaSharp;

namespace ComfyPromptViewer;

// Everything that touches libvlc. It comes from the user's VLC install (vlc-libs on Linux, VLC on Windows)
// instead of being bundled, which keeps ~150 MB of native codecs out of the app. Without it, MP4s still
// list with their metadata but have no thumbnail or playback.
internal static class VideoPlayback
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);
    private static readonly Lazy<LibVLC?> SharedLibVlc = new(CreateLibVlc);

    // True only once loading libvlc was tried and failed; never triggers the load itself, so the UI can ask freely.
    public static bool IsKnownUnavailable => SharedLibVlc.IsValueCreated && SharedLibVlc.Value is null;

    // Blocking; plays the file muted just long enough to get its first frame.
    public static SKBitmap DecodeFirstFrame(string path)
    {
        var libVlc = SharedLibVlc.Value ?? throw new InvalidDataException("VLC is not installed; video frames are unavailable.");
        var firstFrame = new TaskCompletionSource<SKBitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (new FrameSource(libVlc, path, ReadFrameSize(path), withAudio: false,
                   (pixels, info, pitch) => firstFrame.TrySetResult(firstFrame.Task.IsCompleted ? null : CopyFrame(pixels, info, pitch)),
                   () => firstFrame.TrySetResult(null)))
        {
            if (firstFrame.Task.Wait(FirstFrameTimeout) && firstFrame.Task.Result is { } frame)
            {
                return frame;
            }
        }

        throw new InvalidDataException($"No video frame decoded from {path}.");
    }

    // libvlc reports a padded buffer height (1080 arrives as 1090), so the size is taken from the track header
    // and requested explicitly; libvlc then scales the visible picture to it.
    private static SKSizeI ReadFrameSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = Mp4MetadataReader.Read(stream);
            return new SKSizeI(header.Width, header.Height);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return SKSizeI.Empty;
        }
    }

    private static SKBitmap CopyFrame(IntPtr pixels, SKImageInfo info, int pitch)
    {
        var bitmap = new SKBitmap(info);
        using var source = new SKPixmap(info, pixels, pitch);
        source.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes);
        return bitmap;
    }

    private static LibVLC? CreateLibVlc()
    {
        try
        {
            string? directory = null;
            if (OperatingSystem.IsWindows())
            {
                directory = FindWindowsVlcDirectory();
                if (directory is null)
                {
                    DebugLog.Write("VLC is not installed; videos will have no thumbnails or playback.");
                    return null;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                // Distros ship libvlc.so.5; only the -devel package adds the bare name LibVLCSharp imports.
                NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, _, _) =>
                    NativeLibrary.TryLoad(name == "libvlc" ? "libvlc.so.5" : name, out var handle) ? handle : IntPtr.Zero);
            }

            Core.Initialize(directory);
            // VLC 3's PipeWire output segfaults when mute is set while its stream is not running (before playback, or
            // while it starts). Its PulseAudio output does not, and PipeWire serves Pulse clients natively; "any" falls
            // back to whatever else is installed.
            return OperatingSystem.IsLinux()
                ? new LibVLC("--quiet", "--no-video-title-show", "--aout=pulse,any")
                : new LibVLC("--quiet", "--no-video-title-show");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"VLC could not be loaded; videos will have no thumbnails or playback: {ex.Message}");
            return null;
        }
    }

    private static string? FindWindowsVlcDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var directory = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\VideoLAN\VLC", "InstallDir", null) as string
            ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC");
        return File.Exists(System.IO.Path.Combine(directory, "libvlc.dll")) ? directory : null;
    }

    // Plays a video in the large preview, looping, with sound.
    internal sealed class Preview : IDisposable
    {
        private readonly object _gate = new();
        private FrameSource? _source;
        private WriteableBitmap? _bitmap;
        private Action? _frameShown;
        private bool _disposed;

        private Preview(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public WriteableBitmap Bitmap => _bitmap ?? throw new InvalidOperationException("No frame has been decoded yet.");

        // Blocking; returns once the first frame is in Bitmap, or null when VLC is missing or nothing decodes.
        public static Preview? TryOpen(string path, bool muted, int volume)
        {
            if (SharedLibVlc.Value is not { } libVlc)
            {
                return null;
            }

            var preview = new Preview(path);
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            preview._source = new FrameSource(libVlc, path, ReadFrameSize(path), withAudio: true,
                (pixels, info, pitch) =>
                {
                    preview.ShowFrame(pixels, info, pitch);
                    firstFrame.TrySetResult();
                },
                () => firstFrame.TrySetResult(),
                loop: true,
                muted: muted,
                volume: volume);

            if (firstFrame.Task.Wait(FirstFrameTimeout) && preview._bitmap is not null)
            {
                return preview;
            }

            preview.Dispose();
            return null;
        }

        public void Start(Action frameShown)
        {
            _frameShown = frameShown;
        }

        public bool IsPaused { get; private set; }

        public void TogglePause()
        {
            IsPaused = !IsPaused;
            Player.SetPause(IsPaused);
        }

        public bool IsMuted
        {
            get => Player.Mute;
            set => Player.Mute = value;
        }

        // 0 to 100, libvlc's unamplified range.
        public int Volume
        {
            get => Player.Volume;
            set => Player.Volume = Math.Clamp(value, 0, 100);
        }

        // Fraction of the clip, 0 to 1. libvlc renders the new frame even while paused.
        public double Position
        {
            get => Math.Clamp(Player.Position, 0, 1);
            set => Player.Position = (float)Math.Clamp(value, 0, 1);
        }

        public TimeSpan Time => TimeSpan.FromMilliseconds(Math.Max(0, Player.Time));

        public TimeSpan Length => TimeSpan.FromMilliseconds(Math.Max(0, Player.Length));

        private MediaPlayer Player => _source!.Player;

        // Runs on libvlc's video thread.
        private void ShowFrame(IntPtr pixels, SKImageInfo info, int pitch)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _bitmap ??= new WriteableBitmap(new PixelSize(info.Width, info.Height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                using var target = _bitmap.Lock();
                using var source = new SKPixmap(info, pixels, pitch);
                source.ReadPixels(info, target.Address, target.RowBytes);
            }

            if (_frameShown is { } frameShown)
            {
                Dispatcher.UIThread.Post(frameShown);
            }
        }

        public void Dispose()
        {
            _frameShown = null;
            WriteableBitmap? bitmap;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                bitmap = _bitmap;
            }

            // Stopping joins libvlc's decode and audio threads, so keep it off the UI thread.
            var source = _source;
            DebugLog.Observe(Task.Run(() =>
            {
                source?.Dispose();
                bitmap?.Dispose();
            }), "Video preview close for", Path);
        }
    }

    // Renders a file's frames into unmanaged BGRA memory through libvlc's video callbacks.
    private sealed class FrameSource : IDisposable
    {
        private readonly MediaPlayer _player;

        public MediaPlayer Player => _player;
        private readonly Action<IntPtr, SKImageInfo, int> _frameDecoded;

        // Held in fields because libvlc calls them through native pointers the GC cannot see.
        private readonly MediaPlayer.LibVLCVideoFormatCb _format;
        private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
        private readonly MediaPlayer.LibVLCVideoLockCb _lock;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _display;

        private IntPtr _buffer;
        private SKImageInfo _info;
        private int _pitch;

        private readonly SKSizeI _size;

        public FrameSource(LibVLC libVlc, string path, SKSizeI size, bool withAudio, Action<IntPtr, SKImageInfo, int> frameDecoded,
            Action stopped, bool loop = false, bool muted = false, int volume = 100)
        {
            _frameDecoded = frameDecoded;
            _size = size;
            _format = Format;
            _cleanup = Cleanup;
            _lock = Lock;
            _display = Display;

            using var media = new Media(libVlc, path, FromType.FromPath);
            if (!withAudio)
            {
                media.AddOption(":no-audio");
            }

            if (loop)
            {
                media.AddOption(":input-repeat=65535");
            }

            _player = new MediaPlayer(media);
            _player.SetVideoFormatCallbacks(_format, _cleanup);
            _player.SetVideoCallbacks(_lock, null, _display);
            _player.EndReached += (_, _) => stopped();
            _player.EncounteredError += (_, _) => stopped();
            // Set before Play, while there is no audio output yet: libvlc keeps it and starts the output muted. Muting
            // right after the first frame raced the PipeWire output starting up and segfaulted inside the plugin.
            _player.Mute = muted;
            _player.Volume = volume;
            _player.Play();
        }

        private uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
        {
            Marshal.Copy("BGRA"u8.ToArray(), 0, chroma, 4);
            if (!_size.IsEmpty)
            {
                (width, height) = ((uint)_size.Width, (uint)_size.Height);
            }

            _info = new SKImageInfo((int)width, (int)height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            _pitch = (int)width * 4;
            pitches = (uint)_pitch;
            lines = height;
            _buffer = Marshal.AllocHGlobal(_pitch * (int)height);
            return 1;
        }

        private void Cleanup(ref IntPtr opaque)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }

        private IntPtr Lock(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _buffer);
            return IntPtr.Zero;
        }

        private void Display(IntPtr opaque, IntPtr picture)
        {
            _frameDecoded(_buffer, _info, _pitch);
        }

        public void Dispose()
        {
            _player.Stop();
            _player.Dispose();
        }
    }
}
