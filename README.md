# ComfyPromptViewer

ComfyPromptViewer is a local desktop app for browsing AI image output folders and viewing the prompts and generation settings saved inside image files.

It supports images from ComfyUI, Forge Neo, Draw Things, and Stable Diffusion WebUI/A1111-style metadata.

![ComfyPromptViewer Grid Preview](imgs/grid.png)

## Features

- Browse large folders of generated images.
- View prompts, negative prompts, image dimensions, models, samplers, seeds, and common generation settings.
- Search by filename, positive prompt, negative prompt, or all metadata.
- Copy prompts, open images in the file manager, and delete images from the gallery.
- Watch the active folder for newly created or removed images.
- Cache thumbnails and parsed metadata locally for faster repeat browsing.

## Getting Started

### Download a Release
Download the latest packaged build from the [GitHub Releases page](https://github.com/0tub/Comfy-Prompt-Viewer/releases).

### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### Run from Source
```powershell
dotnet run --project src\ComfyPromptViewer\ComfyPromptViewer.csproj
```

### Build Standalone Executables
- **Windows**: Run `.\publish.ps1` (outputs `src\ComfyPromptViewer\bin\Release\net9.0\win-x64\publish\ComfyPromptViewer.exe`)
- **Linux**: Run `./publish.sh` (outputs `src/ComfyPromptViewer/bin/Release/net9.0/linux-x64/publish/ComfyPromptViewer`)

### Checks
After a Debug build, run the built-in self-check:

```powershell
dotnet src\ComfyPromptViewer\bin\Debug\net9.0\ComfyPromptViewer.dll --self-check
```

## Project Structure

- [src/ComfyPromptViewer/MainWindow.axaml](src/ComfyPromptViewer/MainWindow.axaml) - Main UI layout and sidebar details.
- [src/ComfyPromptViewer/MainWindow.axaml.cs](src/ComfyPromptViewer/MainWindow.axaml.cs) - Window initialization, folder loading, sorting, gallery/search/sidebar/menu coordination, and menu/toolbar interactions.
- [src/ComfyPromptViewer/MainWindow.Watcher.cs](src/ComfyPromptViewer/MainWindow.Watcher.cs) - Active-folder auto-refresh with `FileSystemWatcher`, event batching, and debounce dispatch.
- [src/ComfyPromptViewer/MainWindow.Autoscroll.cs](src/ComfyPromptViewer/MainWindow.Autoscroll.cs) - Middle-click autoscroll loop, velocity smoothing, and pointer capture handlers.
- [src/ComfyPromptViewer/MainWindow.Preview.cs](src/ComfyPromptViewer/MainWindow.Preview.cs) - Large preview overlay, zoom calculations, pan clamping, and canvas input handlers.
- [src/ComfyPromptViewer/PromptExtractor.cs](src/ComfyPromptViewer/PromptExtractor.cs) - Metadata parsing logic for ComfyUI.
- [src/ComfyPromptViewer/MetadataIndex.cs](src/ComfyPromptViewer/MetadataIndex.cs) - LiteDB-backed app-local metadata cache.
- [src/ComfyPromptViewer/ImageFileReader.cs](src/ComfyPromptViewer/ImageFileReader.cs) - Low-allocation image header and size reader.
- [src/ComfyPromptViewer/ThumbnailLoadCoordinator.cs](src/ComfyPromptViewer/ThumbnailLoadCoordinator.cs) - Viewport-aware thumbnail loading scheduling.
- [src/ComfyPromptViewer/ImageCache.cs](src/ComfyPromptViewer/ImageCache.cs) - LRU cache for decoded bitmap memory.

## License

This project is licensed under the [MIT License](LICENSE).

Third-party license notices are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
