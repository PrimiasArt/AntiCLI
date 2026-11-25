# AntiCLI Studio — Build & Publish Script
param(
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $dotnet) {
    $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
}
if (-not (Test-Path $dotnet)) {
    throw "Không tìm thấy dotnet CLI. Hãy cài .NET 8 SDK trước."
}

$outDir = Join-Path $PSScriptRoot "dist\AntiCLI"
Write-Host "Building AntiCLI Studio → $outDir ..." -ForegroundColor Cyan

if ($SelfContained) {
    & $dotnet publish (Join-Path $PSScriptRoot "AntiCliUi.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $outDir
} else {
    & $dotnet publish (Join-Path $PSScriptRoot "AntiCliUi.csproj") `
        -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $outDir
}

Write-Host "✅ Hoàn tất! Chạy file: $outDir\AntiCLI.exe" -ForegroundColor Green
