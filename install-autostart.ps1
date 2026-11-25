# Cài hoặc gỡ tự động chạy ngầm AntiCLI.Server khi Windows khởi động (không cần quyền Admin)
param(
    [int]$Port = 3847,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$startupDir = [Environment]::GetFolderPath("Startup")
$lnkPath = Join-Path $startupDir "AntiCLI-Server.lnk"

if ($Uninstall) {
    if (Test-Path $lnkPath) {
        Remove-Item $lnkPath -Force
        Write-Host "✅ Đã gỡ tự khởi động AntiCLI.Server khỏi Startup." -ForegroundColor Green
    } else {
        Write-Host "Chưa cài tự khởi động trước đó." -ForegroundColor Yellow
    }
    return
}

$scriptPath = Join-Path $PSScriptRoot "start-server.ps1"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnkPath)
$sc.TargetPath = "powershell.exe"
$sc.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$scriptPath`" -Port $Port -Background"
$sc.WorkingDirectory = $PSScriptRoot
$sc.Description = "AntiCLI Studio Headless Web Server"
$sc.Save()

Write-Host "✅ Đã cài tự chạy ngầm AntiCLI.Server khi đăng nhập Windows!" -ForegroundColor Green
Write-Host "   Shortcut: $lnkPath" -ForegroundColor Cyan
Write-Host "   Để gỡ: powershell -ExecutionPolicy Bypass -File .\install-autostart.ps1 -Uninstall" -ForegroundColor DarkGray
