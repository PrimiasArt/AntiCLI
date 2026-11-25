using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AntiCliUi.Services;

public sealed class BrowserToolsService
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly SettingsService _settings;

    public BrowserToolsService(SettingsService settings)
    {
        _settings = settings;
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AntiCLI", "1.3.0"));
        return c;
    }

    public string? FindBrowserExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public string? FindNpxExecutable()
    {
        var localNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs");
        var candidates = new List<string>
        {
            Path.Combine(localNode, "npx.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npx.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "npx.cmd")
        };

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        foreach (var dir in (pathEnv + Path.PathSeparator + userPath).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim(), "npx.cmd"));
                candidates.Add(Path.Combine(dir.Trim(), "npx.exe"));
            }
            catch { }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<bool> IsDebugPortAliveAsync(int port)
    {
        try
        {
            using var resp = await Http.GetAsync($"http://127.0.0.1:{port}/json/version");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public bool HasChromeDevToolsActivePort()
    {
        var defaultPortFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data", "DevToolsActivePort");
        return File.Exists(defaultPortFile);
    }

    public async Task<object> GetEnvironmentStatusAsync()
    {
        var port = _settings.Current.ChromeDebugPort > 0 ? _settings.Current.ChromeDebugPort : 9222;
        var browserExe = FindBrowserExecutable();
        var npxExe = FindNpxExecutable();
        var debugPortOpen = await IsDebugPortAliveAsync(port);
        var defaultDevToolsFile = HasChromeDevToolsActivePort();

        return new
        {
            ok = true,
            browserInstalled = !string.IsNullOrEmpty(browserExe),
            browserPath = browserExe ?? "",
            browserName = browserExe is not null && browserExe.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" : "Google Chrome",
            npxInstalled = !string.IsNullOrEmpty(npxExe),
            npxPath = npxExe ?? "",
            debugPort = port,
            debugPortOpen,
            defaultDevToolsActive = defaultDevToolsFile,
            browserReady = debugPortOpen || defaultDevToolsFile,
            enableBrowserMcp = _settings.Current.EnableBrowserMcp,
            autoLaunchDebugChrome = _settings.Current.AutoLaunchDebugChrome
        };
    }

    public async Task<object> LaunchDebugBrowserAsync(string? startUrl = null)
    {
        var exe = FindBrowserExecutable();
        if (string.IsNullOrEmpty(exe))
            return new { ok = false, error = "Không tìm thấy Google Chrome hoặc Microsoft Edge trên máy chủ." };

        var port = _settings.Current.ChromeDebugPort > 0 ? _settings.Current.ChromeDebugPort : 9222;
        if (await IsDebugPortAliveAsync(port))
            return new { ok = true, alreadyRunning = true, port, browserPath = exe };

        var profileDir = Path.Combine(_settings.DataDirectory, "ChromeAgentProfile");
        Directory.CreateDirectory(profileDir);

        var urlArg = string.IsNullOrWhiteSpace(startUrl) ? "about:blank" : $"\"{startUrl}\"";
        var args = $"--remote-debugging-port={port} --user-data-dir=\"{profileDir}\" --no-first-run --no-default-browser-check {urlArg}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true
            });

            // Wait up to 8 seconds for debugging port to respond
            for (int i = 0; i < 16; i++)
            {
                await Task.Delay(500);
                if (await IsDebugPortAliveAsync(port))
                    return new { ok = true, launched = true, port, browserPath = exe };
            }

            return new { ok = true, launched = true, port, warning = "Đã mở trình duyệt nhưng cổng debug phản hồi chậm — hãy thử kiểm tra lại sau vài giây." };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = $"Không thể khởi chạy trình duyệt: {ex.Message}" };
        }
    }

    public async Task<object> InstallPortableNodeJsAsync()
    {
        try
        {
            using var dlHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            dlHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AntiCLI", "1.3.0"));

            // Query official Node.js release index for latest LTS v22 win-x64 zip
            var indexJson = await dlHttp.GetStringAsync("https://nodejs.org/dist/index.json");
            using var doc = JsonDocument.Parse(indexJson);
            string? ltsVer = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("lts", out var lts) && lts.ValueKind == JsonValueKind.String)
                {
                    ltsVer = item.GetProperty("version").GetString();
                    break;
                }
            }
            if (string.IsNullOrEmpty(ltsVer))
                return new { ok = false, error = "Không tìm thấy phiên bản Node.js LTS trên nodejs.org." };

            var zipName = $"node-{ltsVer}-win-x64.zip";
            var zipUrl = $"https://nodejs.org/dist/{ltsVer}/{zipName}";
            var tempRoot = Path.Combine(Path.GetTempPath(), $"AntiCLI-Node-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, zipName);
            var extractDir = Path.Combine(tempRoot, "ext");

            using (var resp = await dlHttp.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            var innerDir = Directory.GetDirectories(extractDir).FirstOrDefault() ?? extractDir;

            var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs");
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(innerDir, targetDir);

            try { Directory.Delete(tempRoot, true); } catch { }

            // Add to User PATH so npx is immediately discoverable
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            if (!userPath.Contains(targetDir, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", $"{targetDir};{userPath}", EnvironmentVariableTarget.User);
            }
            var procPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!procPath.Contains(targetDir, StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", $"{targetDir};{procPath}");
            }

            var npx = Path.Combine(targetDir, "npx.cmd");
            return new { ok = File.Exists(npx), version = ltsVer, npxPath = npx };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = $"Cài Node.js thất bại: {ex.Message}" };
        }
    }

    public async Task<object[]> BuildSessionMcpServersAsync()
    {
        var servers = new List<object>();
        if (!_settings.Current.EnableBrowserMcp) return servers.ToArray();

        var npx = FindNpxExecutable();
        if (string.IsNullOrEmpty(npx)) return servers.ToArray();

        var port = _settings.Current.ChromeDebugPort > 0 ? _settings.Current.ChromeDebugPort : 9222;
        if (_settings.Current.AutoLaunchDebugChrome && !await IsDebugPortAliveAsync(port))
        {
            await LaunchDebugBrowserAsync();
        }

        if (await IsDebugPortAliveAsync(port))
        {
            servers.Add(new
            {
                name = "chrome-devtools",
                command = npx,
                args = new[] { "-y", "chrome-devtools-mcp@latest", $"--browserUrl=http://127.0.0.1:{port}" },
                env = Array.Empty<object>()
            });
        }

        return servers.ToArray();
    }

    public static string BuildEnhancedPathEnv()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var extras = new[]
        {
            Path.Combine(local, "agy", "bin"),
            Path.Combine(local, "Programs", "nodejs"),
            Path.Combine(local, "Programs", "Git", "cmd"),
            Path.Combine(local, "Programs", "gh", "bin"),
            Path.Combine(local, "Microsoft", "dotnet")
        }.Where(Directory.Exists);

        var cur = Environment.GetEnvironmentVariable("PATH") ?? "";
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = extras
            .Concat(cur.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Concat(user.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(Path.PathSeparator, parts);
    }
}
