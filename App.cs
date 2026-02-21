using System.Diagnostics;
using System.Text.Json;
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;
using Spectre.Console;

namespace ClaudeCommandCenter;

public class App
{
    private readonly AppState _state = new();
    private readonly CccConfig _config = ConfigService.Load();
    private Dictionary<string, string> _keyMap = new();
    private string? _capturedPane;
    private Dictionary<string, string> _allCapturedPanes = new();
    private DateTime _lastCapture = DateTime.MinValue;
    private string? _lastSelectedSession;
    private string? _lastSpinnerFrame;
    private bool _hasSpinningSessions;
    private bool _claudeAvailable;
    private Task<string?>? _updateCheck;
    private bool _wantsUpdate;
    private int _lastGridWidth;
    private int _lastGridHeight;

    public void Run()
    {
        if (!TmuxService.HasTmux())
        {
            AnsiConsole.MarkupLine("[red]tmux is not installed or not in PATH.[/]");
            return;
        }

        if (TmuxService.IsInsideTmux())
        {
            AnsiConsole.MarkupLine("[red]ClaudeCommandCenter should run outside tmux.[/]");
            AnsiConsole.MarkupLine("[grey]It manages tmux sessions from the outside. Exit tmux first.[/]");
            return;
        }

        _claudeAvailable = TmuxService.HasClaude();
        if (!_claudeAvailable)
        {
            AnsiConsole.MarkupLine("[yellow]Warning: 'claude' was not found in PATH.[/]");
            AnsiConsole.MarkupLine("[grey]New sessions will fail to start. Install Claude Code: https://docs.anthropic.com/en/docs/claude-code[/]");
        }

        var bindings = KeyBindingService.Resolve(_config);
        _keyMap = KeyBindingService.BuildKeyMap(bindings);
        _state.Keybindings = bindings;

        LoadSessions();
        _updateCheck = UpdateChecker.CheckForUpdateAsync();

        try
        {
            // Try alternate screen buffer for clean TUI
            Console.Write("\e[?1049h"); // Enter alternate screen
            Console.CursorVisible = false;

            MainLoop();
        }
        finally
        {
            Console.CursorVisible = true;
            Console.Write("\e[?1049l"); // Leave alternate screen
        }

        if (_wantsUpdate)
            RunUpdate();
    }

    private void MainLoop()
    {
        Render();

        while (_state.Running)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                HandleKey(key);
                Render();
            }

            // Check if update check completed
            if (_updateCheck is { IsCompleted: true })
            {
                var latest = _updateCheck.Result;
                _updateCheck = null;
                if (latest != null)
                {
                    _state.LatestVersion = latest;
                    Render();
                }
            }

            // Re-render when a status message expires
            if (_state.HasPendingStatus)
                if (_state.GetActiveStatus() == null)
                    Render();

            // Re-render when spinner frame advances (only if sessions were spinning at last poll)
            var spinnerFrame = Renderer.GetSpinnerFrame();
            if (spinnerFrame != _lastSpinnerFrame)
            {
                _lastSpinnerFrame = spinnerFrame;
                if (_hasSpinningSessions)
                    Render();
            }

            // Periodically capture pane content for preview
            if ((DateTime.Now - _lastCapture).TotalMilliseconds > 500)
            {
                ResizeGridPanes();
                if (UpdateCapturedPane())
                    Render();
                _lastCapture = DateTime.Now;
            }

            Thread.Sleep(30);
        }
    }

    private void LoadSessions()
    {
        var oldSessions = _state.Sessions.ToDictionary(s => s.Name);
        _state.Sessions = TmuxService.ListSessions();
        foreach (var s in _state.Sessions)
        {
            if (_config.SessionDescriptions.TryGetValue(s.Name, out var desc))
                s.Description = desc;
            if (_config.SessionColors.TryGetValue(s.Name, out var color))
                s.ColorTag = color;
            s.IsExcluded = _config.ExcludedSessions.Contains(s.Name);
            TmuxService.ApplyStatusColor(s.Name, color ?? "grey42");

            // Preserve content tracking state so sessions don't briefly flash as "working"
            if (oldSessions.TryGetValue(s.Name, out var old))
            {
                s.PreviousContentHash = old.PreviousContentHash;
                s.StableContentCount = old.StableContentCount;
                s.IsWaitingForInput = old.IsWaitingForInput;
            }
        }

        LoadGroups();
        _state.ClampCursor();
    }

    private void LoadGroups()
    {
        var liveSessionNames = new HashSet<string>(_state.Sessions.Select(s => s.Name));
        var sessionLookup = _state.Sessions.ToDictionary(s => s.Name);

        // Clean up persisted config: remove dead sessions and empty groups
        var configChanged = false;
        var emptyGroups = new List<string>();
        foreach (var (name, group) in _config.Groups)
        {
            var removed = group.Sessions.RemoveAll(s => !liveSessionNames.Contains(s));
            if (removed > 0)
                configChanged = true;
            if (group.Sessions.Count == 0)
                emptyGroups.Add(name);
        }

        foreach (var name in emptyGroups)
            _config.Groups.Remove(name);

        if (configChanged)
            ConfigService.SaveConfig(_config);

        _state.Groups = _config.Groups.Values
            .Select(g => new SessionGroup
            {
                Name = g.Name,
                Description = g.Description,
                Color = g.Color,
                WorktreePath = g.WorktreePath,
                Sessions = g.Sessions.ToList(),
            })
            .OrderByDescending(g => g.Sessions.Any(name =>
                sessionLookup.TryGetValue(name, out var s) && s.IsWaitingForInput))
            .ThenBy(g => g.Name)
            .ToList();
        _state.ClampGroupCursor();
    }

    private bool UpdateCapturedPane()
    {
        // Refresh waiting-for-input status on all sessions (single tmux call)
        TmuxService.DetectWaitingForInputBatch(_state.Sessions);
        _hasSpinningSessions = _state.Sessions.Any(s => !s.IsWaitingForInput);

        // Re-sort groups so those needing input stay at the top
        _state.SortGroupsByStatus();

        // In grid mode, capture panes for visible sessions (all or group-filtered)
        if (_state.ViewMode == ViewMode.Grid)
            return UpdateAllCapturedPanes();

        var session = _state.GetSelectedSession();
        var sessionName = session?.Name;

        if (sessionName != _lastSelectedSession)
        {
            _lastSelectedSession = sessionName;
            _capturedPane = session != null ? TmuxService.CapturePaneContent(session.Name) : null;
            return true;
        }

        if (session == null)
            return false;

        var newContent = TmuxService.CapturePaneContent(session.Name);
        if (newContent != _capturedPane)
        {
            _capturedPane = newContent;
            return true;
        }

        return false;
    }

    private bool UpdateAllCapturedPanes()
    {
        var changed = false;
        var newPanes = new Dictionary<string, string>();
        var visibleSessions = _state.GetGridSessions();

        foreach (var session in visibleSessions)
        {
            var content = TmuxService.CapturePaneContent(session.Name);
            if (content != null)
                newPanes[session.Name] = content;

            if (!changed)
            {
                _allCapturedPanes.TryGetValue(session.Name, out var oldContent);
                if (content != oldContent)
                    changed = true;
            }
        }

        _allCapturedPanes = newPanes;
        return changed;
    }

    private void ResizeGridPanes()
    {
        if (_state.ViewMode != ViewMode.Grid)
            return;

        var sessions = _state.GetGridSessions();
        if (sessions.Count == 0)
            return;

        var (cols, gridRows) = _state.GetGridDimensions();
        if (cols == 0 || gridRows == 0)
            return;

        // Width matches grid cell so Claude Code wraps content to fit.
        // Full terminal height so Claude Code isn't vertically cramped.
        var targetWidth = Math.Max(20, Console.WindowWidth / cols - 4);
        var targetHeight = Console.WindowHeight;

        if (targetWidth == _lastGridWidth && targetHeight == _lastGridHeight)
            return;

        _lastGridWidth = targetWidth;
        _lastGridHeight = targetHeight;

        foreach (var session in sessions)
            TmuxService.ResizeWindow(session.Name, targetWidth, targetHeight);
    }

    private void Render()
    {
        Console.SetCursorPosition(0, 0);
        AnsiConsole.Write(Renderer.BuildLayout(_state, _capturedPane, _allCapturedPanes));
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (_state.IsInputMode)
        {
            HandleInputKey(key);
            return;
        }

        if (_state.HasPendingStatus)
        {
            _state.ClearStatus();
            return;
        }

        // Escape from group grid returns to list
        if (key.Key == ConsoleKey.Escape && _state.ActiveGroup != null)
        {
            _state.LeaveGroupGrid();
            _lastSelectedSession = null;
            _lastGridWidth = 0;
            _lastGridHeight = 0;
            return;
        }

        // Tab switches between Sessions and Groups sections in list view
        if (key.Key == ConsoleKey.Tab && _state.ViewMode == ViewMode.List && _state.ActiveGroup == null)
        {
            if (_state.ActiveSection == ActiveSection.Sessions && _state.Groups.Count > 0)
                _state.ActiveSection = ActiveSection.Groups;
            else
                _state.ActiveSection = ActiveSection.Sessions;
            _lastSelectedSession = null;
            return;
        }

        // Grid mode arrow key handling (both regular grid and group grid)
        if (_state.ViewMode == ViewMode.Grid)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    MoveGridCursor(0, -1);
                    return;
                case ConsoleKey.DownArrow:
                    MoveGridCursor(0, 1);
                    return;
                case ConsoleKey.LeftArrow:
                    MoveGridCursor(-1, 0);
                    return;
                case ConsoleKey.RightArrow:
                    MoveGridCursor(1, 0);
                    return;
            }
        }
        else
        {
            // List view arrow keys
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (_state.ActiveSection == ActiveSection.Groups)
                        MoveGroupCursor(-1);
                    else
                        MoveCursor(-1);
                    return;
                case ConsoleKey.DownArrow:
                    if (_state.ActiveSection == ActiveSection.Groups)
                        MoveGroupCursor(1);
                    else
                        MoveCursor(1);
                    return;
            }
        }

        var keyId = ResolveKeyId(key);
        if (_keyMap.TryGetValue(keyId, out var actionId))
            DispatchAction(actionId);
    }

    private static string ResolveKeyId(ConsoleKeyInfo key)
    {
        return key.Key switch
        {
            ConsoleKey.Enter => "Enter",
            ConsoleKey.UpArrow => "UpArrow",
            ConsoleKey.DownArrow => "DownArrow",
            _ => key.KeyChar.ToString(),
        };
    }

    private void DispatchAction(string actionId)
    {
        // When Groups section is focused in list view, intercept attach, delete, and edit
        if (_state.ViewMode == ViewMode.List && _state.ActiveSection == ActiveSection.Groups)
        {
            switch (actionId)
            {
                case "attach":
                    OpenGroup();
                    return;
                case "delete-session":
                    DeleteGroup();
                    return;
                case "edit-session":
                    EditGroup();
                    return;
                case "move-to-group":
                    return; // Not applicable when focused on groups
            }
        }

        // When in group grid, delete removes session from group
        if (_state.ActiveGroup != null && actionId == "delete-session")
        {
            DeleteSessionFromGroup();
            return;
        }

        switch (actionId)
        {
            case "navigate-up":
                MoveCursor(-1);
                break;
            case "navigate-down":
                MoveCursor(1);
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
            case "toggle-grid":
                ToggleGridView();
                break;
            case "new-session":
                CreateNewSession();
                break;
            case "new-group":
                CreateNewGroup();
                break;
            case "open-folder":
                OpenFolder();
                break;
            case "open-ide":
                OpenInIde();
                break;
            case "open-config":
                OpenConfig();
                break;
            case "delete-session":
                DeleteSession();
                break;
            case "edit-session":
                EditSession();
                break;
            case "toggle-exclude":
                ToggleExclude();
                break;
            case "move-to-group":
                MoveSessionToGroup();
                break;
            case "update":
                if (_state.LatestVersion != null)
                {
                    _wantsUpdate = true;
                    _state.Running = false;
                }

                break;
            case "refresh":
                LoadSessions();
                _state.SetStatus("Refreshed");
                break;
            case "quit":
                _state.Running = false;
                break;
        }
    }

    private void HandleInputKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _state.IsInputMode = false;
                _state.InputBuffer = "";
                _state.SetStatus("Cancelled");
                break;

            case ConsoleKey.Enter:
                var text = _state.InputBuffer;
                var target = _state.InputTarget;
                _state.IsInputMode = false;
                _state.InputBuffer = "";

                if (text.Length > 0 && target != null)
                {
                    var sendError = TmuxService.SendKeys(target, text);
                    if (sendError == null)
                    {
                        _state.SetStatus($"Sent to {target}");
                        _lastSelectedSession = null;
                    }
                    else
                    {
                        _state.SetStatus(sendError);
                    }
                }
                else
                {
                    _state.SetStatus("Cancelled");
                }

                break;

            case ConsoleKey.Backspace:
                if (_state.InputBuffer.Length > 0)
                    _state.InputBuffer = _state.InputBuffer[..^1];
                break;

            default:
                if (key.KeyChar >= ' ' && _state.InputBuffer.Length < 500)
                    _state.InputBuffer += key.KeyChar;
                break;
        }
    }

    private void MoveCursor(int delta)
    {
        var visible = _state.GetVisibleSessions();
        if (visible.Count == 0)
            return;
        _state.CursorIndex = Math.Clamp(_state.CursorIndex + delta, 0, visible.Count - 1);
        _lastSelectedSession = null; // Force pane recapture
    }

    private void MoveGridCursor(int dx, int dy)
    {
        var visible = _state.GetGridSessions();
        if (visible.Count == 0)
            return;

        var (cols, rows) = _state.GetGridDimensions();
        if (cols == 0)
            return;

        var col = _state.CursorIndex % cols;
        var row = _state.CursorIndex / cols;

        col += dx;
        row += dy;

        // Wrap
        if (col < 0)
            col = cols - 1;
        if (col >= cols)
            col = 0;
        if (row < 0)
            row = rows - 1;
        if (row >= rows)
            row = 0;

        var newIndex = row * cols + col;
        if (newIndex < visible.Count)
        {
            _state.CursorIndex = newIndex;
            _lastSelectedSession = null;
        }
    }

    private void MoveGroupCursor(int delta)
    {
        if (_state.Groups.Count == 0)
            return;
        _state.GroupCursor = Math.Clamp(_state.GroupCursor + delta, 0, _state.Groups.Count - 1);
    }

    private void OpenGroup()
    {
        var group = _state.GetSelectedGroup();
        if (group == null)
            return;

        if (group.Sessions.Count == 0)
        {
            // Stale group — offer to remove
            _state.SetStatus($"Group '{group.Name}' has no live sessions. Press d to remove.");
            return;
        }

        _state.EnterGroupGrid(group.Name);
        _lastSelectedSession = null;
        ResizeGridPanes();
    }

    private void DeleteGroup()
    {
        var group = _state.GetSelectedGroup();
        if (group == null)
            return;

        var liveCount = group.Sessions.Count;
        var msg = liveCount > 0
            ? $"Kill group '{group.Name}' and {liveCount} session(s)? (y/n)"
            : $"Remove stale group '{group.Name}'? (y/n)";

        _state.SetStatus(msg);
        Render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            foreach (var sessionName in group.Sessions.ToList())
            {
                TmuxService.KillSession(sessionName);
                ConfigService.RemoveDescription(_config, sessionName);
                ConfigService.RemoveColor(_config, sessionName);
                ConfigService.RemoveExcluded(_config, sessionName);
            }

            ConfigService.RemoveGroup(_config, group.Name);
            LoadSessions();
            _state.SetStatus("Group deleted");
        }
        else
        {
            _state.SetStatus("Cancelled");
        }
    }

    private void DeleteSessionFromGroup()
    {
        var session = _state.GetSelectedSession();
        if (session == null || _state.ActiveGroup == null)
            return;

        _state.SetStatus($"Kill '{session.Name}' from group? (y/n)");
        Render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            TmuxService.KillSession(session.Name);
            ConfigService.RemoveDescription(_config, session.Name);
            ConfigService.RemoveColor(_config, session.Name);
            ConfigService.RemoveExcluded(_config, session.Name);
            ConfigService.RemoveSessionFromGroup(_config, _state.ActiveGroup, session.Name);
            LoadSessions();

            // If group is now empty, leave grid
            var group = _state.Groups.FirstOrDefault(g => g.Name == _state.ActiveGroup);
            if (group == null || group.Sessions.Count == 0)
            {
                _state.LeaveGroupGrid();
                _state.SetStatus("Group removed (last session killed)");
            }
            else
            {
                _state.CursorIndex = Math.Clamp(_state.CursorIndex, 0, group.Sessions.Count - 1);
                _state.SetStatus("Session killed");
            }

            _lastSelectedSession = null;
            _lastGridWidth = 0;
            _lastGridHeight = 0;
        }
        else
        {
            _state.SetStatus("Cancelled");
        }
    }

    private void EditGroup()
    {
        var group = _state.GetSelectedGroup();
        if (group == null)
            return;

        Console.CursorVisible = true;
        Console.Clear();

        var escapedName = Markup.Escape(group.Name);
        AnsiConsole.MarkupLine($"[grey70 bold]Edit group[/] [white]'{escapedName}'[/] [grey](empty = keep current)[/]\n");

        // Edit name
        var newName = AnsiConsole.Prompt(
            new TextPrompt<string>($"[grey70]Name[/] [grey50]({escapedName})[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        if (!string.IsNullOrWhiteSpace(newName))
            newName = SanitizeTmuxSessionName(newName);

        // Validate new name doesn't conflict
        if (!string.IsNullOrWhiteSpace(newName) && newName != group.Name && _config.Groups.ContainsKey(newName))
        {
            Console.CursorVisible = false;
            _state.SetStatus($"Group '{newName}' already exists");
            return;
        }

        // Add more sessions
        var currentCount = group.Sessions.Count;
        var remaining = 9 - currentCount;
        var newDirectories = new List<(string Dir, string Label)>();

        if (remaining > 0)
        {
            AnsiConsole.MarkupLine($"\n[grey70]Current sessions: {currentCount}/9 — you can add {remaining} more[/]");

            var addMore = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[grey70]Add sessions?[/]")
                    .HighlightStyle(new Style(Color.White, Color.Grey70))
                    .AddChoices("Yes", "No"));

            if (addMore == "Yes")
            {
                for (var i = 0; i < remaining; i++)
                {
                    AnsiConsole.MarkupLine($"\n[grey70]New session {i + 1} of {remaining}[/]");

                    var dir = PickDirectory();
                    if (dir == null)
                        break;

                    dir = ConfigService.ExpandPath(dir);
                    if (!Directory.Exists(dir))
                    {
                        AnsiConsole.MarkupLine("[red]Directory not found, skipping[/]");
                        continue;
                    }

                    var label = Path.GetFileName(dir.TrimEnd('/'));
                    newDirectories.Add((dir, label));

                    if (currentCount + newDirectories.Count >= 9)
                        break;

                    var more = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[grey70]Add another session?[/]")
                            .HighlightStyle(new Style(Color.White, Color.Grey70))
                            .AddChoices("Yes", "No"));

                    if (more == "No")
                        break;
                }
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"\n[grey50]Group is full (9/9 sessions)[/]");
        }

        // Pick new color
        var currentColor = !string.IsNullOrEmpty(group.Color) ? group.Color : "none";
        AnsiConsole.MarkupLine($"\n[grey70]Current color:[/] [{currentColor}]{currentColor}[/]");
        var newColor = PickColor();

        Console.CursorVisible = false;

        var effectiveName = !string.IsNullOrWhiteSpace(newName) && newName != group.Name ? newName : group.Name;
        var changed = false;

        // Apply name change — rename all tmux sessions and update config
        if (effectiveName != group.Name)
        {
            var oldName = group.Name;

            // Rename tmux sessions: old prefix → new prefix
            var renamedSessions = new List<string>();
            foreach (var sessionName in group.Sessions.ToList())
            {
                string newSessionName;
                if (sessionName.StartsWith(oldName + "-"))
                    newSessionName = effectiveName + sessionName[oldName.Length..];
                else
                    newSessionName = effectiveName + "-" + sessionName;

                var renameError = TmuxService.RenameSession(sessionName, newSessionName);
                if (renameError != null)
                {
                    _state.SetStatus($"Failed to rename session: {renameError}");
                    return;
                }

                ConfigService.RenameDescription(_config, sessionName, newSessionName);
                ConfigService.RenameColor(_config, sessionName, newSessionName);
                renamedSessions.Add(newSessionName);
            }

            // Remove old group, create under new name
            ConfigService.RemoveGroup(_config, oldName);
            group.Name = effectiveName;
            group.Sessions = renamedSessions;
            ConfigService.SaveGroup(_config, group);
            changed = true;
        }

        // Create new sessions
        var usedNames = new HashSet<string>(group.Sessions, StringComparer.Ordinal);
        foreach (var (dir, label) in newDirectories)
        {
            var sessionName = UniqueSessionName(SanitizeTmuxSessionName($"{effectiveName}-{label}"), usedNames);
            usedNames.Add(sessionName);
            var error = TmuxService.CreateSession(sessionName, dir);
            if (error != null)
            {
                _state.SetStatus($"Failed to create session '{sessionName}': {error}");
                break;
            }

            var sessionColor = newColor ?? group.Color;
            if (!string.IsNullOrEmpty(sessionColor))
            {
                ConfigService.SaveColor(_config, sessionName, sessionColor);
                TmuxService.ApplyStatusColor(sessionName, sessionColor);
            }

            group.Sessions.Add(sessionName);
            changed = true;
        }

        // Apply color change to all existing sessions
        if (newColor != null)
        {
            group.Color = newColor;
            foreach (var sessionName in group.Sessions)
            {
                ConfigService.SaveColor(_config, sessionName, newColor);
                TmuxService.ApplyStatusColor(sessionName, newColor);
            }

            changed = true;
        }

        if (changed)
        {
            ConfigService.SaveGroup(_config, group);
            LoadSessions();
            _state.GroupCursor = _state.Groups.FindIndex(g => g.Name == effectiveName);
            if (_state.GroupCursor < 0)
                _state.GroupCursor = 0;
            _state.SetStatus("Group updated");
        }
        else
        {
            _state.SetStatus("No changes");
        }
    }

    private void ToggleGridView()
    {
        // If in group grid, Escape handles exit — G should not toggle
        if (_state.ActiveGroup != null)
            return;

        if (_state.ViewMode == ViewMode.List)
        {
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
            _lastGridWidth = 0;
            _lastGridHeight = 0;
        }

        _lastSelectedSession = null;
    }

    private void AttachToSession()
    {
        var session = _state.GetSelectedSession();
        if (session == null)
            return;

        Console.CursorVisible = true;
        Console.Clear();

        TmuxService.ResetWindowSize(session.Name);
        TmuxService.AttachSession(session.Name);

        // User detached - back to ClaudeCommandCenter
        Console.CursorVisible = false;
        LoadSessions();
        _lastSelectedSession = null;
    }

    private void RunUpdate()
    {
        AnsiConsole.MarkupLine($"[yellow]Updating to v{_state.LatestVersion}...[/]\n");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "bash",
            ArgumentList =
            {
                "-c",
                "curl -fsSL https://raw.githubusercontent.com/AdamGardelov/ClaudeCommandCenter/main/install.sh | bash"
            },
            UseShellExecute = false,
        });
        process?.WaitForExit();
    }

    private void CreateNewSession()
    {
        if (!_claudeAvailable)
        {
            _state.SetStatus("'claude' not found in PATH — install Claude Code first");
            return;
        }

        Console.CursorVisible = true;
        Console.Clear();

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey70]Session name[/] [grey](empty to go back)[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.CursorVisible = false;
            _state.SetStatus("Cancelled");
            return;
        }

        var description = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey70]Description[/] [grey](optional)[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        var color = PickColor();

        var dir = PickDirectory(worktreeBranchHint: name);

        if (dir == null)
        {
            Console.CursorVisible = false;
            _state.SetStatus("Cancelled");
            return;
        }

        dir = ConfigService.ExpandPath(dir);

        if (!Directory.Exists(dir))
        {
            Console.CursorVisible = false;
            _state.SetStatus("Invalid directory");
            return;
        }

        var error = TmuxService.CreateSession(name, dir);
        if (error == null)
        {
            if (!string.IsNullOrWhiteSpace(description))
                ConfigService.SaveDescription(_config, name, description);
            if (color != null)
                ConfigService.SaveColor(_config, name, color);
            TmuxService.ApplyStatusColor(name, color ?? "grey42");
            TmuxService.AttachSession(name);
        }
        else
        {
            _state.SetStatus(error);
        }

        Console.CursorVisible = false;
        LoadSessions();
        _lastSelectedSession = null;
    }

    private void CreateNewGroup()
    {
        if (!_claudeAvailable)
        {
            _state.SetStatus("'claude' not found in PATH — install Claude Code first");
            return;
        }

        Console.CursorVisible = true;
        Console.Clear();

        var basePath = ConfigService.ExpandPath(_config.WorktreeBasePath);
        var hasExistingWorktrees = Directory.Exists(basePath) && ScanWorktreeFeatures(basePath).Count > 0;
        var hasGitRepos = _config.FavoriteFolders.Any(f => GitService.IsGitRepo(ConfigService.ExpandPath(f.Path)));

        var modePrompt = new SelectionPrompt<string>()
            .Title("[grey70]Create group from[/]")
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        if (hasExistingWorktrees)
            modePrompt.AddChoice("Existing worktree feature");
        if (hasGitRepos)
            modePrompt.AddChoice("New worktrees (pick repos)");
        modePrompt.AddChoices("Manual (pick directories)", _cancelChoice);

        var mode = AnsiConsole.Prompt(modePrompt);
        if (mode == _cancelChoice)
        {
            Console.CursorVisible = false;
            _state.SetStatus("Cancelled");
            return;
        }

        if (mode.StartsWith("Existing"))
        {
            CreateGroupFromWorktree(basePath);
            return;
        }

        if (mode.StartsWith("New worktrees"))
        {
            CreateGroupFromNewWorktrees();
            return;
        }

        CreateGroupManually();
    }

    private void CreateGroupFromWorktree(string basePath)
    {
        var features = ScanWorktreeFeatures(basePath);
        var activeGroupNames = new HashSet<string>(_config.Groups.Keys);
        var available = features.Where(f => !activeGroupNames.Contains(f.Name)).ToList();
        if (available.Count == 0)
        {
            Console.CursorVisible = false;
            _state.SetStatus("All worktrees already have active groups");
            return;
        }

        var prompt = new SelectionPrompt<string>()
            .Title("[grey70]Select a worktree feature[/]")
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        prompt.AddChoice(_cancelChoice);
        foreach (var f in available)
        {
            var repos = string.Join(", ", f.Repos.Keys);
            prompt.AddChoice($"{f.Name} - {f.Description} ({repos})");
        }

        var selected = AnsiConsole.Prompt(prompt);
        if (selected == _cancelChoice)
        {
            Console.CursorVisible = false;
            _state.SetStatus("Cancelled");
            return;
        }

        var selectedName = selected.Split(" - ")[0];
        var feature = available.FirstOrDefault(f => f.Name == selectedName);
        if (feature == null)
        {
            Console.CursorVisible = false;
            _state.SetStatus("Feature not found");
            return;
        }

        var color = PickColor();
        Console.CursorVisible = false;

        var sessionNames = new List<string>();
        foreach (var (repoName, repoPath) in feature.Repos)
        {
            var sessionName = SanitizeTmuxSessionName($"{feature.Name}-{repoName}");
            var error = TmuxService.CreateSession(sessionName, repoPath);
            if (error != null)
            {
                _state.SetStatus($"Failed to create session '{sessionName}': {error}");
                return;
            }

            if (color != null)
            {
                ConfigService.SaveColor(_config, sessionName, color);
                TmuxService.ApplyStatusColor(sessionName, color);
            }

            sessionNames.Add(sessionName);
        }

        var group = new SessionGroup
        {
            Name = feature.Name,
            Description = feature.Description,
            Color = color ?? "",
            WorktreePath = feature.WorktreePath,
            Sessions = sessionNames,
        };
        ConfigService.SaveGroup(_config, group);
        FinishGroupCreation(feature.Name);
    }

    private void CreateGroupFromNewWorktrees()
    {
        var gitFavorites = _config.FavoriteFolders
            .Where(f => GitService.IsGitRepo(ConfigService.ExpandPath(f.Path)))
            .ToList();

        if (gitFavorites.Count < 2)
        {
            Console.CursorVisible = false;
            _state.SetStatus("Need at least 2 git repos in favorites");
            return;
        }

        var selectedRepos = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[grey70]Select repos[/]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .InstructionsText("[grey](space to toggle, enter to confirm)[/]")
                .AddChoices(gitFavorites.Select(f => $"{f.Name}  [grey50]{f.Path}[/]")));

        if (selectedRepos.Count < 2)
        {
            Console.CursorVisible = false;
            _state.SetStatus("Groups need at least 2 repos");
            return;
        }

        var featureName = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey70]Feature name[/] [grey](used for branch + folder)[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        if (string.IsNullOrWhiteSpace(featureName))
        {
            Console.CursorVisible = false;
            _state.SetStatus("Cancelled");
            return;
        }

        var sanitizedName = SanitizeTmuxSessionName(featureName);
        var branchName = GitService.SanitizeBranchName(featureName);

        if (_config.Groups.ContainsKey(sanitizedName))
        {
            Console.CursorVisible = false;
            _state.SetStatus($"Group '{sanitizedName}' already exists");
            return;
        }

        var color = PickColor();

        var basePath = ConfigService.ExpandPath(_config.WorktreeBasePath);
        var featurePath = Path.Combine(basePath, branchName);

        // Resolve selected names back to favorites
        var repos = new List<(string Name, string RepoPath)>();
        foreach (var sel in selectedRepos)
        {
            var name = sel.Split("  ")[0];
            var fav = gitFavorites.FirstOrDefault(f => f.Name == name);
            if (fav != null)
                repos.Add((name, ConfigService.ExpandPath(fav.Path)));
        }

        // Create worktrees with progress
        var worktrees = new Dictionary<string, string>();
        string? error = null;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey70))
            .Start("[grey70]Creating worktrees...[/]", ctx =>
            {
                foreach (var (repoName, repoPath) in repos)
                {
                    ctx.Status($"[grey70]Creating worktree [white]{repoName}[/]...[/]");
                    GitService.FetchPrune(repoPath);

                    var dest = Path.Combine(featurePath, repoName);
                    Directory.CreateDirectory(featurePath);

                    error = GitService.CreateWorktree(repoPath, dest, branchName);
                    if (error != null)
                    {
                        error = $"Failed to create worktree for {repoName}: {error}";
                        return;
                    }

                    worktrees[repoName] = dest;
                }
            });

        if (error != null)
        {
            Console.CursorVisible = false;
            _state.SetStatus(error);
            return;
        }

        // Generate .feature-context.json
        WriteFeatureContext(featurePath, featureName, worktrees);

        // Create sessions
        Console.CursorVisible = false;
        var sessionNames = new List<string>();
        foreach (var (repoName, worktreePath) in worktrees)
        {
            var sessionName = SanitizeTmuxSessionName($"{sanitizedName}-{repoName}");
            var sessionError = TmuxService.CreateSession(sessionName, worktreePath);
            if (sessionError != null)
            {
                _state.SetStatus($"Failed to create session '{sessionName}': {sessionError}");
                return;
            }

            if (color != null)
            {
                ConfigService.SaveColor(_config, sessionName, color);
                TmuxService.ApplyStatusColor(sessionName, color);
            }

            sessionNames.Add(sessionName);
        }

        var group = new SessionGroup
        {
            Name = sanitizedName,
            Description = featureName,
            Color = color ?? "",
            WorktreePath = featurePath,
            Sessions = sessionNames,
        };
        ConfigService.SaveGroup(_config, group);
        FinishGroupCreation(sanitizedName);
    }

    private static void WriteFeatureContext(string featurePath, string featureName, Dictionary<string, string> worktrees)
    {
        var repos = new Dictionary<string, object>();
        foreach (var (name, path) in worktrees)
            repos[name] = new { worktree = path };

        var context = new
        {
            feature = featureName,
            description = "",
            repos,
        };

        var json = JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(featurePath, ".feature-context.json"), json);
    }

    private void CreateGroupManually()
    {
        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey70]Group name[/] [grey](empty to cancel)[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.CursorVisible = false;
            _state.SetStatus("Cancelled");
            return;
        }

        name = SanitizeTmuxSessionName(name);

        if (_config.Groups.ContainsKey(name))
        {
            Console.CursorVisible = false;
            _state.SetStatus($"Group '{name}' already exists");
            return;
        }

        var directories = new List<(string Dir, string Label)>();

        for (var i = 0; i < 9; i++)
        {
            AnsiConsole.MarkupLine($"\n[grey70]Session {i + 1} of 9[/]");

            var dir = PickDirectory();
            if (dir == null)
            {
                if (directories.Count == 0)
                {
                    Console.CursorVisible = false;
                    _state.SetStatus("Cancelled");
                    return;
                }

                break; // Done adding sessions
            }

            dir = ConfigService.ExpandPath(dir);
            if (!Directory.Exists(dir))
            {
                AnsiConsole.MarkupLine("[red]Directory not found, skipping[/]");
                continue;
            }

            var label = Path.GetFileName(dir.TrimEnd('/'));
            directories.Add((dir, label));

            if (directories.Count >= 9)
                break;

            if (directories.Count >= 2)
            {
                var more = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[grey70]Add another session?[/]")
                        .HighlightStyle(new Style(Color.White, Color.Grey70))
                        .AddChoices("Yes", "No, create group"));

                if (more.StartsWith("No"))
                    break;
            }
        }

        if (directories.Count < 2)
        {
            Console.CursorVisible = false;
            _state.SetStatus("Groups need at least 2 sessions");
            return;
        }

        var color = PickColor();
        Console.CursorVisible = false;

        var sessionNames = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dir, label) in directories)
        {
            var sessionName = UniqueSessionName(SanitizeTmuxSessionName($"{name}-{label}"), usedNames);
            usedNames.Add(sessionName);
            var error = TmuxService.CreateSession(sessionName, dir);
            if (error != null)
            {
                _state.SetStatus($"Failed to create session '{sessionName}': {error}");
                return;
            }

            if (color != null)
            {
                ConfigService.SaveColor(_config, sessionName, color);
                TmuxService.ApplyStatusColor(sessionName, color);
            }

            sessionNames.Add(sessionName);
        }

        var group = new SessionGroup
        {
            Name = name,
            Description = "",
            Color = color ?? "",
            WorktreePath = "",
            Sessions = sessionNames,
        };
        ConfigService.SaveGroup(_config, group);
        FinishGroupCreation(name);
    }

    private void FinishGroupCreation(string groupName)
    {
        LoadSessions();
        _state.ActiveSection = ActiveSection.Groups;
        _state.GroupCursor = _state.Groups.FindIndex(g => g.Name == groupName);
        if (_state.GroupCursor < 0)
            _state.GroupCursor = 0;
        _state.EnterGroupGrid(groupName);
        _lastSelectedSession = null;
        ResizeGridPanes();
    }

    private static string SanitizeTmuxSessionName(string name)
    {
        // tmux silently replaces dots and colons with underscores in session names
        return name.Replace('.', '_').Replace(':', '_');
    }

    private static string UniqueSessionName(string baseName, ICollection<string> existing)
    {
        if (!existing.Contains(baseName))
            return baseName;

        for (var i = 2; i <= 99; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!existing.Contains(candidate))
                return candidate;
        }

        return baseName; // Shouldn't happen with max 8 sessions
    }

    private record WorktreeFeature(string Name, string Description, string WorktreePath, Dictionary<string, string> Repos);

    private static List<WorktreeFeature> ScanWorktreeFeatures(string basePath)
    {
        var features = new List<WorktreeFeature>();

        foreach (var dir in Directory.GetDirectories(basePath))
        {
            var contextFile = Path.Combine(dir, ".feature-context.json");
            if (File.Exists(contextFile))
            {
                try
                {
                    var json = File.ReadAllText(contextFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var name = root.GetProperty("feature").GetString() ?? "";
                    var description = root.GetProperty("description").GetString() ?? "";

                    var repos = new Dictionary<string, string>();
                    if (root.TryGetProperty("repos", out var reposEl))
                    {
                        foreach (var repo in reposEl.EnumerateObject())
                        {
                            var worktreePath = repo.Value.GetProperty("worktree").GetString();
                            if (worktreePath != null && Directory.Exists(worktreePath))
                                repos[repo.Name] = worktreePath;
                        }
                    }

                    if (repos.Count > 0)
                        features.Add(new WorktreeFeature(name, description, dir, repos));
                }
                catch
                {
                    // Skip malformed context files
                }
            }
            else
            {
                // No context file — detect git worktree subdirectories
                var repos = new Dictionary<string, string>();
                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    var gitFile = Path.Combine(subDir, ".git");
                    if (File.Exists(gitFile) || Directory.Exists(gitFile))
                        repos[Path.GetFileName(subDir)] = subDir;
                }

                if (repos.Count > 0)
                {
                    var name = Path.GetFileName(dir);
                    features.Add(new WorktreeFeature(name, "", dir, repos));
                }
            }
        }

        return features;
    }

    private static readonly (string Label, string SpectreColor)[] _colorPalette =
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

    private string? PickColor()
    {
        var prompt = new SelectionPrompt<string>()
            .Title("[grey70]Color[/] [grey](optional)[/]")
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        var usedColors = new HashSet<string>(_config.SessionColors.Values, StringComparer.OrdinalIgnoreCase);
        var hasUnused = _colorPalette.Any(c => !usedColors.Contains(c.SpectreColor));

        if (hasUnused)
            prompt.AddChoice("[grey70]🎲  Just give me one[/]");
        prompt.AddChoice("None");
        foreach (var (label, spectreColor) in _colorPalette)
            prompt.AddChoice($"[{spectreColor}]████[/]  {label}");

        var selected = AnsiConsole.Prompt(prompt);

        if (selected == "None")
            return null;

        if (selected.Contains("Just give me one"))
        {
            var unused = _colorPalette.Where(c => !usedColors.Contains(c.SpectreColor)).ToArray();
            return unused[Random.Shared.Next(unused.Length)].SpectreColor;
        }

        foreach (var (label, spectreColor) in _colorPalette)
            if (selected.EndsWith(label))
                return spectreColor;

        return null;
    }

    private const string _customPathChoice = "Custom path...";
    private const string _cancelChoice = "Cancel";
    private const string _worktreePrefix = "⑂ ";

    private string? PickDirectory(string? worktreeBranchHint = null)
    {
        var favorites = _config.FavoriteFolders;
        var gitFavorites = worktreeBranchHint != null
            ? favorites.Where(f => GitService.IsGitRepo(ConfigService.ExpandPath(f.Path))).ToList()
            : [];

        while (true)
        {
            var prompt = new SelectionPrompt<string>()
                .Title("[grey70]Pick a directory[/]")
                .PageSize(15)
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .MoreChoicesText("[grey](Move up and down to reveal more)[/]");

            foreach (var fav in favorites)
                prompt.AddChoice($"{fav.Name}  [grey50]{fav.Path}[/]");

            if (gitFavorites.Count > 0)
            {
                foreach (var fav in gitFavorites)
                    prompt.AddChoice($"{_worktreePrefix}{fav.Name}  [grey50](new worktree)[/]");
            }

            prompt.AddChoice(_customPathChoice);
            prompt.AddChoice(_cancelChoice);

            var selected = AnsiConsole.Prompt(prompt);
            switch (selected)
            {
                case _cancelChoice:
                    return null;
                case _customPathChoice:
                {
                    var custom = PromptCustomPath();
                    if (custom != null)
                        return custom;
                    continue; // empty input → back to picker
                }
            }

            // Handle worktree selection
            if (selected.StartsWith(_worktreePrefix))
            {
                var repoName = selected[_worktreePrefix.Length..].Split("  ")[0];
                var fav = gitFavorites.FirstOrDefault(f => f.Name == repoName);
                if (fav == null) continue;

                var repoPath = ConfigService.ExpandPath(fav.Path);
                var branchName = GitService.SanitizeBranchName(worktreeBranchHint!);
                var basePath = ConfigService.ExpandPath(_config.WorktreeBasePath);
                var worktreeDest = Path.Combine(basePath, branchName, repoName);

                return CreateWorktreeWithProgress(repoPath, worktreeDest, branchName);
            }

            // Match back to the favorite by prefix (name before the spacing)
            var selectedName = selected.Split("  ")[0];
            var match = favorites.FirstOrDefault(f => f.Name == selectedName);
            return match?.Path;
        }
    }

    private static string? CreateWorktreeWithProgress(string repoPath, string worktreeDest, string branchName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(worktreeDest)!);

        string? error = null;
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey70))
            .Start($"[grey70]Creating worktree [white]{branchName}[/]...[/]", _ =>
            {
                GitService.FetchPrune(repoPath);
                error = GitService.CreateWorktree(repoPath, worktreeDest, branchName);
            });

        if (error != null)
        {
            AnsiConsole.MarkupLine($"[red]Worktree failed:[/] {Markup.Escape(error)}");
            AnsiConsole.MarkupLine("[grey](Press any key)[/]");
            Console.ReadKey(true);
            return null;
        }

        return worktreeDest;
    }

    private static string? PromptCustomPath()
    {
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey70]Working directory:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private void OpenFolder()
    {
        var session = _state.GetSelectedSession();
        if (session?.CurrentPath == null)
            return;

        try
        {
            var command = GetFileManagerCommand();
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                ArgumentList =
                {
                    session.CurrentPath
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process.Start(startInfo);
            _state.SetStatus($"Opened folder: {session.CurrentPath}");
        }
        catch
        {
            _state.SetStatus("Failed to open folder");
        }
    }

    private static string GetFileManagerCommand()
    {
        if (OperatingSystem.IsMacOS())
            return "open";

        if (OperatingSystem.IsWindows())
            return "explorer";

        // Linux — check for WSL where explorer.exe is available
        try
        {
            var version = File.ReadAllText("/proc/version");
            if (version.Contains("microsoft", StringComparison.OrdinalIgnoreCase))
                return "explorer.exe";
        }
        catch
        {
            /* not WSL */
        }

        return "xdg-open";
    }

    private void OpenInIde()
    {
        var session = _state.GetSelectedSession();
        if (session?.CurrentPath == null)
            return;

        if (string.IsNullOrWhiteSpace(_config.IdeCommand))
        {
            _state.SetStatus("Set ideCommand in config first (press c)");
            return;
        }

        if (LaunchWithIde(_config.IdeCommand, session.CurrentPath))
            _state.SetStatus($"Opened in {_config.IdeCommand}");
        else
            _state.SetStatus($"Failed to run '{_config.IdeCommand}'");
    }

    private void OpenConfig()
    {
        var configPath = ConfigService.GetConfigPath();

        if (string.IsNullOrWhiteSpace(_config.IdeCommand))
        {
            // No IDE configured — fall back to platform default
            try
            {
                var opener = OperatingSystem.IsMacOS() ? "open" :
                    OperatingSystem.IsWindows() ? "explorer" : "xdg-open";
                Process.Start(new ProcessStartInfo
                {
                    FileName = opener,
                    ArgumentList = { configPath },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                _state.SetStatus($"Opened config with {opener}");
            }
            catch
            {
                _state.SetStatus($"Config at: {configPath}");
            }

            return;
        }

        if (LaunchWithIde(_config.IdeCommand, configPath))
            _state.SetStatus($"Opened config in {_config.IdeCommand}");
        else
            _state.SetStatus($"Failed to run '{_config.IdeCommand}'");
    }

    private static bool LaunchWithIde(string command, string path)
    {
        // On macOS, app names like "rider" aren't on PATH — use "open -a" to launch by app name
        if (OperatingSystem.IsMacOS() && !command.StartsWith('/') && !command.Contains('/'))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { "-a", command, path },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                return true;
            }
            catch
            {
                // Fall through to direct launch
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = command,
                ArgumentList = { path },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DeleteSession()
    {
        var session = _state.GetSelectedSession();
        if (session == null)
            return;

        _state.SetStatus($"Kill '{session.Name}'? (y/n)");
        Render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            var killError = TmuxService.KillSession(session.Name);
            if (killError == null)
            {
                ConfigService.RemoveDescription(_config, session.Name);
                ConfigService.RemoveColor(_config, session.Name);
                ConfigService.RemoveExcluded(_config, session.Name);
                _state.SetStatus("Session killed");
            }
            else
            {
                _state.SetStatus(killError);
            }

            LoadSessions();
        }
        else
        {
            _state.SetStatus("Cancelled");
        }
    }

    private void ToggleExclude()
    {
        var session = _state.GetSelectedSession();
        if (session == null)
            return;

        ConfigService.ToggleExcluded(_config, session.Name);
        session.IsExcluded = !session.IsExcluded;

        if (_state.ViewMode == ViewMode.Grid && session.IsExcluded)
        {
            // Session just got excluded from grid — clamp cursor
            var gridSessions = _state.GetGridSessions();
            if (gridSessions.Count < 2)
            {
                // Not enough sessions for grid, switch to list
                _state.ViewMode = ViewMode.List;
            }
            else
            {
                _state.CursorIndex = Math.Clamp(_state.CursorIndex, 0, gridSessions.Count - 1);
            }
        }

        var label = session.IsExcluded ? "Excluded from grid" : "Restored to grid";
        _state.SetStatus(label);
        _lastSelectedSession = null;
    }

    private void MoveSessionToGroup()
    {
        var session = _state.GetSelectedSession();
        if (session == null)
            return;

        // Find groups that don't already contain this session
        var eligible = _state.Groups
            .Where(g => !g.Sessions.Contains(session.Name))
            .ToList();

        if (eligible.Count == 0)
        {
            _state.SetStatus("No groups available");
            return;
        }

        Console.CursorVisible = true;
        Console.Clear();

        var prompt = new SelectionPrompt<string>()
            .Title($"[grey70]Move[/] [white]'{Markup.Escape(session.Name)}'[/] [grey70]to group[/]")
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        prompt.AddChoice(_cancelChoice);
        foreach (var g in eligible)
            prompt.AddChoice($"{Markup.Escape(g.Name)} ({g.Sessions.Count} sessions)");

        var selected = AnsiConsole.Prompt(prompt);
        Console.CursorVisible = false;

        if (selected == _cancelChoice)
        {
            _state.SetStatus("Cancelled");
            return;
        }

        // Extract group name from the selection (everything before the last " (N sessions)")
        var groupName = selected[..selected.LastIndexOf(" (")];
        var group = _state.Groups.FirstOrDefault(g => g.Name == groupName);
        if (group == null)
        {
            _state.SetStatus("Group not found");
            return;
        }

        group.Sessions.Add(session.Name);
        ConfigService.SaveGroup(_config, group);

        // Apply group color to the session if it doesn't have one
        if (!string.IsNullOrEmpty(group.Color) && !_config.SessionColors.ContainsKey(session.Name))
        {
            ConfigService.SaveColor(_config, session.Name, group.Color);
            TmuxService.ApplyStatusColor(session.Name, group.Color);
        }

        LoadSessions();
        _lastSelectedSession = null;
        _state.SetStatus($"Moved to '{groupName}'");
    }

    private void SendQuickKey(string key)
    {
        var session = _state.GetSelectedSession();
        if (session == null)
            return;

        var error = TmuxService.SendKeys(session.Name, key);
        if (error == null)
        {
            _state.SetStatus($"Sent '{key}' to {session.Name}");
            _lastSelectedSession = null; // Force pane refresh
        }
        else
        {
            _state.SetStatus(error);
        }
    }

    private void SendText()
    {
        var session = _state.GetSelectedSession();
        if (session == null)
            return;

        _state.IsInputMode = true;
        _state.InputBuffer = "";
        _state.InputTarget = session.Name;
    }

    private void EditSession()
    {
        var session = _state.GetSelectedSession();
        if (session == null)
            return;

        Console.CursorVisible = true;
        Console.Clear();

        var escapedName = Markup.Escape(session.Name);
        AnsiConsole.MarkupLine($"[grey70 bold]Edit session[/] [white]'{escapedName}'[/] [grey](empty = keep current)[/]\n");

        var newName = AnsiConsole.Prompt(
            new TextPrompt<string>($"[grey70]Name[/] [grey50]({escapedName})[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        var currentDesc = session.Description ?? "";
        var descHint = string.IsNullOrWhiteSpace(currentDesc) ? "none" : Markup.Escape(currentDesc);
        var newDesc = AnsiConsole.Prompt(
            new TextPrompt<string>($"[grey70]Description[/] [grey50]({descHint})[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        var newColor = PickColor();

        Console.CursorVisible = false;

        var currentName = session.Name;
        var changed = false;

        // Apply name change
        if (!string.IsNullOrWhiteSpace(newName) && newName != currentName)
        {
            var renameError = TmuxService.RenameSession(currentName, newName);
            if (renameError == null)
            {
                ConfigService.RenameDescription(_config, currentName, newName);
                ConfigService.RenameColor(_config, currentName, newName);
                ConfigService.RenameExcluded(_config, currentName, newName);
                currentName = newName;
                changed = true;
            }
            else
            {
                _state.SetStatus(renameError);
                return;
            }
        }

        // Apply description change
        if (!string.IsNullOrWhiteSpace(newDesc))
        {
            ConfigService.SaveDescription(_config, currentName, newDesc);
            changed = true;
        }

        // Apply color change
        if (newColor != null)
        {
            ConfigService.SaveColor(_config, currentName, newColor);
            TmuxService.ApplyStatusColor(currentName, newColor);
            changed = true;
        }

        if (changed)
        {
            LoadSessions();
            _state.SetStatus("Session updated");
        }
        else
        {
            _state.SetStatus("No changes");
        }
    }
}
