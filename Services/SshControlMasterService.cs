using System.Diagnostics;

namespace ClaudeCommandCenter.Services;

public static class SshControlMasterService
{
    private static readonly string _socketDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccc", "ssh");

    // Tracks last failed connection attempt per host to throttle retries (30s cooldown)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastFailure = new();
    private static readonly TimeSpan _retryCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ensures a ControlMaster socket exists for the host.
    /// Returns true if the socket is alive (or was successfully started).
    /// Uses a 30s cooldown after failure to avoid hammering unreachable hosts.
    /// </summary>
    public static bool EnsureConnected(string host)
    {
        // Throttle: don't retry a failed host within cooldown period
        if (_lastFailure.TryGetValue(host, out var lastFail)
            && DateTime.UtcNow - lastFail < _retryCooldown)
            return false;

        if (IsAlive(host))
            return true;

        return StartControlMaster(host);
    }

    /// <summary>
    /// Runs a tmux command on the remote host via the ControlMaster socket.
    /// Returns (success, stdout, null) on success, or (false, null, stderr) on failure.
    /// Returns (false, null, null) if the host is offline.
    /// </summary>
    public static (bool Success, string? Output, string? Error) RunTmuxCommand(string host, params string[] tmuxArgs)
    {
        if (!EnsureConnected(host))
            return (false, null, null);

        var socketPath = SocketPath(host);
        var startInfo = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add(socketPath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ConnectTimeout=5");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("tmux");
        foreach (var arg in tmuxArgs)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, null, null);

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0
                ? (true, stdout.TrimEnd(), null)
                : (false, null, stderr.Trim());
        }
        catch
        {
            return (false, null, null);
        }
    }

    /// <summary>
    /// Disconnects the ControlMaster for this host (sends -O exit).
    /// Safe to call even if the socket doesn't exist.
    /// </summary>
    public static void Disconnect(string host)
    {
        try
        {
            var socketPath = SocketPath(host);
            if (!File.Exists(socketPath))
                return;

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add("-O");
            startInfo.ArgumentList.Add("exit");
            startInfo.ArgumentList.Add(host);

            using var process = Process.Start(startInfo);
            process?.WaitForExit(3000);
        }
        catch
        {
            // Best-effort
        }
    }

    private static bool IsAlive(string host)
    {
        try
        {
            var socketPath = SocketPath(host);
            if (!File.Exists(socketPath))
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add("-O");
            startInfo.ArgumentList.Add("check");
            startInfo.ArgumentList.Add(host);

            using var process = Process.Start(startInfo);
            if (process == null) return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartControlMaster(string host)
    {
        try
        {
            Directory.CreateDirectory(_socketDir);
            // Restrict socket dir to owner only
            if (!OperatingSystem.IsWindows())
            {
                var chmodInfo = new ProcessStartInfo { FileName = "chmod", UseShellExecute = false };
                chmodInfo.ArgumentList.Add("700");
                chmodInfo.ArgumentList.Add(_socketDir);
                Process.Start(chmodInfo)?.WaitForExit(1000);
            }

            var socketPath = SocketPath(host);
            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // -M = master mode, -N = no command, -f = daemonize after auth
            startInfo.ArgumentList.Add("-M");
            startInfo.ArgumentList.Add("-N");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("BatchMode=yes");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ConnectTimeout=10");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ControlPersist=no");
            startInfo.ArgumentList.Add(host);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _lastFailure[host] = DateTime.UtcNow;
                return false;
            }

            process.WaitForExit(12000);
            var connected = process.ExitCode == 0;

            if (!connected)
                _lastFailure[host] = DateTime.UtcNow;
            else
                _lastFailure.TryRemove(host, out _);

            return connected;
        }
        catch
        {
            _lastFailure[host] = DateTime.UtcNow;
            return false;
        }
    }

    private static string SocketPath(string host)
    {
        // Replace characters that are invalid in socket filenames
        var safeName = string.Concat(host.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_'));
        return Path.Combine(_socketDir, $"{safeName}.sock");
    }
}
