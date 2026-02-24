namespace ClaudeCommandCenter.Services;

public static class CrashLog
{
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccc", "crash.log");

    private const long _maxBytes = 512 * 1024; // 500 KB

    public static void Write(Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath)!;
            Directory.CreateDirectory(dir);

            TrimIfNeeded();

            using var writer = File.AppendText(_logPath);
            writer.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            writer.WriteLine(ex.ToString());
            writer.WriteLine();
        }
        catch
        {
            // Last resort — don't throw from the crash logger
        }
    }

    private static void TrimIfNeeded()
    {
        if (!File.Exists(_logPath))
            return;

        var info = new FileInfo(_logPath);
        if (info.Length <= _maxBytes)
            return;

        // Keep the last half of the file
        var text = File.ReadAllText(_logPath);
        var keepFrom = text.Length / 2;

        // Find the next entry boundary so we don't cut mid-entry
        var boundary = text.IndexOf("\n--- ", keepFrom, StringComparison.Ordinal);
        File.WriteAllText(_logPath, boundary > 0 ? text[(boundary + 1)..] : text[keepFrom..]);
    }
}
