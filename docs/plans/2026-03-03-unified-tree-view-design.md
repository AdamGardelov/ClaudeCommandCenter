# Unified Tree View for Sessions and Groups

## Problem

Groups only support grid view. When you open a group, you're forced into a multi-pane grid layout. There's no way to arrow through a group's sessions one at a time with live preview — the workflow that works well for standalone sessions in list mode.

## Solution

Replace the two-section panel (Sessions section + Groups section with Tab switching) with a single navigable tree list. Standalone sessions and group headers are peers in one list. Group headers expand/collapse to show their child sessions indented below. One cursor moves through everything.

## List Layout

```
  ✓ standalone-session-1
  ! standalone-session-2
  ▼ auth-feature (3)
     ✓ auth-feature-Core
     ! auth-feature-Model
     ✓ auth-feature-Salary
  ▼ payments (2)
     ✓ payments-BankService
     ✓ payments-Core
```

- Standalone sessions first, then groups — no separator
- Groups expanded by default
- `▼`/`▶` arrows on group headers (replaces aggregate status icons)
- Child sessions indented with extra spaces
- Preview pane shows live output for whichever session has cursor
- Cursor on a group header shows group summary preview (same as today)

## Navigation

- **Arrow keys**: Move cursor through the unified flat list (standalones, group headers, grouped sessions)
- **Enter on group header**: Toggle expand/collapse
- **Enter on session**: Attach (same as today)
- **G on standalone session**: Toggle global grid view (same as today)
- **G on grouped session**: Toggle grid view for that group only
- **G on group header**: Toggle grid view for that group only
- **Tab**: No longer used for section switching (freed up)

## State Changes

### AppState

- **Remove** `ActiveSection` property
- **Remove** `GroupCursor` property
- **Add** `ExpandedGroups: HashSet<string>` — tracks which groups are expanded (all expanded on startup)
- **Add** `GetTreeItems(): List<TreeItem>` — builds flat list of tree entries for cursor navigation
- **Add** `ToggleGroupExpanded(string groupName)`
- `CursorIndex` now indexes into the tree items list

### New Model: TreeItem

Discriminated type representing one row in the tree:

- `TreeItem.Session(Session session, string? groupName)` — session row, `groupName` null for standalone
- `TreeItem.GroupHeader(SessionGroup group, bool isExpanded)` — group header row

### Renderer

- `BuildSessionPanel()` iterates `GetTreeItems()` instead of rendering two sections
- Group headers: `▼`/`▶` + name + `(count)`
- Grouped sessions: indented with extra spaces
- Preview panel: `TreeItem.Session` → live pane output, `TreeItem.GroupHeader` → group summary

### App.cs

- Remove Tab handling for section switching
- Remove `MoveGroupCursor()`
- Key dispatch checks current tree item type instead of `ActiveSection`
- Enter: group header → toggle expand, session → attach
- G: grouped session or group header → group grid, standalone → global grid

## What Gets Removed

- `ActiveSection` enum — delete file
- `AppState.ActiveSection` property
- `AppState.GroupCursor` property
- `Renderer.BuildGroupRow()` — replaced by tree item rendering
- Tab key section switching in `App.cs`
- `MoveGroupCursor()` in `App.cs`
- Section-aware action dispatch (`if ActiveSection == Groups` block)
- Separator rule between sessions and groups

## What Stays the Same

- Grid view rendering
- `EnterGroupGrid()` / `LeaveGroupGrid()`
- Group CRUD (create, delete, edit) — same handlers, invoked from tree context
- Session preview panel — same live pane capture
- Group preview panel — same summary
- Mobile mode — keep current behavior, can adopt tree view later
- All keybindings except Tab
