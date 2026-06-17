# ComfyPromptViewer Standalone Publish Script
# Compiles the app into a compressed, trimmed, single-file Windows x64 executable.

$projectPath = "src\ComfyPromptViewer\ComfyPromptViewer.csproj"

Write-Host "Publishing ComfyPromptViewer as a standalone single-file executable..." -ForegroundColor Cyan

# Execute dotnet publish with optimization flags
dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false

if ($LASTEXITCODE -eq 0) {
    # Clean up third-party PDB symbol files copied into the publish directory
    Get-ChildItem -Path "src\ComfyPromptViewer\bin\Release\net9.0\win-x64\publish\*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

    Write-Host "`nPublish completed successfully!" -ForegroundColor Green
    Write-Host "Your standalone executable is located at:" -ForegroundColor Yellow
    Write-Host "src\ComfyPromptViewer\bin\Release\net9.0\win-x64\publish\ComfyPromptViewer.exe" -ForegroundColor Cyan
} else {
    Write-Error "Publish failed. If you get a file access lock error, make sure the application is closed and try again."
}
