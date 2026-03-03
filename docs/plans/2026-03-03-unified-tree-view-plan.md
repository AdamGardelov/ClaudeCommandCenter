# Unified Tree View Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the two-section session/group panel with a unified tree view where standalone sessions and expandable group headers coexist in a single navigable list.

**Architecture:** Add a `TreeItem` discriminated union and `GetTreeItems()` to AppState. The single `CursorIndex` indexes into this flat list. Remove `ActiveSection`, `GroupCursor`, and Tab switching. Renderer iterates tree items with indentation for grouped sessions. Group headers toggle expand/collapse on Enter.

**Tech Stack:** .NET 10, Spectre.Console

---

### Task 1: Create the TreeItem model

**Files:**
- Create: `Models/TreeItem.cs`

**Step 1: Create the TreeItem type**

```csharp
using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.Models;

public abstract record TreeItem
{
    public record SessionItem(Session Session, string? GroupName) : TreeItem;
    public record GroupHeader(SessionGroup Group, bool IsExpanded) : TreeItem;
}
```

**Step 2: Verify it builds**

Run: `dotnet build`
Expected: BUILD SUCCEEDED

**Step 3: Commit**

```
feat: add TreeItem model for unified tree view
```

---

### Task 2: Add tree state and methods to AppState

**Files:**
- Modify: `UI/AppState.cs`

**Step 1: Add ExpandedGroups state and remove old group state**

Replace the group state section in AppState:

```csharp
// Old — remove these lines:
public ActiveSection ActiveSection { get; set; } = ActiveSection.Sessions;
public int GroupCursor { get; set; }

// New — add:
public HashSet<string> ExpandedGroups { get; set; } = [];
```

Keep `ActiveGroup` and `_savedCursorIndex` — they're still used for grid mode.

**Step 2: Add GetTreeItems() method**

Add this method to AppState (after `GetStandaloneSessions()`):

```csharp
public List<TreeItem> GetTreeItems()
{
    var items = new List<TreeItem>();

    // Standalone sessions first
    foreach (var session in GetStandaloneSessions())
        items.Add(new TreeItem.SessionItem(session, null));

    // Then groups with their sessions
    foreach (var group in Groups)
    {
        var isExpanded = ExpandedGroups.Contains(group.Name);
        items.Add(new TreeItem.GroupHeader(group, isExpanded));

        if (isExpanded)
        {
            var groupSessionNames = new HashSet<string>(group.Sessions);
            var groupSessions = Sessions
                .Where(s => groupSessionNames.Contains(s.Name))
                .ToList();
            foreach (var session in groupSessions)
                items.Add(new TreeItem.SessionItem(session, group.Name));
        }
    }

    return items;
}
```

**Step 3: Add ToggleGroupExpanded() method**

```csharp
public void ToggleGroupExpanded(string groupName)
{
    if (!ExpandedGroups.Remove(groupName))
        ExpandedGroups.Add(groupName);
}
```

**Step 4: Add InitExpandedGroups() method**

Call this after loading groups to expand all by default:

```csharp
public void InitExpandedGroups()
{
    foreach (var group in Groups)
        ExpandedGroups.Add(group.Name);
}
```

**Step 5: Update GetSelectedSession() for tree items**

Replace the list-view path in `GetSelectedSession()`. The old code checked `ActiveSection` — replace with tree item lookup:

```csharp
public Session? GetSelectedSession()
{
    if (MobileMode)
    {
        var mobileSessions = GetMobileVisibleSessions();
        if (CursorIndex >= 0 && CursorIndex < mobileSessions.Count)
            return mobileSessions[CursorIndex];
        return null;
    }

    if (ViewMode == ViewMode.Grid)
    {
        var gridSessions = GetGridSessions();
        if (CursorIndex >= 0 && CursorIndex < gridSessions.Count)
            return gridSessions[CursorIndex];
        return null;
    }

    // List view: resolve from tree items
    var treeItems = GetTreeItems();
    if (CursorIndex >= 0 && CursorIndex < treeItems.Count)
    {
        if (treeItems[CursorIndex] is TreeItem.SessionItem si)
            return si.Session;
    }

    return null;
}
```

**Step 6: Update GetSelectedGroup() for tree items**

Replace with tree-aware version:

```csharp
public SessionGroup? GetSelectedGroup()
{
    var treeItems = GetTreeItems();
    if (CursorIndex >= 0 && CursorIndex < treeItems.Count)
    {
        if (treeItems[CursorIndex] is TreeItem.GroupHeader gh)
            return gh.Group;
    }

    return null;
}
```

**Step 7: Update ClampCursor()**

The list-view path should clamp to tree items count:

```csharp
public void ClampCursor()
{
    if (MobileMode)
    {
        var mobile = GetMobileVisibleSessions();
        CursorIndex = mobile.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, mobile.Count - 1);
        return;
    }

    if (ViewMode == ViewMode.Grid)
    {
        var grid = GetGridSessions();
        CursorIndex = grid.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, grid.Count - 1);
        return;
    }

    var tree = GetTreeItems();
    CursorIndex = tree.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, tree.Count - 1);
}
```

**Step 8: Remove ClampGroupCursor()**

Delete the `ClampGroupCursor()` method entirely.

**Step 9: Remove the `using ClaudeCommandCenter.Enums;` import if ActiveSection was the only enum used from there**

Check — `ViewMode` is also from Enums, so keep the import.

**Step 10: Verify it builds**

Run: `dotnet build`
Expected: Build errors in App.cs, Renderer.cs, GroupHandler.cs referencing removed members. That's expected — we'll fix those in subsequent tasks.

---

### Task 3: Update App.cs — navigation and key dispatch

**Files:**
- Modify: `App.cs`

**Step 1: Remove Tab key handling**

Delete the Tab section (lines ~542-551):

```csharp
// DELETE THIS BLOCK:
if (key.Key == ConsoleKey.Tab && _state.ViewMode == ViewMode.List && _state.ActiveGroup == null)
{
    if (_state.ActiveSection == ActiveSection.Sessions && _state.Groups.Count > 0)
        _state.ActiveSection = ActiveSection.Groups;
    else
        _state.ActiveSection = ActiveSection.Sessions;
    _lastSelectedSession = null;
    return;
}
```

**Step 2: Update arrow key handling in HandleKey()**

Replace the section-aware arrow key block with unified tree navigation:

```csharp
// List view arrow keys — unified tree navigation
switch (key.Key)
{
    case ConsoleKey.UpArrow:
        MoveCursor(-1);
        return;
    case ConsoleKey.DownArrow:
        MoveCursor(1);
        return;
}
```

(Remove the `ActiveSection.Groups` branch and `MoveGroupCursor` calls.)

**Step 3: Update DispatchAction() — replace section-aware dispatch**

Replace the `ActiveSection.Groups` block (lines ~577-594) with tree-item-aware dispatch:

```csharp
// When cursor is on a group header in list view, intercept actions
if (_state.ViewMode == ViewMode.List && _state.ActiveGroup == null)
{
    var currentItem = _state.GetTreeItems().ElementAtOrDefault(_state.CursorIndex);
    if (currentItem is TreeItem.GroupHeader)
    {
        switch (actionId)
        {
            case "attach":
                // Enter on group header = toggle expand/collapse
                var group = _state.GetSelectedGroup();
                if (group != null)
                {
                    _state.ToggleGroupExpanded(group.Name);
                    _state.ClampCursor();
                }
                return;
            case "delete-session":
                _groupHandler.Delete();
                return;
            case "edit-session":
                _groupHandler.Edit();
                return;
            case "move-to-group":
                return; // Not applicable for group headers
        }
    }
}
```

**Step 4: Update ToggleGridView() for tree-aware G key**

Replace `ToggleGridView()`:

```csharp
private void ToggleGridView()
{
    // If in group grid, Escape handles exit — G should not toggle
    if (_state.ActiveGroup != null)
        return;

    if (_state.ViewMode == ViewMode.List)
    {
        // Check if cursor is on a grouped session or group header — open group grid
        var treeItems = _state.GetTreeItems();
        var currentItem = treeItems.ElementAtOrDefault(_state.CursorIndex);

        if (currentItem is TreeItem.SessionItem { GroupName: not null } si)
        {
            _state.EnterGroupGrid(si.GroupName);
            _lastSelectedSession = null;
            ResizeGridPanes();
            return;
        }

        if (currentItem is TreeItem.GroupHeader gh)
        {
            if (gh.Group.Sessions.Count == 0)
            {
                _state.SetStatus("Group has no live sessions");
                return;
            }

            _state.EnterGroupGrid(gh.Group.Name);
            _lastSelectedSession = null;
            ResizeGridPanes();
            return;
        }

        // Standalone session — global grid
        var gridSessions = _state.GetGridSessions();
        if (gridSessions.Count < 2)
        {
            _state.SetStatus("Need at least 2 non-excluded sessions for grid view");
            return;
        }

        var (cols, _) = _state.GetGridDimensions();
        if (cols == 0)
        {
            _state.SetStatus("Too many sessions for grid view (max 9)");
            return;
        }

        _state.ViewMode = ViewMode.Grid;
        ResizeGridPanes();
    }
    else
    {
        _state.ViewMode = ViewMode.List;
    }

    _lastSelectedSession = null;
}
```

**Step 5: Update MoveCursor() for tree items**

```csharp
private void MoveCursor(int delta)
{
    var treeItems = _state.GetTreeItems();
    if (treeItems.Count == 0)
        return;
    _state.CursorIndex = Math.Clamp(_state.CursorIndex + delta, 0, treeItems.Count - 1);
    _lastSelectedSession = null; // Force pane recapture
}
```

**Step 6: Remove MoveGroupCursor()**

Delete the `MoveGroupCursor()` method entirely.

**Step 7: Update LoadGroups() — remove ClampGroupCursor, add InitExpandedGroups**

In `LoadGroups()`, replace:
```csharp
_state.ClampGroupCursor();
```
with:
```csharp
_state.InitExpandedGroups();
```

Note: `InitExpandedGroups` only adds, so already-expanded groups stay expanded across reloads.

**Step 8: Verify it builds**

Run: `dotnet build`
Expected: Build errors in Renderer.cs and GroupHandler.cs. We'll fix those next.

---

### Task 4: Update Renderer.cs — tree view panel

**Files:**
- Modify: `UI/Renderer.cs`

**Step 1: Replace BuildSessionPanel() with tree-based rendering**

Replace the entire `BuildSessionPanel()` method:

```csharp
private static IRenderable BuildSessionPanel(AppState state)
{
    var treeItems = state.GetTreeItems();
    var rows = new List<IRenderable>();

    if (treeItems.Count == 0)
    {
        rows.Add(new Markup("  [grey]No sessions[/]"));
    }
    else
    {
        for (var i = 0; i < treeItems.Count; i++)
        {
            var isSelected = i == state.CursorIndex;
            switch (treeItems[i])
            {
                case TreeItem.SessionItem si:
                    rows.Add(BuildSessionRow(si.Session, isSelected, si.GroupName != null));
                    break;
                case TreeItem.GroupHeader gh:
                    rows.Add(BuildTreeGroupRow(gh, isSelected, state));
                    break;
            }
        }
    }

    // Border color based on selected item
    Color borderColor;
    var selectedItem = treeItems.ElementAtOrDefault(state.CursorIndex);
    if (selectedItem is TreeItem.SessionItem { Session.ColorTag: not null } selSession)
        borderColor = Style.Parse(selSession.Session.ColorTag).Foreground;
    else if (selectedItem is TreeItem.GroupHeader { Group.Color: not null and not "" } selGroup)
        borderColor = Style.Parse(selGroup.Group.Color).Foreground;
    else
        borderColor = Color.Grey42;

    return new Panel(new Rows(rows))
        .Header("[grey70] Workspace [/]")
        .BorderColor(borderColor)
        .Expand();
}
```

**Step 2: Update BuildSessionRow() to accept indentation**

Add an `indented` parameter:

```csharp
private static Markup BuildSessionRow(Session session, bool isSelected, bool indented = false)
{
    var indent = indented ? "   " : "";
    var name = Markup.Escape(session.Name);
    var spinner = Markup.Escape(GetSpinnerFrame());
    var remote = session.RemoteHostName != null ? "[mediumpurple3]☁[/]" : " ";
    var nameWidth = indented ? 19 : 22;

    // ... rest of method uses `indent` prefix and `nameWidth` for padding
```

Update each return statement in the method to use `indent` prefix. For example:

The dead+excluded case:
```csharp
if (isSelected)
    return new Markup($"[grey50 on grey19]{indent} [grey42]†[/] {name,-nameWidth}[/]{remote}");
return new Markup($"{indent} [grey42]†[/] [grey35]{name,-nameWidth}[/]{remote}");
```

Apply the same pattern to all return statements — prepend `indent` and use `nameWidth` instead of hardcoded `22`.

**Step 3: Add BuildTreeGroupRow() method**

```csharp
private static Markup BuildTreeGroupRow(TreeItem.GroupHeader header, bool isSelected, AppState state)
{
    var group = header.Group;
    var name = Markup.Escape(group.Name);
    var totalSessions = group.Sessions.Count;
    var expandIcon = header.IsExpanded ? "▼" : "▶";
    var countLabel = $"({totalSessions})";
    var colorTag = !string.IsNullOrEmpty(group.Color) ? group.Color : "grey50";

    if (isSelected)
    {
        var bg = !string.IsNullOrEmpty(group.Color) ? group.Color : "grey37";
        return new Markup($"[white on {bg}] {expandIcon} {name,-14} {countLabel,-4} [/]");
    }

    if (totalSessions == 0)
        return new Markup($" [grey50]{expandIcon}[/] [grey50 strikethrough]{name,-14}[/] [grey42]{countLabel}[/]");

    return new Markup($" [{colorTag}]{expandIcon}[/] [{colorTag}]{name,-14}[/] [grey50]{countLabel}[/]");
}
```

**Step 4: Update BuildPreviewPanel() — remove ActiveSection check**

In `BuildPreviewPanel()`, replace the `ActiveSection.Groups` check with tree item check:

```csharp
if (session == null)
{
    // Check if cursor is on a group header
    var treeItems = state.GetTreeItems();
    var currentItem = treeItems.ElementAtOrDefault(state.CursorIndex);
    if (currentItem is TreeItem.GroupHeader gh)
        return BuildGroupPreviewPanel(gh.Group, state);

    // ... rest of empty-session fallback (figlet)
```

**Step 5: Update BuildGroupPreviewPanel() hint text**

Change the hint at the bottom from:
```csharp
rows.Add(new Markup("  [grey]Press [/][grey70 bold]Enter[/][grey] to open group grid · [/][grey70 bold]e[/][grey] to edit[/]"));
```
to:
```csharp
rows.Add(new Markup("  [grey]Press [/][grey70 bold]Enter[/][grey] to expand/collapse · [/][grey70 bold]G[/][grey] grid · [/][grey70 bold]e[/][grey] to edit[/]"));
```

**Step 6: Update BuildStatusBar() — remove Tab hint and section dimming**

Remove the `onGroup` / `ActiveSection` logic:

```csharp
private static Markup BuildStatusBar(AppState state)
{
    if (state.IsInputMode)
        return BuildInputStatusBar(state);

    var status = state.GetActiveStatus();
    if (status != null)
        return new Markup($" [yellow]{Markup.Escape(status)}[/]");

    var visible = state.Keybindings
        .Where(b => b.Enabled && b.Label != null && b.StatusBarOrder >= 0)
        .OrderBy(b => b.StatusBarOrder)
        .ToList();

    if (visible.Count == 0)
        return new Markup(" ");

    var hiddenWhenNoGroups = new HashSet<string> { "move-to-group" };
    var hasGroups = state.Groups.Count > 0;

    // Check if cursor is on a group header — dim session-only actions
    var treeItems = state.GetTreeItems();
    var onGroupHeader = treeItems.ElementAtOrDefault(state.CursorIndex) is TreeItem.GroupHeader;
    var sessionOnlyActions = new HashSet<string> { "approve", "reject", "send-text" };

    var parts = new List<string>();
    var prevGroup = -1;

    foreach (var b in visible)
    {
        if (!hasGroups && hiddenWhenNoGroups.Contains(b.ActionId))
            continue;
        var barGroup = b.StatusBarOrder / 10;
        if (prevGroup >= 0 && barGroup != prevGroup)
            parts.Add("[grey]│[/]");
        prevGroup = barGroup;

        var dimmed = onGroupHeader && sessionOnlyActions.Contains(b.ActionId);
        var keyColor = dimmed ? "grey35" : "grey70 bold";
        var labelColor = dimmed ? "grey27" : "grey";
        parts.Add($"[{keyColor}]{Markup.Escape(b.Key)}[/][{labelColor}] {Markup.Escape(b.Label!)} [/]");
    }

    // Remove the old Tab hint — no longer needed
    return new Markup(" " + string.Join(" ", parts));
}
```

**Step 7: Verify it builds**

Run: `dotnet build`
Expected: Build errors in GroupHandler.cs. We'll fix that next.

---

### Task 5: Update GroupHandler.cs — remove ActiveSection references

**Files:**
- Modify: `Handlers/GroupHandler.cs`

**Step 1: Update GroupHandler.Open()**

The `Open()` method currently uses `GetSelectedGroup()` which relied on `GroupCursor`. With the new tree-aware `GetSelectedGroup()`, this still works — it checks the tree item at `CursorIndex`. No code change needed in Open() itself.

**Step 2: Update GroupHandler.Edit() — remove GroupCursor references**

In `Edit()`, replace:
```csharp
state.GroupCursor = state.Groups.FindIndex(g => g.Name == effectiveName);
if (state.GroupCursor < 0)
    state.GroupCursor = 0;
```
with:
```csharp
// Re-position cursor on the renamed group in the tree
var treeItems = state.GetTreeItems();
var newIdx = treeItems.FindIndex(t => t is TreeItem.GroupHeader gh && gh.Group.Name == effectiveName);
if (newIdx >= 0)
    state.CursorIndex = newIdx;
```

**Step 3: Update FinishCreation() — remove ActiveSection and GroupCursor**

Replace `FinishCreation()`:

```csharp
private void FinishCreation(string groupName)
{
    loadSessions();

    // Position cursor on the new group header
    var treeItems = state.GetTreeItems();
    var idx = treeItems.FindIndex(t => t is TreeItem.GroupHeader gh && gh.Group.Name == groupName);
    if (idx >= 0)
        state.CursorIndex = idx;

    state.EnterGroupGrid(groupName);
    resetPaneCache();
    resizeGridPanes();
}
```

**Step 4: Remove the `using ClaudeCommandCenter.Enums;` import**

Since `ActiveSection` was the only enum used from that namespace in GroupHandler, remove the import. Check if `ViewMode` or other enums are used — if not, remove it.

**Step 5: Verify it builds**

Run: `dotnet build`
Expected: BUILD SUCCEEDED (or minor errors from any remaining `ActiveSection` references elsewhere)

---

### Task 6: Delete ActiveSection enum and clean up remaining references

**Files:**
- Delete: `Enums/ActiveSection.cs`
- Modify: any remaining files referencing `ActiveSection`

**Step 1: Delete the ActiveSection enum file**

```bash
rm Enums/ActiveSection.cs
```

**Step 2: Search for remaining ActiveSection references**

```bash
grep -rn "ActiveSection" --include="*.cs" .
```

Fix any remaining references found. Common places:
- Any handler or service that checks `state.ActiveSection`
- Any renderer code that checks section focus

**Step 3: Verify it builds clean**

Run: `dotnet build`
Expected: BUILD SUCCEEDED with 0 warnings related to ActiveSection

**Step 4: Commit**

```
feat: unified tree view for sessions and groups

Replace two-section panel (Sessions/Groups with Tab switching)
with a single navigable tree list. Groups appear as expandable
headers with their sessions indented below. Arrow keys move
through everything, Enter toggles expand/collapse on group
headers, G opens group grid when on a grouped session.

Removes ActiveSection enum, GroupCursor, and Tab section switching.
```

---

### Task 7: Manual testing checklist

No automated tests exist, so verify manually:

**Step 1: Build and run**

```bash
dotnet build && dotnet run
```

**Step 2: Verify tree rendering**

- [ ] Standalone sessions appear at top of list
- [ ] Groups appear below with `▼` expand icon
- [ ] Group sessions are indented under their group header
- [ ] Group header shows `(N)` session count

**Step 3: Verify navigation**

- [ ] Arrow up/down moves through standalones, group headers, and grouped sessions seamlessly
- [ ] Cursor on standalone session shows live preview
- [ ] Cursor on group header shows group summary preview
- [ ] Cursor on grouped session shows live preview of that session

**Step 4: Verify expand/collapse**

- [ ] Enter on group header collapses it (icon changes to `▶`)
- [ ] Enter on collapsed group header expands it (icon changes to `▼`)
- [ ] Collapsed group hides its sessions
- [ ] Cursor clamps correctly after collapse

**Step 5: Verify grid toggle**

- [ ] G on standalone session opens global grid
- [ ] G on grouped session opens group grid
- [ ] G on group header opens group grid
- [ ] G in grid returns to list view

**Step 6: Verify group operations**

- [ ] d on group header deletes group
- [ ] e on group header opens edit flow
- [ ] Creating a new group works
- [ ] Moving a session to a group works

**Step 7: Verify Tab key no longer switches sections**

- [ ] Tab does nothing (or at least doesn't crash)

**Step 8: Update README.md**

Update the keybindings table:
- Remove Tab (switch sections) entry
- Update Enter behavior description for groups
- Note that groups are now inline in the session list
