using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AntiCliUi.Services;

namespace AntiCliServer;

public sealed class ClientSocket
{
    public string ClientId { get; } = Guid.NewGuid().ToString("N")[..10];
    public string RemoteIp { get; }
    public string UserAgent { get; }
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
    public WebSocket Socket { get; }
    public string RoomId { get; set; } = "default";

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public ClientSocket(WebSocket socket, string remoteIp, string userAgent)
    {
        Socket = socket;
        RemoteIp = remoteIp;
        UserAgent = userAgent;
    }

    public async Task SendRawJsonAsync(string json, CancellationToken ct = default)
    {
        if (Socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (Socket.State == WebSocketState.Open)
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch { }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseSafeAsync(string reason)
    {
        try
        {
            if (Socket.State == WebSocketState.Open)
                await Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
        }
        catch { }
    }
}

public sealed record ReplayEntry(string EventName, JsonElement Payload);

public sealed class WorkspaceRoom : IDisposable
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
    private readonly ConcurrentDictionary<string, ClientSocket> _clients = new();
    private readonly object _historyLock = new();
    private readonly List<ReplayEntry> _replay = new();
    private readonly ConcurrentDictionary<string, JsonElement> _openPermissions = new();

    private string _connState = "disconnected";
    private string _connLabel = "Chưa kết nối";
    private bool _busy;
    private string _sessionTitle = "Phiên mới";
    private JsonElement? _lastSessionPayload;
    private JsonElement? _lastConfigOptions;
    private JsonElement? _lastCommands;
    private long _ctxUsed;
    private long _ctxSize = 1048576;

    public string Id { get; }
    public string Name { get; set; }
    public bool IsShared { get; set; }
    public string CurrentWorkspace { get; private set; }
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public int ClientCount => _clients.Count;

    public event Action? OnRoomsChanged;

    public WorkspaceRoom(string id, string name, bool isShared, string initialWorkspace, SettingsService settings)
    {
        Id = id;
        Name = name;
        IsShared = isShared;
        _settings = settings;

        _acp = new AcpClientService(_settings);
        _cli = new AgyCliService(_settings);
        _workspace = new WorkspaceService();
        _git = new GitService();
        _terminal = new TerminalService();

        CurrentWorkspace = Directory.Exists(initialWorkspace)
            ? Path.GetFullPath(initialWorkspace)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _acp.OnBridgeEvent += HandleServiceEvent;
        _cli.OnBridgeEvent += HandleServiceEvent;
        _workspace.OnBridgeEvent += HandleServiceEvent;
        _terminal.OnBridgeEvent += HandleServiceEvent;

        _workspace.WatchWorkspace(CurrentWorkspace);
    }

    public void AttachClient(ClientSocket client)
    {
        client.RoomId = Id;
        _clients[client.ClientId] = client;
        Broadcast("room:presence", new { roomId = Id, clientCount = _clients.Count });
        OnRoomsChanged?.Invoke();
    }

    public void DetachClient(string clientId)
    {
        if (_clients.TryRemove(clientId, out _))
        {
            Broadcast("room:presence", new { roomId = Id, clientCount = _clients.Count });
            OnRoomsChanged?.Invoke();
        }
    }

    public object GetSummary() => new
    {
        id = Id,
        name = Name,
        isShared = IsShared,
        workspace = CurrentWorkspace,
        workspaceName = GetFolderName(CurrentWorkspace),
        sessionTitle = _sessionTitle,
        sessionId = _acp.ActiveSessionId,
        connState = _connState,
        busy = _busy,
        clientCount = _clients.Count,
        createdAtUtc = CreatedAtUtc
    };

    public object GetSnapshotForClient()
    {
        ReplayEntry[] replayCopy;
        lock (_historyLock) { replayCopy = _replay.ToArray(); }

        return new
        {
            room = GetSummary(),
            connState = _connState,
            connLabel = _connLabel,
            busy = _busy,
            sessionId = _acp.ActiveSessionId,
            sessionTitle = _sessionTitle,
            tier = _acp.TierInfo,
            agentInfo = _acp.AgentInfo,
            configOptions = _lastConfigOptions,
            availableCommands = _lastCommands,
            ctxUsed = _ctxUsed,
            ctxSize = _ctxSize,
            openPermissions = _openPermissions.Values.ToArray(),
            replay = replayCopy.Select(r => new { @event = r.EventName, payload = r.Payload }).ToArray()
        };
    }

    private void HandleServiceEvent(string eventName, object? payload)
    {
        if (eventName == "acp:status" && payload is not null)
        {
            try
            {
                var el = JsonSerializer.SerializeToElement(payload, JsonOpts);
                if (el.TryGetProperty("state", out var st)) _connState = st.GetString() ?? _connState;
                if (el.TryGetProperty("message", out var msg)) _connLabel = msg.GetString() ?? _connLabel;
                if (_connState == "connected")
                {
                    var ver = _acp.AgentInfo is JsonElement ai && ai.TryGetProperty("version", out var vp) ? $"v{vp.GetString()}" : "";
                    var prefix = _settings.Current.AuthMode == "gemini-api-key" ? "API Key" : "ACP";
                    _connLabel = $"{prefix} {ver}".Trim();
                }
            }
            catch { }
        }
        else if (eventName == "acp:update" && payload is JsonElement updParam)
        {
            if (updParam.TryGetProperty("update", out var upd) && upd.TryGetProperty("sessionUpdate", out var su))
            {
                var kind = su.GetString() ?? "";
                if (kind == "session_info_update" && upd.TryGetProperty("title", out var t))
                {
                    _sessionTitle = t.GetString() ?? _sessionTitle;
                    OnRoomsChanged?.Invoke();
                }
                else if (kind == "config_option_update" && upd.TryGetProperty("configOptions", out var co))
                {
                    _lastConfigOptions = co.Clone();
                }
                else if (kind == "available_commands_update" && upd.TryGetProperty("availableCommands", out var ac))
                {
                    _lastCommands = ac.Clone();
                }
                else if (kind == "usage_update")
                {
                    if (upd.TryGetProperty("used", out var u) && u.TryGetInt64(out var uv)) _ctxUsed = uv;
                    if (upd.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv)) _ctxSize = sv;
                }
                else if (kind is "agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update" or "plan")
                {
                    RecordReplay(eventName, updParam.Clone());
                }
            }
        }
        else if (eventName == "acp:permission_request" && payload is not null)
        {
            var el = JsonSerializer.SerializeToElement(payload, JsonOpts);
            if (el.TryGetProperty("requestKey", out var rk) && rk.GetString() is string key)
                _openPermissions[key] = el;
            RecordReplay(eventName, el);
        }
        else if (eventName == "stream:event" && payload is JsonElement sev)
        {
            RecordReplay(eventName, sev.Clone());
            if (sev.TryGetProperty("event", out var evKind) && evKind.GetString() == "result")
            {
                _busy = false;
                OnRoomsChanged?.Invoke();
            }
        }

        Broadcast(eventName, payload);
    }

    private void RecordReplay(string eventName, JsonElement payload)
    {
        lock (_historyLock)
        {
            _replay.Add(new ReplayEntry(eventName, payload));
            if (_replay.Count > 600)
                _replay.RemoveRange(0, _replay.Count - 600);
        }
    }

    private void ClearReplay()
    {
        lock (_historyLock) { _replay.Clear(); }
        _openPermissions.Clear();
    }

    public void Broadcast(string eventName, object? payload, string? excludeClientId = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            kind = "event",
            @event = eventName,
            payload
        }, JsonOpts);

        foreach (var kv in _clients)
        {
            if (excludeClientId is not null && kv.Key == excludeClientId) continue;
            _ = kv.Value.SendRawJsonAsync(json);
        }
    }

    public async Task<object> HandleActionAsync(ClientSocket sender, string action, JsonElement payload, RoomManager manager)
    {
        return action switch
        {
            "app:init" => await HandleInitAsync(manager),
            "app:metrics" => GetProcessMetrics(),
            "app:openExternal" => new { ok = true, clientOpen = true },
            "app:checkUpdate" => await manager.Updater.CheckForUpdateAsync(),
            "app:applyUpdate" => await HandleApplyUpdateAsync(manager, GetStr(payload, "downloadUrl"), GetStr(payload, "version")),

            "settings:get" => _settings.ToPublicDto(),
            "settings:save" => await HandleSaveSettingsAsync(payload),

            "workspace:browseHost" => _workspace.BrowseHostDirectories(GetStr(payload, "path")),
            "workspace:pick" => new { ok = false, remoteBrowseRequired = true, currentPath = CurrentWorkspace },
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

            "acp:connect" => await EnsureConnectedAsync(GetBool(payload, "force")),
            "acp:newSession" => await HandleNewSessionAsync(),
            "acp:listSessions" => await _acp.ListSessionsAsync(GetBool(payload, "all") ? null : CurrentWorkspace),
            "acp:loadSession" => await HandleLoadSessionAsync(GetStr(payload, "sessionId"), GetStr(payload, "cwd")),
            "acp:setConfig" => await HandleSetConfigAsync(GetStr(payload, "configId"), GetStr(payload, "value")),
            "acp:prompt" => await HandlePromptAsync(sender, payload),
            "acp:cancel" => await HandleCancelAsync(),
            "acp:respondPermission" => await HandleRespondPermissionAsync(
                GetStr(payload, "requestKey"),
                GetStr(payload, "optionId"),
                GetBool(payload, "cancelled")),
            "acp:authenticate" => await _acp.AuthenticateAsync(GetStr(payload, "methodId")),
            "acp:logout" => await _acp.LogoutAsync(),

            "cli:info" => await _cli.GetCliInfoAsync(),
            "cli:install" => await _cli.InstallAgyCliAsync(),
            "cli:models" => await _cli.ListModelsAsync(CurrentWorkspace),
            "cli:slashReport" => await _cli.RunSlashReportAsync(GetStr(payload, "command"), CurrentWorkspace),
            "cli:login" => new { ok = false, error = "Đang chạy Server từ xa: hãy đăng nhập agy 1 lần trên máy chủ hoặc nhập Gemini API Key trong Settings." },

            "terminal:start" => StartTerminal(),
            "terminal:input" => await SendTerminalInputAsync(GetStr(payload, "command")),
            "terminal:restart" => RestartTerminal(),

            _ => new { ok = false, error = $"Unknown action: {action}" }
        };
    }

    private async Task<object> HandleInitAsync(RoomManager manager)
    {
        var cliInfo = await _cli.GetCliInfoAsync();
        var gitStatus = await _git.GetStatusAsync(CurrentWorkspace);
        var tree = _workspace.ListDirectory(CurrentWorkspace);

        return new
        {
            isServerMode = true,
            appVersion = manager.Updater.CurrentVersion,
            workspace = CurrentWorkspace,
            workspaceName = GetFolderName(CurrentWorkspace),
            settings = _settings.ToPublicDto(),
            cli = cliInfo,
            git = gitStatus,
            tree,
            metrics = GetProcessMetrics(),
            roomSnapshot = GetSnapshotForClient(),
            rooms = manager.ListRooms(),
            serverInfo = manager.GetServerNetworkInfo()
        };
    }

    public async Task<object> EnsureConnectedAsync(bool forceReconnect = false)
    {
        if (!forceReconnect && _settings.Current.TransportMode != "stream-json" && _acp.IsRunning && !string.IsNullOrEmpty(_acp.ActiveSessionId))
        {
            return new
            {
                ok = true,
                reused = true,
                agentInfo = _acp.AgentInfo,
                authMethods = _acp.AuthMethods,
                tier = _acp.TierInfo,
                session = _lastSessionPayload,
                configOptions = _lastConfigOptions
            };
        }

        if (string.Equals(_settings.Current.TransportMode, "stream-json", StringComparison.OrdinalIgnoreCase))
        {
            await _acp.StopAsync();
            _connState = "connected";
            _connLabel = "Stream-JSON CLI";
            return await _cli.StartStreamSessionAsync(CurrentWorkspace, _settings.Current.DefaultModel, _settings.Current.DefaultMode);
        }

        _cli.StopStreamSession();
        var res = await _acp.ConnectAsync(CurrentWorkspace);
        try
        {
            var el = JsonSerializer.SerializeToElement(res, JsonOpts);
            if (el.TryGetProperty("session", out var sess))
            {
                _lastSessionPayload = sess.Clone();
                if (sess.TryGetProperty("configOptions", out var co))
                    _lastConfigOptions = co.Clone();
            }
        }
        catch { }
        OnRoomsChanged?.Invoke();
        return res;
    }

    private async Task<object> HandleNewSessionAsync()
    {
        ClearReplay();
        _sessionTitle = "Phiên mới";
        _ctxUsed = 0;
        _busy = false;

        object res;
        if (string.Equals(_settings.Current.TransportMode, "stream-json", StringComparison.OrdinalIgnoreCase))
            res = await _cli.StartStreamSessionAsync(CurrentWorkspace, _settings.Current.DefaultModel, _settings.Current.DefaultMode);
        else
            res = await _acp.NewSessionAsync(CurrentWorkspace);

        try
        {
            var el = JsonSerializer.SerializeToElement(res, JsonOpts);
            if (el.TryGetProperty("session", out var sess))
            {
                _lastSessionPayload = sess.Clone();
                if (sess.TryGetProperty("configOptions", out var co))
                    _lastConfigOptions = co.Clone();
            }
        }
        catch { }

        Broadcast("room:cleared", new { roomId = Id, session = _lastSessionPayload, tier = _acp.TierInfo });
        OnRoomsChanged?.Invoke();
        return res;
    }

    private async Task<object> HandleLoadSessionAsync(string sessionId, string cwd)
    {
        ClearReplay();
        var res = await _acp.LoadSessionAsync(sessionId, cwd);
        try
        {
            var el = JsonSerializer.SerializeToElement(res, JsonOpts);
            if (el.TryGetProperty("session", out var sess) && sess.TryGetProperty("configOptions", out var co))
                _lastConfigOptions = co.Clone();
        }
        catch { }
        OnRoomsChanged?.Invoke();
        return res;
    }

    private async Task<object> HandleSetConfigAsync(string configId, string value)
    {
        var res = await _acp.SetConfigOptionAsync(configId, value);
        try
        {
            var el = JsonSerializer.SerializeToElement(res, JsonOpts);
            if (el.TryGetProperty("data", out var d) && d.TryGetProperty("configOptions", out var co))
            {
                _lastConfigOptions = co.Clone();
                Broadcast("room:config_updated", new { configOptions = _lastConfigOptions });
            }
        }
        catch { }
        return res;
    }

    private async Task<object> HandlePromptAsync(ClientSocket sender, JsonElement payload)
    {
        if (_busy) return new { ok = false, error = "Room đang xử lý một lệnh khác — nhấn Esc để dừng trước." };

        var displayText = GetStr(payload, "displayText");
        if (string.IsNullOrEmpty(displayText)) displayText = GetStr(payload, "plainText");

        var userEv = JsonSerializer.SerializeToElement(new
        {
            text = displayText,
            senderId = sender.ClientId
        }, JsonOpts);
        RecordReplay("room:user_message", userEv);
        Broadcast("room:user_message", userEv, excludeClientId: sender.ClientId);

        _busy = true;
        Broadcast("room:busy", new { busy = true });
        OnRoomsChanged?.Invoke();

        if (string.Equals(_settings.Current.TransportMode, "stream-json", StringComparison.OrdinalIgnoreCase))
        {
            var text = GetStr(payload, "plainText");
            return await _cli.SendStreamPromptAsync(text);
        }

        try
        {
            if (!payload.TryGetProperty("blocks", out var blocks))
                return new { ok = false, error = "Missing prompt blocks" };

            var res = await _acp.SendPromptAsync(blocks);
            var resEl = JsonSerializer.SerializeToElement(res, JsonOpts);
            var usage = resEl.TryGetProperty("result", out var r) && r.TryGetProperty("usage", out var u) ? u.Clone() : (JsonElement?)null;
            var err = resEl.TryGetProperty("error", out var e) ? e.GetString() : null;

            var doneEv = JsonSerializer.SerializeToElement(new { usage, error = err }, JsonOpts);
            RecordReplay("room:turn_done", doneEv);
            Broadcast("room:turn_done", doneEv, excludeClientId: sender.ClientId);
            return res;
        }
        finally
        {
            _busy = false;
            Broadcast("room:busy", new { busy = false });
            OnRoomsChanged?.Invoke();
        }
    }

    private async Task<object> HandleCancelAsync()
    {
        _openPermissions.Clear();
        if (string.Equals(_settings.Current.TransportMode, "stream-json", StringComparison.OrdinalIgnoreCase))
        {
            await _cli.StartStreamSessionAsync(CurrentWorkspace, _settings.Current.DefaultModel, _settings.Current.DefaultMode);
        }
        else
        {
            await _acp.CancelTurnAsync();
        }
        _busy = false;
        Broadcast("room:busy", new { busy = false });
        OnRoomsChanged?.Invoke();
        return new { ok = true };
    }

    private async Task<object> HandleRespondPermissionAsync(string requestKey, string optionId, bool cancelled)
    {
        _openPermissions.TryRemove(requestKey, out _);
        var res = await _acp.RespondPermissionAsync(requestKey, optionId, cancelled);
        var resolvedEv = JsonSerializer.SerializeToElement(new { requestKey, optionId, cancelled }, JsonOpts);
        RecordReplay("room:permission_resolved", resolvedEv);
        Broadcast("room:permission_resolved", resolvedEv);
        return res;
    }

    private async Task<object> HandleOpenWorkspaceAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new { ok = false, error = "Thư mục không tồn tại trên máy chủ." };

        CurrentWorkspace = Path.GetFullPath(path);
        _settings.RecordWorkspace(CurrentWorkspace);
        _workspace.WatchWorkspace(CurrentWorkspace);
        await _terminal.ChangeDirectoryAsync(CurrentWorkspace);

        ClearReplay();
        _sessionTitle = "Phiên mới";
        _ctxUsed = 0;

        var conn = await EnsureConnectedAsync(forceReconnect: true);
        var git = await _git.GetStatusAsync(CurrentWorkspace);
        var tree = _workspace.ListDirectory(CurrentWorkspace);

        var wsPayload = new
        {
            ok = true,
            workspace = CurrentWorkspace,
            workspaceName = GetFolderName(CurrentWorkspace),
            settings = _settings.ToPublicDto(),
            git,
            tree,
            connection = conn
        };

        Broadcast("room:workspace_changed", wsPayload);
        OnRoomsChanged?.Invoke();
        return wsPayload;
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
            if (!string.IsNullOrEmpty(v)) { _settings.Current.GeminiApiKeyPlain = v.Trim(); needReconnect = true; }
        }
        if (GetBool(payload, "clearGeminiApiKey")) { _settings.Current.GeminiApiKeyPlain = ""; needReconnect = true; }
        if (payload.TryGetProperty("geminiBaseUrl", out var p5))
        {
            var v = p5.GetString()?.Trim() ?? "";
            if (v != _settings.Current.GeminiBaseUrl) { _settings.Current.GeminiBaseUrl = v; needReconnect = true; }
        }
        if (payload.TryGetProperty("theme", out var p6)) _settings.Current.Theme = p6.GetString() ?? "obsidian-cyan";
        if (payload.TryGetProperty("fontSize", out var p7) && p7.TryGetInt32(out var fs)) _settings.Current.FontSize = Math.Clamp(fs, 11, 18);
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
            reconnectResult = await EnsureConnectedAsync(forceReconnect: true);

        return new { ok = true, settings = _settings.ToPublicDto(), reconnected = needReconnect, reconnectResult };
    }

    private async Task<object> HandleApplyUpdateAsync(RoomManager manager, string downloadUrl, string version)
    {
        manager.BroadcastAll("server:updating", new { version });
        var res = await manager.Updater.DownloadAndApplyUpdateAsync(downloadUrl, version);
        return res;
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

    private static object GetProcessMetrics()
    {
        using var proc = Process.GetCurrentProcess();
        return new { workingSetMb = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1) };
    }

    private static string GetFolderName(string path) =>
        Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;

    private static string GetStr(JsonElement el, string prop, string def = "") =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? def : def;

    private static bool GetBool(JsonElement el, string prop, bool def = false) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : def;

    private static int GetInt(JsonElement el, string prop, int def = 0) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i)
            ? i : def;

    public void Dispose()
    {
        _acp.Dispose();
        _cli.Dispose();
        _workspace.Dispose();
        _terminal.Dispose();
    }
}

public sealed class RoomManager : IDisposable
{
    private readonly SettingsService _settings;
    private readonly string _defaultWorkspace;
    private readonly string[] _lanUrls;
    private readonly string _publicUrl;
    private readonly ConcurrentDictionary<string, WorkspaceRoom> _rooms = new();
    private readonly ConcurrentDictionary<string, ClientSocket> _allClients = new();
    private int _roomSeq = 1;

    public UpdateService Updater { get; }

    public RoomManager(SettingsService settings, UpdateService updater, string defaultWorkspace, string[] lanUrls, string publicUrl)
    {
        _settings = settings;
        Updater = updater;
        _defaultWorkspace = Directory.Exists(defaultWorkspace)
            ? Path.GetFullPath(defaultWorkspace)
            : (Directory.Exists(settings.Current.LastWorkspace)
                ? settings.Current.LastWorkspace
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _lanUrls = lanUrls;
        _publicUrl = publicUrl;

        // Create default shared room lazily (doesn't spawn agy until first user connects!)
        var defRoom = new WorkspaceRoom("default", "Shared Room #1", isShared: true, _defaultWorkspace, _settings);
        defRoom.OnRoomsChanged += BroadcastRoomsList;
        _rooms["default"] = defRoom;
    }

    public int TotalConnectedClients => _allClients.Count;

    public void RegisterClient(ClientSocket client) => _allClients[client.ClientId] = client;

    public void UnregisterClient(ClientSocket client)
    {
        _allClients.TryRemove(client.ClientId, out _);
        if (_rooms.TryGetValue(client.RoomId, out var room))
            room.DetachClient(client.ClientId);
    }

    public WorkspaceRoom GetOrDefaultRoom(string? roomId)
    {
        if (!string.IsNullOrWhiteSpace(roomId) && _rooms.TryGetValue(roomId, out var r))
            return r;
        return _rooms["default"];
    }

    public WorkspaceRoom CreateRoom(string? name, bool isShared, string? workspacePath)
    {
        var num = Interlocked.Increment(ref _roomSeq);
        var id = $"room-{Guid.NewGuid():N}"[..11];
        var label = string.IsNullOrWhiteSpace(name)
            ? (isShared ? $"Shared Room #{num}" : $"Isolated #{num}")
            : name.Trim();
        var ws = !string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath)
            ? Path.GetFullPath(workspacePath)
            : _defaultWorkspace;

        var room = new WorkspaceRoom(id, label, isShared, ws, _settings);
        room.OnRoomsChanged += BroadcastRoomsList;
        _rooms[id] = room;
        BroadcastRoomsList();
        return room;
    }

    public bool DeleteRoom(string roomId, out string error)
    {
        error = "";
        if (roomId == "default")
        {
            error = "Không thể xóa Shared Room mặc định.";
            return false;
        }
        if (!_rooms.TryRemove(roomId, out var room))
        {
            error = "Room không tồn tại.";
            return false;
        }
        room.Dispose();
        BroadcastRoomsList();
        return true;
    }

    public WorkspaceRoom SwitchClientRoom(ClientSocket client, string targetRoomId)
    {
        if (_rooms.TryGetValue(client.RoomId, out var oldRoom))
            oldRoom.DetachClient(client.ClientId);

        var nextRoom = GetOrDefaultRoom(targetRoomId);
        nextRoom.AttachClient(client);
        BroadcastRoomsList();
        return nextRoom;
    }

    public object[] ListRooms() =>
        _rooms.Values.OrderBy(r => r.CreatedAtUtc).Select(r => r.GetSummary()).ToArray();

    public object GetServerNetworkInfo() => new
    {
        lanUrls = _lanUrls,
        publicUrl = _publicUrl,
        totalClients = _allClients.Count,
        totalRooms = _rooms.Count,
        machineName = Environment.MachineName
    };

    public async Task DisconnectAllClientsAsync(string reason)
    {
        foreach (var c in _allClients.Values.ToArray())
            await c.CloseSafeAsync(reason);
    }

    public void BroadcastAll(string eventName, object? payload)
    {
        var json = JsonSerializer.Serialize(new
        {
            kind = "event",
            @event = eventName,
            payload
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        foreach (var c in _allClients.Values)
            _ = c.SendRawJsonAsync(json);
    }

    private void BroadcastRoomsList()
    {
        var json = JsonSerializer.Serialize(new
        {
            kind = "event",
            @event = "server:rooms",
            payload = new { rooms = ListRooms(), serverInfo = GetServerNetworkInfo() }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        foreach (var c in _allClients.Values)
            _ = c.SendRawJsonAsync(json);
    }

    public void Dispose()
    {
        foreach (var r in _rooms.Values) r.Dispose();
        _rooms.Clear();
    }
}
