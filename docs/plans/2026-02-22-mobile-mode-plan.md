# Mobile Mode Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `-m`/`--mobile` flag that launches CCC in a stripped-down single-column mode optimized for phone SSH clients.

**Architecture:** Mobile mode is a rendering concern — same app loop, same tmux interactions, just a different `Renderer` path and restricted action set. A `bool MobileMode` on `AppState` gates all differences. Group navigation is replaced by a cycle filter (`g` key).

**Tech Stack:** .NET 10, Spectre.Console 0.54.0, existing CCC architecture.

---

### Task 1: Parse -m flag in Program.cs and pass to App

**Files:**
- Modify: `Program.cs`
- Modify: `App.cs:10-12` (constructor)

**Step 1: Add CLI arg parsing to Program.cs**

```csharp
// Program.cs — replace the try block
try
{
    var mobile = args.Contains("-m") || args.Contains("--mobile");
    var app = new App(mobile);
    app.Run();
}
```

**Step 2: Add constructor parameter to App.cs**

In `App.cs`, change the field declarations and add a constructor:

```csharp
public class App
{
    private readonly AppState _state;
    private readonly CccConfig _config = ConfigService.Load();
    // ... other fields unchanged ...

    public App(bool mobileMode = false)
    {
        _state = new AppState { MobileMode = mobileMode };
    }
```

Remove the field initializer `private readonly AppState _state = new();` since the constructor now handles it.

**Step 3: Build and verify**

Run: `dotnet build`
Expected: Build succeeds. No behavior change when running without `-m`.

**Step 4: Commit**

```
feat: add -m/--mobile CLI flag parsing
```

---

### Task 2: Add mobile state properties to AppState

**Files:**
- Modify: `UI/AppState.cs`

**Step 1: Add MobileMode, GroupFilterIndex, and TopIndex properties**

Add these properties to the `AppState` class, after the existing `ActiveSection` property (around line 19):

```csharp
// Mobile mode state
public bool MobileMode { get; set; }
public int GroupFilterIndex { get; set; } // 0 = All, 1+ = group index
public int TopIndex { get; set; } // Scroll offset for mobile list
```

**Step 2: Add GetMobileVisibleSessions method**

Add this method after `GetGridSessions()` (around line 75):

```csharp
public List<TmuxSession> GetMobileVisibleSessions()
{
    if (GroupFilterIndex == 0)
        return GetStandaloneSessions();

    var groupIdx = GroupFilterIndex - 1;
    if (groupIdx < Groups.Count)
    {
        var group = Groups[groupIdx];
        var groupSessionNames = new HashSet<string>(group.Sessions);
        return Sessions.Where(s => groupSessionNames.Contains(s.Name)).ToList();
    }

    return GetStandaloneSessions();
}
```

**Step 3: Add CycleGroupFilter method**

Add after `GetMobileVisibleSessions`:

```csharp
public void CycleGroupFilter()
{
    if (Groups.Count == 0) return;

    GroupFilterIndex++;
    if (GroupFilterIndex > Groups.Count)
        GroupFilterIndex = 0;

    CursorIndex = 0;
    TopIndex = 0;
}
```

**Step 4: Add GetGroupFilterLabel method**

```csharp
public string GetGroupFilterLabel()
{
    if (GroupFilterIndex == 0 || GroupFilterIndex > Groups.Count)
        return "All";
    return Groups[GroupFilterIndex - 1].Name;
}
```

**Step 5: Update GetSelectedSession for mobile**

Modify `GetSelectedSession()` to handle mobile mode. Replace the existing method:

```csharp
public TmuxSession? GetSelectedSession()
{
    if (MobileMode)
    {
        var sessions = GetMobileVisibleSessions();
        if (CursorIndex >= 0 && CursorIndex < sessions.Count)
            return sessions[CursorIndex];
        return null;
    }

    // When groups section is focused in list view, no session is selected
    if (ViewMode == ViewMode.List && ActiveGroup == null && ActiveSection == ActiveSection.Groups)
        return null;

    var list = ViewMode == ViewMode.Grid ? GetGridSessions() : GetVisibleSessions();
    if (CursorIndex >= 0 && CursorIndex < list.Count)
        return list[CursorIndex];
    return null;
}
```

**Step 6: Update ClampCursor for mobile**

Modify `ClampCursor()` to handle mobile mode:

```csharp
public void ClampCursor()
{
    var visible = MobileMode ? GetMobileVisibleSessions() : GetVisibleSessions();
    CursorIndex = visible.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, visible.Count - 1);
}
```

**Step 7: Add EnsureCursorVisible method for scroll management**

```csharp
public void EnsureCursorVisible(int visibleHeight)
{
    if (CursorIndex < TopIndex)
        TopIndex = CursorIndex;
    else if (CursorIndex >= TopIndex + visibleHeight)
        TopIndex = CursorIndex - visibleHeight + 1;
}
```

**Step 8: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

**Step 9: Commit**

```
feat: add mobile mode state properties to AppState
```

---

### Task 3: Add mobile rendering to Renderer.cs

**Files:**
- Modify: `UI/Renderer.cs`

**Step 1: Add BuildMobileLayout entry point**

Add at the top of `BuildLayout`, before the existing layout code (line 18):

```csharp
public static IRenderable BuildLayout(AppState state, string? capturedPane,
    Dictionary<string, string>? allCapturedPanes = null)
{
    if (state.MobileMode)
        return BuildMobileLayout(state);

    // ... existing code unchanged ...
}
```

**Step 2: Add BuildMobileLayout method**

Add after `BuildLayout`:

```csharp
private static IRenderable BuildMobileLayout(AppState state)
{
    var sessions = state.GetMobileVisibleSessions();
    var listHeight = Math.Max(1, Console.WindowHeight - 6);
    // 1 header + 1 separator + 3 detail + 1 status = 6 lines overhead

    state.EnsureCursorVisible(listHeight);

    var layout = new Layout("Root")
        .SplitRows(
            new Layout("Header").Size(1),
            new Layout("List"),
            new Layout("Detail").Size(4), // 1 separator + 3 info lines
            new Layout("StatusBar").Size(1));

    layout["Header"].Update(BuildMobileHeader(state, sessions.Count));
    layout["List"].Update(BuildMobileSessionList(state, sessions, listHeight));
    layout["Detail"].Update(BuildMobileDetailBar(state));
    layout["StatusBar"].Update(BuildMobileStatusBar(state));

    return layout;
}
```

**Step 3: Add BuildMobileHeader method**

```csharp
private static IRenderable BuildMobileHeader(AppState state, int sessionCount)
{
    var filterLabel = state.GetGroupFilterLabel();
    var left = new Markup($"[mediumpurple3 bold] CCC[/] [grey50]v{_version}[/] [grey]- {sessionCount} sessions[/]");
    var right = new Markup($"[grey50][{Markup.Escape(filterLabel)}][/] ");
    return new Columns(left, right) { Expand = true };
}
```

**Step 4: Add BuildMobileSessionList method**

```csharp
private static IRenderable BuildMobileSessionList(AppState state, List<TmuxSession> sessions, int listHeight)
{
    var rows = new List<IRenderable>();

    if (sessions.Count == 0)
    {
        rows.Add(new Markup("  [grey]No sessions[/]"));
    }
    else
    {
        var end = Math.Min(state.TopIndex + listHeight, sessions.Count);
        for (var i = state.TopIndex; i < end; i++)
        {
            var session = sessions[i];
            var isSelected = i == state.CursorIndex;
            rows.Add(BuildMobileSessionRow(session, isSelected));
        }
    }

    return new Rows(rows);
}
```

**Step 5: Add BuildMobileSessionRow method**

```csharp
private static Markup BuildMobileSessionRow(TmuxSession session, bool isSelected)
{
    var name = Markup.Escape(session.Name);
    var spinner = Markup.Escape(GetSpinnerFrame());
    var isWorking = !session.IsWaitingForInput;
    var status = isWorking ? spinner : "!";

    if (session.IsExcluded)
    {
        var excludedStatus = session.IsWaitingForInput ? "[grey42]![/]" : $"[grey35]{spinner}[/]";
        if (isSelected)
            return new Markup($"[grey50 on grey19] {excludedStatus} {name} [/]");
        return new Markup($" {excludedStatus} [grey35]{name}[/]");
    }

    if (isSelected)
    {
        var bg = session.ColorTag ?? "grey37";
        return new Markup($"[white on {bg}] {status} {name} [/]");
    }

    if (isWorking)
        return new Markup($" [green]{spinner}[/] [navajowhite1]{name}[/]");
    if (session.IsWaitingForInput)
        return new Markup($" [yellow bold]![/] [navajowhite1]{name}[/]");

    return new Markup($"   [grey70]{name}[/]");
}
```

**Step 6: Add BuildMobileDetailBar method**

```csharp
private static IRenderable BuildMobileDetailBar(AppState state)
{
    var session = state.GetSelectedSession();
    var rows = new List<IRenderable>();

    rows.Add(new Rule().RuleStyle(Style.Parse("grey27")));

    if (session == null)
    {
        rows.Add(new Markup(" [grey]No session selected[/]"));
        rows.Add(new Text(""));
        rows.Add(new Text(""));
        return new Rows(rows);
    }

    var color = session.ColorTag ?? "grey70";
    rows.Add(new Markup($" [{color} bold]{Markup.Escape(session.Name)}[/]"));

    var branch = session.GitBranch != null ? $"[aqua]{Markup.Escape(session.GitBranch)}[/]" : "[grey]no branch[/]";
    var path = session.CurrentPath != null ? $" [grey50]{Markup.Escape(ShortenPath(session.CurrentPath))}[/]" : "";
    rows.Add(new Markup($" {branch}{path}"));

    var statusText = session.IsWaitingForInput
        ? "[yellow bold]waiting for input[/]"
        : session.IsAttached ? "[green]attached[/]" : "[grey]working[/]";
    var desc = !string.IsNullOrWhiteSpace(session.Description)
        ? $" [grey50]- {Markup.Escape(session.Description)}[/]"
        : "";
    rows.Add(new Markup($" {statusText}{desc}"));

    return new Rows(rows);
}
```

**Step 7: Add BuildMobileStatusBar method**

```csharp
private static Markup BuildMobileStatusBar(AppState state)
{
    if (state.IsInputMode)
        return BuildInputStatusBar(state);

    var status = state.GetActiveStatus();
    if (status != null)
        return new Markup($" [yellow]{Markup.Escape(status)}[/]");

    var session = state.GetSelectedSession();
    var parts = new List<string>();

    if (session?.IsWaitingForInput == true)
    {
        parts.Add("[grey70 bold]Y[/][grey] approve [/]");
        parts.Add("[grey70 bold]N[/][grey] reject [/]");
    }

    parts.Add("[grey70 bold]S[/][grey] send [/]");
    parts.Add("[grey70 bold]Enter[/][grey] attach [/]");

    if (state.Groups.Count > 0)
        parts.Add("[grey70 bold]g[/][grey] filter [/]");

    parts.Add("[grey70 bold]q[/][grey] quit[/]");

    return new Markup(" " + string.Join(" ", parts));
}
```

**Step 8: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

**Step 9: Commit**

```
feat: add mobile mode renderer
```

---

### Task 4: Wire up mobile mode in App.cs

**Files:**
- Modify: `App.cs`

**Step 1: Skip grid pane resize and grid pane capture in mobile mode**

In `MainLoop()` (around line 116), wrap `ResizeGridPanes()` call:

```csharp
if (!_state.MobileMode)
    ResizeGridPanes();
```

In `UpdateCapturedPane()` (around line 201), wrap the grid branch:

```csharp
if (_state.ViewMode == ViewMode.Grid && !_state.MobileMode)
    return UpdateAllCapturedPanes();
```

In mobile mode we also don't need pane capture at all (no preview), so add early return at the top of `UpdateCapturedPane`:

```csharp
private bool UpdateCapturedPane()
{
    // Refresh waiting-for-input status on all sessions (single tmux call)
    TmuxService.DetectWaitingForInputBatch(_state.Sessions);
    _hasSpinningSessions = _state.Sessions.Any(s => !s.IsWaitingForInput);

    // Re-sort groups so those needing input stay at the top
    _state.SortGroupsByStatus();

    // Mobile mode doesn't show pane previews
    if (_state.MobileMode)
        return true; // Always re-render to update status indicators

    // ... rest unchanged ...
```

**Step 2: Modify Render() to pass mobile layout**

No change needed — `Renderer.BuildLayout` already checks `state.MobileMode` (from Task 3).

**Step 3: Modify HandleKey for mobile mode**

In `HandleKey()`, add mobile-specific handling before the existing escape/tab/grid logic (after input mode check, around line 293):

```csharp
if (_state.MobileMode)
{
    HandleMobileKey(key);
    return;
}
```

**Step 4: Add HandleMobileKey method**

Add new method in App.cs:

```csharp
private void HandleMobileKey(ConsoleKeyInfo key)
{
    if (_state.HasPendingStatus)
    {
        _state.ClearStatus();
        return;
    }

    // Navigation
    switch (key.Key)
    {
        case ConsoleKey.UpArrow:
            MoveMobileCursor(-1);
            return;
        case ConsoleKey.DownArrow:
            MoveMobileCursor(1);
            return;
    }

    var keyId = ResolveKeyId(key);

    // Mobile-only: group filter cycling
    if (keyId == "g")
    {
        _state.CycleGroupFilter();
        _lastSelectedSession = null;
        return;
    }

    if (_keyMap.TryGetValue(keyId, out var actionId))
        DispatchMobileAction(actionId);
}
```

**Step 5: Add MoveMobileCursor method**

```csharp
private void MoveMobileCursor(int delta)
{
    var visible = _state.GetMobileVisibleSessions();
    if (visible.Count == 0) return;
    _state.CursorIndex = Math.Clamp(_state.CursorIndex + delta, 0, visible.Count - 1);
    _lastSelectedSession = null;
}
```

**Step 6: Add DispatchMobileAction method**

```csharp
private void DispatchMobileAction(string actionId)
{
    switch (actionId)
    {
        case "navigate-up":
            MoveMobileCursor(-1);
            break;
        case "navigate-down":
            MoveMobileCursor(1);
            break;
        case "approve":
            SendQuickKey("y");
            break;
        case "reject":
            SendQuickKey("n");
            break;
        case "send-text":
            SendText();
            break;
        case "attach":
            AttachToSession();
            break;
        case "refresh":
            LoadSessions();
            _state.SetStatus("Refreshed");
            break;
        case "quit":
            _state.Running = false;
            break;
        // All other actions are disabled in mobile mode
    }
}
```

**Step 7: Build and verify**

Run: `dotnet build`
Expected: Build succeeds.

**Step 8: Manual test**

Run: `dotnet run -- -m`
Expected: Mobile mode launches with single-column layout, session list, detail bar, and context-sensitive status bar.

**Step 9: Commit**

```
feat: wire up mobile mode input handling and action dispatch
```

---

### Task 5: Update README.md

**Files:**
- Modify: `README.md`

**Step 1: Document the -m flag**

Add a "Mobile Mode" section to README.md describing:
- The `-m`/`--mobile` flag
- What it does (single-column, no grid, no preview)
- Available keys in mobile mode
- Group filter cycling with `g`

Also add `-m` to any existing usage/CLI section.

**Step 2: Commit**

```
docs: add mobile mode documentation to README
```

---

### Task 6: Version bump

**Files:**
- Modify: `ClaudeCommandCenter.csproj`

**Step 1: Bump minor version**

Change `<Version>2.13.0</Version>` to `<Version>2.14.0</Version>` (new feature).

**Step 2: Final build verification**

Run: `dotnet build`
Expected: Build succeeds with version 2.14.0.

**Step 3: Commit**

```
feat: bump version to 2.14.0 for mobile mode
```
