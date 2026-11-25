# Mở truy cập HTTPS qua Internet từ bất kỳ mạng nào (dùng Cloudflare Tunnel — không cần mở port router / không cần quyền Admin)
param(
    [int]$Port = 3847,
    [string]$Password = "",
    [string]$Workspace = "",
    [string]$TunnelToken = "" # Tùy chọn: nhập token từ Cloudflare Zero Trust để dùng tên miền cố định
)

$ErrorActionPreference = "Stop"

# 1. Đảm bảo đã có cloudflared.exe (tải bản chính thức từ GitHub Releases nếu chưa có)
$cfDir = Join-Path $env:LOCALAPPDATA "Programs\cloudflared"
$cfExe = Join-Path $cfDir "cloudflared.exe"
if (-not (Test-Path $cfExe)) {
    New-Item -ItemType Directory -Force -Path $cfDir | Out-Null
    $url = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe"
    Write-Host "Đang tải Cloudflare Tunnel (cloudflared.exe chính thức)..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $cfExe -UseBasicParsing
    Write-Host "✅ Đã tải xong: $cfExe" -ForegroundColor Green
}

# 2. Đảm bảo đã biên dịch AntiCLI.Server.exe
$srvExe = Join-Path $PSScriptRoot "dist\AntiCLI-Server\AntiCLI.Server.exe"
if (-not (Test-Path $srvExe)) {
    & (Join-Path $PSScriptRoot "build.ps1") -Target Server
}

$dotnetDir = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
if (Test-Path $dotnetDir) {
    $env:DOTNET_ROOT = $dotnetDir
    $env:PATH = "$dotnetDir;$env:PATH"
}

$logDir = Join-Path $env:LOCALAPPDATA "AntiCLI"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# 3. Nếu dùng Named Tunnel (tên miền cố định trên Cloudflare Zero Trust)
if ($TunnelToken) {
    Write-Host "Khởi chạy Cloudflare Named Tunnel + AntiCLI.Server (cổng $Port)..." -ForegroundColor Cyan
    $cfProc = Start-Process -FilePath $cfExe -ArgumentList @("tunnel", "run", "--token", $TunnelToken) -WindowStyle Hidden -PassThru
    try {
        & (Join-Path $PSScriptRoot "start-server.ps1") -Port $Port -Password $Password -Workspace $Workspace
    } finally {
        if (-not $cfProc.HasExited) { $cfProc.Kill() }
    }
    return
}

# 4. Quick Tunnel: Tự động tạo link HTTPS ngẫu nhiên https://*.trycloudflare.com
$cfLog = Join-Path $logDir "tunnel.log"
if (Test-Path $cfLog) { Remove-Item $cfLog -Force }

Write-Host "Đang mở Cloudflare Quick Tunnel (HTTPS) tới http://127.0.0.1:$Port ..." -ForegroundColor Cyan
$cfProc = Start-Process -FilePath $cfExe `
    -ArgumentList @("tunnel", "--no-autoupdate", "--url", "http://127.0.0.1:$Port") `
    -WindowStyle Hidden -RedirectStandardError $cfLog -PassThru

$publicUrl = ""
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $cfLog) {
        $txt = Get-Content $cfLog -Raw -ErrorAction SilentlyContinue
        if ($txt -match '(https://[a-z0-9-]+\.trycloudflare\.com)') {
            $publicUrl = $Matches[1]
            break
        }
    }
}

if ($publicUrl) {
    Write-Host ""
    Write-Host "🌐 LINK TRUY CẬP TỪ XA (HTTPS — mọi mạng / 4G / máy khác):" -ForegroundColor Green
    Write-Host "   $publicUrl" -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Host "⚠ Chưa bắt được URL Quick Tunnel trong 15s (kiểm tra $cfLog). Vẫn khởi động Server..." -ForegroundColor Yellow
}

try {
    & (Join-Path $PSScriptRoot "start-server.ps1") -Port $Port -Password $Password -Workspace $Workspace -PublicUrl $publicUrl
} finally {
    if ($cfProc -and -not $cfProc.HasExited) {
        $cfProc.Kill()
        Write-Host "Đã đóng Cloudflare Tunnel." -ForegroundColor DarkGray
    }
}
