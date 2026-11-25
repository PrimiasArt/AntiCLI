using System.Diagnostics;
using System.Text;

namespace AntiCliUi.Services;

public sealed class TerminalService : IDisposable
{
    private Process? _shell;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string _cwd = "";

    public event Action<string, object?>? OnBridgeEvent;

    public bool IsRunning => _shell is { HasExited: false };

    public void Start(string cwd)
    {
        Stop();
        _cwd = Directory.Exists(cwd) ? Path.GetFullPath(cwd) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile -NoExit -Command \"[Console]::InputEncoding=[Text.Encoding]::UTF8; [Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8\"",
            WorkingDirectory = _cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Exited += (_, _) =>
        {
            OnBridgeEvent?.Invoke("terminal:status", new { running = false, cwd = _cwd });
        };

        proc.Start();
        _shell = proc;
        _cts = new CancellationTokenSource();

        _ = Task.Run(() => ReadStreamAsync(proc.StandardOutput, "stdout", _cts.Token));
        _ = Task.Run(() => ReadStreamAsync(proc.StandardError, "stderr", _cts.Token));

        OnBridgeEvent?.Invoke("terminal:status", new { running = true, cwd = _cwd });
    }

    public async Task WriteCommandAsync(string command)
    {
        if (!IsRunning) Start(_cwd);
        if (_shell is null || _shell.HasExited) return;

        await _writeLock.WaitAsync();
        try
        {
            await _shell.StandardInput.WriteLineAsync(command);
            await _shell.StandardInput.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ChangeDirectoryAsync(string newCwd)
    {
        if (string.IsNullOrWhiteSpace(newCwd) || !Directory.Exists(newCwd)) return;
        _cwd = Path.GetFullPath(newCwd);
        if (IsRunning)
        {
            var escaped = _cwd.Replace("'", "''");
            await WriteCommandAsync($"Set-Location -LiteralPath '{escaped}'");
            OnBridgeEvent?.Invoke("terminal:status", new { running = true, cwd = _cwd });
        }
    }

    public void Restart(string? cwd = null)
    {
        Start(string.IsNullOrWhiteSpace(cwd) ? _cwd : cwd);
    }

    private async Task ReadStreamAsync(StreamReader reader, string stream, CancellationToken ct)
    {
        var buffer = new char[2048];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0) break;
                var text = new string(buffer, 0, read);
                OnBridgeEvent?.Invoke("terminal:output", new { stream, text });
            }
        }
        catch { }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_shell is not null)
        {
            try
            {
                if (!_shell.HasExited) _shell.Kill(entireProcessTree: true);
            }
            catch { }
            _shell.Dispose();
            _shell = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _writeLock.Dispose();
    }
}
