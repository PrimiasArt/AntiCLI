using System.Text;

namespace AntiCliUi.Services;

public sealed class WorkspaceService : IDisposable
{
    private static readonly HashSet<string> DefaultIgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", ".venv", "venv",
        "dist", "build", ".next", ".nuxt", "target", "packages", ".cache", "coverage"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp",
        ".pdf", ".zip", ".rar", ".7z", ".gz", ".tar", ".mp3", ".mp4", ".wav", ".avi",
        ".woff", ".woff2", ".ttf", ".otf", ".eot", ".bin", ".dat", ".db", ".sqlite", ".so", ".dylib"
    };

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;
    private string _watchedRoot = "";
    private readonly object _watchLock = new();

    public event Action<string, object?>? OnBridgeEvent;

    public void WatchWorkspace(string rootPath)
    {
        lock (_watchLock)
        {
            _watcher?.Dispose();
            _watcher = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return;
            _watchedRoot = Path.GetFullPath(rootPath);

            try
            {
                var watcher = new FileSystemWatcher(_watchedRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                FileSystemEventHandler onFsEvent = (_, e) => ScheduleWatchNotify(e.FullPath);
                RenamedEventHandler onRenamed = (_, e) => ScheduleWatchNotify(e.FullPath);

                watcher.Changed += onFsEvent;
                watcher.Created += onFsEvent;
                watcher.Deleted += onFsEvent;
                watcher.Renamed += onRenamed;
                _watcher = watcher;
            }
            catch { }
        }
    }

    private void ScheduleWatchNotify(string fullPath)
    {
        // Ignore noisy dirs
        foreach (var seg in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (DefaultIgnoredDirs.Contains(seg)) return;
        }

        lock (_watchLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(_ =>
            {
                OnBridgeEvent?.Invoke("workspace:changed", new
                {
                    root = _watchedRoot,
                    path = fullPath
                });
            }, null, 350, Timeout.Infinite);
        }
    }

    public object ListDirectory(string rootPath, string relativePath = "", bool showHidden = false)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return new { ok = false, error = "Workspace không tồn tại", entries = Array.Empty<object>() };

        var rootFull = Path.GetFullPath(rootPath);
        var target = string.IsNullOrWhiteSpace(relativePath)
            ? rootFull
            : Path.GetFullPath(Path.Combine(rootFull, relativePath));

        if (!IsWithinRoot(rootFull, target) || !Directory.Exists(target))
            return new { ok = false, error = "Đường dẫn không hợp lệ", entries = Array.Empty<object>() };

        var entries = new List<object>();
        try
        {
            var dirInfo = new DirectoryInfo(target);
            foreach (var d in dirInfo.EnumerateDirectories().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!showHidden && (d.Name.StartsWith('.') || DefaultIgnoredDirs.Contains(d.Name)))
                    continue;

                var rel = Path.GetRelativePath(rootFull, d.FullName).Replace('\\', '/');
                entries.Add(new
                {
                    name = d.Name,
                    relativePath = rel,
                    fullPath = d.FullName,
                    isDir = true,
                    size = 0L,
                    ext = ""
                });
            }

            foreach (var f in dirInfo.EnumerateFiles().OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!showHidden && f.Name.StartsWith('.'))
                    continue;

                var rel = Path.GetRelativePath(rootFull, f.FullName).Replace('\\', '/');
                entries.Add(new
                {
                    name = f.Name,
                    relativePath = rel,
                    fullPath = f.FullName,
                    isDir = false,
                    size = f.Length,
                    ext = f.Extension.ToLowerInvariant()
                });
            }
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message, entries = Array.Empty<object>() };
        }

        return new { ok = true, relativePath = relativePath.Replace('\\', '/'), entries };
    }

    public object SearchFiles(string rootPath, string query, int maxResults = 40)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            return new { ok = false, files = Array.Empty<object>() };

        var rootFull = Path.GetFullPath(rootPath);
        var q = (query ?? "").Trim();
        var results = new List<(int score, string rel, string name, string full, long size)>();

        void Walk(string dir, int depth)
        {
            if (depth > 8 || results.Count >= 400) return;
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch { return; }

            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(rootFull, f).Replace('\\', '/');
                var name = Path.GetFileName(f);
                var score = ScoreMatch(rel, name, q);
                if (score > 0)
                {
                    long size = 0;
                    try { size = new FileInfo(f).Length; } catch { }
                    results.Add((score, rel, name, f, size));
                }
            }

            foreach (var d in dirs)
            {
                var dName = Path.GetFileName(d);
                if (DefaultIgnoredDirs.Contains(dName) || dName.StartsWith('.')) continue;
                Walk(d, depth + 1);
            }
        }

        Walk(rootFull, 0);

        var top = results
            .OrderByDescending(r => r.score)
            .ThenBy(r => r.rel.Length)
            .Take(maxResults)
            .Select(r => new
            {
                name = r.name,
                relativePath = r.rel,
                fullPath = r.full,
                size = r.size,
                ext = Path.GetExtension(r.name).ToLowerInvariant()
            })
            .ToArray();

        return new { ok = true, files = top };
    }

    public object ReadFile(string rootPath, string relativeOrAbsolutePath, int maxBytes = 512 * 1024)
    {
        try
        {
            var fullPath = ResolveSafePath(rootPath, relativeOrAbsolutePath, out var relPath);
            if (!File.Exists(fullPath))
                return new { ok = false, error = "Tệp không tồn tại." };

            var fi = new FileInfo(fullPath);
            if (BinaryExtensions.Contains(fi.Extension))
                return new { ok = true, binary = true, relativePath = relPath, fullPath, size = fi.Length, content = "" };

            var truncated = fi.Length > maxBytes;
            string content;
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var toRead = (int)Math.Min(fi.Length, maxBytes);
                var buffer = new byte[toRead];
                var read = fs.Read(buffer, 0, toRead);
                // Detect NUL bytes (binary)
                var checkLen = Math.Min(read, 4096);
                for (int i = 0; i < checkLen; i++)
                {
                    if (buffer[i] == 0)
                        return new { ok = true, binary = true, relativePath = relPath, fullPath, size = fi.Length, content = "" };
                }
                content = Encoding.UTF8.GetString(buffer, 0, read);
            }

            return new
            {
                ok = true,
                binary = false,
                truncated,
                relativePath = relPath,
                fullPath,
                name = fi.Name,
                ext = fi.Extension.ToLowerInvariant(),
                size = fi.Length,
                content
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    public object WriteFile(string rootPath, string relativeOrAbsolutePath, string content)
    {
        try
        {
            var fullPath = ResolveSafePath(rootPath, relativeOrAbsolutePath, out var relPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, content ?? "", new UTF8Encoding(false));
            return new { ok = true, relativePath = relPath, fullPath };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private static string ResolveSafePath(string rootPath, string relativeOrAbsolutePath, out string relPath)
    {
        var rootFull = Path.GetFullPath(rootPath);
        var full = Path.IsPathRooted(relativeOrAbsolutePath)
            ? Path.GetFullPath(relativeOrAbsolutePath)
            : Path.GetFullPath(Path.Combine(rootFull, relativeOrAbsolutePath));

        relPath = IsWithinRoot(rootFull, full)
            ? Path.GetRelativePath(rootFull, full).Replace('\\', '/')
            : full.Replace('\\', '/');

        return full;
    }

    private static bool IsWithinRoot(string rootFull, string candidateFull)
    {
        var normRoot = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normCand = candidateFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normCand, rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
               || candidateFull.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreMatch(string rel, string name, string query)
    {
        if (string.IsNullOrEmpty(query)) return 10;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 500;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 300;
        if (rel.Contains(query, StringComparison.OrdinalIgnoreCase)) return 200;

        // Fuzzy subsequence match
        int qi = 0;
        for (int i = 0; i < rel.Length && qi < query.Length; i++)
        {
            if (char.ToLowerInvariant(rel[i]) == char.ToLowerInvariant(query[qi]))
                qi++;
        }
        return qi == query.Length ? 100 : 0;
    }

    public void Dispose()
    {
        lock (_watchLock)
        {
            _watcher?.Dispose();
            _debounceTimer?.Dispose();
        }
    }
}
