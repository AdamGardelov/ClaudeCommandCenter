using System.Diagnostics;
using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.Services;

public static class GitService
{
    public static bool IsGitRepo(string path)
    {
        var gitPath = Path.Combine(path, ".git");
        return File.Exists(gitPath) || Directory.Exists(gitPath);
    }

    /// <summary>
    /// Creates a git worktree at the specified destination with a new branch.
    /// </summary>
    /// <returns>null on success, error message on failure</returns>
    public static string? CreateWorktree(string repoPath, string worktreeDest, string branchName)
    {
        var (success, output) = RunGit(repoPath, "worktree", "add", "-b", branchName, worktreeDest);
        return success ? null : output ?? "Failed to create worktree";
    }

    /// <summary>
    /// Fetches with prune to clean up deleted remote branches before creating worktrees.
    /// </summary>
    public static void FetchPrune(string repoPath) =>
        RunGit(repoPath, "fetch", "--prune");

    /// <summary>
    /// Sanitizes a string for use as a git branch name.
    /// </summary>
    public static string SanitizeBranchName(string name)
    {
        var sanitized = name
            .Replace(' ', '-')
            .Replace('_', '-')
            .Replace("..", "-")
            .Replace("~", "")
            .Replace("^", "")
            .Replace(":", "")
            .Replace("\\", "")
            .TrimStart('-', '/')
            .TrimEnd('-', '/', '.');

        // Collapse multiple hyphens
        while (sanitized.Contains("--"))
            sanitized = sanitized.Replace("--", "-");

        return sanitized.ToLowerInvariant();
    }

    public static string? GetCurrentCommitSha(string repoPath)
    {
        var (success, output) = RunGit(repoPath, "rev-parse", "HEAD");
        return success ? output : null;
    }

    public static string? GetDiffStat(string repoPath, string sinceCommit)
    {
        var (success, output) = RunGit(repoPath, "diff", "--stat", sinceCommit);
        return success ? output : null;
    }

    public static string? GetFullDiff(string repoPath, string sinceCommit, int maxLines = 5000)
    {
        var (success, output) = RunGit(repoPath, "diff", sinceCommit);
        if (!success || string.IsNullOrWhiteSpace(output))
            return null;

        var lines = output.Split('\n');
        if (lines.Length <= maxLines)
            return output;

        return string.Join('\n', lines.Take(maxLines)) + $"\n\n... truncated ({lines.Length - maxLines} more lines)";
    }

    public static void DetectGitInfo(Session session)
    {
        if (session.CurrentPath == null)
            return;

        var (branchOk, branch) = RunGit(session.CurrentPath, "rev-parse", "--abbrev-ref", "HEAD");
        if (!branchOk || branch == null)
            return;

        session.GitBranch = branch;

        // A worktree's .git is a file pointing to the main repo's worktrees dir,
        // so git-dir will contain "/worktrees/" for linked worktrees
        var (_, gitDir) = RunGit(session.CurrentPath, "rev-parse", "--git-dir");
        session.IsWorktree = gitDir?.Contains("/worktrees/") == true;
    }

    public static void DetectGitInfo(Session session, string? remoteHost)
    {
        if (remoteHost == null)
        {
            DetectGitInfo(session);
            return;
        }

        if (session.CurrentPath == null)
            return;

        var (branchOk, branch) = RunGitRemote(remoteHost, session.CurrentPath, "rev-parse", "--abbrev-ref", "HEAD");
        if (!branchOk || branch == null)
            return;

        session.GitBranch = branch;
        var (_, gitDir) = RunGitRemote(remoteHost, session.CurrentPath, "rev-parse", "--git-dir");
        session.IsWorktree = gitDir?.Contains("/worktrees/") == true;
    }

    public static string? CreateWorktree(string repoPath, string worktreeDest, string branchName, string? remoteHost)
    {
        if (remoteHost == null)
            return CreateWorktree(repoPath, worktreeDest, branchName);

        // Create parent directory on remote
        var parentDir = worktreeDest[..worktreeDest.LastIndexOf('/')];
        SshService.Run(remoteHost, $"mkdir -p {SshService.EscapePath(parentDir)}");

        var (success, output) = RunGitRemote(remoteHost, repoPath, "worktree", "add", "-b", branchName, worktreeDest);
        return success ? null : output ?? "Failed to create worktree";
    }

    public static void FetchPrune(string repoPath, string? remoteHost)
    {
        if (remoteHost == null)
        {
            FetchPrune(repoPath);
            return;
        }

        RunGitRemote(remoteHost, repoPath, "fetch", "--prune");
    }

    public static string? GetCurrentCommitSha(string repoPath, string? remoteHost)
    {
        if (remoteHost == null)
            return GetCurrentCommitSha(repoPath);

        var (success, output) = RunGitRemote(remoteHost, repoPath, "rev-parse", "HEAD");
        return success ? output : null;
    }

    private static (bool Success, string? Output) RunGit(string workingDirectory, params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, "Failed to start git");

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

    private static (bool Success, string? Output) RunGitRemote(string remoteHost, string workingDirectory, params string[] args)
    {
        var gitArgs = string.Join(" ", args);
        return SshService.Run(remoteHost, $"git -C {SshService.EscapePath(workingDirectory)} {gitArgs}");
    }
}
