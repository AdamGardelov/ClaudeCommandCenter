using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.UI;

public class SettingsCategory
{
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public required Func<CccConfig, List<SettingsItem>> BuildItems { get; init; }
}
