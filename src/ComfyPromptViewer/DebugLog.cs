using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ComfyPromptViewer;

public static class DebugLog
{
    private static readonly object Lock = new();
    private static double _lastScrollOffsetY;
    private static double _lastScrollViewportHeight;
    private static double _lastScrollExtentHeight;
    private static int _lastScrollItemCount;
    private static double _lastScrollTileExtent;

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
        var scrollState = $"OffsetY={_lastScrollOffsetY:n1}, ViewportHeight={_lastScrollViewportHeight:n1}, " +
                          $"ExtentHeight={_lastScrollExtentHeight:n1}, Items={_lastScrollItemCount:n0}, " +
                          $"TileExtent={_lastScrollTileExtent:n1}";
        Write($"{source}{Environment.NewLine}LastScroll: {scrollState}{Environment.NewLine}{exception}");
    }

    public static void SetScrollState(double offsetY, double viewportHeight, double extentHeight, int itemCount, double tileExtent)
    {
        _lastScrollOffsetY = offsetY;
        _lastScrollViewportHeight = viewportHeight;
        _lastScrollExtentHeight = extentHeight;
        _lastScrollItemCount = itemCount;
        _lastScrollTileExtent = tileExtent;
    }
}
