# Remote-Native Tmux Design

Run remote sessions with tmux living on the remote machine, not locally. Closing your laptop disconnects
your view but leaves the session running. CCC reconnects and picks up where it left off.

## Problem

The existing remote session design runs a local tmux session containing `ssh -t host 'cd path && claude'`.
If the local tmux session dies (laptop closed, CCC quit), the SSH connection drops and the remote claude
process is killed. There is no persistence.

## Solution

Flip the model: tmux runs on the remote machine. CCC manages it by sending all tmux commands over SSH,
exactly like it runs them locally today — just prefixed with `ssh -S <socket> host`.

## Architecture

```
CCC (local)
  ├── TmuxBackend          → local tmux  (unchanged)
  └── RemoteTmuxBackend    → SshControlMasterService → remote tmux
        (one instance per configured remote host)
```

App goes from a single `_backend : ISessionBackend` field to:

```csharp
Dictionary<string?, ISessionBackend> _backends
// null  → TmuxBackend (local)
// "SUPERCOMPUTER" → RemoteTmuxBackend(host)
```

A `BackendFor(session)` helper routes any operation to the right backend via `session.RemoteHostName`.

## New Components

### `SshControlMasterService` (static service)

Manages one persistent SSH ControlMaster socket per remote host.

- Socket path: `~/.ccc/ssh/<HostName>.sock`
- `EnsureConnected(host)` — starts `ssh -M -N -S <socket> host` in background if socket missing or stale
- Liveness check: `ssh -S <socket> -O check host` before each command; reconnects on failure
- `RunCommand(host, args[])` — executes via `ssh -S <socket> -o BatchMode=yes host <args>`
- `Disconnect(host)` — kills the ControlMaster process; called on `Dispose()`
- Background retry: marks host offline on failure, retries every 30s

Connections are established asynchronously at startup — CCC is immediately usable while SSH handshakes
complete.

### `RemoteTmuxBackend : ISessionBackend`

One instance per configured remote host. Mirrors `TmuxBackend` exactly — every `RunTmux(args)` call
becomes `SshControlMasterService.RunCommand(host, ["tmux", ...args])`.

| Method | Remote command |
|---|---|
| `ListSessions()` | `ssh host tmux list-sessions ...` |
| `CreateSession()` | `ssh host tmux new-session -d -s name -c path 'exec "$SHELL" -lc claude'` |
| `KillSession()` | `ssh host tmux kill-session -t name` |
| `RenameSession()` | `ssh host tmux rename-session -t old new` |
| `CapturePaneContent()` | `ssh host tmux capture-pane -t name -p -e -S -N` |
| `SendKeys()` | `ssh host tmux send-keys -t name -l text` + Enter |
| `AttachSession()` | `ssh -t host tmux attach-session -t name` — real interactive TTY, exits CCC UI |
| `ResizeWindow()` | `ssh host tmux resize-window -t name -x W -y H` |
| `DetachSession()` | no-op (tmux handles via prefix+d on the remote) |
| `ApplyStatusColor()` | `ssh host tmux set-option -t name status-style bg=hex,fg=white` |
| `IsInsideHost()` | returns false (CCC is local) |

## Session Listing & Merging

On each poll, App calls `ListSessions()` on every backend and merges results. Sessions from
`RemoteTmuxBackend` are tagged with `RemoteHostName` before being added to the merged list.

## Offline Handling

When `RemoteTmuxBackend.ListSessions()` fails:

1. Backend returns empty list and raises an `IsOffline` flag
2. App falls back to `config.CachedRemoteSessions[hostName]`
3. Those sessions are added to the list with `Session.IsOffline = true`
4. UI renders offline sessions greyed out with a `✗` prefix
5. All interactive operations (send-keys, kill, attach) are disabled for offline sessions

After every *successful* remote `ListSessions()`, the result is written to config cache:

```json
"cachedRemoteSessions": {
  "SUPERCOMPUTER": [
    { "name": "core-feature", "path": "~/Dev/Core", "created": "2026-03-07T10:00:00" }
  ]
}
```

## Error Handling

| Failure | Behaviour |
|---|---|
| `ListSessions()` SSH error | Mark host offline, use cache, retry in 30s |
| `CreateSession()` failure | Show error message to user (same as local today) |
| `SendKeys()` / `CapturePaneContent()` failure | Mark session `IsDead = true` |
| ControlMaster dies mid-session | Detect on next command, attempt one reconnect, mark offline on failure |
| SSH auth failure (`BatchMode=yes`) | Fail fast, mark offline, surface message in UI |

## Model & Config Changes

### `Session`
```csharp
public bool IsOffline { get; set; }   // new — shown greyed out in UI
// RemoteHostName already exists from original remote sessions design
```

### `CccConfig`
```csharp
public Dictionary<string, List<CachedRemoteSession>> CachedRemoteSessions { get; set; } = new();
```

### New `CachedRemoteSession` model
```csharp
public class CachedRemoteSession
{
    public required string Name { get; set; }
    public string? Path { get; set; }
    public DateTime Created { get; set; }
}
```

## Files Changed

### New Files

| File | Purpose |
|---|---|
| `Services/SshControlMasterService.cs` | ControlMaster lifecycle + command routing |
| `Services/RemoteTmuxBackend.cs` | `ISessionBackend` implementation for remote tmux |
| `Models/CachedRemoteSession.cs` | Cache model for offline session display |

### Modified Files

| File | Change |
|---|---|
| `App.cs` | `_backend` → `_backends` dict; `BackendFor()` helper; merge `ListSessions()`; offline fallback |
| `Models/Session.cs` | Add `IsOffline` property |
| `Models/CccConfig.cs` | Add `CachedRemoteSessions` dictionary |
| `UI/Renderer.cs` | Render offline sessions greyed out with `✗` prefix |

### Unchanged

`TmuxBackend.cs`, `SshService.cs`, `ISessionBackend.cs`, `ConPtyBackend.cs`, `KeyBindingService.cs`,
`SessionHandler.cs`, `FlowHelper.cs`, `GroupHandler.cs`, `DiffHandler.cs`, `SettingsHandler.cs`

## Branch

Feature work happens on branch: `feature/remote-native-tmux`
Use a git worktree so it's isolated from `main`.
