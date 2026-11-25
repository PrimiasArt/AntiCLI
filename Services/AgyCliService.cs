using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AntiCliUi.Services;

public sealed class AgyCliService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _streamWriteLock = new(1, 1);
    private Process? _streamProc;
    private CancellationTokenSource? _streamCts;
    private string? _streamConversationId;

    public event Action<string, object?>? OnBridgeEvent;

    public AgyCliService(SettingsService settings)
    {
        _settings = settings;
    }

    private void Emit(string type, object? payload) => OnBridgeEvent?.Invoke(type, payload);

    public async Task<object> GetCliInfoAsync()
    {
        var exe = _settings.ResolveAgyPath();
        if (!File.Exists(exe))
            return new { installed = false, path = exe, version = "" };

        var (code, stdout, stderr) = await RunOneShotAsync(exe, "--version", Environment.CurrentDirectory, 10000);
        return new
        {
            installed = code == 0,
            path = exe,
            version = stdout.Trim(),
            error = code == 0 ? "" : stderr.Trim()
        };
    }

    public async Task<object> ListModelsAsync(string cwd)
    {
        var exe = _settings.ResolveAgyPath();
        if (!File.Exists(exe))
            return new { ok = false, error = "Không tìm thấy agy.exe", models = Array.Empty<object>() };

        var (code, stdout, stderr) = await RunOneShotAsync(exe, "models", cwd, 20000);
        var models = new List<object>();
        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t', 2);
            if (parts.Length == 2)
                models.Add(new { id = parts[0].Trim(), name = parts[1].Trim() });
            else
            {
                var idx = line.IndexOf("  ", StringComparison.Ordinal);
                if (idx > 0)
                    models.Add(new { id = line[..idx].Trim(), name = line[idx..].Trim() });
                else
                    models.Add(new { id = line, name = line });
            }
        }

        return new { ok = code == 0, models, error = code == 0 ? "" : stderr.Trim() };
    }

    public async Task<object> RunSlashReportAsync(string slashCommand, string cwd)
    {
        var exe = _settings.ResolveAgyPath();
        if (!File.Exists(exe))
            return new { ok = false, output = "", error = "Không tìm thấy agy.exe" };

        var escaped = slashCommand.Replace("\"", "\\\"");
        var (code, stdout, stderr) = await RunOneShotAsync(exe, $"-p \"{escaped}\"", cwd, 30000);
        var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        return new { ok = code == 0, output = output.Trim(), error = code == 0 ? "" : stderr.Trim() };
    }

    public object LaunchInteractiveLogin()
    {
        var exe = _settings.ResolveAgyPath();
        if (!File.Exists(exe))
            return new { ok = false, error = "Không tìm thấy agy.exe" };

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -Command \"& '{exe}' acp --login\"",
                UseShellExecute = true
            });
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public async Task<object> InstallAgyCliAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://antigravity.google/cli/install.ps1 | iex\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await proc.WaitForExitAsync(cts.Token);

            var stdout = await outTask;
            var stderr = await errTask;
            var resolved = _settings.ResolveAgyPath();
            var installed = File.Exists(resolved);
            var ver = "";
            if (installed)
            {
                var (vc, vo, _) = await RunOneShotAsync(resolved, "--version", Environment.CurrentDirectory, 10000);
                if (vc == 0) ver = vo.Trim();
            }

            return new
            {
                ok = installed,
                installed,
                path = resolved,
                version = ver,
                output = (stdout + "\n" + stderr).Trim()
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, installed = false, error = ex.Message };
        }
    }

    // Persistent Stream-JSON mode (agy --input-format stream-json --output-format stream-json)
    public async Task<object> StartStreamSessionAsync(string cwd, string? model = null, string? mode = null, string? resumeConversationId = null)
    {
        var prevCid = resumeConversationId;
        StopStreamSession();

        var exe = _settings.ResolveAgyPath();
        if (!File.Exists(exe))
            return new { ok = false, error = $"Không tìm thấy agy.exe tại {exe}" };

        var args = new List<string> { "--input-format", "stream-json", "--output-format", "stream-json" };
        if (!string.IsNullOrWhiteSpace(prevCid)) { args.Add("--conversation"); args.Add(prevCid); }
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model); }
        if (!string.IsNullOrWhiteSpace(mode) && mode != "default") { args.Add("--mode"); args.Add(mode); }
        if (_settings.Current.EnableSandbox) args.Add("--sandbox");
        if (_settings.Current.AutoApprovePermissions) args.Add("--dangerously-skip-permissions");

        var psi = CreateStartInfo(exe, string.Join(" ", args), cwd);
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Exited += (_, _) =>
        {
            Emit("stream:status", new { state = "disconnected" });
        };

        proc.Start();
        _streamProc = proc;
        _streamCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadStreamLoopAsync(proc, _streamCts.Token));
        _ = Task.Run(() => ReadStreamErrLoopAsync(proc, _streamCts.Token));

        Emit("stream:status", new { state = "connected", workspace = cwd });
        await Task.CompletedTask;
        return new { ok = true };
    }

    public async Task<object> SendStreamPromptAsync(string content)
    {
        if (_streamProc is null || _streamProc.HasExited)
            return new { ok = false, error = "Stream-JSON session chưa khởi động." };

        var line = JsonSerializer.Serialize(new
        {
            @event = "user",
            message = new { content }
        });

        await _streamWriteLock.WaitAsync();
        try
        {
            Emit("acp:wire", new { dir = "out", json = line });
            await _streamProc.StandardInput.WriteLineAsync(line);
            await _streamProc.StandardInput.FlushAsync();
            return new { ok = true };
        }
        finally
        {
            _streamWriteLock.Release();
        }
    }

    public void StopStreamSession()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
        _streamConversationId = null;

        if (_streamProc is not null)
        {
            try
            {
                if (!_streamProc.HasExited) _streamProc.Kill(entireProcessTree: true);
            }
            catch { }
            _streamProc.Dispose();
            _streamProc = null;
        }
    }

    private async Task ReadStreamLoopAsync(Process proc, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                Emit("acp:wire", new { dir = "in", json = line.Length > 4000 ? line[..4000] + "…" : line });
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement.Clone();
                    if (root.TryGetProperty("conversation_id", out var cid))
                        _streamConversationId = cid.GetString();
                    Emit("stream:event", root);
                }
                catch { }
            }
        }
        catch { }
    }

    private async Task ReadStreamErrLoopAsync(Process proc, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardError.ReadLineAsync(ct);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line))
                    Emit("acp:log", new { direction = "stderr", text = line });
            }
        }
        catch { }
    }

    private ProcessStartInfo CreateStartInfo(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["PATH"] = BrowserToolsService.BuildEnhancedPathEnv();

        if (string.Equals(_settings.Current.AuthMode, "gemini-api-key", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_settings.Current.GeminiApiKeyPlain))
        {
            psi.Environment["GEMINI_API_KEY"] = _settings.Current.GeminiApiKeyPlain;
            if (!string.IsNullOrWhiteSpace(_settings.Current.GeminiBaseUrl))
                psi.Environment["GOOGLE_GEMINI_BASE_URL"] = _settings.Current.GeminiBaseUrl;
        }
        return psi;
    }

    private async Task<(int code, string stdout, string stderr)> RunOneShotAsync(string exe, string args, string cwd, int timeoutMs)
    {
        using var proc = new Process { StartInfo = CreateStartInfo(exe, args, cwd) };
        proc.Start();
        proc.StandardInput.Close();

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "Command timed out.");
        }

        return (proc.ExitCode, await outTask, await errTask);
    }

    public void Dispose()
    {
        StopStreamSession();
        _streamWriteLock.Dispose();
    }
}
