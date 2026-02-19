using System.Text.Json;

namespace ClaudeCommandCenter.Services;

public static class UpdateChecker
{
    private static readonly string _currentVersion =
        typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
        DefaultRequestHeaders =
        {
            {
                "User-Agent", "ClaudeCommandCenter"
            }
        },
    };

    /// <summary>
    /// Checks GitHub for a newer release. Returns the version string if an update
    /// is available, null otherwise. Never throws — failures are silently ignored.
    /// </summary>
    public static async Task<string?> CheckForUpdateAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(
                "https://api.github.com/repos/AdamGardelov/ClaudeCommandCenter/releases/latest");

            using var doc = JsonDocument.Parse(json);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString();
            var latest = tagName?.TrimStart('v');

            if (latest != null
                && Version.TryParse(latest, out var latestV)
                && Version.TryParse(_currentVersion, out var currentV)
                && latestV > currentV)
                return latest;

            return null;
        }
        catch
        {
            return null;
        }
    }
}
