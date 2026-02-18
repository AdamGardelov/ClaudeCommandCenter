using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.UI;

public class AppState
{
    public List<TmuxSession> Sessions { get; set; } = [];
    public int CursorIndex { get; set; }

    public bool Running { get; set; } = true;
    private string? StatusMessage { get; set; }
    private DateTime? StatusMessageTime { get; set; }

    public TmuxSession? GetSelectedSession()
    {
        if (CursorIndex >= 0 && CursorIndex < Sessions.Count)
            return Sessions[CursorIndex];
        return null;
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
        StatusMessageTime = DateTime.Now;
    }

    public string? GetActiveStatus()
    {
        if (StatusMessage == null || StatusMessageTime == null) 
            return null;
        
        return (DateTime.Now - StatusMessageTime.Value).TotalSeconds > 3 
            ? null 
            : StatusMessage;
    }

    public void ClampCursor()
    {
        CursorIndex = Sessions.Count == 0 ? 0 : Math.Clamp(CursorIndex, 0, Sessions.Count - 1);
    }
}
