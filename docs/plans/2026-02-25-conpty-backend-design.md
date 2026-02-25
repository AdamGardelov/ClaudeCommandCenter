# ConPTY Backend Design

Add a native Windows session backend using ConPTY, eliminating the WSL/tmux requirement for Windows users.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Session persistence | Ephemeral (die with CCC) | Simplest. No daemon. Windows users expect it. Can add persistence later. |
| Attach model | Inline takeover | CCC hides UI, pipes I/O directly. `Ctrl+]` to detach. Matches tmux UX. |
| Backend selection | Platform auto-detect | `OperatingSystem.IsWindows()` at startup. No settings toggle — WSL reports as Linux, no ambiguous case. |
| Session model | Rename `TmuxSession` → `Session` | Backend-agnostic. `WindowCount` stays (ConPTY sets to 1). |
| Output capture | Per-session background thread + ring buffer | Thread reads PTY output continuously. Ring buffer holds last 500 lines. Instant `CapturePaneContent()`. |
| Compilation | Runtime selection, both backends in every binary | .NET trimmer strips dead code. Avoids `#if` preprocessor complexity. |

## Architecture

```
Before:
  App/Handlers → TmuxService (static) → tmux CLI

After:
  App/Handlers → ISessionBackend (instance)
                    ├── TmuxBackend      (Linux/macOS)
                    └── ConPtyBackend    (Windows)
```

UI layer (Renderer, AppState, AnsiParser, all views) stays untouched. Only the session
lifecycle/capture/interaction layer changes.

## ISessionBackend Interface

```csharp
public interface ISessionBackend
{
    // Lifecycle
    List<Session> ListSessions();
    string? CreateSession(string name, string workingDirectory);
    string? KillSession(string name);
    string? RenameSession(string oldName, string newName);

    // Interaction
    void AttachSession(string name);
    void DetachSession();
    string? SendKeys(string sessionName, string text);
    string? CapturePaneContent(string sessionName, int lines = 500);

    // Display
    void ResizeWindow(string sessionName, int width, int height);
    void ResetWindowSize(string sessionName);
    void ApplyStatusColor(string sessionName, string? spectreColor);

    // State detection
    void DetectWaitingForInputBatch(List<Session> sessions);

    // Environment checks
    bool IsAvailable();
    bool IsInsideHost();
    bool HasClaude();
}
```

### Method Notes

| Method | TmuxBackend | ConPtyBackend |
|--------|-------------|---------------|
| `DetachSession()` | No-op (tmux handles via `Ctrl-b d`) | Signals inline takeover to stop, restores CCC UI |
| `ApplyStatusColor()` | Sets tmux status bar hex color | No-op (CCC UI shows color instead) |
| `IsInsideHost()` | Checks `$TMUX` env var | Always `false` |
| `ResizeWindow()` | `tmux resize-window` | `ResizePseudoConsole()` Win32 call |
| `CapturePaneContent()` | `tmux capture-pane` (shells out) | Reads from ring buffer (instant, no process call) |

## TmuxBackend

Straight extraction of existing `TmuxService`:

- Static methods become instance methods
- Private helpers (`RunTmux`, `RunTmuxWithError`) stay as private instance methods
- `DetectGitInfo` moves to `GitService` (shared between backends)
- Zero logic changes — pure restructuring

## ConPtyBackend

### Internal State

```csharp
internal class ConPtySession
{
    public required string Name { get; set; }
    public required string WorkingDirectory { get; set; }
    public required Process Process { get; set; }
    public required SafeHandle PseudoConsole { get; set; }
    public required StreamWriter Input { get; set; }
    public required Thread ReaderThread { get; set; }
    public required RingBuffer OutputBuffer { get; set; }
    public DateTime Created { get; set; }
    public string? GitBranch { get; set; }
    public bool IsWorktree { get; set; }
}
```

Sessions stored in `Dictionary<string, ConPtySession>`.

### CreateSession Flow

1. Create two pipe pairs (CCC→process input, process→CCC output)
2. `CreatePseudoConsole()` with initial size and pipe handles
3. `CreateProcess()` with `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` to spawn `claude`
4. Start background reader thread → reads output pipe → appends to `RingBuffer`
5. Store in dictionary

### KillSession Flow

1. Kill process
2. Close pseudoconsole handle
3. Signal reader thread to stop
4. Remove from dictionary

### Attach (Inline Takeover)

1. Save current console mode
2. Set raw mode (no echo, no line buffering)
3. Clear screen, hide CCC UI
4. Two forwarding loops:
   - Input: Console keystrokes → session input pipe
   - Output: Session output pipe → Console stdout
5. `Ctrl+]` triggers detach
6. Restore console mode, re-render CCC UI

### Win32 API Surface

| Function | Purpose |
|----------|---------|
| `CreatePipe` | Create input/output pipe pairs |
| `CreatePseudoConsole` | Create the PTY |
| `ResizePseudoConsole` | Handle resize |
| `ClosePseudoConsole` | Cleanup |
| `CreateProcess` (extended startup info) | Spawn claude |

5 P/Invoke declarations total.

## RingBuffer

Thread-safe circular buffer for terminal output:

```csharp
public class RingBuffer(int maxLines = 500)
{
    public void Append(string line);
    public string GetLines(int count);
}
```

- Lock-based synchronization (sufficient for single-digit session count)
- Reader thread writes, `CapturePaneContent` reads
- Unit-testable in isolation

## Wiring

### Backend Selection (Program.cs)

```csharp
ISessionBackend backend = OperatingSystem.IsWindows()
    ? new ConPtyBackend()
    : new TmuxBackend();

var app = new App(backend, mobileMode);
```

### Constructor Threading

```csharp
public class App(ISessionBackend backend, bool mobileMode = false)
// App passes backend to:
_sessionHandler = new SessionHandler(_state, _config, backend, ...);
_groupHandler = new GroupHandler(_state, _config, backend, ...);
```

`FlowHelper`, `DiffHandler`, `SettingsHandler`, `Renderer` don't call the backend — untouched.

## Implementation Plan

| Step | Description | Risk |
|------|-------------|------|
| 1 | Rename `TmuxSession` → `Session` | Low — pure rename |
| 2 | Extract `ISessionBackend` interface | Low — additive |
| 3 | Create `TmuxBackend` from `TmuxService` | Low — move code |
| 4 | Wire backend through App and Handlers | Medium — many call sites, but mechanical |
| 5 | Add `RingBuffer` utility class | Low — standalone, testable |
| 6 | Implement `ConPtyBackend` | High — new platform code, P/Invoke, attach takeover |
| 7 | Platform selection in `Program.cs` | Low — one conditional |
| 8 | CI/CD — add `win-x64` build target | Low — config change |
| 9 | Update README | Low — documentation |

Steps 1–4 are safe refactoring testable on Linux. Step 5 is standalone. Steps 6–7 are Windows-specific.
Step 4 is the "nothing should change" checkpoint — same behavior, new plumbing.
