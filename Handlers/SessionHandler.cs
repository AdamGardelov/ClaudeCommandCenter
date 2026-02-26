using System.Diagnostics;
using ClaudeCommandCenter.Enums;
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;
using Spectre.Console;

namespace ClaudeCommandCenter.Handlers;

public class SessionHandler(
    AppState state,
    CccConfig config,
    FlowHelper flow,
    ISessionBackend backend,
    Action loadSessions,
    Action render,
    Action resetPaneCache)
{
    public void Create(bool claudeAvailable)
    {
        if (!claudeAvailable)
        {
            state.SetStatus("'claude' not found in PATH — install Claude Code first");
            return;
        }

        FlowHelper.RunFlow("New Session", () =>
        {
            var hasRemotes = config.RemoteHosts.Count > 0;
            var totalSteps = hasRemotes ? 5 : 4;
            var step = 0;

            // Step: Target (only if remote hosts configured)
            RemoteHost? remoteHost = null;
            if (hasRemotes)
            {
                FlowHelper.PrintStep(++step, totalSteps, "Target");
                remoteHost = flow.PickTarget();

                if (remoteHost != null)
                {
                    var alive = false;
                    AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(new Style(Color.Grey70))
                        .Start($"[grey70]Checking connection to [white]{remoteHost.Name}[/]...[/]", _ =>
                        {
                            alive = SshService.CheckConnectivity(remoteHost.Host);
                        });

                    if (!alive)
                        throw new FlowCancelledException($"Cannot reach {remoteHost.Name} ({remoteHost.Host})");
                }
            }

            // Step: Directory
            FlowHelper.PrintStep(++step, totalSteps, "Directory");
            string? worktreeBranch = null;
            string? dir;

            if (remoteHost != null)
            {
                dir = flow.PickRemoteDirectory(remoteHost,
                          onWorktreeBranchCreated: branch => worktreeBranch = branch)
                      ?? throw new FlowCancelledException();
            }
            else
            {
                dir = flow.PickDirectory(
                          onWorktreeBranchCreated: branch => worktreeBranch = branch)
                      ?? throw new FlowCancelledException();

                dir = ConfigService.ExpandPath(dir);
                if (!Directory.Exists(dir))
                    throw new FlowCancelledException("Invalid directory");
            }

            // Step: Name
            FlowHelper.PrintStep(++step, totalSteps, "Name");
            var dirName = worktreeBranch ?? dir.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? "session";
            var defaultName = FlowHelper.SanitizeSessionName(dirName);
            var existingNames = new HashSet<string>(state.Sessions.Select(s => s.Name), StringComparer.Ordinal);
            defaultName = FlowHelper.UniqueSessionName(defaultName, existingNames, " ");
            var name = flow.PromptWithDefault("Session name", defaultName);

            // Step: Description
            FlowHelper.PrintStep(++step, totalSteps, "Description");
            var description = flow.PromptOptional("Description", null);

            // Step: Color
            FlowHelper.PrintStep(++step, totalSteps, "Color");
            var color = flow.PickColor();

            // Create session
            var claudeConfigDir = remoteHost == null
                ? ConfigService.ResolveClaudeConfigDir(config, dir)
                : null;
            var error = backend.CreateSession(name, dir, claudeConfigDir, remoteHost?.Host);
            if (error != null)
                throw new FlowCancelledException(error);

            if (!string.IsNullOrWhiteSpace(description))
                ConfigService.SaveDescription(config, name, description);
            if (color != null)
                ConfigService.SaveColor(config, name, color);
            if (remoteHost != null)
                ConfigService.SaveRemoteHost(config, name, remoteHost.Name);
            backend.ApplyStatusColor(name, color ?? "grey42");
            backend.AttachSession(name);
            loadSessions();
            resetPaneCache();
        }, state);
    }

    public void Delete()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        state.SetStatus($"Kill '{session.Name}'? (y/n)");
        render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            var killError = backend.KillSession(session.Name);
            if (killError == null)
            {
                ConfigService.RemoveDescription(config, session.Name);
                ConfigService.RemoveColor(config, session.Name);
                ConfigService.RemoveExcluded(config, session.Name);
                ConfigService.RemoveStartCommit(config, session.Name);
                ConfigService.RemoveRemoteHost(config, session.Name);
                state.SetStatus("Session killed");
            }
            else
            {
                state.SetStatus(killError);
            }

            loadSessions();
        }
        else
        {
            state.SetStatus("Cancelled");
        }
    }

    public void Edit()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        FlowHelper.RunFlow($"Edit Session — {session.Name}", () =>
        {
            FlowHelper.PrintStep(1, 3, "Name");
            var newName = flow.PromptOptional("Name", session.Name);

            FlowHelper.PrintStep(2, 3, "Description");
            var newDesc = flow.PromptOptional("Description", session.Description);

            FlowHelper.PrintStep(3, 3, "Color");
            var newColor = flow.PickColor();

            var currentName = session.Name;
            var changed = false;

            if (!string.IsNullOrWhiteSpace(newName) && newName != currentName)
            {
                var renameError = backend.RenameSession(currentName, newName);
                if (renameError != null)
                    throw new FlowCancelledException(renameError);

                ConfigService.RenameDescription(config, currentName, newName);
                ConfigService.RenameColor(config, currentName, newName);
                ConfigService.RenameExcluded(config, currentName, newName);
                ConfigService.RenameStartCommit(config, currentName, newName);
                ConfigService.RenameRemoteHost(config, currentName, newName);
                currentName = newName;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(newDesc))
            {
                ConfigService.SaveDescription(config, currentName, newDesc);
                changed = true;
            }

            if (newColor != null)
            {
                ConfigService.SaveColor(config, currentName, newColor);
                backend.ApplyStatusColor(currentName, newColor);
                changed = true;
            }

            if (changed)
            {
                loadSessions();
                state.SetStatus("Session updated");
            }
            else
            {
                state.SetStatus("No changes");
            }
        }, state);
    }

    public void Attach()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        // Exit CCC's alternate screen so the ConPTY session renders to normal screen
        Console.Write("\e[?1049l"); // Leave alternate screen
        Console.Write("\e[?25h");   // Show cursor
        Console.Write("\e[2J\e[H"); // Clear screen + cursor home

        backend.ResetWindowSize(session.Name);
        backend.AttachSession(session.Name);

        // User detached — reset cooldown so next idle transition notifies fresh
        NotificationService.ResetCooldown(session.Name);

        // Re-enter CCC's alternate screen buffer
        Console.Write("\e[?1049h"); // Enter alternate screen buffer
        Console.Write("\e[?1003l\e[?1006l\e[?1015l\e[?1000l"); // Disable mouse tracking
        Console.Write("\e[2J");     // Clear screen
        Console.Write("\e[H");      // Cursor home
        Console.Write("\e[?25l");   // Re-hide cursor
        loadSessions();
        resetPaneCache();
        render();
    }

    public void ToggleExclude()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        ConfigService.ToggleExcluded(config, session.Name);
        session.IsExcluded = !session.IsExcluded;

        if (state.ViewMode == ViewMode.Grid && session.IsExcluded)
        {
            // Session just got excluded from grid — clamp cursor
            var gridSessions = state.GetGridSessions();
            if (gridSessions.Count < 2)
                // Not enough sessions for grid, switch to list
                state.ViewMode = ViewMode.List;
            else
                state.CursorIndex = Math.Clamp(state.CursorIndex, 0, gridSessions.Count - 1);
        }

        var label = session.IsExcluded ? "Excluded from grid" : "Restored to grid";
        state.SetStatus(label);
        resetPaneCache();
    }

    public void SendQuickKey(string key)
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        var error = backend.SendKeys(session.Name, key);
        if (error == null)
        {
            state.SetStatus($"Sent '{key}' to {session.Name}");
            resetPaneCache(); // Force pane refresh
        }
        else
        {
            state.SetStatus(error);
        }
    }

    public void SendText()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        state.IsInputMode = true;
        state.InputBuffer = "";
        state.InputTarget = session.Name;
    }

    public void OpenFolder()
    {
        var session = state.GetSelectedSession();
        if (session?.CurrentPath == null)
            return;

        if (session.RemoteHostName != null)
        {
            state.SetStatus("Open folder not available for remote sessions");
            return;
        }

        try
        {
            var command = FlowHelper.GetFileManagerCommand();
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                ArgumentList =
                {
                    session.CurrentPath
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process.Start(startInfo);
            state.SetStatus($"Opened folder: {session.CurrentPath}");
        }
        catch
        {
            state.SetStatus("Failed to open folder");
        }
    }

    public void OpenInIde()
    {
        var session = state.GetSelectedSession();
        if (session?.CurrentPath == null)
            return;

        if (session.RemoteHostName != null)
        {
            state.SetStatus("Open in IDE not available for remote sessions");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.IdeCommand))
        {
            state.SetStatus("Set ideCommand in settings first (press s)");
            return;
        }

        state.SetStatus(FlowHelper.LaunchWithIde(config.IdeCommand, session.CurrentPath)
            ? $"Opened in {config.IdeCommand}"
            : $"Failed to run '{config.IdeCommand}'");
    }
}
