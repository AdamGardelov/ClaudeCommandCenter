namespace ClaudeCommandCenter.Models;

public class CccConfig
{
    public List<FavoriteFolder> FavoriteFolders { get; set; } = [];
    public string IdeCommand { get; set; } = "";
    public Dictionary<string, string> SessionDescriptions { get; set; } = new();
    public Dictionary<string, string> SessionColors { get; set; } = new();
}