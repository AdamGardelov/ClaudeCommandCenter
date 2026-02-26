using System.Diagnostics;

namespace ClaudeCommandCenter.Services;

public static class SshService
{
    /// <summary>
    /// Runs a command locally or on a remote host via SSH.
    /// When remoteHost is null, runs locally. When set, runs via ssh.
    /// </summary>
    public static (bool Success, string? Output) Run(string? remoteHost, string command)
    {
        return remoteHost == null ? RunLocal(command) : RunSsh(remoteHost, command);
    }

    /// <summary>
    /// Builds the filename and arguments for launching a Claude session.
    /// Local: shell -lc claude
    /// Remote: ssh -t host 'cd path && claude'
    /// </summary>
    public static (string FileName, List<string> Args) BuildSessionCommand(
        string? remoteHost, string workingDirectory)
    {
        if (remoteHost == null)
        {
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
            return (shell, ["-lc", "claude"]);
        }

        return ("ssh", ["-t", remoteHost, $"cd {EscapePath(workingDirectory)} && claude"]);
    }

    /// <summary>
    /// Checks if a path is a git repo, locally or remotely.
    /// </summary>
    public static bool IsGitRepo(string? remoteHost, string path)
    {
        if (remoteHost == null)
            return GitService.IsGitRepo(path);

        var (success, _) = RunSsh(remoteHost, $"git -C {EscapePath(path)} rev-parse --git-dir");
        return success;
    }

    private static (bool Success, string? Output) RunLocal(string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd" : "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
            }
            else
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(command);
            }

            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, "Failed to start process");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0
                ? (true, stdout.Trim())
                : (false, stderr.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool Success, string? Output) RunSsh(string remoteHost, string command)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(remoteHost);
            startInfo.ArgumentList.Add(command);

            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, "Failed to start ssh");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0
                ? (true, stdout.Trim())
                : (false, stderr.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    internal static string EscapePath(string path) =>
        $"'{path.Replace("'", "'\\''")}'";
}
