using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;

namespace ClaudeCommandCenter.Handlers;

public class DiffHandler(AppState state)
{
    public void Open()
    {
        var session = state.GetSelectedSession();
        if (session == null)
            return;

        if (session.StartCommitSha == null)
        {
            state.SetStatus("No baseline commit — cannot show diff");
            return;
        }

        if (session.CurrentPath == null)
        {
            state.SetStatus("Session has no working directory");
            return;
        }

        if (session.RemoteHostName != null)
        {
            state.SetStatus("Diff overlay not available for remote sessions");
            return;
        }

        var fullDiff = GitService.GetFullDiff(session.CurrentPath, session.StartCommitSha);
        if (string.IsNullOrWhiteSpace(fullDiff))
        {
            state.SetStatus("No changes since session start");
            return;
        }

        var stat = GitService.GetDiffStat(session.CurrentPath, session.StartCommitSha);
        var lines = fullDiff.Split('\n');
        state.EnterDiffOverlay(session.Name, session.GitBranch, stat, lines);
    }

    public void HandleKey(ConsoleKeyInfo key)
    {
        var totalLines = state.DiffOverlayLines.Length;
        // viewport = terminal height - header(1) - status bar(1) - panel border(2) - stat section
        var statOverhead = Renderer.DiffStatRowCount(state);
        var viewportHeight = Math.Max(1, Console.WindowHeight - 4 - statOverhead);
        var maxScroll = Math.Max(0, totalLines - viewportHeight);

        // Non-rebindable keys (always work)
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                state.LeaveDiffOverlay();
                return;
            case ConsoleKey.DownArrow:
                JumpToNextFile(maxScroll);
                return;
            case ConsoleKey.UpArrow:
                JumpToPreviousFile();
                return;
            case ConsoleKey.PageDown:
                state.DiffScrollOffset = Math.Min(state.DiffScrollOffset + viewportHeight, maxScroll);
                return;
            case ConsoleKey.PageUp:
                state.DiffScrollOffset = Math.Max(0, state.DiffScrollOffset - viewportHeight);
                return;
        }

        // Rebindable keys (looked up from keybindings)
        var keyId = FlowHelper.ResolveKeyId(key);
        var diffAction = state.Keybindings
            .FirstOrDefault(b => b.Enabled && b.ActionId.StartsWith("diff-") && b.Key == keyId)
            ?.ActionId;

        switch (diffAction)
        {
            case "diff-scroll-down":
                state.DiffScrollOffset = Math.Min(state.DiffScrollOffset + 1, maxScroll);
                break;
            case "diff-scroll-up":
                state.DiffScrollOffset = Math.Max(0, state.DiffScrollOffset - 1);
                break;
            case "diff-page-down":
                state.DiffScrollOffset = Math.Min(state.DiffScrollOffset + viewportHeight, maxScroll);
                break;
            case "diff-top":
                state.DiffScrollOffset = 0;
                break;
            case "diff-bottom":
                state.DiffScrollOffset = maxScroll;
                break;
            case "diff-toggle-stats":
                state.DiffStatExpanded = !state.DiffStatExpanded;
                break;
            case "diff-close":
                state.LeaveDiffOverlay();
                break;
        }
    }

    private void JumpToNextFile(int maxScroll)
    {
        var boundaries = state.DiffFileBoundaries;
        if (boundaries.Length == 0)
            return;

        // Find the first file boundary after the current scroll position
        foreach (var boundary in boundaries)
        {
            if (boundary > state.DiffScrollOffset)
            {
                state.DiffScrollOffset = Math.Min(boundary, maxScroll);
                return;
            }
        }

        // Already past last file — stay put
    }

    private void JumpToPreviousFile()
    {
        var boundaries = state.DiffFileBoundaries;
        if (boundaries.Length == 0)
            return;

        // Find the last file boundary before the current scroll position
        for (var i = boundaries.Length - 1; i >= 0; i--)
        {
            if (boundaries[i] < state.DiffScrollOffset)
            {
                state.DiffScrollOffset = boundaries[i];
                return;
            }
        }

        // Already before first file — jump to top
        state.DiffScrollOffset = 0;
    }
}
