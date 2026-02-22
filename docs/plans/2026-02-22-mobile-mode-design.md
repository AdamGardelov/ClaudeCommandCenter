# Mobile Mode Design

## Overview

A stripped-down TUI mode for CCC, activated by `ccc -m`, optimized for phone SSH clients like Termius. Same binary, same app loop, same tmux interactions — just a different renderer path and reduced action set.

## Use Cases

1. **Monitor & triage** — See session status, approve/reject waiting prompts, send short text
2. **Quick attach** — Jump into a session to check on it

## Flag

- `ccc -m` or `ccc --mobile` launches mobile mode
- Runtime only, no config persistence, no auto-detection
- Desktop is the default when no flag is provided

## Layout

Single column, 4 fixed zones:

```
┌─────────────────────────────┐
│ Header                  1 ln│
│─────────────────────────────│
│                             │
│ Session List         dynamic│
│                             │
│─────────────────────────────│
│ Detail Bar              3 ln│
│─────────────────────────────│
│ Status Bar              1 ln│
└─────────────────────────────┘
```

**Header (1 line):** `CCC v{version} - {count} sessions` with group filter `[All]` or `[GroupName]` right-aligned.

**Session List (dynamic):** Single-line rows, full width. Format: `> {status} {name}`. Selected row highlighted with session color as background. Excluded sessions dimmed. Available height: terminal height - 5. Scrolls when list exceeds visible area.

**Detail Bar (3 lines):** Info for selected session:
- Line 1: Session name (with color)
- Line 2: Git branch + truncated path (dimmed)
- Line 3: Status / description

**Status Bar (1 line):** Context-sensitive keybindings.

## Actions

### Available in Mobile Mode

| Action | Key | Notes |
|--------|-----|-------|
| navigate up/down | j/k/arrows | Same as desktop |
| approve | Y | Shown when selected session is waiting |
| reject | N | Shown when selected session is waiting |
| send text | S | Input mode, same as desktop |
| attach | Enter | Attach to selected session |
| cycle group filter | g | Cycles All > Group1 > Group2 > All |
| refresh | r | Hidden from status bar |
| quit | q | Exit |

### Disabled in Mobile Mode

new-session, new-group, edit-session, delete-session, move-to-group, toggle-grid, toggle-exclude, open-folder, open-ide, open-config, update.

Session creation is the first candidate for re-enabling later.

### Context-Sensitive Status Bar

| State | Shows |
|-------|-------|
| Selected session waiting | Y approve  N reject  S send  Enter attach  q quit |
| Selected session working | Enter attach  S send  q quit |
| No sessions | q quit |

## Group Filter

Replaces the desktop Tab-switching groups section.

- `g` cycles through: All > Group1 > Group2 > All
- Header shows active filter: `[All]` or `[GroupName]`
- All = standalone sessions (not in any group)
- Cursor resets to 0 on filter change
- No group management (create/edit/delete) — mobile consumes existing groups as read-only filters
- `g` key hidden if no groups exist

## Attach Flow

Unchanged from desktop. Enter attaches, tmux handles terminal reflow natively. No pane resizing.

## Implementation

### Files That Change

| File | Change |
|------|--------|
| Program.cs | Parse -m/--mobile flag, pass to App constructor |
| App.cs | Store MobileMode bool, pass to AppState. Skip grid logic when mobile. Add group filter cycling. Disable non-mobile actions in DispatchAction. |
| UI/AppState.cs | Add MobileMode bool, GroupFilterIndex int, ActiveGroupFilter property, TopIndex int for scroll offset |
| UI/Renderer.cs | New BuildMobileLayout() method. Helpers: BuildMobileSessionList(), BuildMobileDetailBar(), BuildMobileStatusBar() |

### Files That Don't Change

TmuxService, ConfigService, GitService, KeyBindingService, AnsiParser, UpdateChecker, CrashLog, all models.

### Key Decisions

- Mobile is a rendering concern, not a state machine change. Same AppState drives both layouts.
- No new files. Mobile renderer is methods on Renderer.cs.
- Group filter state only used when MobileMode is true.
- Scroll offset is a simple TopIndex int — cursor movement keeps selection visible.
