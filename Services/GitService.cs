using System.Diagnostics;
using System.Text;

namespace AntiCliUi.Services;

public sealed class GitService
{
    public async Task<object> GetStatusAsync(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            return new { available = false, isRepo = false, branch = "", files = Array.Empty<object>() };

        var (revCode, revOut, _) = await RunGitAsync(cwd, "rev-parse --is-inside-work-tree");
        if (revCode != 0 || !revOut.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return new { available = revCode != -99, isRepo = false, branch = "", files = Array.Empty<object>() };

        var (_, branchOut, _) = await RunGitAsync(cwd, "rev-parse --abbrev-ref HEAD");
        var (_, statusOut, _) = await RunGitAsync(cwd, "status --porcelain=v1 -uall");

        var files = new List<object>();
        foreach (var raw in statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 4) continue;
            var xy = raw[..2];
            var path = raw[3..].Trim().Trim('"');
            var status = xy.Contains('?') ? "untracked"
                : xy.Contains('A') ? "added"
                : xy.Contains('D') ? "deleted"
                : xy.Contains('R') ? "renamed"
                : "modified";
            files.Add(new { path, code = xy.Trim(), status });
        }

        return new
        {
            available = true,
            isRepo = true,
            branch = branchOut.Trim(),
            files
        };
    }

    public async Task<object> GetFileDiffAsync(string cwd, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            return new { ok = false, error = "Workspace không tồn tại" };

        var rel = relativePath.Replace('\\', '/');
        var (oldCode, oldText, _) = await RunGitAsync(cwd, $"show \"HEAD:{rel}\"");
        var fullPath = Path.Combine(cwd, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var newText = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, Encoding.UTF8) : "";
        var (_, diffText, _) = await RunGitAsync(cwd, $"diff -- \"{rel}\"");

        return new
        {
            ok = true,
            path = rel,
            oldText = oldCode == 0 ? oldText : "",
            newText,
            diff = diffText
        };
    }

    private static async Task<(int code, string stdout, string stderr)> RunGitAsync(string cwd, string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = cwd,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            proc.Start();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(8000);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch { try { proc.Kill(); } catch { } return (-1, "", "git timeout"); }
            return (proc.ExitCode, await outTask, await errTask);
        }
        catch
        {
            return (-99, "", "git not installed");
        }
    }
}
