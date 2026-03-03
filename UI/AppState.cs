using ClaudeCommandCenter.Enums;
using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.UI;

public class AppState
{
    public List<Session> Sessions { get; set; } = [];
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
    public HashSet<string> ExpandedGroups { get; set; } = [];
    public string? ActiveGroup { get; set; }
    private int _savedCursorIndex;
    private HashSet<string> _knownGroupNames = [];


    // Mobile mode state
    public bool MobileMode { get; set; }
    private int GroupFilterIndex { get; set; } // 0 = All, 1+ = group index
    public int TopIndex { get; set; } // Scroll offset for mobile list

    // Settings state
    public int SettingsCategory { get; set; }
    public int SettingsItemCursor { get; set; }
    public bool SettingsFocusRight { get; set; }
    public bool IsSettingsEditing { get; set; }
    public bool IsSettingsRebinding { get; set; }
    public string SettingsEditBuffer { get; set; } = "";

    // Diff overlay state
    public int DiffScrollOffset { get; set; }
    public string[] DiffOverlayLines { get; set; } = [];
    public string? DiffOverlaySessionName { get; set; }
    public string? DiffOverlayBranch { get; set; }
    public string? DiffOverlayStatSummary { get; set; }

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

    public List<Session> GetVisibleSessions()
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

    public List<Session> GetStandaloneSessions()
    {
        var groupedNames = new HashSet<string>(Groups.SelectMany(g => g.Sessions));
        return Sessions
            .Where(s => !groupedNames.Contains(s.Name))
            .OrderBy(s => s.IsExcluded)
            .ThenBy(s => s.Created)
            .ThenBy(s => s.Name)
            .ToList();
    }

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

    public void ToggleGroupExpanded(string groupName)
    {
        if (!ExpandedGroups.Remove(groupName))
            ExpandedGroups.Add(groupName);
    }

    public void InitExpandedGroups()
    {
        var currentNames = new HashSet<string>(Groups.Select(g => g.Name));
        // Clean up stale entries for groups that no longer exist
        ExpandedGroups.RemoveWhere(n => !currentNames.Contains(n));
        _knownGroupNames.RemoveWhere(n => !currentNames.Contains(n));
        // Only expand groups we haven't seen before
        foreach (var group in Groups)
        {
            if (_knownGroupNames.Add(group.Name))
                ExpandedGroups.Add(group.Name);
        }
    }

    public List<Session> GetGridSessions() =>
        GetVisibleSessions()
            .Where(s => !s.IsExcluded)
            .ToList();

    public List<Session> GetMobileVisibleSessions()
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
        if (Groups.Count == 0)
            return;

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
        if (gridRows == 0)
            return 0;

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

    public int[] DiffFileBoundaries { get; set; } = [];
    public bool DiffStatExpanded { get; set; }

    public List<KeyBinding> Keybindings { get; set; } = [];

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

    public void EnterDiffOverlay(string name, string? branch, string? stat, string[] lines)
    {
        ViewMode = ViewMode.DiffOverlay;
        DiffScrollOffset = 0;
        DiffOverlaySessionName = name;
        DiffOverlayBranch = branch;
        DiffOverlayStatSummary = stat;
        DiffOverlayLines = lines;

        // Pre-compute file boundaries for arrow-key file navigation
        var boundaries = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("diff --git "))
                boundaries.Add(i);
        DiffFileBoundaries = boundaries.ToArray();
        DiffStatExpanded = false;
    }

    public void LeaveDiffOverlay()
    {
        ViewMode = ViewMode.List;
        DiffOverlayLines = [];
        DiffOverlaySessionName = null;
        DiffOverlayBranch = null;
        DiffOverlayStatSummary = null;
        DiffFileBoundaries = [];
    }

    public void EnterSettings()
    {
        ViewMode = ViewMode.Settings;
        SettingsCategory = 0;
        SettingsItemCursor = 0;
        SettingsFocusRight = false;
        IsSettingsEditing = false;
        IsSettingsRebinding = false;
        SettingsEditBuffer = "";
    }

    public void LeaveSettings()
    {
        ViewMode = ViewMode.List;
        IsSettingsEditing = false;
        IsSettingsRebinding = false;
        SettingsEditBuffer = "";
    }
}
