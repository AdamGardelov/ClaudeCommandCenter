# Claude Command Center (ccc)

A terminal UI for managing multiple Claude Code sessions via tmux. Lists your sessions, shows a live preview of the selected pane, and highlights sessions waiting for input.

## Requirements

- [.NET 10](https://dotnet.microsoft.com/download) SDK or runtime
- [tmux](https://github.com/tmux/tmux)
- Linux or macOS

## Build

```bash
dotnet build
```

## Install

Publish a self-contained single-file binary and copy it to your PATH:

### Linux

```bash
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist
sudo cp dist/ccc /usr/local/bin/ccc
```

### macOS

```bash
# Apple Silicon
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o dist
# Intel
dotnet publish -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true -o dist

sudo cp dist/ccc /usr/local/bin/ccc
```

### Windows (WSL)

tmux requires a Unix environment. Run inside WSL and follow the Linux instructions above.

After installing, the `ccc` command is available from any terminal.

## Usage

Run outside of tmux:

```bash
ccc
```

The app shows a split-panel TUI — sessions on the left, a live pane preview on the right. Sessions that have been idle for a few seconds are marked with `!` (waiting for input).

### Keybindings

| Key | Action |
|-----|--------|
| `j` / `k` / arrows | Navigate sessions |
| `J` / `K` | Scroll preview |
| `Enter` | Attach to selected session |
| `n` | Create new session (launches `claude` in a given directory) |
| `i` | Open session directory in IDE |
| `d` | Delete session (with confirmation) |
| `R` | Rename session |
| `r` | Refresh session list |
| `y` | Approve — sends `y` to the selected session |
| `N` | Reject — sends `n` to the selected session |
| `s` | Send — type a message and send it to the selected session |
| `q` | Quit |

When you attach to a session, detach with the standard tmux prefix (`Ctrl-b d`) to return to the command center.

### Configuration

Create `~/.ccc/config.json` to configure favorite folders. When creating a new session, you'll be able to pick from this list instead of typing a full path.

```json
{
  "favoriteFolders": [
    { "name": "Core", "path": "~/Dev/Wint/Core" },
    { "name": "Salary", "path": "~/Dev/Wint/Wint.Salary" }
  ],
  "ideCommand": "rider"
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `favoriteFolders` | `[]` | Quick-pick directories when creating sessions |
| `ideCommand` | `` | Command to run when pressing `i` (e.g. `rider`, `code`, `cursor`) |

The config file is created automatically on first run. Tilde (`~`) paths are expanded automatically.
