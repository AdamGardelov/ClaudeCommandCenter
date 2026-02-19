namespace ClaudeCommandCenter.Models;

public class KeyBinding
{
    public required string ActionId { get; init; }
    public required string Key { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Label { get; init; }
    public bool CanDisable { get; init; } = true;
    public int StatusBarOrder { get; init; }
}
