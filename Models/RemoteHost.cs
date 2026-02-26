namespace ClaudeCommandCenter.Models;

public class RemoteHost
{
    public required string Name { get; set; }
    public required string Host { get; set; }
    public string WorktreeBasePath { get; set; } = "~/worktrees";
    public List<FavoriteFolder> FavoriteFolders { get; set; } = [];
}
