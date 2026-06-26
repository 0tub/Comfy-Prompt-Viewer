using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

public static class DebugLog
{
    private static readonly object Lock = new();
    private static string _lastScrollState = "";

    public static string LogPath => Path.Combine(UserPreferences.AppDataDir, "debug.log");

    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteException("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteException("TaskScheduler.UnobservedTaskException", e.Exception);
        };
    }

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(UserPreferences.AppDataDir);
            lock (Lock)
            {
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Intentionally silent: debug logging must never break app behavior.
        }
    }

    public static void WriteException(string source, Exception? exception)
    {
        Write($"{source}{Environment.NewLine}LastScroll: {_lastScrollState}{Environment.NewLine}{exception}");
    }

    public static void SetScrollState(double offsetY, double viewportHeight, double extentHeight, int itemCount, double tileExtent)
    {
        _lastScrollState = $"OffsetY={offsetY:n1}, ViewportHeight={viewportHeight:n1}, ExtentHeight={extentHeight:n1}, Items={itemCount:n0}, TileExtent={tileExtent:n1}";
    }
}
