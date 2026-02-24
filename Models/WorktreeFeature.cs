namespace ClaudeCommandCenter.Models;

public record WorktreeFeature(string Name, string Description, string WorktreePath, Dictionary<string, string> Repos);
