# Refactor App.cs into Handler Classes

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Split the 2600-line App.cs monolith into focused handler classes, reducing it to ~500 lines of orchestration.

**Architecture:** Extract five handler classes into a new `Handlers/` folder. Each handler receives `AppState` and `CccConfig` via primary constructor, plus `Action` callbacks for cross-cutting concerns (reload sessions, render, reset caches). App.cs becomes a thin shell: main loop, pane capture, key routing, and lifecycle. All method bodies are moved as-is — no behavior changes.

**Tech Stack:** .NET 10, C#, Spectre.Console

---

## Overview

### What moves where

| New File | Methods from App.cs | ~Lines |
|----------|-------------------|--------|
| `Handlers/FlowHelper.cs` | RunFlow, PrintStep, RequireText, PromptWithDefault, PromptOptional, PickDirectory, PickColor, PromptCustomPath, CreateWorktreeWithProgress, SanitizeTmuxSessionName, UniqueSessionName, ScanWorktreeFeatures, WriteFeatureContext, LaunchWithIde, GetFileManagerCommand, constants, _colorPalette | ~350 |
| `Handlers/DiffHandler.cs` | OpenDiffOverlay, HandleDiffOverlayKey, JumpToNextFile, JumpToPreviousFile | ~130 |
| `Handlers/SettingsHandler.cs` | HandleSettingsKey, HandleSettingsEditKey, HandleSettingsRebindKey, ActivateSettingsItem, HandleSettingsAction, AddFavorite, DeleteFavorite, OpenConfig | ~300 |
| `Handlers/SessionHandler.cs` | CreateNewSession, DeleteSession, EditSession, AttachToSession, ToggleExclude, SendQuickKey, SendText, OpenFolder, OpenInIde | ~400 |
| `Handlers/GroupHandler.cs` | OpenGroup, DeleteGroup, EditGroup, DeleteSessionFromGroup, CreateNewGroup, CreateGroupFromWorktree, CreateGroupFromNewWorktrees, CreateGroupManually, FinishGroupCreation, MoveSessionToGroup | ~500 |

### What stays in App.cs (~500 lines)

- `Run()`, `MainLoop()` — lifecycle
- `LoadSessions()`, `LoadGroups()` — orchestration across concerns
- `Render()` — rendering dispatch
- `HandleKey()`, `DispatchAction()`, `HandleMobileKey()`, `DispatchMobileAction()` — key routing
- `HandleInputKey()` — text input mode (small, tightly coupled)
- `UpdateCapturedPane()`, `UpdateAllCapturedPanes()`, `ResizeGridPanes()` — pane capture
- `MoveCursor()`, `MoveGridCursor()`, `MoveGroupCursor()`, `MoveMobileCursor()` — cursor movement
- `ResolveKeyId()` — static key name mapping
- `RefreshKeybindings()` — updates `_keyMap` + `_state.Keybindings`
- `ToggleGridView()` — grid state management
- `RunUpdate()` — self-update

### Shared state via callbacks

Handlers need to trigger App-level side effects. These are passed as `Action` callbacks:

| Callback | Purpose | Used by |
|----------|---------|---------|
| `Action loadSessions` | Reload sessions + groups from tmux | Session, Group, Settings |
| `Action render` | Force a re-render (for confirmation dialogs) | Session, Group, Settings |
| `Action resetPaneCache` | Set `_lastSelectedSession = null` | Session, Group |
| `Action resetGridCache` | Zero out `_lastGridWidth/Height` | Group |
| `Action refreshKeybindings` | Rebuild `_keyMap` + `_state.Keybindings` | Settings |

---

## Task 1: Create FlowHelper

**Files:**
- Create: `Handlers/FlowHelper.cs`
- Modify: `App.cs`

### Step 1: Create `Handlers/FlowHelper.cs`

```csharp
using System.Diagnostics;
using System.Text.Json;
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;
using Spectre.Console;

namespace ClaudeCommandCenter.Handlers;

public class FlowHelper(CccConfig config)
{
    private static string? _flowTitle;

    public const string CancelChoice = "Cancel";
    public const string CustomPathChoice = "Custom path...";
    public const string WorktreePrefix = "\u2442 "; // ⑂

    public static readonly (string Label, string SpectreColor)[] ColorPalette =
    [
        ("Steel Blue", "SteelBlue"),
        ("Indian Red", "IndianRed"),
        ("Medium Purple", "MediumPurple"),
        ("Cadet Blue", "CadetBlue"),
        ("Light Salmon", "LightSalmon3"),
        ("Dark Sea Green", "DarkSeaGreen"),
        ("Dark Khaki", "DarkKhaki"),
        ("Plum", "Plum4"),
        ("Rosy Brown", "RosyBrown"),
        ("Grey Violet", "MediumPurple4"),
        ("Slate", "LightSlateGrey"),
        ("Dusty Teal", "DarkCyan"),
        ("Thistle", "Thistle3"),
    ];

    // --- Static utilities (moved as-is from App.cs) ---

    public static void RunFlow(string title, Action body, AppState state) { /* body from App.cs:2048-2067 */ }
    public static void PrintStep(int current, int total, string label) { /* body from App.cs:2071-2080 */ }
    public static string RequireText(string prompt) { /* body from App.cs:2082-2091 */ }
    public static string? PromptCustomPath() { /* body from App.cs:2237-2245 */ }
    public static string? CreateWorktreeWithProgress(string repoPath, string worktreeDest, string branchName) { /* body from App.cs:2212-2235 */ }
    public static string SanitizeTmuxSessionName(string name) { /* body from App.cs:2913-2917 */ }
    public static string UniqueSessionName(string baseName, ICollection<string> existing, string separator = "-") { /* body from App.cs:2919-2932 */ }
    public static List<WorktreeFeature> ScanWorktreeFeatures(string basePath) { /* body from App.cs:1935-1992 */ }
    public static void WriteFeatureContext(string featurePath, string featureName, Dictionary<string, string> worktrees) { /* body from App.cs:1791-1812 */ }
    public static bool LaunchWithIde(string command, string path) { /* body from App.cs:2353-2400 */ }
    public static string GetFileManagerCommand() { /* body from App.cs:2276-2297 */ }

    // --- Instance methods (need config) ---

    public string PromptWithDefault(string label, string defaultValue) { /* body from App.cs:2093-2107 */ }
    public string PromptOptional(string label, string? currentValue) { /* body from App.cs:2109-2130 */ }
    public string? PickDirectory(string? worktreeBranchHint = null, Action<string>? onWorktreeBranchCreated = null) { /* body from App.cs:2136-2210 */ }
    public string? PickColor() { /* body from App.cs:2011-2045 */ }
}
```

Key transformations when moving method bodies:
- `RunFlow`: Add `AppState state` parameter. Replace `_state.SetStatus(ex.Message)` with `state.SetStatus(ex.Message)`.
- `PickDirectory`: Replace `_config` with `config` (primary constructor parameter). Replace `_cancelChoice` with `CancelChoice`, `_customPathChoice` with `CustomPathChoice`, `_worktreePrefix` with `WorktreePrefix`.
- `PickColor`: Replace `_config.SessionColors` with `config.SessionColors`. Replace `_cancelChoice` with `CancelChoice`. Replace `_colorPalette` with `ColorPalette`.
- `PromptWithDefault`/`PromptOptional`: Replace `_cancelChoice` with `CancelChoice`.

### Step 2: Update App.cs

Replace all internal calls to the moved methods with FlowHelper calls:

```csharp
// In App constructor area, add:
private readonly FlowHelper _flow = new(config); // where config = _config

// Replace calls like:
//   RunFlow("title", () => { ... });
// With:
//   FlowHelper.RunFlow("title", () => { ... }, _state);

// Replace calls like:
//   var dir = PickDirectory(...);
// With:
//   var dir = _flow.PickDirectory(...);

// Replace calls like:
//   SanitizeTmuxSessionName(name)
// With:
//   FlowHelper.SanitizeTmuxSessionName(name)
```

Remove the moved methods, constants, and `_colorPalette` from App.cs.

### Step 3: Build

```bash
dotnet build
```

Expected: success, zero warnings from moved code.

### Step 4: Commit

```
refactor: extract FlowHelper from App.cs
```

---

## Task 2: Create DiffHandler

**Files:**
- Create: `Handlers/DiffHandler.cs`
- Modify: `App.cs`

### Step 1: Create `Handlers/DiffHandler.cs`

```csharp
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;

namespace ClaudeCommandCenter.Handlers;

public class DiffHandler(AppState state)
{
    public void Open()
    {
        // Body from App.cs:1389-1417 (OpenDiffOverlay)
        // Replace _state with state
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
        // Body from App.cs:1419-1474 (HandleDiffOverlayKey)
        // Replace _state with state
    }

    private void JumpToNextFile(int maxScroll)
    {
        // Body from App.cs:1476-1493
    }

    private void JumpToPreviousFile()
    {
        // Body from App.cs:1495-1513
    }
}
```

No other transformations needed — this handler only reads/writes `AppState`.

### Step 2: Update App.cs

```csharp
// Add field:
private readonly DiffHandler _diffHandler;
// Initialize in constructor or after _state is created:
_diffHandler = new DiffHandler(_state);

// In HandleKey(), replace:
//   HandleDiffOverlayKey(key);
// With:
//   _diffHandler.HandleKey(key);

// In DispatchAction(), replace:
//   case "toggle-diff": OpenDiffOverlay(); break;
// With:
//   case "toggle-diff": _diffHandler.Open(); break;
```

Remove `OpenDiffOverlay`, `HandleDiffOverlayKey`, `JumpToNextFile`, `JumpToPreviousFile` from App.cs.

### Step 3: Build

```bash
dotnet build
```

### Step 4: Commit

```
refactor: extract DiffHandler from App.cs
```

---

## Task 3: Create SettingsHandler

**Files:**
- Create: `Handlers/SettingsHandler.cs`
- Modify: `App.cs`

### Step 1: Create `Handlers/SettingsHandler.cs`

```csharp
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;
using Spectre.Console;

namespace ClaudeCommandCenter.Handlers;

public class SettingsHandler(
    AppState state,
    CccConfig config,
    FlowHelper flow,
    Action render,
    Action refreshKeybindings)
{
    public void HandleKey(ConsoleKeyInfo key)
    {
        // Body from App.cs:627-745 (HandleSettingsKey)
        // Replace _state → state, _config → config
        // Replace _flow calls, AddFavorite/DeleteFavorite/OpenConfig are now this.
    }

    private void HandleEditKey(ConsoleKeyInfo key, List<SettingsItem> items)
    {
        // Body from App.cs:747-777 (HandleSettingsEditKey)
    }

    private void HandleRebindKey(ConsoleKeyInfo key, List<SettingsItem> items)
    {
        // Body from App.cs:779-830 (HandleSettingsRebindKey)
        // Calls refreshKeybindings() instead of RefreshKeybindings()
    }

    private void ActivateItem(SettingsItem item)
    {
        // Body from App.cs:832-852 (ActivateSettingsItem)
        // Calls refreshKeybindings() for toggle items
    }

    private void HandleAction(string label)
    {
        // Body from App.cs:854-882 (HandleSettingsAction)
        // "Reset Keybindings" calls refreshKeybindings()
        // "Open Config File" calls OpenConfig()
        // "+ Add Favorite" calls AddFavorite()
    }

    private void AddFavorite()
    {
        // Body from App.cs:891-906
        // Uses FlowHelper.RunFlow(..., state), FlowHelper.PrintStep, FlowHelper.RequireText
    }

    private void DeleteFavorite()
    {
        // Body from App.cs:908-930
        // Uses render() for confirmation dialog
    }

    private void OpenConfig()
    {
        // Body from App.cs:2316-2351
        // Uses config.IdeCommand, FlowHelper.LaunchWithIde()
    }
}
```

### Step 2: Update App.cs

```csharp
// Add field:
private readonly SettingsHandler _settingsHandler;
// Initialize after _state, _config, _flow are ready:
_settingsHandler = new SettingsHandler(_state, _config, _flow, Render, RefreshKeybindings);

// In HandleKey(), replace:
//   HandleSettingsKey(key);
// With:
//   _settingsHandler.HandleKey(key);
```

Remove `HandleSettingsKey`, `HandleSettingsEditKey`, `HandleSettingsRebindKey`, `ActivateSettingsItem`, `HandleSettingsAction`, `AddFavorite`, `DeleteFavorite`, `OpenConfig` from App.cs.

### Step 3: Build

```bash
dotnet build
```

### Step 4: Commit

```
refactor: extract SettingsHandler from App.cs
```

---

## Task 4: Create SessionHandler

**Files:**
- Create: `Handlers/SessionHandler.cs`
- Modify: `App.cs`

### Step 1: Create `Handlers/SessionHandler.cs`

```csharp
using System.Diagnostics;
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;
using Spectre.Console;

namespace ClaudeCommandCenter.Handlers;

public class SessionHandler(
    AppState state,
    CccConfig config,
    FlowHelper flow,
    Action loadSessions,
    Action render,
    Action resetPaneCache)
{
    public void Create(bool claudeAvailable)
    {
        // Body from App.cs:1531-1576 (CreateNewSession)
        // Guard: if (!claudeAvailable) { state.SetStatus(...); return; }
        // Uses FlowHelper.RunFlow(..., state), FlowHelper.PrintStep, flow.PickDirectory,
        //   FlowHelper.SanitizeTmuxSessionName, FlowHelper.UniqueSessionName,
        //   flow.PromptWithDefault, flow.PromptOptional, flow.PickColor
    }

    public void Delete()
    {
        // Body from App.cs:2402-2434 (DeleteSession)
        // Uses render() for confirmation
    }

    public void Edit()
    {
        // Body from App.cs:2543-2600 (EditSession)
        // Uses FlowHelper.RunFlow(..., state), flow.PromptOptional, flow.PickColor
    }

    public void Attach()
    {
        // Body from App.cs:1362-1387 (AttachToSession)
        // After detach: re-enter alt screen, loadSessions(), resetPaneCache(), render()
    }

    public void ToggleExclude()
    {
        // Body from App.cs:2436-2463 (ToggleExclude)
        // Uses resetPaneCache()
    }

    public void SendQuickKey(string key)
    {
        // Body from App.cs:2514-2530
        // Uses resetPaneCache()
    }

    public void SendText()
    {
        // Body from App.cs:2532-2541
    }

    public void OpenFolder()
    {
        // Body from App.cs:2247-2274
        // Uses FlowHelper.GetFileManagerCommand()
    }

    public void OpenInIde()
    {
        // Body from App.cs:2299-2314
        // Uses FlowHelper.LaunchWithIde()
    }
}
```

### Step 2: Update App.cs

```csharp
// Add field:
private readonly SessionHandler _sessionHandler;
// Initialize:
_sessionHandler = new SessionHandler(_state, _config, _flow, LoadSessions, Render, () => _lastSelectedSession = null);

// In DispatchAction(), replace:
//   case "attach": AttachToSession(); break;
// With:
//   case "attach": _sessionHandler.Attach(); break;
// (Same pattern for all session actions)
```

Remove `CreateNewSession`, `DeleteSession`, `EditSession`, `AttachToSession`, `ToggleExclude`, `SendQuickKey`, `SendText`, `OpenFolder`, `OpenInIde`, `LaunchWithIde`, `GetFileManagerCommand` from App.cs. (`LaunchWithIde` and `GetFileManagerCommand` already moved to FlowHelper in Task 1.)

### Step 3: Build

```bash
dotnet build
```

### Step 4: Commit

```
refactor: extract SessionHandler from App.cs
```

---

## Task 5: Create GroupHandler

**Files:**
- Create: `Handlers/GroupHandler.cs`
- Modify: `App.cs`

### Step 1: Create `Handlers/GroupHandler.cs`

```csharp
using System.Text.Json;
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;
using Spectre.Console;

namespace ClaudeCommandCenter.Handlers;

public class GroupHandler(
    AppState state,
    CccConfig config,
    FlowHelper flow,
    Action loadSessions,
    Action render,
    Action resetPaneCache,
    Action resetGridCache)
{
    public void Open()
    {
        // Body from App.cs:1060-1076 (OpenGroup)
        // After EnterGroupGrid: needs to trigger ResizeGridPanes
        // Pass Action resizeGridPanes callback, OR have App call ResizeGridPanes after Open returns
    }

    public void Delete()
    {
        // Body from App.cs:1078-1112 (DeleteGroup)
    }

    public void Edit()
    {
        // Body from App.cs:1157-1325 (EditGroup)
        // Largest single method — uses FlowHelper extensively
    }

    public void DeleteSessionFromGroup()
    {
        // Body from App.cs:1114-1155
        // After killing last session: state.LeaveGroupGrid(), resetPaneCache(), resetGridCache()
    }

    public void CreateNew(bool claudeAvailable)
    {
        // Body from App.cs:1578-1620 (CreateNewGroup)
        // Routes to CreateFromWorktree, CreateFromNewWorktrees, or CreateManually
    }

    public void MoveSessionToGroup()
    {
        // Body from App.cs:2465-2512
    }

    private void CreateFromWorktree(string basePath)
    {
        // Body from App.cs:1622-1681
    }

    private void CreateFromNewWorktrees()
    {
        // Body from App.cs:1683-1789
    }

    private void CreateManually()
    {
        // Body from App.cs:1814-1899
    }

    private void FinishCreation(string groupName)
    {
        // Body from App.cs:1901-1911
        // Calls loadSessions(), state.EnterGroupGrid(), resetPaneCache()
        // Note: needs ResizeGridPanes — use a callback or have App call it after
    }
}
```

**ResizeGridPanes concern:** `OpenGroup` and `FinishCreation` call `ResizeGridPanes()` which lives in App (it accesses `_lastGridWidth/Height` and the grid session list). Two options:

- **Option A (simpler):** Add an `Action resizeGridPanes` callback to GroupHandler constructor.
- **Option B:** Have GroupHandler set state (EnterGroupGrid), then App.cs checks and calls ResizeGridPanes when needed.

Use **Option A** — add `Action resizeGridPanes` to constructor.

### Step 2: Update App.cs

```csharp
// Add field:
private readonly GroupHandler _groupHandler;
// Initialize:
_groupHandler = new GroupHandler(
    _state, _config, _flow, LoadSessions, Render,
    () => _lastSelectedSession = null,
    () => { _lastGridWidth = 0; _lastGridHeight = 0; },
    ResizeGridPanes);  // 8th param for resizeGridPanes callback

// In DispatchAction(), the group-focused section becomes:
//   case "attach": _groupHandler.Open(); return;
//   case "delete-session": _groupHandler.Delete(); return;
//   case "edit-session": _groupHandler.Edit(); return;

// Group grid delete:
//   if (_state.ActiveGroup != null && actionId == "delete-session")
//       { _groupHandler.DeleteSessionFromGroup(); return; }

// Main switch:
//   case "new-group": _groupHandler.CreateNew(_claudeAvailable); break;
//   case "move-to-group": _groupHandler.MoveSessionToGroup(); break;
```

Remove `OpenGroup`, `DeleteGroup`, `EditGroup`, `DeleteSessionFromGroup`, `CreateNewGroup`, `CreateGroupFromWorktree`, `CreateGroupFromNewWorktrees`, `CreateGroupManually`, `FinishGroupCreation`, `MoveSessionToGroup`, `ScanWorktreeFeatures`, `WriteFeatureContext` from App.cs. (`ScanWorktreeFeatures` and `WriteFeatureContext` already moved to FlowHelper in Task 1.)

### Step 3: Build

```bash
dotnet build
```

### Step 4: Commit

```
refactor: extract GroupHandler from App.cs
```

---

## Task 6: Final cleanup + verify

**Files:**
- Modify: `App.cs`
- Modify: `CLAUDE.md`

### Step 1: Clean up App.cs

- Remove any dead `using` statements
- Verify all handler fields are initialized
- Verify `DispatchAction` and `DispatchMobileAction` route to correct handlers
- Remove any orphaned private methods

### Step 2: Verify line count

```bash
wc -l App.cs Handlers/*.cs
```

Expected: App.cs ~500 lines, handlers total ~1700 lines, sum ~2200 (slight reduction from removing duplication in dispatch).

### Step 3: Build

```bash
dotnet build
```

### Step 4: Update CLAUDE.md

Update the "App.cs (~2000 lines)" section to reflect the new structure:

```markdown
### App.cs (~500 lines)

Orchestration shell. Contains the main loop, pane capture, key routing, cursor movement, and lifecycle management. Delegates domain logic to handlers.

### Handlers/

| Handler | Purpose |
|---------|---------|
| `FlowHelper` | Flow UI scaffolding (RunFlow, prompts, pickers), static utilities (sanitize names, worktree scanning, IDE launch) |
| `DiffHandler` | Diff overlay view — open, scroll, jump between files |
| `SettingsHandler` | Settings view — navigation, editing, rebinding, favorites |
| `SessionHandler` | Session CRUD — create, delete, edit, attach, exclude, send keys, open folder/IDE |
| `GroupHandler` | Group CRUD — create (3 modes), delete, edit, move session to group |
```

### Step 5: Commit

```
refactor: clean up App.cs and update CLAUDE.md after handler extraction
```
