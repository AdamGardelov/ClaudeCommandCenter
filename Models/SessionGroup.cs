namespace ClaudeCommandCenter.Models;

public class SessionGroup
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "";
    public string WorktreePath { get; set; } = "";
    public List<string> Sessions { get; set; } = [];
}
