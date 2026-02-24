using System.Diagnostics;
using System.Text.RegularExpressions;
using ClaudeCommandCenter.Models;
using Spectre.Console;

namespace ClaudeCommandCenter.Services;

public abstract partial class TmuxService
{
    public static List<TmuxSession> ListSessions()
    {
        var output = RunTmux("list-sessions", "-F", "#{session_name}\t#{session_created}\t#{session_attached}\t#{session_windows}\t#{pane_current_path}\t#{pane_dead}");
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
                IsDead = parts.Length > 5 && parts[5] == "1",
            };

            if (long.TryParse(parts[1], out var epoch))
                session.Created = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;

            sessions.Add(session);
        }

        // Detect git info (input state is tracked by App.cs across polls)
        foreach (var session in sessions)
            DetectGitInfo(session);

        return sessions.OrderBy(s => s.Created).ThenBy(s => s.Name).ToList();
    }

    public static string? CapturePaneContent(string sessionName, int lines = 500) =>
        RunTmux("capture-pane", "-t", sessionName, "-p", "-e", "-S", $"-{lines}");

    public static void ResetWindowSize(string sessionName) =>
        RunTmux("set-option", "-u", "-t", sessionName, "window-size");

    public static void ResizeWindow(string sessionName, int width, int height) =>
        RunTmux("resize-window", "-t", sessionName, "-x", width.ToString(), "-y", height.ToString());

    // Number of consecutive stable polls before marking as "waiting for input"
    // 4 polls × 500ms = 2 seconds — avoids false positives from short pauses between tool calls
    private const int _stableThreshold = 4;

    public static void DetectWaitingForInputBatch(List<TmuxSession> sessions)
    {
        if (sessions.Count == 0)
            return;

        foreach (var session in sessions)
        {
            // Dead panes have no running process — skip content hashing
            if (session.IsDead)
            {
                session.IsWaitingForInput = false;
                continue;
            }

            // Capture last 20 lines without ANSI codes
            var output = RunTmux("capture-pane", "-t", session.Name, "-p", "-S", "-20");
            if (output == null)
            {
                session.IsWaitingForInput = true;
                continue;
            }

            // Strip the status bar (last non-empty line contains the timer that
            // updates continuously). Compare everything above it between polls.
            var content = GetContentAboveStatusBar(output);

            if (content == session.PreviousContent)
                session.StableContentCount++;
            else
            {
                session.StableContentCount = 0;
                session.PreviousContent = content;
            }

            // Content unchanged for consecutive polls → stable.
            // Distinguish idle (at ❯ prompt, done working) from genuinely waiting for input.
            var isStable = session.StableContentCount >= _stableThreshold;
            session.IsIdle = isStable && IsIdlePrompt(content);
            session.IsWaitingForInput = isStable && !session.IsIdle;
        }
    }

    /// <summary>
    /// Detects the Claude Code idle prompt: a ❯ line between two ─ separator lines.
    /// Returns false if Claude's last message ends with '?' (asking a question).
    /// </summary>
    private static bool IsIdlePrompt(string content)
    {
        var lines = content.Split('\n');

        // Find the last two non-empty lines: bottom ─ separator and ❯ prompt
        int bottomSep = -1, prompt = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            if (bottomSep < 0)
                bottomSep = i;
            else
            {
                prompt = i;
                break;
            }
        }

        if (prompt < 0)
            return false;

        // Last non-empty line: horizontal rule (all ─)
        var rule = lines[bottomSep].Trim();
        if (rule.Length < 3 || rule.Any(c => c != '─'))
            return false;

        // Line above: starts with ❯ (Claude Code's input prompt)
        if (!lines[prompt].TrimStart().StartsWith('❯'))
            return false;

        // Found the idle prompt. Scan upward for the top ─ separator,
        // then check if Claude's last message above it ends with '?'
        for (var i = prompt - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var trimmed = lines[i].Trim();
            if (trimmed.Length >= 3 && trimmed.All(c => c == '─'))
            {
                // Found top separator — find Claude's last message above it.
                // Skip tool output (⎿), collapsed lines (…), user input (❯),
                // tool headers (●), and thinking markers (✻).
                for (var j = i - 1; j >= 0; j--)
                {
                    if (string.IsNullOrWhiteSpace(lines[j]))
                        continue;
                    var line = lines[j].TrimStart();
                    if (line.StartsWith('⎿') || line.StartsWith('…') || line.StartsWith('❯')
                        || line.StartsWith('●') || line.StartsWith('✻'))
                        continue;
                    // If Claude's last message ends with ?, it's asking a question
                    return !line.TrimEnd().EndsWith('?');
                }
                return true; // No content above separator (fresh session)
            }

            return true; // No top separator — still idle
        }

        return true;
    }

    // Matches status bar timer suffixes like "45s", "24m24s", "1h2m", "1h30m24s"
    private static readonly Regex _statusBarTimerPattern = StatusBarTimerRegex();

    private static string GetContentAboveStatusBar(string paneOutput)
    {
        var lines = paneOutput.Split('\n');

        // Scan from bottom up for the status bar: the first non-empty line ending with a timer.
        // This correctly skips mode indicator lines (e.g., "⏸ plan mode on") that may appear below the status bar.
        var statusBarIndex = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            if (_statusBarTimerPattern.IsMatch(lines[i]))
            {
                statusBarIndex = i;
                break;
            }
        }

        // Strip the status bar and everything below it (mode indicators, empty lines).
        // Fall back to stripping the last non-empty line if no timer pattern found.
        if (statusBarIndex >= 0)
            return string.Join('\n', lines.AsSpan(0, statusBarIndex));

        var lastNonEmpty = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                lastNonEmpty = i;
                break;
            }

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

    [GeneratedRegex(@"\d+[hms]\d*[ms]?\s*$", RegexOptions.Compiled)]
    private static partial Regex StatusBarTimerRegex();
}
