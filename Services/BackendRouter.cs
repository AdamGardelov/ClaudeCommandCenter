using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.Services;

/// <summary>
/// Routes ISessionBackend calls to the correct local or remote backend.
/// This is the single backend instance seen by App and all handlers.
/// </summary>
public class BackendRouter(ISessionBackend local, Dictionary<string, RemoteTmuxBackend> remotes, CccConfig config) : ISessionBackend
{
    // Maps session name → remote host name (null = local). Rebuilt on each ListSessions().
    private Dictionary<string, string?> _sessionHosts = new();

    public List<Session> ListSessions()
    {
        var all = new List<Session>();

        // Local sessions
        all.AddRange(local.ListSessions());

        // Remote sessions
        foreach (var (hostName, remoteBackend) in remotes)
        {
            // ListSessions() sets IsOffline as a side effect — result is empty list on failure
            var remoteSessions = remoteBackend.ListSessions();

            if (remoteBackend.IsOffline)
            {
                // Use cached sessions, marked offline so UI greys them out
                // and DetectWaitingForInputBatch skips SSH calls for them
                var cached = config.CachedRemoteSessions.GetValueOrDefault(hostName) ?? [];
                var offlineSessions = cached.Select(c => new Session
                {
                    Name = c.Name,
                    CurrentPath = c.Path,
                    Created = c.Created,
                    RemoteHostName = hostName,
                    IsOffline = true,
                }).ToList();
                all.AddRange(offlineSessions);
            }
            else
            {
                // Update cache with fresh data
                ConfigService.SaveRemoteSessionCache(config, hostName, remoteSessions);
                all.AddRange(remoteSessions);
            }
        }

        // Rebuild routing map
        _sessionHosts = all.ToDictionary(s => s.Name, s => s.RemoteHostName);

        return all;
    }

    public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null,
        string? remoteHost = null, bool dangerouslySkipPermissions = false)
    {
        if (remoteHost != null && remotes.TryGetValue(remoteHost, out var remoteBackend))
            return remoteBackend.CreateSession(name, workingDirectory, claudeConfigDir, null, dangerouslySkipPermissions);

        return local.CreateSession(name, workingDirectory, claudeConfigDir, null, dangerouslySkipPermissions);
    }

    public string? KillSession(string name) => BackendFor(name).KillSession(name);

    public string? RenameSession(string oldName, string newName)
    {
        var result = BackendFor(oldName).RenameSession(oldName, newName);
        if (result == null)
        {
            // Update routing map: old name is gone, new name takes its host
            if (_sessionHosts.TryGetValue(oldName, out var host))
            {
                _sessionHosts.Remove(oldName);
                _sessionHosts[newName] = host;
            }
        }
        return result;
    }

    public void AttachSession(string name) => BackendFor(name).AttachSession(name);

    public void DetachSession() => local.DetachSession();

    public string? SendKeys(string sessionName, string text) =>
        BackendFor(sessionName).SendKeys(sessionName, text);

    public void ForwardKey(string sessionName, ConsoleKeyInfo key) =>
        BackendFor(sessionName).ForwardKey(sessionName, key);

    public void ForwardLiteralBatch(string sessionName, string text) =>
        BackendFor(sessionName).ForwardLiteralBatch(sessionName, text);

    public string? CapturePaneContent(string sessionName, int lines = 500) =>
        BackendFor(sessionName).CapturePaneContent(sessionName, lines);

    public void ResizeWindow(string sessionName, int width, int height) =>
        BackendFor(sessionName).ResizeWindow(sessionName, width, height);

    public void ResetWindowSize(string sessionName) =>
        BackendFor(sessionName).ResetWindowSize(sessionName);

    public void ApplyStatusColor(string sessionName, string? spectreColor) =>
        BackendFor(sessionName).ApplyStatusColor(sessionName, spectreColor);

    public void DetectWaitingForInputBatch(List<Session> sessions)
    {
        // Split sessions by backend and dispatch to each
        var localSessions = sessions.Where(s => s.RemoteHostName == null).ToList();
        if (localSessions.Count > 0)
            local.DetectWaitingForInputBatch(localSessions);

        foreach (var (hostName, remoteBackend) in remotes)
        {
            // Skip sessions for offline hosts — they have IsOffline = true and
            // RemoteTmuxBackend.DetectWaitingForInputBatch skips them, but we
            // avoid the SSH round-trip entirely by not calling it when offline
            if (remoteBackend.IsOffline)
                continue;

            var remoteSessions = sessions
                .Where(s => s.RemoteHostName == hostName)
                .ToList();
            if (remoteSessions.Count > 0)
                remoteBackend.DetectWaitingForInputBatch(remoteSessions);
        }
    }

    public bool IsAvailable() => local.IsAvailable();
    public bool IsInsideHost() => local.IsInsideHost();
    public bool HasClaude() => local.HasClaude();

    public void Dispose()
    {
        local.Dispose();
        foreach (var remote in remotes.Values)
            remote.Dispose();
    }

    private ISessionBackend BackendFor(string sessionName)
    {
        if (_sessionHosts.TryGetValue(sessionName, out var hostName)
            && hostName != null
            && remotes.TryGetValue(hostName, out var remoteBackend))
            return remoteBackend;

        return local;
    }
}
