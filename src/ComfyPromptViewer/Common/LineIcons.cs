using Avalonia.Media;

namespace ComfyPromptViewer;

// Native vector geometry: no OS icon font, emoji fallback, or extra dependency.
public static class LineIcons
{
    public static Geometry Folder { get; } = Geometry.Parse("M3,7 L3,4 L9,4 L12,7 L21,7 L21,20 L3,20 Z M3,9 L21,9");
    public static Geometry Image { get; } = Geometry.Parse("M3,3 L21,3 L21,21 L3,21 Z M3,17 L9,11 L14,16 L17,13 L21,17 M16,7 L17,7");
    public static Geometry Brush { get; } = Geometry.Parse("M14,10 L20,3 L22,5 L16,12 Z M14,10 L8,11 L3,19 Q9,23 15,20 L16,12 M7,15 L5,20 M11,16 L9,21");
    public static Geometry Keyboard { get; } = Geometry.Parse("M2,5 L22,5 L22,19 L2,19 Z M6,9 L7,9 M11,9 L12,9 M16,9 L17,9 M6,12 L7,12 M11,12 L12,12 M16,12 L17,12 M7,16 L17,16");
    public static Geometry Settings { get; } = Geometry.Parse("M3,6 L8,6 M12,6 L21,6 M8,3 L12,3 L12,9 L8,9 Z M3,17 L14,17 M18,17 L21,17 M14,14 L18,14 L18,20 L14,20 Z");
    public static Geometry Trash { get; } = Geometry.Parse("M3,6 L21,6 M9,6 L9,3 L15,3 L15,6 M5,6 L6,21 L18,21 L19,6 M10,10 L10,17 M14,10 L14,17");
    public static Geometry Pin { get; } = Geometry.Parse("M8,3 L16,3 M10,3 L10,9 L6,14 L18,14 L14,9 L14,3 M12,14 L12,21");
    // Filled rather than stroked, like a media player's.
    public static Geometry Close { get; } = Geometry.Parse("M6,6 L18,18 M18,6 L6,18");
    public static Geometry Play { get; } = Geometry.Parse("M7,4 L20,12 L7,20 Z");
    public static Geometry Pause { get; } = Geometry.Parse("M6,4 L10,4 L10,20 L6,20 Z M14,4 L18,4 L18,20 L14,20 Z");
    // Speaker body filled; the sound waves and the mute cross are thin filled strokes so they fill the same way.
    public static Geometry Volume { get; } = Geometry.Parse(
        "M3,9 L7,9 L12,4 L12,20 L7,15 L3,15 Z " +
        "M15,8.5 A5,5 0 0 1 15,15.5 L16.4,16.9 A7,7 0 0 0 16.4,7.1 Z " +
        "M17.8,5.7 A9,9 0 0 1 17.8,18.3 L19.2,19.7 A11,11 0 0 0 19.2,4.3 Z");
    public static Geometry Muted { get; } = Geometry.Parse(
        "M3,9 L7,9 L12,4 L12,20 L7,15 L3,15 Z " +
        "M14.5,9.4 L15.9,8 L18.5,10.6 L21.1,8 L22.5,9.4 L19.9,12 L22.5,14.6 L21.1,16 L18.5,13.4 L15.9,16 L14.5,14.6 L17.1,12 Z");
    public static Geometry Database { get; } = Geometry.Parse("M3,6 C3,1 21,1 21,6 C21,11 3,11 3,6 Z M3,6 L3,18 C3,23 21,23 21,18 L21,6 M3,12 C3,17 21,17 21,12");
}
