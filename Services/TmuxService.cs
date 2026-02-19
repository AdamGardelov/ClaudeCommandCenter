using System.Diagnostics;
using ClaudeCommandCenter.Models;
using Spectre.Console;

namespace ClaudeCommandCenter.Services;

public abstract class TmuxService
{
    public static List<TmuxSession> ListSessions()
    {
        var output = RunTmux("list-sessions", "-F", "#{session_name}\t#{session_created}\t#{session_attached}\t#{session_windows}\t#{pane_current_path}");
        if (output == null)
            return [];

        var sessions = new List<TmuxSession>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4)
                continue;

            var session = new TmuxSession
            {
                Name = parts[0],
                IsAttached = parts[2] != "0",
                WindowCount = int.TryParse(parts[3], out var wc) ? wc : 0,
                CurrentPath = parts.Length > 4 ? parts[4] : null,
            };

            if (long.TryParse(parts[1], out var epoch))
                session.Created = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;

            sessions.Add(session);
        }

        // Detect input state and git info
        foreach (var session in sessions)
            DetectGitInfo(session);
        DetectWaitingForInputBatch(sessions);

        return sessions.OrderByDescending(s => s.Created).ToList();
    }

    public static string? CapturePaneContent(string sessionName, int lines = 500) =>
        RunTmux("capture-pane", "-t", sessionName, "-p", "-e", "-S", $"-{lines}");

    // Number of consecutive stable polls before marking as "waiting for input"
    private const int _stableThreshold = 1;

    public static void DetectWaitingForInputBatch(List<TmuxSession> sessions)
    {
        if (sessions.Count == 0)
            return;

        foreach (var session in sessions)
        {
            // Capture last 20 lines without ANSI codes
            var output = RunTmux("capture-pane", "-t", session.Name, "-p", "-S", "-20");
            if (output == null)
            {
                session.IsWaitingForInput = true;
                continue;
            }

            // Strip the status bar (last non-empty line contains the timer that
            // updates continuously). Compare everything above it between polls.
            var contentHash = HashContentAboveStatusBar(output);

            if (contentHash == session.PreviousContentHash)
                session.StableContentCount++;
            else
            {
                session.StableContentCount = 0;
                session.PreviousContentHash = contentHash;
            }

            // Content unchanged for consecutive polls → waiting for input
            session.IsWaitingForInput = session.StableContentCount >= _stableThreshold;
        }
    }

    private static string HashContentAboveStatusBar(string paneOutput)
    {
        var lines = paneOutput.Split('\n');

        // Find the last non-empty line (the status bar) and exclude it.
        // The status bar contains a continuously updating timer, so it always changes.
        var lastNonEmpty = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                lastNonEmpty = i;
                break;
            }

        // Use everything above the status bar for comparison
        var end = lastNonEmpty >= 0 ? lastNonEmpty : lines.Length;
        return string.Join('\n', lines.AsSpan(0, end));
    }

    private static void DetectGitInfo(TmuxSession session)
    {
        if (session.CurrentPath == null)
            return;

        var branch = RunGit(session.CurrentPath, "rev-parse", "--abbrev-ref", "HEAD");
        if (branch == null)
            return;

        session.GitBranch = branch;

        // A worktree's .git is a file pointing to the main repo's worktrees dir,
        // so git-dir will contain "/worktrees/" for linked worktrees
        var gitDir = RunGit(session.CurrentPath, "rev-parse", "--git-dir");
        session.IsWorktree = gitDir?.Contains("/worktrees/") == true;
    }

    public static void ApplyStatusColor(string sessionName, string? spectreColor)
    {
        if (string.IsNullOrWhiteSpace(spectreColor))
            return;

        try
        {
            var color = Style.Parse(spectreColor).Foreground;
            var hex = $"#{color.R:x2}{color.G:x2}{color.B:x2}";
            RunTmux("set-option", "-t", sessionName, "status-style", $"bg={hex},fg=white");
        }
        catch
        {
            // Invalid color name — skip silently
        }
    }

    public static string? CreateSession(string name, string workingDirectory)
    {
        // Use a login shell to ensure the user's full PATH is loaded (e.g. npm-installed claude on WSL2)
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        var (success, error) = RunTmuxWithError("new-session", "-d", "-s", name, "-n", name, "-c", workingDirectory, $"{shell} -lc claude");
        if (!success)
            return error ?? "Failed to create tmux session";
        // Prevent tmux from renaming the window to the running command
        RunTmux("set-option", "-t", name, "automatic-rename", "off");
        return null;
    }

    public static void AttachSession(string name)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tmux",
            ArgumentList =
            {
                "attach-session",
                "-t",
                name
            },
            UseShellExecute = false,
        };

        try
        {
            var process = Process.Start(startInfo);
            process?.WaitForExit();
        }
        catch
        {
            // attach failed silently
        }
    }

    public static string? SendKeys(string sessionName, string text)
    {
        // send-keys with -l sends literal text (no key name interpretation),
        // then a separate Enter keypress to submit
        var (success, error) = RunTmuxWithError("send-keys", "-t", sessionName, "-l", text);
        if (!success)
            return error ?? "Failed to send keys";

        RunTmux("send-keys", "-t", sessionName, "Enter");
        return null;
    }

    public static string? KillSession(string name)
    {
        var (success, error) = RunTmuxWithError("kill-session", "-t", name);
        return success ? null : error ?? "Failed to kill session";
    }

    public static string? RenameSession(string oldName, string newName)
    {
        var (success, error) = RunTmuxWithError("rename-session", "-t", oldName, newName);
        return success ? null : error ?? "Failed to rename session";
    }

    public static bool HasTmux()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tmux",
                Arguments = "-V",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            var process = Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasClaude()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var process = Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsInsideTmux() => Environment.GetEnvironmentVariable("TMUX") != null;

    private static string? RunGit(string workingDirectory, params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 ? output.TrimEnd() : null;
        }
        catch
        {
            return null;
        }
    }

    private static (bool Success, string? Error) RunTmuxWithError(params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tmux",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            var process = Process.Start(startInfo);
            if (process == null)
                return (false, "Failed to start tmux");

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
                return (true, null);

            var error = stderr.Trim();
            return (false, string.IsNullOrEmpty(error) ? "tmux exited with an error" : error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? RunTmux(params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tmux",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 ? output.TrimEnd() : null;
        }
        catch
        {
            return null;
        }
    }
}
