using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AntiCliUi.Services;

public sealed class AcpClientService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, JsonElement> _pendingPermissions = new();

    private Process? _proc;
    private CancellationTokenSource? _readCts;
    private int _nextId;
    private bool _disposed;

    public bool IsRunning => _proc is { HasExited: false };
    public string? ActiveSessionId { get; private set; }
    public string ActiveWorkspace { get; private set; } = "";
    public JsonElement? AgentInfo { get; private set; }
    public JsonElement? AgentCapabilities { get; private set; }
    public JsonElement? AuthMethods { get; private set; }
    public JsonElement? TierInfo { get; private set; }

    public event Action<string, object?>? OnBridgeEvent;

    public AcpClientService(SettingsService settings)
    {
        _settings = settings;
    }

    private void Emit(string type, object? payload) => OnBridgeEvent?.Invoke(type, payload);

    public async Task<object> ConnectAsync(string workspacePath)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            await StopInternalAsync();

            ActiveWorkspace = Directory.Exists(workspacePath)
                ? Path.GetFullPath(workspacePath)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var agyExe = _settings.ResolveAgyPath();
            if (!File.Exists(agyExe))
            {
                Emit("acp:status", new
                {
                    state = "agy_missing",
                    message = "Chưa cài Antigravity CLI (agy.exe)"
                });
                return new
                {
                    ok = false,
                    agyMissing = true,
                    error = "Máy chủ chưa cài Antigravity CLI (agy.exe). Bấm nút cài tự động hoặc chạy: irm https://antigravity.google/cli/install.ps1 | iex"
                };
            }

            Emit("acp:status", new { state = "connecting", message = "Đang khởi động Antigravity ACP Server..." });

            var args = new List<string> { "acp" };
            if (_settings.Current.EnableSandbox)
                args.Add("--sandbox");
            if (_settings.Current.AutoApprovePermissions)
                args.Add("--dangerously-skip-permissions");

            var psi = new ProcessStartInfo
            {
                FileName = agyExe,
                Arguments = string.Join(" ", args),
                WorkingDirectory = ActiveWorkspace,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (string.Equals(_settings.Current.AuthMode, "gemini-api-key", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(_settings.Current.GeminiApiKeyPlain))
                    psi.Environment["GEMINI_API_KEY"] = _settings.Current.GeminiApiKeyPlain;
                if (!string.IsNullOrWhiteSpace(_settings.Current.GeminiBaseUrl))
                    psi.Environment["GOOGLE_GEMINI_BASE_URL"] = _settings.Current.GeminiBaseUrl;
            }
            else
            {
                psi.Environment.Remove("GEMINI_API_KEY");
                psi.Environment.Remove("GOOGLE_GEMINI_BASE_URL");
            }

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) =>
            {
                var exitCode = -1;
                try { exitCode = proc.ExitCode; } catch { }
                FailAllPending($"ACP server exited (code {exitCode})");
                Emit("acp:status", new
                {
                    state = "disconnected",
                    message = $"ACP Server đã dừng (exit {exitCode})"
                });
            };

            proc.Start();
            _proc = proc;
            _readCts = new CancellationTokenSource();

            _ = Task.Run(() => ReadStdoutLoopAsync(proc, _readCts.Token));
            _ = Task.Run(() => ReadStderrLoopAsync(proc, _readCts.Token));

            var initResult = await SendRequestAsync("initialize", new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    fs = new { readTextFile = true, writeTextFile = true }
                },
                clientInfo = new
                {
                    name = "AntiCLI Studio",
                    version = "1.0.0"
                }
            }, TimeSpan.FromSeconds(25));

            if (initResult.TryGetProperty("agentInfo", out var ai))
                AgentInfo = ai.Clone();
            if (initResult.TryGetProperty("agentCapabilities", out var ac))
                AgentCapabilities = ac.Clone();
            if (initResult.TryGetProperty("authMethods", out var am))
                AuthMethods = am.Clone();

            // If user selected Gemini API key mode explicitly, authenticate via gemini-api-key
            if (string.Equals(_settings.Current.AuthMode, "gemini-api-key", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_settings.Current.GeminiApiKeyPlain))
            {
                try
                {
                    await SendRequestAsync("authenticate", new { methodId = "gemini-api-key" }, TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    Emit("acp:log", new { direction = "err", text = $"authenticate(gemini-api-key): {ex.Message}" });
                }
            }

            var sessionResult = await CreateSessionCoreAsync(ActiveWorkspace);

            Emit("acp:status", new
            {
                state = "connected",
                authMode = _settings.Current.AuthMode,
                agentInfo = AgentInfo,
                authMethods = AuthMethods,
                tier = TierInfo,
                sessionId = ActiveSessionId,
                workspace = ActiveWorkspace
            });

            return new
            {
                ok = true,
                agentInfo = AgentInfo,
                authMethods = AuthMethods,
                tier = TierInfo,
                session = sessionResult
            };
        }
        catch (AcpRpcException rpcEx) when (rpcEx.Code == -32000)
        {
            Emit("acp:status", new
            {
                state = "auth_required",
                message = rpcEx.Message,
                authMethods = AuthMethods
            });
            return new { ok = false, authRequired = true, error = rpcEx.Message, authMethods = AuthMethods };
        }
        catch (Exception ex)
        {
            Emit("acp:status", new
            {
                state = "error",
                message = ex.Message
            });
            return new { ok = false, error = ex.Message };
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<JsonElement> CreateSessionCoreAsync(string cwd)
    {
        var result = await SendRequestAsync("session/new", new
        {
            cwd,
            mcpServers = Array.Empty<object>()
        }, TimeSpan.FromSeconds(30));

        if (result.TryGetProperty("sessionId", out var sid))
            ActiveSessionId = sid.GetString();

        if (result.TryGetProperty("_meta", out var meta)
            && meta.TryGetProperty("antigravity", out var agyMeta)
            && agyMeta.TryGetProperty("tier", out var tier))
        {
            TierInfo = tier.Clone();
        }

        // Apply saved preferred defaults if present
        if (!string.IsNullOrEmpty(ActiveSessionId))
        {
            if (!string.IsNullOrWhiteSpace(_settings.Current.DefaultModel))
            {
                try { result = await SetConfigOptionInternalAsync(ActiveSessionId, "model", _settings.Current.DefaultModel, result); }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(_settings.Current.DefaultThoughtLevel))
            {
                try { result = await SetConfigOptionInternalAsync(ActiveSessionId, "thought_level", _settings.Current.DefaultThoughtLevel, result); }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(_settings.Current.DefaultMode) && _settings.Current.DefaultMode != "default")
            {
                try { result = await SetConfigOptionInternalAsync(ActiveSessionId, "mode", _settings.Current.DefaultMode, result); }
                catch { }
            }
        }

        return result;
    }

    private async Task<JsonElement> SetConfigOptionInternalAsync(string sessionId, string configId, string value, JsonElement fallback)
    {
        var res = await SendRequestAsync("session/set_config_option", new
        {
            sessionId,
            configId,
            value
        }, TimeSpan.FromSeconds(15));

        if (res.TryGetProperty("configOptions", out var opts))
        {
            var merged = JsonNode.Parse(fallback.GetRawText())?.AsObject();
            if (merged is not null)
            {
                merged["configOptions"] = JsonNode.Parse(opts.GetRawText());
                return JsonDocument.Parse(merged.ToJsonString()).RootElement.Clone();
            }
        }
        return fallback;
    }

    public async Task<object> NewSessionAsync(string? cwd = null)
    {
        if (!IsRunning) return await ConnectAsync(cwd ?? ActiveWorkspace);
        try
        {
            var targetCwd = !string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd)
                ? Path.GetFullPath(cwd)
                : ActiveWorkspace;
            ActiveWorkspace = targetCwd;
            var res = await CreateSessionCoreAsync(targetCwd);
            return new { ok = true, session = res, tier = TierInfo };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public async Task<object> ListSessionsAsync(string? cwd = null)
    {
        if (!IsRunning) return new { ok = false, sessions = Array.Empty<object>() };
        try
        {
            object paramObj = string.IsNullOrWhiteSpace(cwd)
                ? new { }
                : new { cwd = Path.GetFullPath(cwd) };
            var res = await SendRequestAsync("session/list", paramObj, TimeSpan.FromSeconds(15));
            return new { ok = true, data = res };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public async Task<object> LoadSessionAsync(string sessionId, string? cwd = null)
    {
        if (!IsRunning) return new { ok = false, error = "ACP chưa kết nối" };
        var normCwd = NormalizeWorkspacePath(cwd);
        var targetCwd = !string.IsNullOrWhiteSpace(normCwd) && Directory.Exists(normCwd)
            ? Path.GetFullPath(normCwd)
            : ActiveWorkspace;
        try
        {
            var res = await SendRequestAsync("session/load", new
            {
                sessionId,
                cwd = targetCwd,
                mcpServers = Array.Empty<object>()
            }, TimeSpan.FromSeconds(30));

            ActiveSessionId = sessionId;
            return new { ok = true, sessionId, session = res };
        }
        catch (Exception ex)
        {
            // Fallback to session/resume if load is unsupported for that session
            try
            {
                var res = await SendRequestAsync("session/resume", new
                {
                    sessionId,
                    cwd = targetCwd,
                    mcpServers = Array.Empty<object>()
                }, TimeSpan.FromSeconds(20));

                ActiveSessionId = sessionId;
                return new { ok = true, sessionId, session = res, resumedWithoutReplay = true };
            }
            catch (Exception ex2)
            {
                return new { ok = false, error = $"{ex.Message} / {ex2.Message}" };
            }
        }
    }

    private static string? NormalizeWorkspacePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            s = s[7..];
        try { s = Uri.UnescapeDataString(s); } catch { }
        if (s.Length >= 3 && s[0] == '/' && char.IsLetter(s[1]) && s[2] == ':')
            s = s[1..];
        return s.Replace('/', Path.DirectorySeparatorChar);
    }

    public async Task<object> SetConfigOptionAsync(string configId, string value)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(ActiveSessionId))
            return new { ok = false, error = "Chưa có session hoạt động" };

        try
        {
            var res = await SendRequestAsync("session/set_config_option", new
            {
                sessionId = ActiveSessionId,
                configId,
                value
            }, TimeSpan.FromSeconds(15));

            if (configId == "model") _settings.Current.DefaultModel = value;
            else if (configId == "thought_level") _settings.Current.DefaultThoughtLevel = value;
            else if (configId == "mode") _settings.Current.DefaultMode = value;
            _settings.Save();

            return new { ok = true, data = res };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public async Task<object> SendPromptAsync(JsonElement promptBlocks)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(ActiveSessionId))
            return new { ok = false, error = "ACP chưa kết nối hoặc chưa có session." };

        try
        {
            var result = await SendRequestAsync("session/prompt", new
            {
                sessionId = ActiveSessionId,
                prompt = promptBlocks
            }, Timeout.InfiniteTimeSpan);

            return new { ok = true, result };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public async Task CancelTurnAsync()
    {
        // Cancel any open permission prompts first
        foreach (var kv in _pendingPermissions.ToArray())
        {
            if (_pendingPermissions.TryRemove(kv.Key, out var rawId))
            {
                await SendRawResponseAsync(rawId, new
                {
                    outcome = new { outcome = "cancelled" }
                });
            }
        }

        if (!IsRunning || string.IsNullOrWhiteSpace(ActiveSessionId)) return;
        try
        {
            await SendNotificationAsync("session/cancel", new { sessionId = ActiveSessionId });
        }
        catch { }
    }

    public async Task<object> RespondPermissionAsync(string requestKey, string? optionId, bool cancelled)
    {
        if (!_pendingPermissions.TryRemove(requestKey, out var rawId))
            return new { ok = false, error = "Yêu cầu quyền đã hết hạn hoặc đã xử lý." };

        object outcome = cancelled || string.IsNullOrWhiteSpace(optionId)
            ? new { outcome = "cancelled" }
            : new { outcome = "selected", optionId };

        await SendRawResponseAsync(rawId, new { outcome });
        return new { ok = true };
    }

    public async Task<object> AuthenticateAsync(string methodId)
    {
        if (!IsRunning) return new { ok = false, error = "ACP chưa khởi chạy" };
        try
        {
            var res = await SendRequestAsync("authenticate", new { methodId }, TimeSpan.FromMinutes(3));
            return new { ok = true, result = res };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public async Task<object> LogoutAsync()
    {
        if (!IsRunning) return new { ok = false, error = "ACP chưa khởi chạy" };
        try
        {
            await SendRequestAsync("logout", new { }, TimeSpan.FromSeconds(15));
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? paramObj, TimeSpan timeout)
    {
        if (_proc is null || _proc.HasExited)
            throw new InvalidOperationException("ACP server process is not running.");

        var id = Interlocked.Increment(ref _nextId);
        var key = id.ToString();
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = paramObj
        });

        await WriteLineAsync(payload);

        if (timeout == Timeout.InfiniteTimeSpan)
            return await tcs.Task;

        using var cts = new CancellationTokenSource(timeout);
        using var reg = cts.Token.Register(() =>
        {
            if (_pending.TryRemove(key, out var p))
                p.TrySetException(new TimeoutException($"ACP request '{method}' timed out after {timeout.TotalSeconds:F0}s."));
        });

        return await tcs.Task;
    }

    private async Task SendNotificationAsync(string method, object? paramObj)
    {
        if (_proc is null || _proc.HasExited) return;
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params = paramObj
        });
        await WriteLineAsync(payload);
    }

    private async Task SendRawResponseAsync(JsonElement idElement, object resultObj)
    {
        if (_proc is null || _proc.HasExited) return;
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(idElement.GetRawText()),
            ["result"] = JsonSerializer.SerializeToNode(resultObj)
        };
        await WriteLineAsync(obj.ToJsonString());
    }

    private async Task SendRawErrorAsync(JsonElement idElement, int code, string message)
    {
        if (_proc is null || _proc.HasExited) return;
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(idElement.GetRawText()),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        await WriteLineAsync(obj.ToJsonString());
    }

    private async Task WriteLineAsync(string line)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (_proc is null || _proc.HasExited)
                throw new InvalidOperationException("ACP server is not running.");
            Emit("acp:wire", new { dir = "out", json = TruncateWire(line) });
            await _proc.StandardInput.WriteLineAsync(line);
            await _proc.StandardInput.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutLoopAsync(Process proc, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                Emit("acp:wire", new { dir = "in", json = TruncateWire(line) });

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    var hasMethod = root.TryGetProperty("method", out var methodProp) && methodProp.ValueKind == JsonValueKind.String;
                    var hasId = root.TryGetProperty("id", out var idProp) && idProp.ValueKind != JsonValueKind.Null && idProp.ValueKind != JsonValueKind.Undefined;

                    if (hasMethod && hasId)
                    {
                        // Agent -> Client RPC Request
                        var method = methodProp.GetString()!;
                        var idClone = idProp.Clone();
                        var paramsClone = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                        _ = Task.Run(() => HandleIncomingRequestAsync(idClone, method, paramsClone));
                    }
                    else if (hasMethod && !hasId)
                    {
                        // Agent -> Client Notification
                        var method = methodProp.GetString()!;
                        var paramsClone = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                        HandleIncomingNotification(method, paramsClone);
                    }
                    else if (hasId)
                    {
                        // Response to our Client -> Agent Request
                        var idKey = IdToKey(idProp);
                        if (_pending.TryRemove(idKey, out var tcs))
                        {
                            if (root.TryGetProperty("error", out var errProp))
                            {
                                var code = errProp.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : -32000;
                                var msg = errProp.TryGetProperty("message", out var m) ? m.GetString() ?? "ACP error" : "ACP error";
                                tcs.TrySetException(new AcpRpcException(code, msg));
                            }
                            else if (root.TryGetProperty("result", out var resProp))
                            {
                                tcs.TrySetResult(resProp.Clone());
                            }
                            else
                            {
                                tcs.TrySetResult(default);
                            }
                        }
                    }
                }
                catch (Exception parseEx)
                {
                    Emit("acp:log", new { direction = "err", text = $"JSON parse error: {parseEx.Message}" });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Emit("acp:log", new { direction = "err", text = $"ACP stdout read loop ended: {ex.Message}" });
        }
    }

    private async Task ReadStderrLoopAsync(Process proc, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardError.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                Emit("acp:log", new { direction = "stderr", text = line });
            }
        }
        catch { }
    }

    private void HandleIncomingNotification(string method, JsonElement paramObj)
    {
        if (method == "session/update")
        {
            Emit("acp:update", paramObj);
        }
        else
        {
            Emit("acp:notification", new { method, @params = paramObj });
        }
    }

    private async Task HandleIncomingRequestAsync(JsonElement idElement, string method, JsonElement paramObj)
    {
        try
        {
            if (method == "session/request_permission")
            {
                var requestKey = $"{Guid.NewGuid():N}";
                _pendingPermissions[requestKey] = idElement;
                Emit("acp:permission_request", new
                {
                    requestKey,
                    @params = paramObj
                });
                return;
            }

            if (method == "fs/read_text_file")
            {
                var path = paramObj.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    await SendRawErrorAsync(idElement, -32602, $"File not found: {path}");
                    return;
                }

                var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
                if (paramObj.TryGetProperty("line", out var lineProp) && lineProp.TryGetInt32(out var startLine))
                {
                    var lines = text.Split('\n');
                    var skip = Math.Max(0, startLine - 1);
                    var limit = paramObj.TryGetProperty("limit", out var limProp) && limProp.TryGetInt32(out var limVal)
                        ? limVal
                        : lines.Length;
                    text = string.Join("\n", lines.Skip(skip).Take(limit));
                }

                await SendRawResponseAsync(idElement, new { content = text });
                return;
            }

            if (method == "fs/write_text_file")
            {
                var path = paramObj.TryGetProperty("path", out var p) ? p.GetString() : null;
                var content = paramObj.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(path))
                {
                    await SendRawErrorAsync(idElement, -32602, "Missing path");
                    return;
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
                await SendRawResponseAsync(idElement, new { });
                Emit("workspace:changed", new { path });
                return;
            }

            await SendRawErrorAsync(idElement, -32601, $"Unsupported client method: {method}");
        }
        catch (Exception ex)
        {
            try { await SendRawErrorAsync(idElement, -32000, ex.Message); } catch { }
        }
    }

    private static string IdToKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.GetRawText(),
        JsonValueKind.String => id.GetString() ?? "",
        _ => id.GetRawText()
    };

    private static string TruncateWire(string s) => s.Length > 4000 ? s[..4000] + " …[truncated]" : s;

    private void FailAllPending(string reason)
    {
        foreach (var kv in _pending.ToArray())
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new InvalidOperationException(reason));
        }
        _pendingPermissions.Clear();
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try { await StopInternalAsync(); }
        finally { _lifecycleLock.Release(); }
    }

    private async Task StopInternalAsync()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;

        var proc = _proc;
        _proc = null;
        ActiveSessionId = null;

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    try { proc.StandardInput.Close(); } catch { }
                    using var cts = new CancellationTokenSource(1500);
                    try { await proc.WaitForExitAsync(cts.Token); } catch { }
                    if (!proc.HasExited) proc.Kill(entireProcessTree: true);
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }

        FailAllPending("ACP stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _readCts?.Cancel(); } catch { }
        try
        {
            if (_proc is { HasExited: false })
                _proc.Kill(entireProcessTree: true);
            _proc?.Dispose();
        }
        catch { }
        _writeLock.Dispose();
        _lifecycleLock.Dispose();
    }
}

public sealed class AcpRpcException : Exception
{
    public int Code { get; }
    public AcpRpcException(int code, string message) : base(message) => Code = code;
}
