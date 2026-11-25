using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AntiCliUi.Services;

public sealed class UpdateService
{
    private const string RepoSlug = "PrimiasArt/AntiCLI";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{RepoSlug}/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly bool _isServer;
    private readonly Action _requestAppShutdown;

    public UpdateService(bool isServer, Action requestAppShutdown)
    {
        _isServer = isServer;
        _requestAppShutdown = requestAppShutdown;
    }

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AntiCLI-Updater", "1.2.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public string CurrentVersion
    {
        get
        {
            var infoVer = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
            {
                var plus = infoVer.IndexOf('+');
                return (plus > 0 ? infoVer[..plus] : infoVer).TrimStart('v', 'V');
            }
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v is not null ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "1.0.0";
        }
    }

    private bool DetectIsPortable()
    {
        try
        {
            var procPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(procPath) && File.Exists(procPath))
            {
                // Self-contained single-file .NET 8 exe is > 25 MB; framework-dependent is < 2 MB
                if (new FileInfo(procPath).Length > 15 * 1024 * 1024)
                    return true;
            }
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")))
                return true;
        }
        catch { }
        return false;
    }

    public async Task<object> CheckForUpdateAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(LatestReleaseApi);
            if (!resp.IsSuccessStatusCode)
            {
                return new
                {
                    ok = false,
                    currentVersion = CurrentVersion,
                    error = $"GitHub API trả về mã lỗi {(int)resp.StatusCode}."
                };
            }

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var latestVer = tagName.TrimStart('v', 'V');
            var releaseName = root.TryGetProperty("name", out var rn) ? rn.GetString() ?? tagName : tagName;
            var releaseNotes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";

            var isPortable = DetectIsPortable();
            var rolePrefix = _isServer ? "AntiCLI-Server-" : "AntiCLI-Desktop-";
            var flavorSuffix = isPortable ? "-portable.zip" : "-lite.zip";
            var variantLabel = $"{(_isServer ? "Server" : "Desktop")} ({(isPortable ? "Portable" : "Lite")})";

            string? assetName = null;
            string? downloadUrl = null;
            long assetSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.StartsWith(rolePrefix, StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(flavorSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        downloadUrl = a.TryGetProperty("browser_download_url", out var du) ? du.GetString() : null;
                        assetSize = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0;
                        break;
                    }
                }
            }

            var hasUpdate = CompareSemVer(latestVer, CurrentVersion) > 0;

            return new
            {
                ok = true,
                currentVersion = CurrentVersion,
                latestVersion = latestVer,
                hasUpdate,
                variant = variantLabel,
                releaseName,
                releaseNotes,
                releaseUrl = htmlUrl,
                assetName = assetName ?? "",
                assetSizeMb = Math.Round(assetSize / (1024.0 * 1024.0), 2),
                downloadUrl = downloadUrl ?? "",
                canAutoInstall = !string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(Environment.ProcessPath)
            };
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                currentVersion = CurrentVersion,
                error = $"Không thể kiểm tra cập nhật: {ex.Message}"
            };
        }
    }

    public async Task<object> DownloadAndApplyUpdateAsync(string? explicitDownloadUrl = null, string? targetVersion = null)
    {
        try
        {
            var downloadUrl = explicitDownloadUrl;
            var latestVersion = targetVersion ?? "";

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                var check = await CheckForUpdateAsync();
                var el = JsonSerializer.SerializeToElement(check);
                if (!el.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
                    return check;
                downloadUrl = el.TryGetProperty("downloadUrl", out var du) ? du.GetString() : "";
                latestVersion = el.TryGetProperty("latestVersion", out var lv) ? lv.GetString() ?? "" : "";
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
                return new { ok = false, error = "Không tìm thấy gói tải về phù hợp trong bản phát hành mới nhất." };

            var procPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(procPath) || !File.Exists(procPath))
                return new { ok = false, error = "Không xác định được đường dẫn file thực thi hiện tại." };

            var appDir = Path.GetDirectoryName(procPath)!;
            var exeName = Path.GetFileName(procPath);

            var workRoot = Path.Combine(Path.GetTempPath(), $"AntiCLI-Update-{Guid.NewGuid():N}");
            var zipPath = Path.Combine(workRoot, "update.zip");
            var stagingDir = Path.Combine(workRoot, "staging");
            Directory.CreateDirectory(stagingDir);

            // 1. Download release ZIP
            using (var resp = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs);
            }

            // 2. Extract and validate executable exists inside ZIP
            ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);
            var stagedExe = Path.Combine(stagingDir, exeName);
            if (!File.Exists(stagedExe))
            {
                try { Directory.Delete(workRoot, true); } catch { }
                return new { ok = false, error = $"Gói cập nhật không chứa {exeName}." };
            }

            // 3. Build detached PowerShell updater script that swaps files after current PID exits & relaunches
            var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var psArgsArray = cmdArgs.Length == 0
                ? "@()"
                : "@(" + string.Join(", ", cmdArgs.Select(a => "'" + a.Replace("'", "''") + "'")) + ")";

            var scriptPath = Path.Combine(workRoot, "apply-update.ps1");
            var pid = Environment.ProcessId;
            var cwd = Environment.CurrentDirectory;

            var script = $$"""
$ErrorActionPreference = 'SilentlyContinue'
$targetPid = {{pid}}
for ($i = 0; $i -lt 40; $i++) {
    if (-not (Get-Process -Id $targetPid -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 300
}
Start-Sleep -Milliseconds 400

$src = '{{stagingDir.Replace("'", "''")}}'
$dst = '{{appDir.Replace("'", "''")}}'

Get-ChildItem -Path $src -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length).TrimStart('\', '/')
    $out = Join-Path $dst $rel
    $outDir = Split-Path $out -Parent
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
    for ($retry = 0; $retry -lt 10; $retry++) {
        try {
            Copy-Item -LiteralPath $_.FullName -Destination $out -Force -ErrorAction Stop
            break
        } catch {
            Start-Sleep -Milliseconds 350
        }
    }
}

$exe = '{{procPath.Replace("'", "''")}}'
$cwd = '{{cwd.Replace("'", "''")}}'
$argList = {{psArgsArray}}
if ($argList.Count -gt 0) {
    Start-Process -FilePath $exe -ArgumentList $argList -WorkingDirectory $cwd -WindowStyle {{(_isServer ? "Hidden" : "Normal")}}
} else {
    Start-Process -FilePath $exe -WorkingDirectory $cwd -WindowStyle {{(_isServer ? "Hidden" : "Normal")}}
}

Start-Sleep -Seconds 2
Remove-Item -LiteralPath '{{workRoot.Replace("'", "''")}}' -Recurse -Force -ErrorAction SilentlyContinue
""";

            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false));

            // Launch updater right after returning HTTP/WS response to client
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                catch { }
                await Task.Delay(250);
                _requestAppShutdown();
            });

            return new
            {
                ok = true,
                restarting = true,
                newVersion = latestVersion
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = $"Cập nhật thất bại: {ex.Message}" };
        }
    }

    private static int CompareSemVer(string a, string b)
    {
        static int[] Parse(string s) => s
            .Split('-', 2)[0]
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();

        var pa = Parse(a ?? "");
        var pb = Parse(b ?? "");
        var len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            var va = i < pa.Length ? pa[i] : 0;
            var vb = i < pb.Length ? pb[i] : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}
