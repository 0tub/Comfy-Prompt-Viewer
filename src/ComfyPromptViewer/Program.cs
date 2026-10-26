using Avalonia;
using Avalonia.Skia;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ComfyPromptViewer;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DecodedImageCache.ConfigureLinuxNativeAllocator();
        PreferMesaEgl();
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

    // libglvnd loads every installed EGL vendor. With the display on an AMD or Intel GPU beside an NVIDIA card,
    // Mesa draws anyway and the NVIDIA libraries only cost memory (about 40 MB RSS). If EGL then fails, the
    // rendering chain below still falls back to GLX and software. User settings win.
    private static void PreferMesaEgl()
    {
        const string vendors = "__EGL_VENDOR_LIBRARY_FILENAMES", mesa = "/usr/share/glvnd/egl_vendor.d/50_mesa.json";
        if (!OperatingSystem.IsLinux() || !File.Exists(mesa) ||
            new[] { vendors, "__EGL_VENDOR_LIBRARY_DIRS", "COMFYPROMPTVIEWER_DRIVER_DEFAULT" }.Any(v => Environment.GetEnvironmentVariable(v) != null))
        {
            return;
        }

        try
        {
            var cards = new DirectoryInfo("/sys/class/drm").GetDirectories()
                .Select(d => Path.Combine(d.FullName, "device"))
                .Where(d => File.Exists(Path.Combine(d, "boot_vga")))
                .Select(d => (Primary: File.ReadAllText(Path.Combine(d, "boot_vga")).Trim() == "1",
                              Driver: Path.GetFileName(new DirectoryInfo(Path.Combine(d, "driver")).LinkTarget ?? "")))
                .ToList();
            if (cards.Any(c => c.Primary && c.Driver is "amdgpu" or "radeon" or "i915" or "xe") && cards.Any(c => c.Driver == "nvidia"))
            {
                // Environment.SetEnvironmentVariable does not reach the native environment libEGL reads.
                setenv(vendors, mesa, 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException)
        {
        }
    }

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int setenv(string name, string value, int overwrite);

    // The gallery pushes hundreds of decoded bitmaps through Skia, so the GPU resource cache is a real
    // budget rather than an afterthought. Windows gets more headroom than Linux because the Windows build
    // is the primary target and the D3D/ANGLE path recycles textures better than the X11 fallbacks do.
    private const long WindowsMaxGpuResourceSizeBytes = 128 * 1024 * 1024;
    private const long LinuxMaxGpuResourceSizeBytes = 64 * 1024 * 1024;

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        // Skips the GPU driver stack, which measured about 100 MB less resident memory on Linux at the cost of
        // more CPU while scrolling. Set by the Advanced Maintenance toggle or the environment variable.
        var software = Environment.GetEnvironmentVariable("COMFYPROMPTVIEWER_SOFTWARE_RENDERING") == "1" ||
                       new UserPreferencesStore(AppPaths.LocalDataDirectory).LoadSoftwareRendering();

        if (OperatingSystem.IsLinux())
        {
            builder = builder
                .With(new X11PlatformOptions
                {
                    RenderingMode = software
                        ? [X11RenderingMode.Software]
                        : [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
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
                    RenderingMode = software
                        ? [Win32RenderingMode.Software]
                        : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Wgl, Win32RenderingMode.Software],
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
