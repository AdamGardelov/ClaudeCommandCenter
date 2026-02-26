using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.Services;

public interface ISessionBackend : IDisposable
{
    // Lifecycle
    List<Session> ListSessions();
    string? CreateSession(string name, string workingDirectory);
    string? KillSession(string name);
    string? RenameSession(string oldName, string newName);

    // Interaction
    void AttachSession(string name);
    void DetachSession();
    string? SendKeys(string sessionName, string text);
    void ForwardKey(string sessionName, ConsoleKeyInfo key);
    string? CapturePaneContent(string sessionName, int lines = 500);

    // Display
    void ResizeWindow(string sessionName, int width, int height);
    void ResetWindowSize(string sessionName);
    void ApplyStatusColor(string sessionName, string? spectreColor);

    // State detection
    void DetectWaitingForInputBatch(List<Session> sessions);

    // Environment checks
    bool IsAvailable();
    bool IsInsideHost();
    bool HasClaude();
}
