using System;
using System.IO;

namespace ComfyPromptViewer;

internal interface IImageContainerReader
{
    bool Supports(ReadOnlySpan<char> extension);
    ImageReadResult Read(Stream stream);
}

internal sealed class PngContainerReader : IImageContainerReader
{
    public bool Supports(ReadOnlySpan<char> extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase);

    public ImageReadResult Read(Stream stream) => ImageFileReader.ReadPng(stream);
}

internal sealed class JpegContainerReader : IImageContainerReader
{
    public bool Supports(ReadOnlySpan<char> extension) =>
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    public ImageReadResult Read(Stream stream) => ImageFileReader.ReadJpeg(stream);
}

internal sealed class WebPContainerReader : IImageContainerReader
{
    public bool Supports(ReadOnlySpan<char> extension) =>
        extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);

    public ImageReadResult Read(Stream stream) => ImageFileReader.ReadWebP(stream);
}
