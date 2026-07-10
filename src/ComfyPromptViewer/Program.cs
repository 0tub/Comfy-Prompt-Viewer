using Avalonia;
using System;

namespace ComfyPromptViewer;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
            .LogToTrace()
#endif
            ;
}
