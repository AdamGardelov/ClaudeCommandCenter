# Remote Sessions Design

Run Claude Code sessions on remote machines via SSH while managing them from the local CCC dashboard. Remote sessions behave identically to local sessions — same directory picker, same git info, same worktree creation, same capture/attach/detach — the only difference is the process runs over SSH.

## Config Model

New `remoteHosts` array in `~/.ccc/config.json`:

```json
{
  "remoteHosts": [
    {
      "name": "SUPERCOMPUTER",
      "host": "adam@supercomputer.example.com",
      "worktreeBasePath": "~/worktrees",
      "favoriteFolders": [
        { "name": "Core", "path": "~/Dev/Wint/Core" },
        { "name": "Salary", "path": "~/Dev/Wint/Wint.Salary" }
      ]
    },
    {
      "name": "MODERMODEMET",
      "host": "adam@modem.local",
      "worktreeBasePath": "~/worktrees",
      "favoriteFolders": [
        { "name": "Core", "path": "~/projects/Core" }
      ]
    }
  ]
}
```

New per-session tracking in existing config dictionaries:

```json
{
  "sessionRemoteHosts": {
    "core-remote": "SUPERCOMPUTER"
  }
}
```

Maps session name to remote host name. Absent = local session.

## Session Model

One new field on `Session`:

```csharp
public string? RemoteHostName { get; set; }  // null = local
```

Populated from `config.SessionRemoteHosts` when sessions are loaded.

## Backend Changes

The backend interface (`ISessionBackend`) stays unchanged in shape. The difference is purely in what command gets launched.

`CreateSession` gets one new parameter:

```csharp
string? CreateSession(string name, string workingDirectory,
    string? claudeConfigDir = null, string? remoteHost = null);
```

**TmuxBackend** — currently runs `bash -lc claude`. For remote: `ssh -t host 'cd path && claude'`.

**ConPtyBackend** — currently runs `claude.exe`. For remote: `ssh -t host 'cd path && claude'`.

Everything else (attach, detach, capture, send-keys, kill) works unchanged because tmux/ConPTY don't care what process is running inside.

## Session Creation Flow

When the user presses `n`:

**Step 0 — Target** (only shown if `remoteHosts` is non-empty):

```
Where to run?
  Local
  SUPERCOMPUTER
  MODERMODEMET
```

If no remote hosts configured, this step is skipped entirely. Existing behavior preserved.

**Step 1 — Directory:**

- If **Local**: shows local `favoriteFolders`, including worktree entries and "Custom path..."
- If **Remote**: shows that host's `favoriteFolders`, including worktree entries and "Custom path..."

**Steps 2-4** — Name, Description, Color — unchanged.

After creation, `config.SessionRemoteHosts[name]` is persisted so CCC remembers the association across restarts.

Step count goes from 4 to 5 when remotes exist. Step dots update accordingly.

## Remote Command Execution

### SshService

New static service for running commands locally or remotely:

```csharp
public static class SshService
{
    // Run a command locally or remotely depending on session
    public static (bool success, string output) Run(
        string? remoteHost, string command);

    // Build the shell command for session creation
    public static string BuildSessionCommand(
        string? remoteHost, string workingDirectory);
}
```

**`Run`** — if `remoteHost` is null, runs locally. If set, runs `ssh host 'command'`. Used by GitService for branch detection, worktree creation, repo checks.

**`BuildSessionCommand`** — returns:

- Local: `bash -lc claude`
- Remote: `ssh -t adam@supercomputer.example.com 'cd ~/Dev/Wint/Core && claude'`

### GitService Changes

GitService methods get an optional `string? remoteHost` parameter. They route through `SshService.Run()` instead of `Process.Start` directly. When `remoteHost` is null, behavior is identical to today.

### Polling Cadence

Git info for remote sessions polls on a slower cadence (~5 seconds instead of every 500ms tick) to avoid SSH overhead.

## Remote Worktree Creation

When the user picks a worktree favorite folder on a remote host:

1. Prompt for branch/feature name
2. `GitService.FetchPrune(repoPath, remoteHost)` — runs `git fetch --prune` on the remote
3. `GitService.CreateWorktree(repoPath, destPath, branchName, remoteHost)` — runs `git worktree add` on the remote
4. Write `.feature-context.json` on the remote via `SshService.Run()`

The `worktreeBasePath` from the remote host config determines where worktrees land on the remote machine.

The git repo detection for showing the worktree icon runs via SSH and can be cached after first load.

## Files to Change

### New Files

| File | Purpose |
|------|---------|
| `Services/SshService.cs` | Static helper — `Run()`, `BuildSessionCommand()` |
| `Models/RemoteHost.cs` | Config model — `Name`, `Host`, `WorktreeBasePath`, `FavoriteFolders` |

### Modified Files

| File | Change |
|------|--------|
| `Models/CccConfig.cs` | Add `RemoteHosts` list, `SessionRemoteHosts` dictionary |
| `Models/Session.cs` | Add `RemoteHostName` property |
| `Services/ISessionBackend.cs` | Add `remoteHost` parameter to `CreateSession` |
| `Services/TmuxBackend.cs` | Use `SshService.BuildSessionCommand()` when `remoteHost` is set |
| `Services/ConPtyBackend.cs` | Same — build SSH command for remote sessions |
| `Services/GitService.cs` | Route commands through `SshService.Run()` with optional `remoteHost` |
| `Handlers/SessionHandler.cs` | Add target picker step, pass remote host through the flow |
| `Handlers/FlowHelper.cs` | Support remote favorite folders and remote worktree creation in `PickDirectory` |
| `App.cs` | Pass `remoteHost` from config to git detection, slower poll cadence for remote sessions |
| `UI/SettingsDefinition.cs` | Settings entries for managing remote hosts |

### Unchanged

`Renderer.cs`, `AnsiParser.cs`, `KeyBindingService.cs`, `UpdateChecker.cs`, `RingBuffer.cs`
