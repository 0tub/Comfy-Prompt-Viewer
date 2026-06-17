# ComfyPromptViewer

A local-first desktop app (.NET 9 + Avalonia 12) for browsing ComfyUI output images and viewing generation metadata (prompts, sampler settings, and models) without loading them back into the ComfyUI web interface.

## Features

- **Virtualized Gallery**: Smooth, low-latency scrolling through folders with thousands of images.
- **Metadata Viewer**: Parses positive prompts, negative prompts, models, samplers, seed, steps, CFG, and schedulers from PNG, JPEG, and WebP metadata.
- **Directory Watcher**: Monitors the active folder and automatically refreshes when new images are generated.
- **Zoomable Preview**: Double-click any image to open a full-window overlay with mouse-wheel zoom and drag-to-pan.
- **Performance Optimized**: Implements a disk cache for resized thumbnails and an in-memory LRU cache to keep RAM usage low.
- **Copy Prompts**: Quick-copy positive and negative prompts directly from the UI.

## Getting Started

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### Run from Source
```powershell
dotnet run --project src\ComfyPromptViewer\ComfyPromptViewer.csproj
```

### Build Standalone Executables
Runs native trimming, compression, and outputs a single-file application requiring no external dependencies:

- **Windows**: Run `.\publish.ps1` (outputs `src\ComfyPromptViewer\bin\Release\net9.0\win-x64\publish\ComfyPromptViewer.exe`)
- **Linux**: Run `./publish.sh` (outputs `src/ComfyPromptViewer/bin/Release/net9.0/linux-x64/publish/ComfyPromptViewer`)

## File Structure

- [src/ComfyPromptViewer/MainWindow.axaml](src/ComfyPromptViewer/MainWindow.axaml) — Core layout & sidebar details.
- [src/ComfyPromptViewer/PromptExtractor.cs](src/ComfyPromptViewer/PromptExtractor.cs) — Metadata parsing logic for ComfyUI.
- [src/ComfyPromptViewer/ImageFileReader.cs](src/ComfyPromptViewer/ImageFileReader.cs) — Low-allocation image header and size reader.
- [src/ComfyPromptViewer/ThumbnailLoadCoordinator.cs](src/ComfyPromptViewer/ThumbnailLoadCoordinator.cs) — Viewport-aware thumbnail loading scheduling.
- [src/ComfyPromptViewer/ImageCache.cs](src/ComfyPromptViewer/ImageCache.cs) — LRU cache for decoded bitmap memory.

## License

This project is licensed under the [MIT License](LICENSE).
