using System.Diagnostics;
using System.Text.RegularExpressions;
using ClaudeCommandCenter.Models;
using Spectre.Console;

namespace ClaudeCommandCenter.Services;

public partial class TmuxBackend : ISessionBackend
{
    public List<Session> ListSessions()
    {
        var output = RunTmux("list-sessions", "-F", "#{session_name}\t#{session_created}\t#{session_attached}\t#{session_windows}\t#{pane_current_path}\t#{pane_dead}");
        if (output == null)
            return [];

        var sessions = new List<Session>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4)
                continue;

            var session = new Session
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

        foreach (var session in sessions)
            GitService.DetectGitInfo(session);

        return sessions.OrderBy(s => s.Created).ThenBy(s => s.Name).ToList();
    }

    public string? CreateSession(string name, string workingDirectory)
    {
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        var (success, error) = RunTmuxWithError("new-session", "-d", "-s", name, "-n", name, "-c", workingDirectory, $"{shell} -lc claude");
        if (!success)
            return error ?? "Failed to create tmux session";
        RunTmux("set-option", "-t", name, "automatic-rename", "off");
        return null;
    }

    public string? KillSession(string name)
    {
        var (success, error) = RunTmuxWithError("kill-session", "-t", name);
        return success ? null : error ?? "Failed to kill session";
    }

    public string? RenameSession(string oldName, string newName)
    {
        var (success, error) = RunTmuxWithError("rename-session", "-t", oldName, newName);
        return success ? null : error ?? "Failed to rename session";
    }

    public void AttachSession(string name)
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

    public void DetachSession()
    {
        // No-op — tmux handles detach via Ctrl-b d
    }

    public string? SendKeys(string sessionName, string text)
    {
        var (success, error) = RunTmuxWithError("send-keys", "-t", sessionName, "-l", text);
        if (!success)
            return error ?? "Failed to send keys";

        RunTmux("send-keys", "-t", sessionName, "Enter");
        return null;
    }

    public string? CapturePaneContent(string sessionName, int lines = 500) =>
        RunTmux("capture-pane", "-t", sessionName, "-p", "-e", "-S", $"-{lines}");

    public void ResizeWindow(string sessionName, int width, int height) =>
        RunTmux("resize-window", "-t", sessionName, "-x", width.ToString(), "-y", height.ToString());

    public void ResetWindowSize(string sessionName) =>
        RunTmux("set-option", "-u", "-t", sessionName, "window-size");

    public void ApplyStatusColor(string sessionName, string? spectreColor)
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

    // Number of consecutive stable polls before marking as "waiting for input"
    // 4 polls × 500ms = 2 seconds — avoids false positives from short pauses between tool calls
    private const int StableThreshold = 4;

    public void DetectWaitingForInputBatch(List<Session> sessions)
    {
        if (sessions.Count == 0)
            return;

        foreach (var session in sessions)
        {
            if (session.IsDead)
            {
                session.IsWaitingForInput = false;
                session.IsIdle = false;
                continue;
            }

            var hookState = HookStateService.ReadState(session.Name);
            if (hookState != null)
            {
                session.IsWaitingForInput = hookState == "waiting";
                session.IsIdle = hookState == "idle";
                continue;
            }

            DetectWaitingByPaneContent(session);
        }
    }

    public bool IsAvailable()
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

    public bool IsInsideHost() => Environment.GetEnvironmentVariable("TMUX") != null;

    public bool HasClaude()
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

    private void DetectWaitingByPaneContent(Session session)
    {
        var output = RunTmux("capture-pane", "-t", session.Name, "-p", "-S", "-20");
        if (output == null)
        {
            session.IsWaitingForInput = true;
            return;
        }

        var content = GetContentAboveStatusBar(output);

        if (content == session.PreviousContent)
            session.StableContentCount++;
        else
        {
            session.StableContentCount = 0;
            session.PreviousContent = content;
        }

        var isStable = session.StableContentCount >= StableThreshold;
        session.IsIdle = isStable && IsIdlePrompt(content);
        session.IsWaitingForInput = isStable && !session.IsIdle;
    }

    /// <summary>
    /// Detects the Claude Code idle prompt: a ❯ line between two ─ separator lines.
    /// Returns false if Claude's last message ends with '?' (asking a question).
    /// </summary>
    private static bool IsIdlePrompt(string content)
    {
        var lines = content.Split('\n');

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

        var rule = lines[bottomSep].Trim();
        if (rule.Length < 3 || rule.Any(c => c != '─'))
            return false;

        if (!lines[prompt].TrimStart().StartsWith('❯'))
            return false;

        for (var i = prompt - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var trimmed = lines[i].Trim();
            if (trimmed.Length >= 3 && trimmed.All(c => c == '─'))
            {
                for (var j = i - 1; j >= 0; j--)
                {
                    if (string.IsNullOrWhiteSpace(lines[j]))
                        continue;
                    var line = lines[j].TrimStart();
                    if (line.StartsWith('⎿') || line.StartsWith('…') || line.StartsWith('❯')
                        || line.StartsWith('●') || line.StartsWith('✻'))
                        continue;
                    return !line.TrimEnd().EndsWith('?');
                }

                return true;
            }

            return true;
        }

        return true;
    }

    private static readonly Regex StatusBarTimerPattern = StatusBarTimerRegex();

    private static string GetContentAboveStatusBar(string paneOutput)
    {
        var lines = paneOutput.Split('\n');

        var statusBarIndex = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            if (StatusBarTimerPattern.IsMatch(lines[i]))
            {
                statusBarIndex = i;
                break;
            }
        }

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
