using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.UI;

public class SettingsItem
{
    public required string Label { get; init; }
    public SettingsItemType Type { get; init; }
    public Func<CccConfig, string>? GetValue { get; init; }
    public Action<CccConfig, string>? SetValue { get; init; }
    public string? ActionId { get; init; }
}
