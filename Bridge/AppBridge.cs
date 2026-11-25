using System.Diagnostics;
using System.Text.Json;
using AntiCliUi.Services;

namespace AntiCliUi.Bridge;

public sealed class AppBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly AcpClientService _acp;
    private readonly AgyCliService _cli;
    private readonly WorkspaceService _workspace;
    private readonly GitService _git;
    private readonly TerminalService _terminal;
    private readonly UpdateService _updater;
    private readonly Action<string> _postJsonToUi;
    private readonly Func<string?, string?> _pickFolderOnUiThread;
    private readonly Action<string> _setWindowTitle;

    public string CurrentWorkspace { get; private set; }

    public AppBridge(
        Action<string> postJsonToUi,
        Func<string?, string?> pickFolderOnUiThread,
        Action<string> setWindowTitle,
        Action? requestShutdown = null)
    {
        _postJsonToUi = postJsonToUi;
        _pickFolderOnUiThread = pickFolderOnUiThread;
        _setWindowTitle = setWindowTitle;

        _settings = new SettingsService();
        _acp = new AcpClientService(_settings);
        _cli = new AgyCliService(_settings);
        _workspace = new WorkspaceService();
        _git = new GitService();
        _terminal = new TerminalService();
        _updater = new UpdateService(isServer: false, requestAppShutdown: requestShutdown ?? (() => Environment.Exit(0)));

        _acp.OnBridgeEvent += SendEvent;
        _cli.OnBridgeEvent += SendEvent;
        _workspace.OnBridgeEvent += SendEvent;
        _terminal.OnBridgeEvent += SendEvent;

        CurrentWorkspace = ResolveInitialWorkspace();
    }

    private string ResolveInitialWorkspace()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Current.LastWorkspace) && Directory.Exists(_settings.Current.LastWorkspace))
            return _settings.Current.LastWorkspace;

        var cwd = Environment.CurrentDirectory;
        if (Directory.Exists(cwd)) return cwd;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public void SendEvent(string eventName, object? payload)
    {
        try
        {
            var envelope = JsonSerializer.Serialize(new
            {
                kind = "event",
                @event = eventName,
                payload
            }, JsonOpts);
            _postJsonToUi(envelope);
        }
        catch { }
    }

    public async Task HandleWebMessageAsync(string rawJson)
    {
        string? reqId = null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            reqId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var action = root.TryGetProperty("action", out var actEl) ? actEl.GetString() ?? "" : "";
            var payload = root.TryGetProperty("payload", out var payEl) ? payEl : default;

            object result = action switch
            {
                "app:init" => await HandleAppInitAsync(),
                "app:metrics" => GetProcessMetrics(),
                "app:openExternal" => OpenExternal(GetStr(payload, "url")),
                "app:checkUpdate" => await _updater.CheckForUpdateAsync(),
                "app:applyUpdate" => await _updater.DownloadAndApplyUpdateAsync(GetStr(payload, "downloadUrl"), GetStr(payload, "version")),

                "settings:get" => _settings.ToPublicDto(),
                "settings:save" => await HandleSaveSettingsAsync(payload),

                "workspace:pick" => await HandlePickWorkspaceAsync(),
                "workspace:browseHost" => _workspace.BrowseHostDirectories(GetStr(payload, "path")),
                "workspace:open" => await HandleOpenWorkspaceAsync(GetStr(payload, "path")),
                "workspace:listDir" => _workspace.ListDirectory(
                    CurrentWorkspace,
                    GetStr(payload, "relativePath"),
                    GetBool(payload, "showHidden")),
                "workspace:searchFiles" => _workspace.SearchFiles(
                    CurrentWorkspace,
                    GetStr(payload, "query"),
                    GetInt(payload, "maxResults", 40)),
                "workspace:readFile" => _workspace.ReadFile(CurrentWorkspace, GetStr(payload, "path")),
                "workspace:writeFile" => _workspace.WriteFile(
                    CurrentWorkspace,
                    GetStr(payload, "path"),
                    GetStr(payload, "content")),

                "git:status" => await _git.GetStatusAsync(CurrentWorkspace),
                "git:diff" => await _git.GetFileDiffAsync(CurrentWorkspace, GetStr(payload, "path")),

                "acp:connect" => await ConnectTransportAsync(CurrentWorkspace),
                "acp:newSession" => await HandleNewSessionAsync(),
                "acp:listSessions" => await _acp.ListSessionsAsync(GetBool(payload, "all") ? null : CurrentWorkspace),
                "acp:loadSession" => await _acp.LoadSessionAsync(GetStr(payload, "sessionId"), GetStr(payload, "cwd")),
                "acp:setConfig" => await _acp.SetConfigOptionAsync(GetStr(payload, "configId"), GetStr(payload, "value")),
                "acp:prompt" => await HandlePromptAsync(payload),
                "acp:cancel" => await HandleCancelAsync(),
                "acp:respondPermission" => await _acp.RespondPermissionAsync(
                    GetStr(payload, "requestKey"),
                    GetStr(payload, "optionId"),
                    GetBool(payload, "cancelled")),
                "acp:authenticate" => await _acp.AuthenticateAsync(GetStr(payload, "methodId")),
                "acp:logout" => await _acp.LogoutAsync(),

                "cli:info" => await _cli.GetCliInfoAsync(),
                "cli:install" => await _cli.InstallAgyCliAsync(),
                "cli:models" => await _cli.ListModelsAsync(CurrentWorkspace),
                "cli:slashReport" => await _cli.RunSlashReportAsync(GetStr(payload, "command"), CurrentWorkspace),
                "cli:login" => _cli.LaunchInteractiveLogin(),

                "terminal:start" => StartTerminal(),
                "terminal:input" => await SendTerminalInputAsync(GetStr(payload, "command")),
                "terminal:restart" => RestartTerminal(),

                _ => new { ok = false, error = $"Unknown action: {action}" }
            };

            if (!string.IsNullOrEmpty(reqId))
            {
                var resp = JsonSerializer.Serialize(new { kind = "response", id = reqId, ok = true, data = result }, JsonOpts);
                _postJsonToUi(resp);
            }
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(reqId))
            {
                var resp = JsonSerializer.Serialize(new { kind = "response", id = reqId, ok = false, error = ex.Message }, JsonOpts);
                _postJsonToUi(resp);
            }
        }
    }

    private async Task<object> HandleAppInitAsync()
    {
        _settings.RecordWorkspace(CurrentWorkspace);
        _workspace.WatchWorkspace(CurrentWorkspace);
        UpdateTitle();

        var cliInfo = await _cli.GetCliInfoAsync();
        var gitStatus = await _git.GetStatusAsync(CurrentWorkspace);
        var tree = _workspace.ListDirectory(CurrentWorkspace);

        return new
        {
            appVersion = _updater.CurrentVersion,
            workspace = CurrentWorkspace,
            workspaceName = Path.GetFileName(CurrentWorkspace.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : CurrentWorkspace,
            settings = _settings.ToPublicDto(),
            cli = cliInfo,
            git = gitStatus,
            tree,
            metrics = GetProcessMetrics()
        };
    }

    private async Task<object> ConnectTransportAsync(string workspace)
    {
        if (string.Equals(_settings.Current.TransportMode, "stream-json", StringComparison.OrdinalIgnoreCase))
        {
            await _acp.StopAsync();
            var model = _settings.Current.DefaultModel;
            var mode = _settings.Current.DefaultMode;
            return await _cli.StartStreamSessionAsync(workspace, model, mode);
        }

        _cli.StopStreamSession();
        return await _acp.ConnectAsync(workspace);
    }

    private async Task<object> HandleNewSessionAsync()
    {
        if (string.Equals(_settings.Current.TransportMode, "stream-json", StringComparison.OrdinalIgnoreCase))
            return await _cli.StartStreamSessionAsync(CurrentWorkspace, _settings.Current.DefaultModel, _settings.Current.DefaultMode);

        return await _acp.NewSessionAsync(CurrentWorkspace);
    }

    private async Task<object> HandlePromptAsync(JsonElement payload)
    {
        if (string.Equals(_settings.Current.TransportMode, "stream-json", StringComparison.OrdinalIgnoreCase))
        {
            var text = GetStr(payload, "plainText");
            return await _cli.SendStreamPromptAsync(text);
        }

        if (payload.TryGetProperty("blocks", out var blocks))
            return await _acp.SendPromptAsync(blocks);

        return new { ok = false, error = "Missing prompt blocks" };
    }

    private async Task<object> HandleCancelAsync()
    {
        if (string.Equals(_settings.Current.TransportMode, "stream-json", StringComparison.OrdinalIgnoreCase))
        {
            await _cli.StartStreamSessionAsync(CurrentWorkspace, _settings.Current.DefaultModel, _settings.Current.DefaultMode);
            return new { ok = true };
        }
        await _acp.CancelTurnAsync();
        return new { ok = true };
    }

    private async Task<object> HandleSaveSettingsAsync(JsonElement payload)
    {
        var needReconnect = false;

        if (payload.TryGetProperty("agyPath", out var p1))
        {
            var v = p1.GetString()?.Trim() ?? "";
            if (v != _settings.Current.AgyPath) { _settings.Current.AgyPath = v; needReconnect = true; }
        }
        if (payload.TryGetProperty("authMode", out var p2))
        {
            var v = p2.GetString() ?? "antigravity";
            if (v != _settings.Current.AuthMode) { _settings.Current.AuthMode = v; needReconnect = true; }
        }
        if (payload.TryGetProperty("transportMode", out var p3))
        {
            var v = p3.GetString() ?? "acp";
            if (v != _settings.Current.TransportMode) { _settings.Current.TransportMode = v; needReconnect = true; }
        }
        if (payload.TryGetProperty("geminiApiKey", out var p4))
        {
            var v = p4.GetString() ?? "";
            if (!string.IsNullOrEmpty(v))
            {
                _settings.Current.GeminiApiKeyPlain = v.Trim();
                needReconnect = true;
            }
        }
        if (GetBool(payload, "clearGeminiApiKey"))
        {
            _settings.Current.GeminiApiKeyPlain = "";
            needReconnect = true;
        }
        if (payload.TryGetProperty("geminiBaseUrl", out var p5))
        {
            var v = p5.GetString()?.Trim() ?? "";
            if (v != _settings.Current.GeminiBaseUrl) { _settings.Current.GeminiBaseUrl = v; needReconnect = true; }
        }
        if (payload.TryGetProperty("theme", out var p6))
            _settings.Current.Theme = p6.GetString() ?? "obsidian-cyan";
        if (payload.TryGetProperty("fontSize", out var p7) && p7.TryGetInt32(out var fs))
            _settings.Current.FontSize = Math.Clamp(fs, 11, 18);
        if (payload.TryGetProperty("showThinkingByDefault", out var p8) && p8.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _settings.Current.ShowThinkingByDefault = p8.GetBoolean();
        if (payload.TryGetProperty("enableSandbox", out var p9) && p9.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var v = p9.GetBoolean();
            if (v != _settings.Current.EnableSandbox) { _settings.Current.EnableSandbox = v; needReconnect = true; }
        }
        if (payload.TryGetProperty("autoApprovePermissions", out var p10) && p10.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            var v = p10.GetBoolean();
            if (v != _settings.Current.AutoApprovePermissions) { _settings.Current.AutoApprovePermissions = v; needReconnect = true; }
        }

        _settings.Save();

        object? reconnectResult = null;
        if (needReconnect || GetBool(payload, "reconnect"))
            reconnectResult = await ConnectTransportAsync(CurrentWorkspace);

        return new
        {
            ok = true,
            settings = _settings.ToPublicDto(),
            reconnected = needReconnect,
            reconnectResult
        };
    }

    private async Task<object> HandlePickWorkspaceAsync()
    {
        var picked = _pickFolderOnUiThread(CurrentWorkspace);
        if (string.IsNullOrWhiteSpace(picked) || !Directory.Exists(picked))
            return new { ok = false, cancelled = true };

        return await HandleOpenWorkspaceAsync(picked);
    }

    private async Task<object> HandleOpenWorkspaceAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new { ok = false, error = "Thư mục không tồn tại." };

        CurrentWorkspace = Path.GetFullPath(path);
        _settings.RecordWorkspace(CurrentWorkspace);
        _workspace.WatchWorkspace(CurrentWorkspace);
        UpdateTitle();
        await _terminal.ChangeDirectoryAsync(CurrentWorkspace);

        var conn = await ConnectTransportAsync(CurrentWorkspace);
        var git = await _git.GetStatusAsync(CurrentWorkspace);
        var tree = _workspace.ListDirectory(CurrentWorkspace);

        return new
        {
            ok = true,
            workspace = CurrentWorkspace,
            workspaceName = Path.GetFileName(CurrentWorkspace.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : CurrentWorkspace,
            settings = _settings.ToPublicDto(),
            git,
            tree,
            connection = conn
        };
    }

    private object StartTerminal()
    {
        if (!_terminal.IsRunning) _terminal.Start(CurrentWorkspace);
        return new { ok = true, running = _terminal.IsRunning, cwd = CurrentWorkspace };
    }

    private async Task<object> SendTerminalInputAsync(string command)
    {
        await _terminal.WriteCommandAsync(command);
        return new { ok = true };
    }

    private object RestartTerminal()
    {
        _terminal.Restart(CurrentWorkspace);
        return new { ok = true, running = _terminal.IsRunning, cwd = CurrentWorkspace };
    }

    private object OpenExternal(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return new { ok = false };
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return new { ok = true };
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }
    }

    private object GetProcessMetrics()
    {
        using var proc = Process.GetCurrentProcess();
        var mb = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1);
        return new { workingSetMb = mb };
    }

    private void UpdateTitle()
    {
        var name = Path.GetFileName(CurrentWorkspace.TrimEnd('\\', '/'));
        _setWindowTitle(string.IsNullOrEmpty(name) ? "AntiCLI Studio" : $"{name} — AntiCLI Studio");
    }

    private static string GetStr(JsonElement el, string prop, string def = "")
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? def;
        return def;
    }

    private static bool GetBool(JsonElement el, string prop, bool def = false)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return v.GetBoolean();
        return def;
    }

    private static int GetInt(JsonElement el, string prop, int def = 0)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i))
            return i;
        return def;
    }

    public void Dispose()
    {
        _acp.Dispose();
        _cli.Dispose();
        _workspace.Dispose();
        _terminal.Dispose();
    }
}
