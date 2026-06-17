using Avalonia.Collections;

namespace ComfyPromptViewer;

public sealed class GalleryViewModel
{
    public AvaloniaList<ImageItem> Items { get; } = [];
}
