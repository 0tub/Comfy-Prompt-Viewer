using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;

namespace ComfyPromptViewer;

public partial class AboutWindow : Window
{
    private sealed record LicenseDocument(string Title, string ResourcePath);

    private static readonly LicenseDocument[] Documents =
    [
        new("Comfy Prompt Viewer — MIT License", "Assets/Legal/LICENSE"),
        new("Third-Party Notices", "Assets/Legal/THIRD-PARTY-NOTICES.md"),
        new("SkiaSharp & HarfBuzzSharp — Bundled Native Components", "Assets/Legal/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt")
    ];

    private readonly Dictionary<string, string> _loaded = [];

    public AboutWindow()
    {
        InitializeComponent();

        var version = typeof(AboutWindow).Assembly.GetName().Version;
        VersionText.Text = version is null ? "" : $"Version {version.ToString(3)}";

        foreach (var document in Documents)
        {
            DocumentSelector.Items.Add(document.Title);
        }
        DocumentSelector.SelectedIndex = 0;
    }

    private void DocumentSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = DocumentSelector.SelectedIndex;
        if (index < 0 || index >= Documents.Length)
        {
            return;
        }

        DocumentText.Text = Load(Documents[index].ResourcePath);
        DocumentScroller.ScrollToHome();
    }

    private string Load(string resourcePath)
    {
        if (_loaded.TryGetValue(resourcePath, out var cached))
        {
            return cached;
        }

        string text;
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://ComfyPromptViewer/{resourcePath}"));
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Failed to load license document {resourcePath}: {ex}");
            text = $"Could not load {resourcePath}.\n\nThese notices are also published at:\nhttps://github.com/0tub/Comfy-Prompt-Viewer";
        }

        _loaded[resourcePath] = text;
        return text;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
