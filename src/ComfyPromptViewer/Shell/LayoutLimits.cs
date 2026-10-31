using System;

namespace ComfyPromptViewer;

// The gallery tile size the user can pick. Every value that arrives (saved file, slider) goes through
// Normalize once, so nothing downstream re-clamps or re-rounds it.
internal static class TileSizeLimits
{
    public const double Default = 120;
    public const double Min = 80;
    public const double Max = 320;
    public const double Step = 10;

    public static double Normalize(double value)
    {
        return double.IsFinite(value)
            ? Math.Clamp(Math.Round(value / Step) * Step, Min, Max)
            : Default;
    }
}

// The sidebar width the splitter can drag to. The window ratio is only a ceiling that keeps a narrow window
// from handing the whole client area to the sidebar.
internal static class SidebarWidthLimits
{
    public const double Default = 380;
    public const double Min = 260;
    public const double Max = 560;
    private const double MaxWindowRatio = 0.45;

    public static double Normalize(double value)
    {
        return double.IsFinite(value) ? Math.Clamp(value, Min, Max) : Default;
    }

    // The ratio is a ceiling, never a target: a wide window leaves the dragged width alone, and only a window
    // too narrow to keep the gallery usable pulls it back down.
    public static double FitToWindow(double currentWidth, double windowWidth)
    {
        var maxForWindow = Math.Max(Min, windowWidth * MaxWindowRatio);
        return Math.Clamp(currentWidth, Min, Math.Min(Max, maxForWindow));
    }
}
