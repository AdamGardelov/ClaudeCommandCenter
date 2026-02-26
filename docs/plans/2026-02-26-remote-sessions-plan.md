# Remote Sessions Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Allow CCC to create and manage Claude Code sessions on remote machines via SSH, with full parity to local sessions (directory picker, git info, worktree creation).

**Architecture:** Both backends (TmuxBackend, ConPtyBackend) change only what command they launch — `ssh -t host 'cd path && claude'` instead of the local claude command. A new `SshService` provides a unified way to run commands locally or remotely. GitService methods gain an optional `remoteHost` parameter to route git commands through SSH when needed.

**Tech Stack:** .NET 10, Spectre.Console, SSH (via `Process.Start("ssh", ...)`), JSON config

---

### Task 1: Add RemoteHost model

**Files:**
- Create: `Models/RemoteHost.cs`

**Step 1: Create the model file**

```csharp
namespace ClaudeCommandCenter.Models;

public class RemoteHost
{
    public required string Name { get; set; }
    public required string Host { get; set; }
    public string WorktreeBasePath { get; set; } = "~/worktrees";
    public List<FavoriteFolder> FavoriteFolders { get; set; } = [];
}
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 3: Commit**

```
feat: add RemoteHost model
```

---

### Task 2: Add remote fields to CccConfig and Session

**Files:**
- Modify: `Models/CccConfig.cs`
- Modify: `Models/Session.cs`

**Step 1: Add RemoteHosts and SessionRemoteHosts to CccConfig**

In `Models/CccConfig.cs`, add two new properties:

```csharp
public List<RemoteHost> RemoteHosts { get; set; } = [];
public Dictionary<string, string> SessionRemoteHosts { get; set; } = new();
```

**Step 2: Add RemoteHostName to Session**

In `Models/Session.cs`, add:

```csharp
public string? RemoteHostName { get; set; }
```

**Step 3: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 4: Commit**

```
feat: add remote host config and session tracking fields
```

---

### Task 3: Create SshService

**Files:**
- Create: `Services/SshService.cs`

**Step 1: Create SshService with Run and BuildSessionCommand**

```csharp
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
        if (remoteHost == null)
            return RunLocal(command);

        return RunSsh(remoteHost, command);
    }

    /// <summary>
    /// Builds the shell command string for launching a Claude session.
    /// Local: "bash -lc claude" (or equivalent)
    /// Remote: "ssh -t host 'cd path && claude'"
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

    private static string EscapePath(string path) =>
        $"'{path.Replace("'", "'\\''")}'";
}
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 3: Commit**

```
feat: add SshService for local/remote command execution
```

---

### Task 4: Add remoteHost parameter to GitService

**Files:**
- Modify: `Services/GitService.cs`

The key insight: GitService currently uses a private `RunGit(workingDirectory, args)` method that runs git locally. We need to add overloads that accept `string? remoteHost` and route through SSH when set.

**Step 1: Add remote-aware RunGit overload**

Add a new private method at the bottom of `GitService`:

```csharp
private static (bool Success, string? Output) RunGit(string? remoteHost, string workingDirectory, params string[] args)
{
    if (remoteHost == null)
        return RunGit(workingDirectory, args);

    var gitArgs = string.Join(" ", args.Select(a => a.Contains(' ') ? $"'{a}'" : a));
    return SshService.Run(remoteHost, $"git -C {EscapePath(workingDirectory)} {gitArgs}");
}

private static string EscapePath(string path) =>
    $"'{path.Replace("'", "'\\''")}'";
```

**Step 2: Add remote-aware public method overloads**

Add these overloads (keeping existing methods unchanged for backward compat):

```csharp
public static void DetectGitInfo(Session session, string? remoteHost)
{
    var path = session.CurrentPath;
    if (path == null)
        return;

    var (branchOk, branch) = RunGit(remoteHost, path, "rev-parse", "--abbrev-ref", "HEAD");
    if (!branchOk || branch == null)
        return;

    session.GitBranch = branch;
    var (_, gitDir) = RunGit(remoteHost, path, "rev-parse", "--git-dir");
    session.IsWorktree = gitDir?.Contains("/worktrees/") == true;
}

public static string? CreateWorktree(string repoPath, string worktreeDest, string branchName, string? remoteHost)
{
    if (remoteHost == null)
        return CreateWorktree(repoPath, worktreeDest, branchName);

    // Create parent directory on remote
    var parentDir = worktreeDest[..worktreeDest.LastIndexOf('/')];
    SshService.Run(remoteHost, $"mkdir -p {EscapePath(parentDir)}");

    var (success, output) = RunGit(remoteHost, repoPath, "worktree", "add", "-b", branchName, worktreeDest);
    return success ? null : output ?? "Failed to create worktree";
}

public static void FetchPrune(string repoPath, string? remoteHost)
{
    if (remoteHost == null)
    {
        FetchPrune(repoPath);
        return;
    }

    RunGit(remoteHost, repoPath, "fetch", "--prune");
}

public static string? GetCurrentCommitSha(string repoPath, string? remoteHost)
{
    if (remoteHost == null)
        return GetCurrentCommitSha(repoPath);

    var (success, output) = RunGit(remoteHost, repoPath, "rev-parse", "HEAD");
    return success ? output : null;
}
```

**Step 3: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 4: Commit**

```
feat: add remote-aware overloads to GitService
```

---

### Task 5: Update ISessionBackend and TmuxBackend

**Files:**
- Modify: `Services/ISessionBackend.cs`
- Modify: `Services/TmuxBackend.cs`

**Step 1: Add remoteHost parameter to ISessionBackend.CreateSession**

In `Services/ISessionBackend.cs`, change line 9:

```csharp
string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null, string? remoteHost = null);
```

**Step 2: Update TmuxBackend.CreateSession**

In `Services/TmuxBackend.cs`, update the `CreateSession` method signature and command construction:

```csharp
public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null, string? remoteHost = null)
{
    var envArgs = new List<string> { "-e", $"CCC_SESSION_NAME={name}" };
    if (!string.IsNullOrEmpty(claudeConfigDir))
    {
        envArgs.Add("-e");
        envArgs.Add($"CLAUDE_CONFIG_DIR={claudeConfigDir}");
    }

    var (cmdFile, cmdArgs) = SshService.BuildSessionCommand(remoteHost, workingDirectory);
    var shellCommand = remoteHost != null
        ? $"{cmdFile} {string.Join(" ", cmdArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}"
        : $"{cmdFile} {string.Join(" ", cmdArgs)}";

    var args = new List<string> { "new-session", "-d", "-s", name, "-n", name };
    args.AddRange(envArgs);

    // For remote sessions, tmux working dir is irrelevant (we cd on the remote),
    // but we still set it to $HOME for a sane fallback
    var tmuxWorkDir = remoteHost != null
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : workingDirectory;

    args.AddRange(["-c", tmuxWorkDir, shellCommand]);

    var (success, error) = RunTmuxWithError(args.ToArray());
    if (!success)
        return error ?? "Failed to create tmux session";
    RunTmux("set-option", "-t", name, "automatic-rename", "off");
    return null;
}
```

**Step 3: Update TmuxBackend.ListSessions to use remote-aware git detection**

In `TmuxBackend.ListSessions()`, the call to `GitService.DetectGitInfo(session)` at line 38 needs to pass the remote host. But at this point we don't know which sessions are remote — that info comes from config.

The cleanest approach: keep `ListSessions` calling local `DetectGitInfo` as before. Then in `App.LoadSessions()`, re-detect git info for remote sessions using the remote-aware overload. This avoids the backend needing to know about config.

So **no change to ListSessions** here — we'll handle it in Task 7 (App.cs changes).

**Step 4: Build to verify**

Run: `dotnet build`
Expected: Build succeeded (ConPtyBackend will error — fixed in next task)

---

### Task 6: Update ConPtyBackend

**Files:**
- Modify: `Services/ConPty/ConPtyBackend.cs`

**Step 1: Update CreateSession signature**

Change line 54:

```csharp
public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null, string? remoteHost = null)
```

**Step 2: Update StartProcess to support remote**

Change `StartProcess` signature (line 343) and the command line construction:

```csharp
private static ConPtySession StartProcess(string name, string workingDirectory, string? claudeConfigDir, string? remoteHost)
```

Inside `StartProcess`, change the `commandLine` variable (around line 412):

```csharp
string commandLine;
string processWorkDir;
if (remoteHost != null)
{
    var (_, sshArgs) = SshService.BuildSessionCommand(remoteHost, workingDirectory);
    commandLine = $"ssh {string.Join(" ", sshArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
    processWorkDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
else
{
    commandLine = "claude";
    processWorkDir = workingDirectory;
}
```

Update the `CreateProcessW` call to use `processWorkDir` instead of `workingDirectory`.

Update the caller at line 64 to pass the new parameter:

```csharp
var session = StartProcess(name, workingDirectory, claudeConfigDir, remoteHost);
```

**Step 3: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 4: Commit**

```
feat: update both backends to support remote session creation via SSH
```

---

### Task 7: Update App.cs — LoadSessions and remote git detection

**Files:**
- Modify: `App.cs`

**Step 1: Hydrate RemoteHostName in LoadSessions**

In `App.LoadSessions()`, after the existing hydration loop (after line 262 where `StartCommitSha` is set), add remote host hydration:

```csharp
if (_config.SessionRemoteHosts.TryGetValue(s.Name, out var remoteHostName))
    s.RemoteHostName = remoteHostName;
```

**Step 2: Re-detect git info for remote sessions**

After the main `foreach` loop in `LoadSessions`, before the `startCommitsDirty` check, add remote git re-detection. Remote sessions need their git info fetched via SSH, overriding the local detection that the backend did:

```csharp
// Re-detect git info for remote sessions (backend only does local detection)
foreach (var s in _state.Sessions.Where(s => s.RemoteHostName != null))
{
    var remoteHost = _config.RemoteHosts.FirstOrDefault(h => h.Name == s.RemoteHostName);
    if (remoteHost != null)
        GitService.DetectGitInfo(s, remoteHost.Host);
}
```

**Note:** This runs synchronously on LoadSessions. For the initial implementation this is acceptable — git info for remote sessions will add SSH latency to session list refresh. We can optimize later with async/caching.

**Step 3: Handle StartCommitSha for remote sessions**

In the existing block that snapshots `StartCommitSha` (around line 253), update the `else if` to use the remote-aware version:

```csharp
else if (s.CurrentPath != null && s.GitBranch != null)
{
    var remoteHost = _config.RemoteHosts.FirstOrDefault(h => h.Name == s.RemoteHostName);
    var headSha = GitService.GetCurrentCommitSha(s.CurrentPath, remoteHost?.Host);
    if (headSha != null)
    {
        s.StartCommitSha = headSha;
        _config.SessionStartCommits[s.Name] = headSha;
        startCommitsDirty = true;
    }
}
```

**Step 4: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 5: Commit**

```
feat: hydrate remote host info and git data for remote sessions
```

---

### Task 8: Add ConfigService helpers for remote hosts

**Files:**
- Modify: `Services/ConfigService.cs`

**Step 1: Add SaveRemoteHost and RemoveRemoteHost methods**

```csharp
public static void SaveRemoteHost(CccConfig config, string sessionName, string remoteHostName)
{
    config.SessionRemoteHosts[sessionName] = remoteHostName;
    Save(config);
}

public static void RemoveRemoteHost(CccConfig config, string sessionName)
{
    if (config.SessionRemoteHosts.Remove(sessionName))
        Save(config);
}

public static void RenameRemoteHost(CccConfig config, string oldName, string newName)
{
    if (config.SessionRemoteHosts.Remove(oldName, out var host))
    {
        config.SessionRemoteHosts[newName] = host;
        Save(config);
    }
}
```

**Step 2: Update SessionHandler.Delete to clean up remote host**

In `Handlers/SessionHandler.cs`, in the `Delete()` method, after the existing `RemoveStartCommit` call (around line 84), add:

```csharp
ConfigService.RemoveRemoteHost(config, session.Name);
```

**Step 3: Update SessionHandler.Edit to rename remote host**

In `Handlers/SessionHandler.cs`, in the `Edit()` method, after the existing `RenameStartCommit` call (around line 128), add:

```csharp
ConfigService.RenameRemoteHost(config, currentName, newName);
```

**Step 4: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 5: Commit**

```
feat: add ConfigService helpers for remote host tracking
```

---

### Task 9: Add target picker to FlowHelper

**Files:**
- Modify: `Handlers/FlowHelper.cs`

**Step 1: Add PickTarget method**

Add a new method to `FlowHelper`:

```csharp
/// <summary>
/// Picks between Local and configured remote hosts.
/// Returns null for local, or the RemoteHost for remote.
/// Skips the picker entirely if no remote hosts are configured.
/// </summary>
public RemoteHost? PickTarget()
{
    if (config.RemoteHosts.Count == 0)
        return null;

    var prompt = new SelectionPrompt<string>()
        .Title("[grey70]Where to run?[/]")
        .HighlightStyle(new Style(Color.White, Color.Grey70));

    prompt.AddChoice("Local");
    foreach (var host in config.RemoteHosts)
        prompt.AddChoice(host.Name);
    prompt.AddChoice(CancelChoice);

    var selected = AnsiConsole.Prompt(prompt);

    if (selected == CancelChoice)
        throw new FlowCancelledException();
    if (selected == "Local")
        return null;

    return config.RemoteHosts.First(h => h.Name == selected);
}
```

**Step 2: Add PickDirectory overload for remote hosts**

Add a new overload that accepts a `RemoteHost`:

```csharp
public string? PickRemoteDirectory(RemoteHost remoteHost, Action<string>? onWorktreeBranchCreated = null)
{
    var favorites = remoteHost.FavoriteFolders;

    // Cache which favorites are git repos (via SSH) to show worktree icon
    var gitFavorites = new List<FavoriteFolder>();
    foreach (var fav in favorites)
    {
        if (SshService.IsGitRepo(remoteHost.Host, fav.Path))
            gitFavorites.Add(fav);
    }

    while (true)
    {
        var prompt = new SelectionPrompt<string>()
            .Title($"[grey70]Pick a directory on[/] [white]{Markup.Escape(remoteHost.Name)}[/]")
            .PageSize(15)
            .HighlightStyle(new Style(Color.White, Color.Grey70))
            .MoreChoicesText("[grey](Move up and down to reveal more)[/]");

        foreach (var fav in favorites)
            prompt.AddChoice($"{fav.Name}  [grey50]{fav.Path}[/]");

        if (gitFavorites.Count > 0)
        {
            foreach (var fav in gitFavorites)
                prompt.AddChoice($"{_worktreePrefix}{fav.Name}  [grey50](new worktree)[/]");
        }

        prompt.AddChoice(_customPathChoice);
        prompt.AddChoice(CancelChoice);

        var selected = AnsiConsole.Prompt(prompt);
        switch (selected)
        {
            case CancelChoice:
                return null;
            case _customPathChoice:
            {
                var custom = PromptCustomPath();
                if (custom != null)
                    return custom;
                continue;
            }
        }

        // Handle worktree selection
        if (selected.StartsWith(_worktreePrefix))
        {
            var repoName = selected[_worktreePrefix.Length..].Split("  ")[0];
            var fav = gitFavorites.FirstOrDefault(f => f.Name == repoName);
            if (fav == null)
                continue;

            var hint = AnsiConsole.Prompt(
                new TextPrompt<string>("[grey70]Name[/] [grey](used for branch and session)[/][grey70]:[/]")
                    .AllowEmpty()
                    .PromptStyle(new Style(Color.White)));
            if (string.IsNullOrWhiteSpace(hint))
                continue;

            var branchName = GitService.SanitizeBranchName(hint);
            var worktreeDest = $"{remoteHost.WorktreeBasePath.TrimEnd('/')}/{branchName}/{repoName}";

            string? error = null;
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(new Style(Color.Grey70))
                .Start($"[grey70]Creating worktree [white]{branchName}[/] on {Markup.Escape(remoteHost.Name)}...[/]", _ =>
                {
                    GitService.FetchPrune(fav.Path, remoteHost.Host);
                    error = GitService.CreateWorktree(fav.Path, worktreeDest, branchName, remoteHost.Host);
                });

            if (error != null)
            {
                AnsiConsole.MarkupLine($"[red]Worktree failed:[/] {Markup.Escape(error)}");
                AnsiConsole.MarkupLine("[grey](Press any key)[/]");
                Console.ReadKey(true);
                continue;
            }

            onWorktreeBranchCreated?.Invoke(branchName);
            return worktreeDest;
        }

        // Match back to the favorite by prefix
        var selectedName = selected.Split("  ")[0];
        var match = favorites.FirstOrDefault(f => f.Name == selectedName);
        return match?.Path;
    }
}
```

**Step 3: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 4: Commit**

```
feat: add target picker and remote directory picker to FlowHelper
```

---

### Task 10: Update SessionHandler.Create for remote support

**Files:**
- Modify: `Handlers/SessionHandler.cs`

**Step 1: Update the Create method**

Replace the Create method body to add the target picker step and route through remote when selected:

```csharp
public void Create(bool claudeAvailable)
{
    if (!claudeAvailable)
    {
        state.SetStatus("'claude' not found in PATH — install Claude Code first");
        return;
    }

    FlowHelper.RunFlow("New Session", () =>
    {
        var hasRemotes = config.RemoteHosts.Count > 0;
        var totalSteps = hasRemotes ? 5 : 4;
        var step = 0;

        // Step 0 (conditional): Target
        RemoteHost? remoteHost = null;
        if (hasRemotes)
        {
            FlowHelper.PrintStep(++step, totalSteps, "Target");
            remoteHost = flow.PickTarget();
        }

        // Step 1: Directory
        FlowHelper.PrintStep(++step, totalSteps, "Directory");
        string? worktreeBranch = null;
        string? dir;

        if (remoteHost != null)
        {
            dir = flow.PickRemoteDirectory(remoteHost,
                      onWorktreeBranchCreated: branch => worktreeBranch = branch)
                  ?? throw new FlowCancelledException();
        }
        else
        {
            dir = flow.PickDirectory(
                      onWorktreeBranchCreated: branch => worktreeBranch = branch)
                  ?? throw new FlowCancelledException();

            dir = ConfigService.ExpandPath(dir);
            if (!Directory.Exists(dir))
                throw new FlowCancelledException("Invalid directory");
        }

        // Step 2: Name
        FlowHelper.PrintStep(++step, totalSteps, "Name");
        var defaultName = FlowHelper.SanitizeSessionName(worktreeBranch ?? dir.Split('/').Last(s => s.Length > 0));
        var existingNames = new HashSet<string>(state.Sessions.Select(s => s.Name), StringComparer.Ordinal);
        defaultName = FlowHelper.UniqueSessionName(defaultName, existingNames, " ");
        var name = flow.PromptWithDefault("Session name", defaultName);

        // Step 3: Description
        FlowHelper.PrintStep(++step, totalSteps, "Description");
        var description = flow.PromptOptional("Description", null);

        // Step 4: Color
        FlowHelper.PrintStep(++step, totalSteps, "Color");
        var color = flow.PickColor();

        // Create session
        var claudeConfigDir = remoteHost == null
            ? ConfigService.ResolveClaudeConfigDir(config, dir)
            : null; // Claude config routing is local-only
        var error = backend.CreateSession(name, dir, claudeConfigDir, remoteHost?.Host);
        if (error != null)
            throw new FlowCancelledException(error);

        if (!string.IsNullOrWhiteSpace(description))
            ConfigService.SaveDescription(config, name, description);
        if (color != null)
            ConfigService.SaveColor(config, name, color);
        if (remoteHost != null)
            ConfigService.SaveRemoteHost(config, name, remoteHost.Name);
        backend.ApplyStatusColor(name, color ?? "grey42");
        backend.AttachSession(name);
        loadSessions();
        resetPaneCache();
    }, state);
}
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 3: Commit**

```
feat: integrate remote target selection into session creation flow
```

---

### Task 11: Update Renderer to show remote indicator

**Files:**
- Modify: `UI/Renderer.cs`

**Step 1: Find the session rendering code**

Search for where session names and descriptions are rendered in the list view. Add a remote host indicator (e.g., the host name) next to the session name.

Look for where `s.Description` or session name is rendered and add:

```csharp
if (s.RemoteHostName != null)
    // Add remote indicator, e.g., "[grey50]@ SUPERCOMPUTER[/]"
```

The exact location and markup will depend on the existing rendering code. The key is that remote sessions are visually distinguishable.

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded

**Step 3: Commit**

```
feat: show remote host indicator in session list
```

---

### Task 12: Build and manual test

**Step 1: Full build**

Run: `dotnet build`
Expected: Build succeeded, 0 errors, 0 warnings

**Step 2: Run and verify local sessions unchanged**

Run: `dotnet run`
- Press `n` — with no `remoteHosts` in config, flow should be identical to before (4 steps)
- Create a local session, verify it works
- Kill the session

**Step 3: Add a test remote host to config**

Edit `~/.ccc/config.json` and add:

```json
"remoteHosts": [
  {
    "name": "TEST-REMOTE",
    "host": "localhost",
    "worktreeBasePath": "~/worktrees",
    "favoriteFolders": [
      { "name": "CCC", "path": "~/Dev/Lab/ClaudeCommandCenter" }
    ]
  }
]
```

**Step 4: Test remote flow**

Run: `dotnet run`
- Press `n` — should now show "Where to run?" with Local and TEST-REMOTE
- Select TEST-REMOTE — should show the remote favorites
- Select CCC — should create session via `ssh localhost 'cd ... && claude'`
- Verify session appears in list with remote indicator

**Step 5: Commit**

```
feat: remote sessions — complete initial implementation
```

---

### Task 13: Update README.md

**Files:**
- Modify: `README.md`

**Step 1: Add Remote Sessions section**

Add a section documenting:
- How to configure remote hosts in `~/.ccc/config.json`
- The `remoteHosts` config format
- How the creation flow changes when remotes are configured
- That SSH key-based auth is required (no password prompts)
- Known limitations (git info may be slower for remote sessions)

**Step 2: Commit**

```
docs: add remote sessions documentation to README
```
