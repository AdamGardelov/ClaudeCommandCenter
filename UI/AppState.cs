using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.UI;

public class AppState
{
    public List<TmuxSession> Sessions { get; set; } = [];
    public int CursorIndex { get; set; }
    public ViewMode ViewMode { get; set; } = ViewMode.List;
    public bool Running { get; set; } = true;
    public bool IsInputMode { get; set; }
    public string InputBuffer { get; set; } = "";
    public string? InputTarget { get; set; }
    private string? StatusMessage { get; set; }
    private DateTime? StatusMessageTime { get; set; }

    // Group state
    public List<SessionGroup> Groups { get; set; } = [];
    public ActiveSection ActiveSection { get; set; } = ActiveSection.Sessions;
    public int GroupCursor { get; set; }
    public string? ActiveGroup { get; set; }
    private int _savedCursorIndex;

    // Mobile mode state
    public bool MobileMode { get; set; }
    public int GroupFilterIndex { get; set; } // 0 = All, 1+ = group index
    public int TopIndex { get; set; } // Scroll offset for mobile list

    // Settings state
    public int SettingsCategory { get; set; }
    public int SettingsItemCursor { get; set; }
    public bool SettingsFocusRight { get; set; }
    public bool IsSettingsEditing { get; set; }
    public string SettingsEditBuffer { get; set; } = "";

    public TmuxSession? GetSelectedSession()
    {
        if (MobileMode)
        {
            var mobileSessions = GetMobileVisibleSessions();
            if (CursorIndex >= 0 && CursorIndex < mobileSessions.Count)
                return mobileSessions[CursorIndex];
            return null;
        }

        // When groups section is focused in list view, no session is selected
        if (ViewMode == ViewMode.List && ActiveGroup == null && ActiveSection == ActiveSection.Groups)
            return null;

        var sessions = ViewMode == ViewMode.Grid ? GetGridSessions() : GetVisibleSessions();
        if (CursorIndex >= 0 && CursorIndex < sessions.Count)
            return sessions[CursorIndex];
        return null;
    }

    public SessionGroup? GetSelectedGroup()
    {
        if (GroupCursor >= 0 && GroupCursor < Groups.Count)
            return Groups[GroupCursor];
        return null;
    }

    public List<TmuxSession> GetVisibleSessions()
    {
        // Group grid: show only group's sessions
        if (ActiveGroup != null)
        {
            var group = Groups.FirstOrDefault(g => g.Name == ActiveGroup);
            if (group != null)
            {
                var groupSessionNames = new HashSet<string>(group.Sessions);
                return Sessions.Where(s => groupSessionNames.Contains(s.Name)).ToList();
            }
        }

        // Standalone sessions only (list view sessions section + global grid)
        return GetStandaloneSessions();
    }

    public List<TmuxSession> GetStandaloneSessions()
    {
        var groupedNames = new HashSet<string>(Groups.SelectMany(g => g.Sessions));
        return Sessions
            .Where(s => !groupedNames.Contains(s.Name))
            .OrderBy(s => s.IsExcluded)
            .ThenBy(s => s.Created)
            .ThenBy(s => s.Name)
            .ToList();
    }

    public List<TmuxSession> GetGridSessions()
    {
        return GetVisibleSessions().Where(s => !s.IsExcluded).ToList();
    }

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

    public void CycleGroupFilter()
    {
        if (Groups.Count == 0) return;

        GroupFilterIndex++;
        if (GroupFilterIndex > Groups.Count)
            GroupFilterIndex = 0;

        CursorIndex = 0;
        TopIndex = 0;
    }

    public string GetGroupFilterLabel()
    {
        if (GroupFilterIndex == 0 || GroupFilterIndex > Groups.Count)
            return "All";
        return Groups[GroupFilterIndex - 1].Name;
    }

    public void EnsureCursorVisible(int visibleHeight)
    {
        if (CursorIndex < TopIndex)
            TopIndex = CursorIndex;
        else if (CursorIndex >= TopIndex + visibleHeight)
            TopIndex = CursorIndex - visibleHeight + 1;
    }

    public void EnterGroupGrid(string groupName)
    {
        _savedCursorIndex = CursorIndex;
        ActiveGroup = groupName;
        ViewMode = ViewMode.Grid;
        CursorIndex = 0;
    }

    public void LeaveGroupGrid()
    {
        ActiveGroup = null;
        ViewMode = ViewMode.List;
        CursorIndex = _savedCursorIndex;
        ClampCursor();
    }

    /// <summary>
    /// Returns (columns, rows) for the grid based on session count.
    /// </summary>
    public (int Cols, int Rows) GetGridDimensions()
    {
        var count = GetGridSessions().Count;
        return count switch
        {
            0 => (1, 1),
            1 => (1, 1),
            2 => (2, 1),
            3 or 4 => (2, 2),
            5 or 6 => (3, 2),
            7 or 8 or 9 => (3, 3),
            _ => (0, 0), // Signals "too many for grid"
        };
    }

    /// <summary>
    /// Returns the number of pane output lines to show per grid cell,
    /// calculated dynamically based on available terminal height.
    /// </summary>
    public int GetGridCellOutputLines()
    {
        var (_, gridRows) = GetGridDimensions();
        if (gridRows == 0) return 0;

        var terminalHeight = Console.WindowHeight;
        // Each row gets an equal share of: terminal height - app header (1) - status bar (1)
        var rowHeight = (terminalHeight - 2) / gridRows;
        // Subtract cell overhead: panel border (2) + name line (1) + path line (1) + rule (1)
        var outputLines = rowHeight - 5;
        return Math.Max(1, outputLines);
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
        StatusMessageTime = DateTime.Now;
    }

    public void ClearStatus()
    {
        StatusMessage = null;
        StatusMessageTime = null;
    }

    public bool HasPendingStatus =>
        StatusMessage != null && StatusMessageTime != null;

    public string? GetActiveStatus()
    {
        if (StatusMessage == null || StatusMessageTime == null)
            return null;

        if ((DateTime.Now - StatusMessageTime.Value).TotalSeconds > 2)
        {
            ClearStatus();
            return null;
        }

        return StatusMessage;
    }

    public string? LatestVersion { get; set; }

    public bool DiffMode { get; set; }

    public List<KeyBinding> Keybindings { get; set; } = [];

    public void ClampCursor()
    {
        var visible = MobileMode ? GetMobileVisibleSessions() : GetVisibleSessions();
        CursorIndex = visible.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, visible.Count - 1);
    }

    public void ClampGroupCursor() => GroupCursor = Groups.Count == 0
            ? 0
            : Math.Clamp(GroupCursor, 0, Groups.Count - 1);

    public void EnterSettings()
    {
        ViewMode = ViewMode.Settings;
        SettingsCategory = 0;
        SettingsItemCursor = 0;
        SettingsFocusRight = false;
        IsSettingsEditing = false;
        SettingsEditBuffer = "";
    }

    public void LeaveSettings()
    {
        ViewMode = ViewMode.List;
        IsSettingsEditing = false;
        SettingsEditBuffer = "";
    }

}
