using System.Diagnostics;
using ClaudeCommandCenter.Enums;
using ClaudeCommandCenter.Handlers;
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;
using Spectre.Console;

namespace ClaudeCommandCenter;

public class App(ISessionBackend backend, bool mobileMode = false)
{
    private readonly AppState _state = new()
    {
        MobileMode = mobileMode
    };

    private readonly CccConfig _config = ConfigService.Load();
    private FlowHelper _flow = null!;
    private DiffHandler _diffHandler = null!;
    private SettingsHandler _settingsHandler = null!;
    private SessionHandler _sessionHandler = null!;
    private GroupHandler _groupHandler = null!;
    private Dictionary<string, string> _keyMap = new();
    private string? _capturedPane;
    private Dictionary<string, string> _allCapturedPanes = new();
    private DateTime _lastCapture = DateTime.MinValue;
    private string? _lastSelectedSession;
    private string? _lastSpinnerFrame;
    private bool _hasSpinningSessions;
    private bool _claudeAvailable;
    private Task<string?>? _updateCheck;
    private DateTime _lastUpdateCheck = DateTime.UtcNow;
    private static readonly TimeSpan _updateCheckInterval = TimeSpan.FromMinutes(20);
    private bool _wantsUpdate;
    private int _startupPollCount;

    public void Run()
    {
        if (!backend.IsAvailable())
        {
            AnsiConsole.MarkupLine("[red]Session backend is not available.[/]");
            return;
        }

        if (backend.IsInsideHost())
        {
            AnsiConsole.MarkupLine("[red]ClaudeCommandCenter should run outside the session host.[/]");
            AnsiConsole.MarkupLine("[grey]It manages sessions from the outside. Exit tmux first.[/]");
            return;
        }

        _flow = new FlowHelper(_config);
        _diffHandler = new DiffHandler(_state);
        _settingsHandler = new SettingsHandler(_state, _config, Render, RefreshKeybindings);
        _sessionHandler = new SessionHandler(_state, _config, _flow, backend, LoadSessions, Render, () =>
        {
            _lastSelectedSession = null;

            // Resize pane back to preview width and immediately re-capture so
            // the next render shows fresh content (not stale pre-attach data)
            var session = _state.GetSelectedSession();
            if (session != null && !_state.MobileMode)
            {
                var previewWidth = Math.Max(20, Console.WindowWidth - 35 - 8);
                backend.ResizeWindow(session.Name, previewWidth, Console.WindowHeight);
                _capturedPane = backend.CapturePaneContent(session.Name);
                _lastSelectedSession = session.Name;
            }
        });
        _groupHandler = new GroupHandler(
            _state, _config, _flow, backend, LoadSessions, Render,
            () => _lastSelectedSession = null,
            () => { },
            ResizeGridPanes);

        _claudeAvailable = backend.HasClaude();
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
            Console.Write("\e[?1003l\e[?1006l\e[?1015l\e[?1000l"); // Disable mouse tracking
            Console.CursorVisible = false;

            MainLoop();
        }
        finally
        {
            Console.CursorVisible = true;
            Console.Write("\e[?1049l"); // Leave alternate screen
            backend.Dispose();
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
                // Drain all buffered keys before rendering once —
                // prevents input lag over slow SSH connections
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    HandleKey(key);
                }

                Render();
            }

            // Check if update check completed
            if (_updateCheck is { IsCompleted: true })
            {
                var latest = _updateCheck.Result;
                _updateCheck = null;
                _lastUpdateCheck = DateTime.UtcNow;
                if (latest != null)
                {
                    _state.LatestVersion = latest;
                    Render();
                }
            }

            // Periodic update check
            if (_updateCheck == null && DateTime.UtcNow - _lastUpdateCheck > _updateCheckInterval)
                _updateCheck = UpdateChecker.CheckForUpdateAsync();

            // Re-render when a status message expires
            if (_state.HasPendingStatus)
                if (_state.GetActiveStatus() == null)
                    Render();

            // Re-render when spinner frame advances (only if sessions were spinning at last poll)
            // Skip in mobile mode — spinner updates aren't worth the render cost over SSH
            if (!_state.MobileMode)
            {
                var spinnerFrame = Renderer.GetSpinnerFrame();
                if (spinnerFrame != _lastSpinnerFrame)
                {
                    _lastSpinnerFrame = spinnerFrame;
                    if (_hasSpinningSessions && _state.ViewMode != ViewMode.Settings && _state.ViewMode != ViewMode.DiffOverlay)
                        Render();
                }
            }

            // Periodically capture pane content for preview
            if (_state.ViewMode != ViewMode.Settings && _state.ViewMode != ViewMode.DiffOverlay && (DateTime.Now - _lastCapture).TotalMilliseconds > 500)
            {
                if (!_state.MobileMode)
                {
                    ResizeGridPanes();
                    ResizePreviewPane();
                }

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
        _state.Sessions = backend.ListSessions();
        var startCommitsDirty = false;
        foreach (var s in _state.Sessions)
        {
            if (_config.SessionDescriptions.TryGetValue(s.Name, out var desc))
                s.Description = desc;
            if (_config.SessionColors.TryGetValue(s.Name, out var color))
                s.ColorTag = color;
            s.IsExcluded = _config.ExcludedSessions.Contains(s.Name);
            backend.ApplyStatusColor(s.Name, color ?? "grey42");

            // Preserve content tracking state so sessions don't briefly flash as "working"
            if (oldSessions.TryGetValue(s.Name, out var old))
            {
                s.PreviousContent = old.PreviousContent;
                s.StableContentCount = old.StableContentCount;
                s.IsWaitingForInput = old.IsWaitingForInput;
                s.IsIdle = old.IsIdle;
            }

            // Hydrate or snapshot StartCommitSha for diff tracking
            if (_config.SessionStartCommits.TryGetValue(s.Name, out var sha))
            {
                s.StartCommitSha = sha;
            }
            else if (s.CurrentPath != null && s.GitBranch != null)
            {
                var headSha = GitService.GetCurrentCommitSha(s.CurrentPath);
                if (headSha != null)
                {
                    s.StartCommitSha = headSha;
                    _config.SessionStartCommits[s.Name] = headSha;
                    startCommitsDirty = true;
                }
            }
        }

        if (startCommitsDirty)
            ConfigService.SaveConfig(_config);

        LoadGroups();
        _state.ClampCursor();
        NotificationService.Cleanup(_state.Sessions.Select(s => s.Name));
        HookStateService.Cleanup(_state.Sessions.Select(s => s.Name));
    }

    private void LoadGroups()
    {
        var liveSessionNames = new HashSet<string>(_state.Sessions.Select(s => s.Name));

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
            .OrderBy(g => g.Name)
            .ToList();
        _state.ClampGroupCursor();
    }

    private bool UpdateCapturedPane()
    {
        // Snapshot waiting/idle state before detection
        var wasWaiting = _state.Sessions
            .Where(s => !s.IsExcluded)
            .ToDictionary(s => s.Name, s => s.IsWaitingForInput);
        var wasIdle = _state.Sessions
            .Where(s => !s.IsExcluded)
            .ToDictionary(s => s.Name, s => s.IsIdle);

        // Refresh waiting-for-input status on all sessions (single tmux call)
        backend.DetectWaitingForInputBatch(_state.Sessions);
        _hasSpinningSessions = _state.Sessions.Any(s => !s.IsWaitingForInput && !s.IsIdle && !s.IsDead);

        // Detect false -> true transitions and notify.
        // Skip the first 6 polls (~3 seconds) so sessions have time to establish their
        // baseline waiting state — avoids a burst of notifications on startup.
        if (_startupPollCount > 5)
        {
            var selectedName = _state.GetSelectedSession()?.Name;
            var transitioned = _state.Sessions
                .Where(s => !s.IsExcluded
                            && s.IsWaitingForInput
                            && wasWaiting.TryGetValue(s.Name, out var was) && !was
                            && s.Name != selectedName)
                .ToList();

            if (transitioned.Count > 0)
            {
                var notified = NotificationService.NotifyWaiting(transitioned, _config.Notifications);
                if (notified != null)
                    _state.SetStatus($"⏳ {notified}");
            }
        }
        else
            _startupPollCount++;

        // Mobile mode doesn't show pane previews — only re-render
        // when a session's waiting status actually changed
        if (_state.MobileMode)
        {
            return _state.Sessions.Any(s =>
                wasWaiting.TryGetValue(s.Name, out var was) && was != s.IsWaitingForInput);
        }

        // In grid mode, capture panes for visible sessions (all or group-filtered)
        if (_state.ViewMode == ViewMode.Grid)
            return UpdateAllCapturedPanes();

        var session = _state.GetSelectedSession();
        var sessionName = session?.Name;

        if (sessionName != _lastSelectedSession)
        {
            _lastSelectedSession = sessionName;
            _capturedPane = session != null ? backend.CapturePaneContent(session.Name) : null;
            return true;
        }

        // Re-render if any session's status icon changed (waiting/idle transitions)
        var statusChanged = _state.Sessions.Any(s =>
            !s.IsExcluded
            && ((wasWaiting.TryGetValue(s.Name, out var ww) && ww != s.IsWaitingForInput)
                || (wasIdle.TryGetValue(s.Name, out var wi) && wi != s.IsIdle)));

        if (session == null)
            return statusChanged;

        var changed = statusChanged;
        var newContent = backend.CapturePaneContent(session.Name);
        if (newContent != _capturedPane)
        {
            _capturedPane = newContent;
            changed = true;
        }

        return changed;
    }

    private bool UpdateAllCapturedPanes()
    {
        var changed = false;
        var newPanes = new Dictionary<string, string>();
        var visibleSessions = _state.GetGridSessions();

        foreach (var session in visibleSessions)
        {
            var content = backend.CapturePaneContent(session.Name);
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

        // Resize every visible session — ResizeWindow is a no-op per-session
        // when dimensions haven't changed, so this is safe to call every tick.
        foreach (var session in sessions)
            backend.ResizeWindow(session.Name, targetWidth, targetHeight);
    }

    private void ResizePreviewPane()
    {
        if (_state.ViewMode != ViewMode.List)
            return;

        var session = _state.GetSelectedSession();
        if (session == null)
            return;

        // Match the width calculation in Renderer.BuildPreviewPanel.
        // ResizeWindow is a no-op when the session is already at this size.
        var targetWidth = Math.Max(20, Console.WindowWidth - 35 - 8);
        backend.ResizeWindow(session.Name, targetWidth, Console.WindowHeight);
    }

    private void Render()
    {
        Console.SetCursorPosition(0, 0);
        if (_state.ViewMode == ViewMode.Settings)
            AnsiConsole.Write(Renderer.BuildSettingsLayout(_state, _config));
        else if (_state.ViewMode == ViewMode.DiffOverlay)
            AnsiConsole.Write(Renderer.BuildDiffOverlayLayout(_state));
        else
            AnsiConsole.Write(Renderer.BuildLayout(_state, _capturedPane, _allCapturedPanes));
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (_state.IsGridFocused)
        {
            HandleGridFocusKey(key);
            return;
        }

        if (_state.IsInputMode)
        {
            HandleInputKey(key);
            return;
        }

        if (_state.MobileMode)
        {
            HandleMobileKey(key);
            return;
        }

        if (_state.ViewMode == ViewMode.Settings)
        {
            _settingsHandler.HandleKey(key);
            return;
        }

        if (_state.ViewMode == ViewMode.DiffOverlay)
        {
            _diffHandler.HandleKey(key);
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

        var keyId = FlowHelper.ResolveKeyId(key);
        if (_keyMap.TryGetValue(keyId, out var actionId))
            DispatchAction(actionId);
    }

    private void DispatchAction(string actionId)
    {
        // When Groups section is focused in list view, intercept attach, delete, and edit
        if (_state.ViewMode == ViewMode.List && _state.ActiveSection == ActiveSection.Groups)
        {
            switch (actionId)
            {
                case "attach":
                    _groupHandler.Open();
                    return;
                case "delete-session":
                    _groupHandler.Delete();
                    return;
                case "edit-session":
                    _groupHandler.Edit();
                    return;
                case "move-to-group":
                    return; // Not applicable when focused on groups
            }
        }

        // When in group grid, delete removes session from group
        if (_state.ActiveGroup != null && actionId == "delete-session")
        {
            _groupHandler.DeleteSessionFromGroup();
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
                _sessionHandler.SendQuickKey("y");
                break;
            case "reject":
                _sessionHandler.SendQuickKey("n");
                break;
            case "send-text":
                _sessionHandler.SendText();
                break;
            case "attach":
                if (_state.ViewMode == ViewMode.Grid)
                    EnterGridFocus();
                else
                    _sessionHandler.Attach();
                break;
            case "toggle-diff":
                _diffHandler.Open();
                break;
            case "toggle-grid":
                ToggleGridView();
                break;
            case "new-session":
                _sessionHandler.Create(_claudeAvailable);
                break;
            case "new-group":
                _groupHandler.CreateNew(_claudeAvailable);
                break;
            case "open-folder":
                _sessionHandler.OpenFolder();
                break;
            case "open-ide":
                _sessionHandler.OpenInIde();
                break;
            case "open-settings":
                _state.EnterSettings();
                break;
            case "delete-session":
                _sessionHandler.Delete();
                break;
            case "edit-session":
                _sessionHandler.Edit();
                break;
            case "toggle-exclude":
                _sessionHandler.ToggleExclude();
                break;
            case "move-to-group":
                _groupHandler.MoveSessionToGroup();
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
                var activeCount = _state.Sessions.Count(s => !s.IsDead);
                var quitMsg = OperatingSystem.IsWindows() && activeCount > 0
                    ? $"Quit? This will terminate {activeCount} active session(s). (y/n)"
                    : "Quit? (y/n)";
                _state.SetStatus(quitMsg);
                Render();
                var quitConfirm = Console.ReadKey(true);
                if (quitConfirm.Key == ConsoleKey.Y)
                    _state.Running = false;
                else
                    _state.SetStatus("Cancelled");
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
                    var sendError = backend.SendKeys(target, text);
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

    private void EnterGridFocus()
    {
        var session = _state.GetSelectedSession();
        if (session == null || session.IsDead)
            return;

        _state.IsGridFocused = true;
    }

    private void HandleGridFocusKey(ConsoleKeyInfo key)
    {
        var session = _state.GetSelectedSession();

        // Exit focus mode
        if (key.Key == ConsoleKey.Escape)
        {
            _state.IsGridFocused = false;
            return;
        }

        // Ctrl+Arrow: navigate between cells while staying focused
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
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

        // Forward everything else to the selected session
        if (session != null && !session.IsDead)
        {
            backend.ForwardKey(session.Name, key);
            _lastCapture = DateTime.MinValue; // Force capture on next tick (~30ms)
        }
    }

    private void RefreshKeybindings()
    {
        var bindings = KeyBindingService.Resolve(_config);
        _keyMap = KeyBindingService.BuildKeyMap(bindings);
        _state.Keybindings = bindings;
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

    private void HandleMobileKey(ConsoleKeyInfo key)
    {
        if (_state.HasPendingStatus)
        {
            _state.ClearStatus();
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveMobileCursor(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveMobileCursor(1);
                return;
        }

        var keyId = FlowHelper.ResolveKeyId(key);

        if (keyId == "g")
        {
            _state.CycleGroupFilter();
            _lastSelectedSession = null;
            return;
        }

        if (_keyMap.TryGetValue(keyId, out var actionId))
            DispatchMobileAction(actionId);
    }

    private void MoveMobileCursor(int delta)
    {
        var visible = _state.GetMobileVisibleSessions();
        if (visible.Count == 0)
            return;
        _state.CursorIndex = Math.Clamp(_state.CursorIndex + delta, 0, visible.Count - 1);
        _lastSelectedSession = null;
    }

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
                _sessionHandler.SendQuickKey("y");
                break;
            case "reject":
                _sessionHandler.SendQuickKey("n");
                break;
            case "send-text":
                _sessionHandler.SendText();
                break;
            case "attach":
                _sessionHandler.Attach();
                break;
            case "refresh":
                LoadSessions();
                _state.SetStatus("Refreshed");
                break;
            case "quit":
                var mobileActiveCount = _state.Sessions.Count(s => !s.IsDead);
                var mobileQuitMsg = OperatingSystem.IsWindows() && mobileActiveCount > 0
                    ? $"Quit? This will terminate {mobileActiveCount} active session(s). (y/n)"
                    : "Quit? (y/n)";
                _state.SetStatus(mobileQuitMsg);
                Render();
                var quitConfirm2 = Console.ReadKey(true);
                if (quitConfirm2.Key == ConsoleKey.Y)
                    _state.Running = false;
                else
                    _state.SetStatus("Cancelled");
                break;
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
            _state.IsGridFocused = false;
        }

        _lastSelectedSession = null;
    }

    private void RunUpdate()
    {
        AnsiConsole.MarkupLine($"[yellow]Updating to v{_state.LatestVersion}...[/]\n");

        if (OperatingSystem.IsWindows())
        {
            var script = "irm https://raw.githubusercontent.com/AdamGardelov/ClaudeCommandCenter/main/install.ps1 | iex";
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                ArgumentList = { "-NoProfile", "-Command", script },
                UseShellExecute = false,
            });
            process?.WaitForExit();
        }
        else
        {
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
    }
}
