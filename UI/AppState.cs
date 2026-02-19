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

    public TmuxSession? GetSelectedSession()
    {
        if (CursorIndex >= 0 && CursorIndex < Sessions.Count)
            return Sessions[CursorIndex];
        return null;
    }

    /// <summary>
    /// Returns (columns, rows) for the grid based on session count.
    /// </summary>
    public (int Cols, int Rows) GetGridDimensions()
    {
        return Sessions.Count switch
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
    /// Returns the number of pane output lines to show per grid cell.
    /// </summary>
    public int GetGridCellOutputLines()
    {
        return Sessions.Count switch
        {
            1 => 30,
            2 => 15,
            3 or 4 => 10,
            5 or 6 => 5,
            7 or 8 or 9 => 3,
            _ => 0,
        };
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

    public List<KeyBinding> Keybindings { get; set; } = [];

    public void ClampCursor()
    {
        CursorIndex = Sessions.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, Sessions.Count - 1);
    }
}