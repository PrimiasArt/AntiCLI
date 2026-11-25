using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AntiCliServer;

public sealed class AuthStoreModel
{
    public string PasswordSaltBase64 { get; set; } = "";
    public string PasswordHashBase64 { get; set; } = "";
    public string SigningKeyBase64 { get; set; } = "";
    public int TokenVersion { get; set; } = 1;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ServerAuthService
{
    public const string SessionCookieName = "anticli_session";
    private const int Pbkdf2Iterations = 100_000;
    private const int MaxFailedAttempts = 8;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private readonly string _authFilePath;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, ThrottleEntry> _throttles = new();
    private AuthStoreModel _store = new();

    public string? GeneratedInitialPassword { get; private set; }
    public event Action? OnAllSessionsRevoked;

    private sealed class ThrottleEntry
    {
        public int FailCount;
        public DateTime LockUntilUtc;
    }

    public ServerAuthService(string dataDir, string? overridePassword = null)
    {
        Directory.CreateDirectory(dataDir);
        _authFilePath = Path.Combine(dataDir, "server-auth.json");
        LoadOrInitialize(overridePassword);
    }

    private void LoadOrInitialize(string? overridePassword)
    {
        lock (_lock)
        {
            var loaded = false;
            if (File.Exists(_authFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_authFilePath, Encoding.UTF8);
                    var model = JsonSerializer.Deserialize<AuthStoreModel>(json);
                    if (model is not null && !string.IsNullOrEmpty(model.PasswordHashBase64) && !string.IsNullOrEmpty(model.SigningKeyBase64))
                    {
                        _store = model;
                        loaded = true;
                    }
                }
                catch { }
            }

            if (!loaded)
            {
                _store = new AuthStoreModel
                {
                    SigningKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    TokenVersion = 1
                };
                var initialPwd = !string.IsNullOrWhiteSpace(overridePassword)
                    ? overridePassword.Trim()
                    : GenerateFriendlyPassword();
                SetPasswordInternal(initialPwd, revokeExisting: false);
                GeneratedInitialPassword = initialPwd;
            }
            else if (!string.IsNullOrWhiteSpace(overridePassword))
            {
                SetPasswordInternal(overridePassword.Trim(), revokeExisting: false);
            }
        }
    }

    public (bool ok, string? error, int retryAfterSeconds) VerifyPassword(string? password, string remoteIp)
    {
        var ip = string.IsNullOrWhiteSpace(remoteIp) ? "unknown" : remoteIp;
        var now = DateTime.UtcNow;

        if (_throttles.TryGetValue(ip, out var entry) && entry.LockUntilUtc > now)
        {
            var wait = (int)Math.Ceiling((entry.LockUntilUtc - now).TotalSeconds);
            return (false, $"Quá nhiều lần nhập sai. Vui lòng thử lại sau {wait} giây.", wait);
        }

        if (string.IsNullOrEmpty(password))
            return (false, "Vui lòng nhập mật khẩu truy cập.", 0);

        bool matched;
        lock (_lock)
        {
            var salt = Convert.FromBase64String(_store.PasswordSaltBase64);
            var expected = Convert.FromBase64String(_store.PasswordHashBase64);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
            matched = CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        if (matched)
        {
            _throttles.TryRemove(ip, out _);
            return (true, null, 0);
        }

        var updated = _throttles.AddOrUpdate(
            ip,
            _ => new ThrottleEntry { FailCount = 1, LockUntilUtc = DateTime.MinValue },
            (_, existing) =>
            {
                existing.FailCount++;
                if (existing.FailCount >= MaxFailedAttempts)
                    existing.LockUntilUtc = DateTime.UtcNow.Add(LockoutDuration);
                return existing;
            });

        var remaining = Math.Max(0, MaxFailedAttempts - updated.FailCount);
        var msg = remaining > 0
            ? $"Mật khẩu không đúng (còn {remaining} lần thử)."
            : "Tạm khóa đăng nhập 5 phút do nhập sai quá số lần cho phép.";
        return (false, msg, remaining == 0 ? (int)LockoutDuration.TotalSeconds : 0);
    }

    public string IssueSessionToken(string deviceLabel, bool remember)
    {
        var lifetime = remember ? TimeSpan.FromDays(14) : TimeSpan.FromHours(12);
        var exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        int ver;
        byte[] key;
        lock (_lock)
        {
            ver = _store.TokenVersion;
            key = Convert.FromBase64String(_store.SigningKeyBase64);
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            exp,
            ver,
            dev = (deviceLabel ?? "web").Length > 48 ? deviceLabel![..48] : (deviceLabel ?? "web"),
            jti = Guid.NewGuid().ToString("N")[..12]
        });
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var sig = ComputeHmac(key, payloadB64);
        return $"{payloadB64}.{sig}";
    }

    public bool ValidateSessionToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot >= token.Length - 1) return false;

        var payloadB64 = token[..dot];
        var sigB64 = token[(dot + 1)..];

        int currentVer;
        byte[] key;
        lock (_lock)
        {
            currentVer = _store.TokenVersion;
            key = Convert.FromBase64String(_store.SigningKeyBase64);
        }

        var expectedSig = ComputeHmac(key, payloadB64);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sigB64), Encoding.ASCII.GetBytes(expectedSig)))
            return false;

        try
        {
            var payloadBytes = Base64UrlDecode(payloadB64);
            using var doc = JsonDocument.Parse(payloadBytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("exp", out var expEl) || !expEl.TryGetInt64(out var exp)) return false;
            if (!root.TryGetProperty("ver", out var verEl) || !verEl.TryGetInt32(out var ver)) return false;
            if (ver != currentVer) return false;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp;
        }
        catch
        {
            return false;
        }
    }

    public bool IsRequestAuthenticated(HttpContext ctx)
    {
        if (ctx.Request.Cookies.TryGetValue(SessionCookieName, out var cookieToken) && ValidateSessionToken(cookieToken))
            return true;

        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authHeader[7..].Trim();
            if (ValidateSessionToken(bearer)) return true;
        }

        if (ctx.Request.Query.TryGetValue("token", out var qTok) && ValidateSessionToken(qTok.ToString()))
            return true;

        return false;
    }

    public (bool ok, string? error) ChangePassword(string currentPassword, string newPassword, bool revokeOtherSessions)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Trim().Length < 6)
            return (false, "Mật khẩu mới phải có ít nhất 6 ký tự.");

        var (valid, err, _) = VerifyPassword(currentPassword, "local-change");
        if (!valid) return (false, err ?? "Mật khẩu hiện tại không đúng.");

        lock (_lock)
        {
            SetPasswordInternal(newPassword.Trim(), revokeExisting: revokeOtherSessions);
        }
        if (revokeOtherSessions) OnAllSessionsRevoked?.Invoke();
        return (true, null);
    }

    public void RevokeAllSessions()
    {
        lock (_lock)
        {
            _store.TokenVersion++;
            _store.UpdatedAtUtc = DateTime.UtcNow;
            SaveStoreLocked();
        }
        OnAllSessionsRevoked?.Invoke();
    }

    private void SetPasswordInternal(string plainPassword, bool revokeExisting)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(plainPassword), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        _store.PasswordSaltBase64 = Convert.ToBase64String(salt);
        _store.PasswordHashBase64 = Convert.ToBase64String(hash);
        if (revokeExisting) _store.TokenVersion++;
        _store.UpdatedAtUtc = DateTime.UtcNow;
        SaveStoreLocked();
    }

    private void SaveStoreLocked()
    {
        var json = JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_authFilePath, json, Encoding.UTF8);
    }

    private static string ComputeHmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static string GenerateFriendlyPassword()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(12);
        var sb = new StringBuilder(14);
        for (int i = 0; i < 12; i++)
        {
            if (i == 4 || i == 8) sb.Append('-');
            sb.Append(chars[bytes[i] % chars.Length]);
        }
        return sb.ToString();
    }
}
