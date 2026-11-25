# AntiCLI Studio — Build & Publish Script
param(
    [switch]$SelfContained,
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $dotnet) {
    $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
}
if (-not (Test-Path $dotnet)) {
    throw "Không tìm thấy dotnet CLI. Hãy cài .NET 8 SDK trước."
}

if (-not $OutDir) {
    $OutDir = if ($SelfContained) { Join-Path $PSScriptRoot "dist\portable" } else { Join-Path $PSScriptRoot "dist\AntiCLI" }
}
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
Write-Host "Building AntiCLI Studio → $OutDir ..." -ForegroundColor Cyan

if ($SelfContained) {
    & $dotnet publish (Join-Path $PSScriptRoot "AntiCliUi.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $OutDir
} else {
    & $dotnet publish (Join-Path $PSScriptRoot "AntiCliUi.csproj") `
        -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $OutDir
}

# Remove unused IntelliSense XML doc files from output
Get-ChildItem $OutDir -Filter "*.xml" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "✅ Hoàn tất! Chạy file: $OutDir\AntiCLI.exe" -ForegroundColor Green
