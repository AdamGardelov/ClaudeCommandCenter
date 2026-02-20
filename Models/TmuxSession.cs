namespace ClaudeCommandCenter.Models;

public class TmuxSession
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime? Created { get; set; }
    public bool IsAttached { get; set; }
    public int WindowCount { get; set; }
    public string? ColorTag { get; set; }
    public string? CurrentPath { get; set; }
    public bool IsWaitingForInput { get; set; }
    public string? PreviousContentHash { get; set; }
    public int StableContentCount { get; set; }
    public string? GitBranch { get; set; }
    public bool IsWorktree { get; set; }
    public bool IsExcluded { get; set; }
}
