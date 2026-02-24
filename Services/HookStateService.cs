namespace ClaudeCommandCenter.Services;

public static class HookStateService
{
    private static readonly string _stateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccc", "states");

    /// <summary>
    /// Reads the hook-written state file for a session.
    /// Returns "working", "idle", or "waiting", or null if no hook state exists.
    /// </summary>
    public static string? ReadState(string sessionName)
    {
        var path = Path.Combine(_stateDir, sessionName);
        if (!File.Exists(path))
            return null;

        try
        {
            // Ignore stale state files (older than 10 minutes — session likely ended)
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > TimeSpan.FromMinutes(10))
                return null;

            return File.ReadAllText(path).Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Removes state files for sessions that no longer exist.
    /// </summary>
    public static void Cleanup(IEnumerable<string> liveSessionNames)
    {
        if (!Directory.Exists(_stateDir))
            return;

        var live = new HashSet<string>(liveSessionNames);
        try
        {
            foreach (var file in Directory.GetFiles(_stateDir))
            {
                var name = Path.GetFileName(file);
                if (!live.Contains(name))
                    File.Delete(file);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
