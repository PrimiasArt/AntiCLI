# AntiCLI Studio — Unified Build & Publish Script (Desktop + Headless Web Server)
param(
    [ValidateSet("All", "Desktop", "Server")]
    [string]$Target = "All",
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

function Publish-Project($csproj, $outDir, $isSelfContained) {
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    Write-Host "Building $csproj → $outDir ..." -ForegroundColor Cyan

    if ($isSelfContained) {
        & $dotnet publish $csproj `
            -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=none -p:DebugSymbols=false `
            -o $outDir
    } else {
        & $dotnet publish $csproj `
            -c Release -r win-x64 --self-contained false `
            -p:PublishSingleFile=true `
            -p:DebugType=none -p:DebugSymbols=false `
            -o $outDir
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

    Get-ChildItem $outDir -Filter "*.xml" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

if ($Target -eq "All" -or $Target -eq "Desktop") {
    $deskOut = if ($SelfContained) { Join-Path $PSScriptRoot "dist\portable-desktop" } else { Join-Path $PSScriptRoot "dist\AntiCLI" }
    Publish-Project (Join-Path $PSScriptRoot "AntiCliUi.csproj") $deskOut $SelfContained
    Write-Host "✅ Desktop: $deskOut\AntiCLI.exe" -ForegroundColor Green
}

if ($Target -eq "All" -or $Target -eq "Server") {
    $srvOut = if ($SelfContained) { Join-Path $PSScriptRoot "dist\portable-server" } else { Join-Path $PSScriptRoot "dist\AntiCLI-Server" }
    Publish-Project (Join-Path $PSScriptRoot "Server\AntiCliServer.csproj") $srvOut $SelfContained
    Write-Host "✅ Web Server: $srvOut\AntiCLI.Server.exe" -ForegroundColor Green
}
