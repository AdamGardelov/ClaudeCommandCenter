using ClaudeCommandCenter.Enums;
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;
using Spectre.Console;

namespace ClaudeCommandCenter.Handlers;

public class GroupHandler(
    AppState state,
    CccConfig config,
    FlowHelper flow,
    ISessionBackend backend,
    Action loadSessions,
    Action render,
    Action resetPaneCache,
    Action resetGridCache,
    Action resizeGridPanes)
{
    public void Open()
    {
        var group = state.GetSelectedGroup();
        if (group == null)
            return;

        if (group.Sessions.Count == 0)
        {
            // Stale group — offer to remove
            state.SetStatus($"Group '{group.Name}' has no live sessions. Press d to remove.");
            return;
        }

        state.EnterGroupGrid(group.Name);
        resetPaneCache();
        resizeGridPanes();
    }

    public void Delete()
    {
        var group = state.GetSelectedGroup();
        if (group == null)
            return;

        var liveCount = group.Sessions.Count;
        var msg = liveCount > 0
            ? $"Kill group '{group.Name}' and {liveCount} session(s)? (y/n)"
            : $"Remove stale group '{group.Name}'? (y/n)";

        state.SetStatus(msg);
        render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            foreach (var sessionName in group.Sessions.ToList())
            {
                backend.KillSession(sessionName);
                ConfigService.RemoveDescription(config, sessionName);
                ConfigService.RemoveColor(config, sessionName);
                ConfigService.RemoveExcluded(config, sessionName);
                ConfigService.RemoveStartCommit(config, sessionName);
                ConfigService.RemoveRemoteHost(config, sessionName);
            }

            ConfigService.RemoveGroup(config, group.Name);
            loadSessions();
            state.SetStatus("Group deleted");
        }
        else
        {
            state.SetStatus("Cancelled");
        }
    }

    public void Edit()
    {
        var group = state.GetSelectedGroup();
        if (group == null)
            return;

        FlowHelper.RunFlow($"Edit Group — {group.Name}", () =>
        {
            // Edit name
            FlowHelper.PrintStep(1, 3, "Name");
            var newName = flow.PromptOptional("Name", group.Name);

            if (!string.IsNullOrWhiteSpace(newName))
                newName = FlowHelper.SanitizeSessionName(newName);

            if (!string.IsNullOrWhiteSpace(newName) && newName != group.Name && config.Groups.ContainsKey(newName))
                throw new FlowCancelledException($"Group '{newName}' already exists");

            // Add more sessions
            FlowHelper.PrintStep(2, 3, "Sessions");
            var currentCount = group.Sessions.Count;
            var remaining = 9 - currentCount;
            var newDirectories = new List<(string Dir, string Label)>();

            if (remaining > 0)
            {
                AnsiConsole.MarkupLine($"[grey70]Current sessions: {currentCount}/9 — you can add {remaining} more[/]");

                var addMore = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[grey70]Add sessions?[/]")
                        .HighlightStyle(new Style(Color.White, Color.Grey70))
                        .AddChoices("Yes", "No", FlowHelper.CancelChoice));

                switch (addMore)
                {
                    case FlowHelper.CancelChoice:
                        throw new FlowCancelledException();
                    case "Yes":
                    {
                        for (var i = 0; i < remaining; i++)
                        {
                            AnsiConsole.MarkupLine($"\n[grey70]New session {i + 1} of {remaining}[/]");

                            var dir = flow.PickDirectory();
                            if (dir == null)
                                break;

                            dir = ConfigService.ExpandPath(dir);
                            if (!Directory.Exists(dir))
                            {
                                AnsiConsole.MarkupLine("[red]Directory not found, skipping[/]");
                                continue;
                            }

                            var label = Path.GetFileName(dir.TrimEnd('/'));
                            newDirectories.Add((dir, label));

                            if (currentCount + newDirectories.Count >= 9)
                                break;

                            var more = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title("[grey70]Add another session?[/]")
                                    .HighlightStyle(new Style(Color.White, Color.Grey70))
                                    .AddChoices("Yes", "No"));

                            if (more == "No")
                                break;
                        }

                        break;
                    }
                }
            }
            else
            {
                AnsiConsole.MarkupLine("\n[grey50]Group is full (9/9 sessions)[/]");
            }

            // Pick new color
            FlowHelper.PrintStep(3, 3, "Color");
            var currentColor = !string.IsNullOrEmpty(group.Color) ? group.Color : "none";
            AnsiConsole.MarkupLine($"[grey70]Current color:[/] [{currentColor}]{currentColor}[/]");
            var newColor = flow.PickColor();

            var effectiveName = !string.IsNullOrWhiteSpace(newName) && newName != group.Name ? newName : group.Name;
            var changed = false;

            // Apply name change — rename all tmux sessions and update config
            if (effectiveName != group.Name)
            {
                var oldName = group.Name;

                var renamedSessions = new List<string>();
                foreach (var sessionName in group.Sessions.ToList())
                {
                    string newSessionName;
                    if (sessionName.StartsWith(oldName + "-"))
                        newSessionName = effectiveName + sessionName[oldName.Length..];
                    else
                        newSessionName = effectiveName + "-" + sessionName;

                    var renameError = backend.RenameSession(sessionName, newSessionName);
                    if (renameError != null)
                        throw new FlowCancelledException($"Failed to rename session: {renameError}");

                    ConfigService.RenameDescription(config, sessionName, newSessionName);
                    ConfigService.RenameColor(config, sessionName, newSessionName);
                    ConfigService.RenameStartCommit(config, sessionName, newSessionName);
                    ConfigService.RenameRemoteHost(config, sessionName, newSessionName);
                    renamedSessions.Add(newSessionName);
                }

                ConfigService.RemoveGroup(config, oldName);
                group.Name = effectiveName;
                group.Sessions = renamedSessions;
                ConfigService.SaveGroup(config, group);
                changed = true;
            }

            // Create new sessions
            var usedNames = new HashSet<string>(group.Sessions, StringComparer.Ordinal);
            foreach (var (dir, label) in newDirectories)
            {
                var sessionName = FlowHelper.UniqueSessionName(FlowHelper.SanitizeSessionName($"{effectiveName}-{label}"), usedNames);
                usedNames.Add(sessionName);
                var error = backend.CreateSession(sessionName, dir, ConfigService.ResolveClaudeConfigDir(config, dir));
                if (error != null)
                    throw new FlowCancelledException($"Failed to create session '{sessionName}': {error}");

                var sessionColor = newColor ?? group.Color;
                if (!string.IsNullOrEmpty(sessionColor))
                {
                    ConfigService.SaveColor(config, sessionName, sessionColor);
                    backend.ApplyStatusColor(sessionName, sessionColor);
                }

                group.Sessions.Add(sessionName);
                changed = true;
            }

            // Apply color change to all existing sessions
            if (newColor != null)
            {
                group.Color = newColor;
                foreach (var sessionName in group.Sessions)
                {
                    ConfigService.SaveColor(config, sessionName, newColor);
                    backend.ApplyStatusColor(sessionName, newColor);
                }

                changed = true;
            }

            if (changed)
            {
                ConfigService.SaveGroup(config, group);
                loadSessions();
                state.GroupCursor = state.Groups.FindIndex(g => g.Name == effectiveName);
                if (state.GroupCursor < 0)
                    state.GroupCursor = 0;
                state.SetStatus("Group updated");
            }
            else
            {
                state.SetStatus("No changes");
            }
        }, state);
    }

    public void DeleteSessionFromGroup()
    {
        var session = state.GetSelectedSession();
        if (session == null || state.ActiveGroup == null)
            return;

        state.SetStatus($"Kill '{session.Name}' from group? (y/n)");
        render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            backend.KillSession(session.Name);
            ConfigService.RemoveDescription(config, session.Name);
            ConfigService.RemoveColor(config, session.Name);
            ConfigService.RemoveExcluded(config, session.Name);
            ConfigService.RemoveStartCommit(config, session.Name);
            ConfigService.RemoveRemoteHost(config, session.Name);
            ConfigService.RemoveSessionFromGroup(config, state.ActiveGroup, session.Name);
            loadSessions();

            // If group is now empty, leave grid
            var group = state.Groups.FirstOrDefault(g => g.Name == state.ActiveGroup);
            if (group == null || group.Sessions.Count == 0)
            {
                state.LeaveGroupGrid();
                state.SetStatus("Group removed (last session killed)");
            }
            else
            {
                state.CursorIndex = Math.Clamp(state.CursorIndex, 0, group.Sessions.Count - 1);
                state.SetStatus("Session killed");
            }

            resetPaneCache();
            resetGridCache();
        }
        else
        {
            state.SetStatus("Cancelled");
        }
    }

    public void CreateNew(bool claudeAvailable)
    {
        if (!claudeAvailable)
        {
            state.SetStatus("'claude' not found in PATH — install Claude Code first");
            return;
        }

        FlowHelper.RunFlow("New Group", () =>
        {
            var basePath = ConfigService.ExpandPath(config.WorktreeBasePath);
            var hasExistingWorktrees = Directory.Exists(basePath) && FlowHelper.ScanWorktreeFeatures(basePath).Count > 0;
            var hasGitRepos = config.FavoriteFolders.Any(f => GitService.IsGitRepo(ConfigService.ExpandPath(f.Path)));

            var modePrompt = new SelectionPrompt<string>()
                .Title("[grey70]Create group from[/]")
                .HighlightStyle(new Style(Color.White, Color.Grey70));

            if (hasExistingWorktrees)
                modePrompt.AddChoice("Existing worktree feature");
            if (hasGitRepos)
                modePrompt.AddChoice("New worktrees (pick repos)");
            modePrompt.AddChoices("Manual (pick directories)", FlowHelper.CancelChoice);

            var mode = AnsiConsole.Prompt(modePrompt);
            if (mode == FlowHelper.CancelChoice)
                throw new FlowCancelledException();

            if (mode.StartsWith("Existing"))
            {
                CreateFromWorktree(basePath);
                return;
            }

            if (mode.StartsWith("New worktrees"))
            {
                CreateFromNewWorktrees();
                return;
            }

            CreateManually();
        }, state);
    }

    private void CreateFromWorktree(string basePath)
    {
        var features = FlowHelper.ScanWorktreeFeatures(basePath);
        var activeGroupNames = new HashSet<string>(config.Groups.Keys);
        var available = features.Where(f => !activeGroupNames.Contains(f.Name)).ToList();
        if (available.Count == 0)
            throw new FlowCancelledException("All worktrees already have active groups");

        FlowHelper.PrintStep(1, 2, "Worktree");
        var prompt = new SelectionPrompt<string>()
            .Title("[grey70]Select a worktree feature[/]")
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        foreach (var f in available)
        {
            var repos = string.Join(", ", f.Repos.Keys);
            prompt.AddChoice($"{f.Name} - {f.Description} ({repos})");
        }

        prompt.AddChoice(FlowHelper.CancelChoice);

        var selected = AnsiConsole.Prompt(prompt);
        if (selected == FlowHelper.CancelChoice)
            throw new FlowCancelledException();

        var selectedName = selected.Split(" - ")[0];
        var feature = available.FirstOrDefault(f => f.Name == selectedName)
                      ?? throw new FlowCancelledException("Feature not found");

        FlowHelper.PrintStep(2, 2, "Color");
        var color = flow.PickColor();

        var sessionNames = new List<string>();
        foreach (var (repoName, repoPath) in feature.Repos)
        {
            var sessionName = FlowHelper.SanitizeSessionName($"{feature.Name}-{repoName}");
            var error = backend.CreateSession(sessionName, repoPath, ConfigService.ResolveClaudeConfigDir(config, repoPath));
            if (error != null)
                throw new FlowCancelledException($"Failed to create session '{sessionName}': {error}");

            if (color != null)
            {
                ConfigService.SaveColor(config, sessionName, color);
                backend.ApplyStatusColor(sessionName, color);
            }

            sessionNames.Add(sessionName);
        }

        var group = new SessionGroup
        {
            Name = feature.Name,
            Description = feature.Description,
            Color = color ?? "",
            WorktreePath = feature.WorktreePath,
            Sessions = sessionNames,
        };
        ConfigService.SaveGroup(config, group);
        FinishCreation(feature.Name);
    }

    private void CreateFromNewWorktrees()
    {
        var gitFavorites = config.FavoriteFolders
            .Where(f => GitService.IsGitRepo(ConfigService.ExpandPath(f.Path)))
            .ToList();

        if (gitFavorites.Count < 2)
            throw new FlowCancelledException("Need at least 2 git repos in favorites");

        FlowHelper.PrintStep(1, 3, "Repositories");
        var selectedRepos = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[grey70]Select repos[/]")
                .PageSize(10)
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .InstructionsText("[grey](space to toggle, enter to confirm)[/]")
                .AddChoices(gitFavorites.Select(f => $"{f.Name}  [grey50]{f.Path}[/]")));

        if (selectedRepos.Count < 2)
            throw new FlowCancelledException("Groups need at least 2 repos");

        FlowHelper.PrintStep(2, 3, "Feature name");
        var featureName = FlowHelper.RequireText("[grey70]Feature name[/] [grey](used for branch + folder)[/][grey70]:[/]");

        var sanitizedName = FlowHelper.SanitizeSessionName(featureName);
        var branchName = GitService.SanitizeBranchName(featureName);

        if (config.Groups.ContainsKey(sanitizedName))
            throw new FlowCancelledException($"Group '{sanitizedName}' already exists");

        FlowHelper.PrintStep(3, 3, "Color");
        var color = flow.PickColor();

        var basePath = ConfigService.ExpandPath(config.WorktreeBasePath);
        var featurePath = Path.Combine(basePath, branchName);

        // Resolve selected names back to favorites
        var repos = new List<(string Name, string RepoPath)>();
        foreach (var sel in selectedRepos)
        {
            var name = sel.Split("  ")[0];
            var fav = gitFavorites.FirstOrDefault(f => f.Name == name);
            if (fav != null)
                repos.Add((name, ConfigService.ExpandPath(fav.Path)));
        }

        // Create worktrees with progress
        var worktrees = new Dictionary<string, string>();
        string? error = null;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey70))
            .Start("[grey70]Creating worktrees...[/]", ctx =>
            {
                foreach (var (repoName, repoPath) in repos)
                {
                    ctx.Status($"[grey70]Creating worktree [white]{repoName}[/]...[/]");
                    GitService.FetchPrune(repoPath);

                    var dest = Path.Combine(featurePath, repoName);
                    Directory.CreateDirectory(featurePath);

                    error = GitService.CreateWorktree(repoPath, dest, branchName);
                    if (error != null)
                    {
                        error = $"Failed to create worktree for {repoName}: {error}";
                        return;
                    }

                    worktrees[repoName] = dest;
                }
            });

        if (error != null)
            throw new FlowCancelledException(error);

        FlowHelper.WriteFeatureContext(featurePath, featureName, worktrees);

        var sessionNames = new List<string>();
        foreach (var (repoName, worktreePath) in worktrees)
        {
            var sessionName = FlowHelper.SanitizeSessionName($"{sanitizedName}-{repoName}");
            var sessionError = backend.CreateSession(sessionName, worktreePath, ConfigService.ResolveClaudeConfigDir(config, worktreePath));
            if (sessionError != null)
                throw new FlowCancelledException($"Failed to create session '{sessionName}': {sessionError}");

            if (color != null)
            {
                ConfigService.SaveColor(config, sessionName, color);
                backend.ApplyStatusColor(sessionName, color);
            }

            sessionNames.Add(sessionName);
        }

        var group = new SessionGroup
        {
            Name = sanitizedName,
            Description = featureName,
            Color = color ?? "",
            WorktreePath = featurePath,
            Sessions = sessionNames,
        };
        ConfigService.SaveGroup(config, group);
        FinishCreation(sanitizedName);
    }

    private void CreateManually()
    {
        FlowHelper.PrintStep(1, 3, "Name");
        var name = FlowHelper.RequireText("[grey70]Group name:[/]");
        name = FlowHelper.SanitizeSessionName(name);

        if (config.Groups.ContainsKey(name))
            throw new FlowCancelledException($"Group '{name}' already exists");

        FlowHelper.PrintStep(2, 3, "Directories");
        var directories = new List<(string Dir, string Label)>();

        for (var i = 0; i < 9; i++)
        {
            AnsiConsole.MarkupLine($"\n[grey70]Session {i + 1} of 9[/]");

            var dir = flow.PickDirectory();
            if (dir == null)
            {
                if (directories.Count == 0)
                    throw new FlowCancelledException();
                break;
            }

            dir = ConfigService.ExpandPath(dir);
            if (!Directory.Exists(dir))
            {
                AnsiConsole.MarkupLine("[red]Directory not found, skipping[/]");
                continue;
            }

            var label = Path.GetFileName(dir.TrimEnd('/'));
            directories.Add((dir, label));

            if (directories.Count >= 9)
                break;

            if (directories.Count >= 2)
            {
                var more = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[grey70]Add another session?[/]")
                        .HighlightStyle(new Style(Color.White, Color.Grey70))
                        .AddChoices("Yes", "No, create group"));

                if (more.StartsWith("No"))
                    break;
            }
        }

        if (directories.Count < 2)
            throw new FlowCancelledException("Groups need at least 2 sessions");

        FlowHelper.PrintStep(3, 3, "Color");
        var color = flow.PickColor();

        var sessionNames = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dir, label) in directories)
        {
            var sessionName = FlowHelper.UniqueSessionName(FlowHelper.SanitizeSessionName($"{name}-{label}"), usedNames);
            usedNames.Add(sessionName);
            var error = backend.CreateSession(sessionName, dir, ConfigService.ResolveClaudeConfigDir(config, dir));
            if (error != null)
                throw new FlowCancelledException($"Failed to create session '{sessionName}': {error}");

            if (color != null)
            {
                ConfigService.SaveColor(config, sessionName, color);
                backend.ApplyStatusColor(sessionName, color);
            }

            sessionNames.Add(sessionName);
        }

        var group = new SessionGroup
        {
            Name = name,
            Description = "",
            Color = color ?? "",
            WorktreePath = "",
            Sessions = sessionNames,
        };
        ConfigService.SaveGroup(config, group);
        FinishCreation(name);
    }

    private void FinishCreation(string groupName)
    {
        loadSessions();
        state.ActiveSection = ActiveSection.Groups;
        state.GroupCursor = state.Groups.FindIndex(g => g.Name == groupName);
        if (state.GroupCursor < 0)
            state.GroupCursor = 0;
        state.EnterGroupGrid(groupName);
        resetPaneCache();
        resizeGridPanes();
    }

    public void MoveSessionToGroup()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        var eligible = state.Groups
            .Where(g => !g.Sessions.Contains(session.Name))
            .ToList();

        if (eligible.Count == 0)
        {
            state.SetStatus("No groups available");
            return;
        }

        FlowHelper.RunFlow($"Move Session — {session.Name}", () =>
        {
            var prompt = new SelectionPrompt<string>()
                .Title($"[grey70]Move[/] [white]'{Markup.Escape(session.Name)}'[/] [grey70]to group[/]")
                .HighlightStyle(new Style(Color.White, Color.Grey70));

            foreach (var g in eligible)
                prompt.AddChoice($"{Markup.Escape(g.Name)} ({g.Sessions.Count} sessions)");
            prompt.AddChoice(FlowHelper.CancelChoice);

            var selected = AnsiConsole.Prompt(prompt);
            if (selected == FlowHelper.CancelChoice)
                throw new FlowCancelledException();

            var groupName = selected[..selected.LastIndexOf(" (", StringComparison.Ordinal)];
            var group = state.Groups.FirstOrDefault(g => g.Name == groupName)
                        ?? throw new FlowCancelledException("Group not found");

            group.Sessions.Add(session.Name);
            ConfigService.SaveGroup(config, group);

            if (!string.IsNullOrEmpty(group.Color) && !config.SessionColors.ContainsKey(session.Name))
            {
                ConfigService.SaveColor(config, session.Name, group.Color);
                backend.ApplyStatusColor(session.Name, group.Color);
            }

            loadSessions();
            resetPaneCache();
            state.SetStatus($"Moved to '{groupName}'");
        }, state);
    }
}
