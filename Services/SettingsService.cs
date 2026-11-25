using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntiCliUi.Services;

public sealed class AppSettings
{
    public string AgyPath { get; set; } = "";
    public string AuthMode { get; set; } = "antigravity"; // "antigravity" | "gemini-api-key"
    public string TransportMode { get; set; } = "acp"; // "acp" | "stream-json"
    public string EncryptedGeminiApiKey { get; set; } = "";
    public string GeminiBaseUrl { get; set; } = "";
    public string LastWorkspace { get; set; } = "";
    public List<string> RecentWorkspaces { get; set; } = new();
    public string Theme { get; set; } = "obsidian-cyan";
    public int FontSize { get; set; } = 13;
    public bool ShowThinkingByDefault { get; set; } = false;
    public bool EnableSandbox { get; set; } = false;
    public bool AutoApprovePermissions { get; set; } = false;
    public bool EnableBrowserMcp { get; set; } = true;
    public bool AutoLaunchDebugChrome { get; set; } = false;
    public int ChromeDebugPort { get; set; } = 9222;
    public string DefaultModel { get; set; } = "";
    public string DefaultThoughtLevel { get; set; } = "";
    public string DefaultMode { get; set; } = "default";
    public Dictionary<string, string> SavedSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string GeminiApiKeyPlain { get; set; } = "";
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDir;
    private readonly string _settingsPath;

    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AntiCLI");
        Directory.CreateDirectory(_dataDir);
        _settingsPath = Path.Combine(_dataDir, "settings.json");
        Load();
    }

    public string DataDirectory => _dataDir;

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded is not null)
                {
                    Current = loaded;
                    Current.GeminiApiKeyPlain = UnprotectSecret(Current.EncryptedGeminiApiKey);
                }
            }
        }
        catch
        {
            Current = new AppSettings();
        }

        Current.RecentWorkspaces = Current.RecentWorkspaces
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    public void Save()
    {
        Current.EncryptedGeminiApiKey = ProtectSecret(Current.GeminiApiKeyPlain);
        var json = JsonSerializer.Serialize(Current, JsonOpts);
        File.WriteAllText(_settingsPath, json, Encoding.UTF8);
    }

    public string? GetSavedSessionId(string scopeKey)
    {
        if (string.IsNullOrWhiteSpace(scopeKey)) return null;
        return Current.SavedSessions.TryGetValue(scopeKey, out var sid) && !string.IsNullOrWhiteSpace(sid)
            ? sid
            : null;
    }

    public void SetSavedSessionId(string scopeKey, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(scopeKey)) return;
        if (string.IsNullOrWhiteSpace(sessionId))
            Current.SavedSessions.Remove(scopeKey);
        else
            Current.SavedSessions[scopeKey] = sessionId;
        try { Save(); } catch { }
    }

    public void RecordWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var full = Path.GetFullPath(path);
        Current.LastWorkspace = full;
        Current.RecentWorkspaces.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        Current.RecentWorkspaces.Insert(0, full);
        if (Current.RecentWorkspaces.Count > 12)
            Current.RecentWorkspaces = Current.RecentWorkspaces.Take(12).ToList();
        Save();
    }

    public string ResolveAgyPath()
    {
        if (!string.IsNullOrWhiteSpace(Current.AgyPath) && File.Exists(Current.AgyPath))
            return Current.AgyPath;

        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "agy.exe"),
        };

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim(), "agy.exe"));
                candidates.Add(Path.Combine(dir.Trim(), "agy.cmd"));
            }
            catch { }
        }

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return "agy.exe";
    }

    public object ToPublicDto() => new
    {
        agyPath = Current.AgyPath,
        resolvedAgyPath = ResolveAgyPath(),
        agyInstalled = File.Exists(ResolveAgyPath()),
        authMode = Current.AuthMode,
        transportMode = Current.TransportMode,
        hasGeminiApiKey = !string.IsNullOrWhiteSpace(Current.GeminiApiKeyPlain),
        geminiApiKeyMasked = MaskKey(Current.GeminiApiKeyPlain),
        geminiBaseUrl = Current.GeminiBaseUrl,
        lastWorkspace = Current.LastWorkspace,
        recentWorkspaces = Current.RecentWorkspaces,
        theme = Current.Theme,
        fontSize = Current.FontSize,
        showThinkingByDefault = Current.ShowThinkingByDefault,
        enableSandbox = Current.EnableSandbox,
        autoApprovePermissions = Current.AutoApprovePermissions,
        enableBrowserMcp = Current.EnableBrowserMcp,
        autoLaunchDebugChrome = Current.AutoLaunchDebugChrome,
        chromeDebugPort = Current.ChromeDebugPort,
        defaultModel = Current.DefaultModel,
        defaultThoughtLevel = Current.DefaultThoughtLevel,
        defaultMode = Current.DefaultMode
    };

    private static string MaskKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        if (key.Length <= 8) return new string('•', key.Length);
        return $"{key[..4]}{new string('•', Math.Min(16, key.Length - 8))}{key[^4..]}";
    }

    private static string ProtectSecret(string plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return "";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return "";
        }
    }

    private static string UnprotectSecret(string cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher)) return "";
        try
        {
            var bytes = Convert.FromBase64String(cipher);
            var plainBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return "";
        }
    }
}
