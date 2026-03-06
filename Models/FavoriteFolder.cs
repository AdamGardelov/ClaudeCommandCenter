namespace ClaudeCommandCenter.Models;

public class FavoriteFolder
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public string DefaultBranch { get; set; } = "";
}
