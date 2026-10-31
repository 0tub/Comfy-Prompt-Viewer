namespace ComfyPromptViewer;

// What one file's metadata says, as the sidebar, search and cache use it. Immutable, so an item swaps the whole
// value when its metadata lands and nothing can observe it half-applied. A new field is added here, in the
// repository's document mapping, and wherever it is shown.
public sealed record ImageMetadata(
    int Width,
    int Height,
    string Prompt,
    string NegativePrompt,
    string Tool,
    string Model,
    string Sampler,
    string Seed,
    string Settings,
    string Lora,
    string Resources)
{
    public static ImageMetadata Empty { get; } = new(0, 0, "", "", "", "", "", "", "", "", "");

    public static ImageMetadata From(ImageReadResult read, ExtractedPromptMetadata extracted)
    {
        var settings = extracted.GenerationSettings;
        return new ImageMetadata(
            read.Width,
            read.Height,
            extracted.Prompt,
            extracted.NegativePrompt,
            settings.Tool,
            settings.Model,
            settings.Sampler,
            settings.Seed,
            settings.Settings,
            settings.Lora,
            settings.Resources);
    }

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);
    public bool HasNegativePrompt => !string.IsNullOrWhiteSpace(NegativePrompt);
    public string DimensionsText => Width > 0 && Height > 0 ? $"{Width} x {Height}" : "Unknown size";
}
