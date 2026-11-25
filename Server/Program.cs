using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AntiCliServer;
using AntiCliUi.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;

try { Console.OutputEncoding = Encoding.UTF8; } catch { }

var host = GetArg(args, "--host") ?? Environment.GetEnvironmentVariable("ANTICLI_HOST") ?? "0.0.0.0";
var portStr = GetArg(args, "--port") ?? Environment.GetEnvironmentVariable("ANTICLI_PORT") ?? "3847";
var port = int.TryParse(portStr, out var parsedPort) ? parsedPort : 3847;
var overridePassword = GetArg(args, "--password") ?? Environment.GetEnvironmentVariable("ANTICLI_PASSWORD");
var defaultWorkspace = GetArg(args, "--workspace") ?? Environment.GetEnvironmentVariable("ANTICLI_WORKSPACE") ?? Environment.CurrentDirectory;
var publicUrl = GetArg(args, "--public-url") ?? Environment.GetEnvironmentVariable("ANTICLI_PUBLIC_URL") ?? "";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.TimestampFormat = "[HH:mm:ss] "; o.SingleLine = true; });
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Parse(host == "localhost" ? "127.0.0.1" : host), port);
});

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(25)
});

var settings = new SettingsService();
var auth = new ServerAuthService(settings.DataDirectory, overridePassword);
var lanUrls = DiscoverLanUrls(port);
var updater = new UpdateService(isServer: true, requestAppShutdown: () => app.Lifetime.StopApplication());
var roomManager = new RoomManager(settings, updater, defaultWorkspace, lanUrls, publicUrl);

auth.OnAllSessionsRevoked += () =>
{
    _ = roomManager.DisconnectAllClientsAsync("Session revoked");
};

app.Lifetime.ApplicationStopping.Register(() =>
{
    roomManager.Dispose();
});

// Serve static UI from wwwroot
var wwwrootPath = ResolveWwwRoot();
var fileProvider = new PhysicalFileProvider(wwwrootPath);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = fileProvider,
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true
};

// ============================================================================
// Auth & Server Info REST Endpoints
// ============================================================================
app.MapGet("/api/health", () => Results.Ok(new { ok = true, version = updater.CurrentVersion }));

app.MapGet("/api/auth/status", (HttpContext ctx) =>
{
    var authed = auth.IsRequestAuthenticated(ctx);
    return Results.Json(new
    {
        isServerMode = true,
        authenticated = authed,
        machineName = Environment.MachineName,
        lanUrls = authed ? lanUrls : Array.Empty<string>(),
        publicUrl = authed ? publicUrl : "",
        totalClients = authed ? roomManager.TotalConnectedClients : 0
    }, jsonOpts);
});

app.MapPost("/api/auth/login", async (HttpContext ctx) =>
{
    var remoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    JsonElement body;
    try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
    catch { return Results.BadRequest(new { ok = false, error = "Invalid JSON" }); }

    var password = body.TryGetProperty("password", out var p) ? p.GetString() : null;
    var remember = body.TryGetProperty("remember", out var r) && r.ValueKind == JsonValueKind.True;

    var (ok, error, retryAfter) = auth.VerifyPassword(password, remoteIp);
    if (!ok)
    {
        if (retryAfter > 0) ctx.Response.Headers.RetryAfter = retryAfter.ToString();
        return Results.Json(new { ok = false, error }, statusCode: retryAfter > 0 ? 429 : 401);
    }

    var ua = ctx.Request.Headers.UserAgent.ToString();
    var token = auth.IssueSessionToken(ua, remember);
    var isHttps = ctx.Request.IsHttps || string.Equals(ctx.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);

    ctx.Response.Cookies.Append(ServerAuthService.SessionCookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = isHttps,
        Path = "/",
        Expires = remember ? DateTimeOffset.UtcNow.AddDays(14) : null
    });

    return Results.Json(new { ok = true, authenticated = true }, jsonOpts);
});

app.MapPost("/api/auth/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(ServerAuthService.SessionCookieName, new CookieOptions { Path = "/" });
    return Results.Json(new { ok = true }, jsonOpts);
});

app.MapPost("/api/auth/change-password", async (HttpContext ctx) =>
{
    if (!auth.IsRequestAuthenticated(ctx))
        return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);

    JsonElement body;
    try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); }
    catch { return Results.BadRequest(new { ok = false, error = "Invalid JSON" }); }

    var current = body.TryGetProperty("currentPassword", out var c) ? c.GetString() ?? "" : "";
    var next = body.TryGetProperty("newPassword", out var n) ? n.GetString() ?? "" : "";
    var revoke = body.TryGetProperty("revokeOthers", out var rv) && rv.ValueKind == JsonValueKind.True;

    var (ok, err) = auth.ChangePassword(current, next, revoke);
    if (!ok) return Results.Json(new { ok = false, error = err }, statusCode: 400);

    // Re-issue cookie for the caller so they stay logged in
    var token = auth.IssueSessionToken(ctx.Request.Headers.UserAgent.ToString(), true);
    var isHttps = ctx.Request.IsHttps || string.Equals(ctx.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
    ctx.Response.Cookies.Append(ServerAuthService.SessionCookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = isHttps,
        Path = "/",
        Expires = DateTimeOffset.UtcNow.AddDays(14)
    });

    return Results.Json(new { ok = true }, jsonOpts);
});

app.MapPost("/api/auth/revoke-all", (HttpContext ctx) =>
{
    if (!auth.IsRequestAuthenticated(ctx))
        return Results.Json(new { ok = false, error = "Unauthorized" }, statusCode: 401);
    auth.RevokeAllSessions();
    ctx.Response.Cookies.Delete(ServerAuthService.SessionCookieName, new CookieOptions { Path = "/" });
    return Results.Json(new { ok = true }, jsonOpts);
});

// ============================================================================
// WebSocket Bridge Endpoint (/ws?room=...)
// ============================================================================
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    if (!auth.IsRequestAuthenticated(ctx))
    {
        ctx.Response.StatusCode = 401;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var client = new ClientSocket(ws, ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", ctx.Request.Headers.UserAgent.ToString());
    roomManager.RegisterClient(client);

    var requestedRoom = ctx.Request.Query["room"].ToString();
    var currentRoom = roomManager.GetOrDefaultRoom(requestedRoom);
    currentRoom.AttachClient(client);

    var buffer = new byte[16 * 1024];
    using var ms = new MemoryStream();

    try
    {
        while (ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;

            var raw = Encoding.UTF8.GetString(ms.ToArray());
            _ = Task.Run(async () =>
            {
                string? reqId = null;
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    reqId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var action = root.TryGetProperty("action", out var actEl) ? actEl.GetString() ?? "" : "";
                    var payload = root.TryGetProperty("payload", out var payEl) ? payEl.Clone() : default;

                    object data;
                    if (action == "room:list")
                    {
                        data = new { ok = true, rooms = roomManager.ListRooms(), currentRoomId = client.RoomId, serverInfo = roomManager.GetServerNetworkInfo() };
                    }
                    else if (action == "room:create")
                    {
                        var rName = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var isShared = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("isShared", out var sh) && sh.ValueKind == JsonValueKind.True;
                        var rWs = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("workspace", out var w) ? w.GetString() : null;
                        var created = roomManager.CreateRoom(rName, isShared, rWs);
                        currentRoom = roomManager.SwitchClientRoom(client, created.Id);
                        data = new
                        {
                            ok = true,
                            room = created.GetSummary(),
                            rooms = roomManager.ListRooms(),
                            init = await created.HandleActionAsync(client, "app:init", default, roomManager)
                        };
                    }
                    else if (action == "room:join")
                    {
                        var targetId = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("roomId", out var tid) ? tid.GetString() ?? "default" : "default";
                        currentRoom = roomManager.SwitchClientRoom(client, targetId);
                        data = new
                        {
                            ok = true,
                            room = currentRoom.GetSummary(),
                            rooms = roomManager.ListRooms(),
                            init = await currentRoom.HandleActionAsync(client, "app:init", default, roomManager)
                        };
                    }
                    else if (action == "room:delete")
                    {
                        var targetId = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("roomId", out var tid) ? tid.GetString() ?? "" : "";
                        var wasInside = client.RoomId == targetId;
                        if (wasInside) currentRoom = roomManager.SwitchClientRoom(client, "default");
                        var ok = roomManager.DeleteRoom(targetId, out var err);
                        data = new
                        {
                            ok,
                            error = err,
                            rooms = roomManager.ListRooms(),
                            switchedToDefault = wasInside,
                            init = wasInside ? await currentRoom.HandleActionAsync(client, "app:init", default, roomManager) : null
                        };
                    }
                    else
                    {
                        var activeRoom = roomManager.GetOrDefaultRoom(client.RoomId);
                        data = await activeRoom.HandleActionAsync(client, action, payload, roomManager);
                    }

                    if (!string.IsNullOrEmpty(reqId))
                    {
                        var resp = JsonSerializer.Serialize(new { kind = "response", id = reqId, ok = true, data }, jsonOpts);
                        await client.SendRawJsonAsync(resp);
                    }
                }
                catch (Exception ex)
                {
                    if (!string.IsNullOrEmpty(reqId))
                    {
                        var resp = JsonSerializer.Serialize(new { kind = "response", id = reqId, ok = false, error = ex.Message }, jsonOpts);
                        await client.SendRawJsonAsync(resp);
                    }
                }
            });
        }
    }
    catch { }
    finally
    {
        roomManager.UnregisterClient(client);
    }
});

// Startup Banner
PrintBanner(port, lanUrls, publicUrl, auth.GeneratedInitialPassword, settings.DataDirectory);

await app.RunAsync();

static string? GetArg(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string ResolveWwwRoot()
{
    var exeDir = AppContext.BaseDirectory;
    var candidate = Path.Combine(exeDir, "wwwroot");
    if (Directory.Exists(candidate)) return candidate;
    var parent = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "wwwroot"));
    if (Directory.Exists(parent)) return parent;
    var cwd = Path.Combine(Environment.CurrentDirectory, "wwwroot");
    if (Directory.Exists(cwd)) return cwd;
    Directory.CreateDirectory(candidate);
    return candidate;
}

static string[] DiscoverLanUrls(int port)
{
    var list = new List<string> { $"http://localhost:{port}" };
    try
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;
                list.Add($"http://{ip}:{port}");
            }
        }
    }
    catch { }
    return list.Distinct().ToArray();
}

static void PrintBanner(int port, string[] lanUrls, string publicUrl, string? initialPwd, string dataDir)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("======================================================================");
    Console.WriteLine("  >_ AntiCLI Studio Server (Headless Multi-Room Web Workbench)");
    Console.WriteLine("======================================================================");
    Console.ResetColor();
    Console.WriteLine($"  Port      : {port}");
    Console.WriteLine($"  Host PC   : {Environment.MachineName}");
    Console.WriteLine("  URLs      :");
    foreach (var u in lanUrls) Console.WriteLine($"              • {u}");
    if (!string.IsNullOrWhiteSpace(publicUrl))
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Public    : • {publicUrl}");
        Console.ResetColor();
    }
    Console.WriteLine();
    if (!string.IsNullOrEmpty(initialPwd))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  [FIRST RUN] Mật khẩu đăng nhập tự tạo: {initialPwd}");
        Console.WriteLine("  (Hãy lưu lại hoặc đổi trong Settings > Bảo mật trên giao diện Web)");
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine($"  Auth      : Đang bật bảo vệ mật khẩu ({Path.Combine(dataDir, "server-auth.json")})");
        Console.WriteLine("              Đổi nhanh mật khẩu bằng tham số: --password <mat-khau-moi>");
    }
    Console.WriteLine("----------------------------------------------------------------------");
    Console.WriteLine("  Nhấn Ctrl+C để dừng máy chủ an toàn (tự động đóng mọi tiến trình agy).");
    Console.WriteLine();
}
