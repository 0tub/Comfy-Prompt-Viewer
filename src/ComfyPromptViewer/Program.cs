using Avalonia;
using Avalonia.Skia;
using System;

namespace ComfyPromptViewer;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DecodedImageCache.ConfigureLinuxNativeAllocator();
        DebugLog.InstallGlobalHandlers();
        DebugLog.Write("App starting");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            DebugLog.WriteException("Program.Main", ex);
            throw;
        }
    }

    // The gallery pushes hundreds of decoded bitmaps through Skia, so the GPU resource cache is a real
    // budget rather than an afterthought. Windows gets more headroom than Linux because the Windows build
    // is the primary target and the D3D/ANGLE path recycles textures better than the X11 fallbacks do.
    private const long WindowsMaxGpuResourceSizeBytes = 128 * 1024 * 1024;
    private const long LinuxMaxGpuResourceSizeBytes = 64 * 1024 * 1024;

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        if (OperatingSystem.IsLinux())
        {
            builder = builder
                .With(new X11PlatformOptions
                {
                    RenderingMode =
                    [
                        X11RenderingMode.Egl,
                        X11RenderingMode.Glx,
                        X11RenderingMode.Software
                    ],
                    UseRetainedFramebuffer = false
                })
                .With(new SkiaOptions
                {
                    MaxGpuResourceSizeBytes = LinuxMaxGpuResourceSizeBytes
                });
        }
        else if (OperatingSystem.IsWindows())
        {
            builder = builder
                .With(new Win32PlatformOptions
                {
                    // Mirrors the X11 fallback chain: a machine with a broken ANGLE/D3D path drops to WGL
                    // and then to software instead of failing to start.
                    RenderingMode =
                    [
                        Win32RenderingMode.AngleEgl,
                        Win32RenderingMode.Wgl,
                        Win32RenderingMode.Software
                    ],
                    // Per-monitor DPI is what makes RenderScaling change when the window moves between
                    // displays, which is the signal ImageItem uses to pick a thumbnail decode bucket.
                    DpiAwareness = Win32DpiAwareness.PerMonitorDpiAware
                })
                .With(new SkiaOptions
                {
                    MaxGpuResourceSizeBytes = WindowsMaxGpuResourceSizeBytes
                });
        }

#if DEBUG
        builder = builder
            .WithDeveloperTools()
            .LogToTrace();
#endif

        return builder;
    }
}
