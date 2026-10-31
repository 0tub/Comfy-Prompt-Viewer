using System;
using Avalonia.Controls;

namespace ComfyPromptViewer;

public enum ImageAction
{
    Open,
    CopyPrompt,
    CopyNegativePrompt,
    CopyPath,
    Delete
}

// The menu on a gallery card and on the large preview. Both act on the same selection, so the items and
// their wording are defined once here; the window decides what each action does.
public sealed class ImageContextMenu : ContextMenu
{
    private readonly MenuItem _open;
    private readonly MenuItem _copyPrompt;
    private readonly MenuItem _copyNegative;
    private readonly MenuItem _copyPath;
    private readonly MenuItem _delete;

    public ImageContextMenu()
    {
        _open = AddItem("Open in File Explorer", ImageAction.Open);
        _copyPrompt = AddItem("Copy Prompt", ImageAction.CopyPrompt);
        _copyNegative = AddItem("Copy Negative Prompt", ImageAction.CopyNegativePrompt);
        _copyPath = AddItem("Copy Image Path", ImageAction.CopyPath);
        Items.Add(new Separator());
        _delete = AddItem("Delete Image", ImageAction.Delete);
    }

    // Styled as a plain ContextMenu by the theme.
    protected override Type StyleKeyOverride => typeof(ContextMenu);

    public event Action<ImageContextMenu, ImageAction>? ActionInvoked;

    // Called while opening, for the image it opened on and how many are selected along with it.
    internal void Configure(int selectedCount, bool canCopyPrompt, bool canCopyNegative)
    {
        var single = selectedCount == 1;
        _open.IsVisible = single;
        _copyNegative.IsVisible = single;
        _copyPath.IsVisible = single;
        _copyPrompt.Header = single ? "Copy Prompt" : "Copy Selected Prompts";
        _copyPrompt.IsEnabled = canCopyPrompt;
        _copyNegative.IsEnabled = canCopyNegative;
        _delete.Header = single ? "Delete Image" : $"Delete {selectedCount:n0} Images";
    }

    private MenuItem AddItem(string header, ImageAction action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => ActionInvoked?.Invoke(this, action);
        Items.Add(item);
        return item;
    }
}
