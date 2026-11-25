# Khởi chạy AntiCLI.Server (Headless Web & WebSocket Server)
param(
    [int]$Port = 3847,
    [string]$BindHost = "0.0.0.0",
    [string]$Password = "",
    [string]$Workspace = "",
    [string]$PublicUrl = "",
    [switch]$Background
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "dist\AntiCLI-Server\AntiCLI.Server.exe"
if (-not (Test-Path $exe)) {
    Write-Host "Chưa tìm thấy AntiCLI.Server.exe, đang tự động biên dịch..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "build.ps1") -Target Server
}

$dotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
if (Test-Path $dotnetDir) {
    $env:DOTNET_ROOT = $dotnetDir
    $env:PATH = "$dotnetDir;$env:PATH"
}

$argsList = @("--host", $BindHost, "--port", "$Port")
if ($Password) { $argsList += @("--password", $Password) }
if ($Workspace) { $argsList += @("--workspace", $Workspace) }
if ($PublicUrl) { $argsList += @("--public-url", $PublicUrl) }

if ($Background) {
    $logDir = Join-Path $env:LOCALAPPDATA "AntiCLI"
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $logFile = Join-Path $logDir "server.log"
    $errFile = Join-Path $logDir "server.err.log"
    $p = Start-Process -FilePath $exe -ArgumentList $argsList -WorkingDirectory (Split-Path $exe) `
        -WindowStyle Hidden -RedirectStandardOutput $logFile -RedirectStandardError $errFile -PassThru
    Start-Sleep -Seconds 2
    Write-Host "✅ AntiCLI.Server đang chạy ngầm (PID=$($p.Id)) tại cổng $Port" -ForegroundColor Green
    Write-Host "   Xem địa chỉ LAN & mật khẩu lần đầu trong log: $logFile" -ForegroundColor Cyan
    if (Test-Path $logFile) { Get-Content $logFile -Tail 25 }
} else {
    & $exe @argsList
}
