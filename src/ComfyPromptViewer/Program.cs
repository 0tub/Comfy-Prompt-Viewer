using Avalonia;
using Avalonia.Skia;
using System;

namespace ComfyPromptViewer;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ImageCache.ConfigureLinuxNativeAllocator();

        if (args.Length == 1 && string.Equals(args[0], "--self-check", StringComparison.OrdinalIgnoreCase))
        {
            SelfCheck.Run();
            return;
        }

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
                    MaxGpuResourceSizeBytes = 64 * 1024 * 1024
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
