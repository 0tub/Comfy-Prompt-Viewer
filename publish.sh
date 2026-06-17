#!/bin/bash
# ComfyPromptViewer Standalone Publish Script for Linux
# Compiles the app into a compressed, trimmed, single-file Linux x64 executable.

PROJECT_PATH="src/ComfyPromptViewer/ComfyPromptViewer.csproj"

echo "Publishing ComfyPromptViewer as a standalone Linux x64 executable..."

# Execute dotnet publish with optimization flags
dotnet publish "$PROJECT_PATH" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishTrimmed=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -p:DebugSymbols=false

if [ $? -eq 0 ]; then
    # Clean up third-party PDB symbol files and native libraries copied into the publish directory
    rm -f src/ComfyPromptViewer/bin/Release/net9.0/linux-x64/publish/*.pdb
    rm -f src/ComfyPromptViewer/bin/Release/net9.0/linux-x64/publish/*.so
    
    echo -e "\nPublish completed successfully!"
    echo "Your standalone executable is located at:"
    echo "src/ComfyPromptViewer/bin/Release/net9.0/linux-x64/publish/ComfyPromptViewer"
else
    echo "Publish failed. Please check the logs above."
    exit 1
fi
