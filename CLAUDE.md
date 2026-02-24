# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build                    # Build debug
dotnet run                      # Build + run (must be outside tmux)
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist  # Release binary
```

No test project exists. Verify changes by building and manual testing.

## Architecture

**Claude Command Center (ccc)** is a terminal dashboard for managing multiple Claude Code tmux sessions. Single .NET 10 executable, single dependency (Spectre.Console).

### Core Loop

`Program.cs` creates `App`, which runs a polling-based TUI main loop (30ms tick):
1. Process keyboard input via `HandleKey()` → `DispatchAction()`
2. Refresh pane captures every 500ms
3. Detect waiting-for-input sessions via content hash stability
4. Re-render with `Renderer.BuildLayout()`

### App.cs (~2000 lines)

The monolith. Contains all application logic: session/group CRUD, directory picker, worktree creation, input handling, grid management, and update checking. Key sections:

- **Session creation** (`CreateNewSession`): name → description → color → pick directory → `tmux new-session ... claude`
- **Group creation** (`CreateNewGroup`): 3 modes — existing worktree features, new worktrees from repos, manual directory picks
- **Directory picker** (`PickDirectory`): Shows favorites + worktree entries (prefixed with `⑂`). Worktree entries create git worktrees using the session name as branch.
- **Key dispatch** (`DispatchAction`): Maps action IDs to methods. Actions defined in `KeyBindingService.Defaults`.
- **Worktree scanning** (`ScanWorktreeFeatures`): Reads `.feature-context.json` or auto-detects git subdirectories under `worktreeBasePath`.

### Services (all static)

| Service | Purpose |
|---------|---------|
| `TmuxService` | All tmux interaction — create/kill/attach sessions, capture panes, send keys, detect waiting-for-input |
| `ConfigService` | JSON persistence to `~/.ccc/config.json`. Handles session metadata, groups, keybindings |
| `KeyBindingService` | Resolves keybindings from defaults + config overrides. Builds key→action map |
| `GitService` | `git worktree add`, `IsGitRepo`, `FetchPrune`, branch name sanitization |
| `UpdateChecker` | Async GitHub API check for newer releases |
| `CrashLog` | Exception logging to `~/.ccc/crash.log` with auto-trim |

### UI Layer

| File | Purpose |
|------|---------|
| `AppState` | Mutable state container: sessions, cursor, view mode, input buffer, groups |
| `Renderer` | Stateless rendering — builds Spectre.Console `IRenderable` from `AppState` |
| `AnsiParser` | Converts tmux ANSI escape sequences to Spectre styles (256-color + truecolor) |
| `SettingsDefinition` | Builds settings categories and items for the settings view |

### Models

- `CccConfig`: Root config (favorites, groups, colors, descriptions, keybindings, worktreeBasePath)
- `TmuxSession`: Runtime session with tmux metadata + git info + waiting-for-input status
- `SessionGroup`: Named group of session names with optional worktree path
- `KeyBinding` / `KeyBindingConfig`: Resolved keybinding and JSON config override
- `SettingsItem` / `SettingsCategory`: Settings view data structures

### Enums

- `ViewMode`: List, Grid, Settings, DiffOverlay
- `ActiveSection`: Sessions, Groups
- `SettingsItemType`: Text, Toggle, Number, Action

## Code Style

- **One type per file.** Each class, enum, record, struct, and exception gets its own file. No nested types.
- **Folder names describe contents.** `Models/`, `Enums/`, `Services/`, `UI/` — a file's folder should tell you what kind of thing it is.

## Key Patterns

**Session naming**: `SanitizeTmuxSessionName()` replaces dots/colons with underscores (tmux requirement). Group sessions named `{groupName}-{repoName}`.

**Waiting-for-input detection**: Hash pane content (excluding status bar), track consecutive stable polls. After threshold, mark session idle with `!` indicator.

**Grid view**: Auto-scales 1x1 to 3x3 (max 9 sessions). Resizes tmux panes to match grid cell width before capture to prevent line-wrap artifacts.

**Worktree creation**: `PickDirectory` shows `⑂` entries for git repo favorites. Creates worktrees at `worktreeBasePath/{branchName}/{repoName}/`. Group flow generates `.feature-context.json` for later discovery.

**Color system**: 13 predefined Spectre.Console colors. Converted to RGB hex for tmux `status-style`. "Just give me one" picks random unused color.

## Config

Stored at `~/.ccc/config.json`. Key fields: `favoriteFolders`, `ideCommand`, `groups`, `sessionColors`, `sessionDescriptions`, `worktreeBasePath`, `keybindings`, `excludedSessions`.

## CI/CD

`.github/workflows/release.yml`: Push to main triggers semantic version bump (based on commit prefixes), cross-platform build (linux-x64, osx-arm64, osx-x64), and GitHub release with binary archives.

## Documentation

Always update `README.md` when adding or changing features. The README is the primary user-facing documentation — keybindings table, config options, and feature descriptions must stay in sync with the code.

## Version

Bumped in `ClaudeCommandCenter.csproj` `<Version>` property. CI auto-calculates from git tags + commit messages.
