using System.Diagnostics;
using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.Services;

public abstract class TmuxService
{
    public static List<TmuxSession> ListSessions()
    {
        var output = RunTmux("list-sessions", "-F", "#{session_name}\t#{session_created}\t#{session_attached}\t#{session_windows}\t#{pane_current_path}");
        if (output == null) return [];

        var sessions = new List<TmuxSession>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4) continue;

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

        // Enable silence monitoring, detect input state, and detect git info
        foreach (var session in sessions)
        {
            EnableSilenceMonitoring(session.Name);
            DetectWaitingForInput(session);
            DetectGitInfo(session);
        }

        return sessions.OrderByDescending(s => s.Created).ToList();
    }

    public static string? CapturePaneContent(string sessionName, int lines = 500)
    {
        return RunTmux("capture-pane", "-t", sessionName, "-p", "-S", $"-{lines}");
    }

    public static void DetectWaitingForInput(TmuxSession session)
    {
        // Use tmux's monitor-silence flag: when a pane has no output for N seconds,
        // silence_flag = 1. This means Claude is done working and waiting for input.
        var output = RunTmux("list-windows", "-t", session.Name, "-F", "#{window_silence_flag}");
        session.IsWaitingForInput = output?.Trim() == "1";
    }

    private static void DetectGitInfo(TmuxSession session)
    {
        if (session.CurrentPath == null) return;

        var branch = RunGit(session.CurrentPath, "rev-parse", "--abbrev-ref", "HEAD");
        if (branch == null) return;

        session.GitBranch = branch;

        // A worktree's .git is a file pointing to the main repo's worktrees dir,
        // so git-dir will contain "/worktrees/" for linked worktrees
        var gitDir = RunGit(session.CurrentPath, "rev-parse", "--git-dir");
        session.IsWorktree = gitDir?.Contains("/worktrees/") == true;
    }

    private static void EnableSilenceMonitoring(string sessionName, int silenceSeconds = 3) => 
        RunTmux("set-option", "-t", sessionName, "monitor-silence", silenceSeconds.ToString());

    public static bool CreateSession(string name, string workingDirectory)
    {
        var result = RunTmux("new-session", "-d", "-s", name, "-c", workingDirectory, "claude");
        return result != null;
    }

    public static void AttachSession(string name)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tmux",
            ArgumentList = { "attach-session", "-t", name },
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

    public static bool SendKeys(string sessionName, string text)
    {
        // send-keys with -l sends literal text (no key name interpretation),
        // then a separate Enter keypress to submit
        var result = RunTmux("send-keys", "-t", sessionName, "-l", text);
        if (result == null) return false;
        RunTmux("send-keys", "-t", sessionName, "Enter");
        return true;
    }

    public static void KillSession(string name)
    {
        RunTmux("kill-session", "-t", name);
    }

    public static bool RenameSession(string oldName, string newName)
    {
        var result = RunTmux("rename-session", "-t", oldName, newName);
        return result != null;
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

    public static bool IsInsideTmux()
    {
        return Environment.GetEnvironmentVariable("TMUX") != null;
    }

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
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 ? output.TrimEnd() : null;
        }
        catch
        {
            return null;
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
            if (process == null) return null;

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
