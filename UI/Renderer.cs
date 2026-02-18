using ClaudeCommandCenter.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ClaudeCommandCenter.UI;

public static class Renderer
{
    private static readonly IReadOnlyList<string> SpinnerFrames = Spinner.Known.Dots.Frames;
    private static readonly TimeSpan SpinnerInterval = Spinner.Known.Dots.Interval;

    private static string GetSpinnerFrame()
    {
        var index = (int)(DateTime.Now.Ticks / SpinnerInterval.Ticks % SpinnerFrames.Count);
        return SpinnerFrames[index];
    }

    public static IRenderable BuildLayout(AppState state, string? capturedPane)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("Main"),
                new Layout("StatusBar").Size(1));

        layout["Header"].Update(BuildHeader(state));

        layout["Main"].SplitColumns(
            new Layout("Sessions").Size(35),
            new Layout("Preview"));

        layout["Sessions"].Update(BuildSessionPanel(state));
        layout["Preview"].Update(BuildPreviewPanel(state, capturedPane));
        layout["StatusBar"].Update(BuildStatusBar(state));

        return layout;
    }

    private static readonly string Version =
        typeof(Renderer).Assembly.GetName().Version?.ToString(3) ?? "?";

    private static Columns BuildHeader(AppState state)
    {
        var left = new Markup($"[darkorange bold] Claude Command Center[/] [grey50]v{Version}[/]");
        var right = new Markup($"[grey]{state.Sessions.Count} session(s)[/] ");

        return new Columns(left, right) { Expand = true };
    }

    private static Panel BuildSessionPanel(AppState state)
    {
        var rows = new List<IRenderable>();

        for (var i = 0; i < state.Sessions.Count; i++)
        {
            var session = state.Sessions[i];
            var isSelected = i == state.CursorIndex;
            var time = session.Created?.ToString("HH:mm") ?? "     ";
            var name = Markup.Escape(session.Name);

            var spinner = Markup.Escape(GetSpinnerFrame());
            var status = session.IsWaitingForInput ? "!" : session.IsAttached ? spinner : " ";

            if (isSelected)
                rows.Add(new Markup($"[white on darkorange] {status} {name,-18} {time} [/]"));
            else if (session.IsWaitingForInput)
                rows.Add(new Markup($" [yellow bold]![/] [navajowhite1]{name,-18}[/] [grey50]{time}[/]"));
            else if (session.IsAttached)
                rows.Add(new Markup($" [green]{spinner}[/] [navajowhite1]{name,-18}[/] [grey50]{time}[/]"));
            else
                rows.Add(new Markup($"   [grey70]{name,-18}[/] [grey42]{time}[/]"));
        }

        if (rows.Count != 0)
            return new Panel(new Rows(rows))
                .Header("[darkorange] Sessions [/]")
                .BorderColor(Color.Grey42)
                .Expand();

        rows.Add(new Text(""));
        rows.Add(new Markup("[grey]  No tmux sessions found[/]"));
        rows.Add(new Markup("[grey]  Press [/][darkorange bold]n[/][grey] to create one[/]"));

        return new Panel(new Rows(rows))
            .Header("[darkorange] Sessions [/]")
            .BorderColor(Color.Grey42)
            .Expand();
    }

    private static Panel BuildPreviewPanel(AppState state, string? capturedPane)
    {
        var session = state.GetSelectedSession();

        if (session == null)
        {
            // Panel width = terminal - session panel (35) - panel borders (4)
            var panelWidth = Math.Max(20, Console.WindowWidth - 35 - 4);

            return new Panel(
                new Rows(
                    new Text(""),
                    CenterFiglet("Claude", panelWidth, Color.DarkOrange),
                    CenterFiglet("Command center", panelWidth, Color.DarkOrange),
                    new Text(""),
                    Align.Center(new Markup("[grey50]Select a session to see preview[/]"))))
                .Header("[darkorange] Preview [/]")
                .BorderColor(Color.Grey42)
                .Expand();
        }

        var rows = new List<IRenderable>
        {
            new Markup($" [grey50]Session:[/]  [white]{Markup.Escape(session.Name)}[/]"),
            new Markup($" [grey50]Path:[/]     [white]{Markup.Escape(session.CurrentPath ?? "unknown")}[/]"),
        };

        if (session.GitBranch != null)
            rows.Add(new Markup($" [grey50]Branch:[/]   [aqua]{Markup.Escape(session.GitBranch)}[/]"));

        if (session.IsWorktree)
            rows.Add(new Markup(" [grey50]Worktree:[/] [mediumpurple2]yes[/]"));

        rows.Add(new Markup($" [grey50]Created:[/]  [white]{session.Created?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown"}[/]"));
        rows.Add(new Markup($" [grey50]Status:[/]   {StatusLabel(session)}"));
        rows.Add(new Rule().RuleStyle(Style.Parse("grey42")));

        if (!string.IsNullOrWhiteSpace(capturedPane))
        {
            // Preview width = terminal width - session panel (35) - borders (6) - padding (2)
            var maxWidth = Math.Max(20, Console.WindowWidth - 35 - 8);
            var lines = capturedPane.Split('\n');

            // Available height = terminal - header (1) - status bar (1) - panel border (2) - info rows (5)
            var availableLines = Math.Max(1, Console.WindowHeight - 9);

            var maxOffset = Math.Max(0, lines.Length - availableLines);
            if (state.PreviewFollowBottom)
                state.PreviewScrollOffset = maxOffset;
            if (state.PreviewScrollOffset > maxOffset)
                state.PreviewScrollOffset = maxOffset;

            var visibleLines = lines.AsSpan(state.PreviewScrollOffset,
                Math.Min(availableLines, lines.Length - state.PreviewScrollOffset));

            foreach (var line in visibleLines)
            {
                var trimmed = line.Length > maxWidth ? line[..maxWidth] : line;
                rows.Add(new Markup($" {Markup.Escape(trimmed)}"));
            }

            if (state.PreviewScrollOffset > 0 || state.PreviewScrollOffset < maxOffset)
            {
                var indicator = $"[grey42] lines {state.PreviewScrollOffset + 1}-{state.PreviewScrollOffset + visibleLines.Length}/{lines.Length}[/]";
                rows.Add(new Markup(indicator));
            }
        }
        else
        {
            rows.Add(new Markup("[grey] No pane content available[/]"));
        }

        return new Panel(new Rows(rows))
            .Header("[darkorange] Preview [/]")
            .BorderColor(Color.Grey42)
            .Expand();
    }

    private static Markup BuildStatusBar(AppState state)
    {
        var status = state.GetActiveStatus();

        if (status != null)
            return new Markup($" [yellow]{Markup.Escape(status)}[/]");

        return new Markup(
            " [darkorange bold]j/k[/][grey] navigate [/] " +
            "[darkorange bold]J/K[/][grey] scroll [/] " +
            "[darkorange bold]Enter[/][grey] attach [/] " +
            "[darkorange bold]n[/][grey] new [/] " +
            "[darkorange bold]i[/][grey] ide [/] " +
            "[darkorange bold]d[/][grey] delete [/] " +
            "[darkorange bold]R[/][grey] rename [/] " +
            "[darkorange bold]r[/][grey] refresh [/] " +
            "[darkorange bold]q[/][grey] quit[/]");
    }

    private static IRenderable CenterFiglet(string text, int availableWidth, Color color)
    {
        var figlet = new FigletText(text) { Pad = false }.Color(color).LeftJustified();
        var options = RenderOptions.Create(AnsiConsole.Console, AnsiConsole.Console.Profile.Capabilities);
        var measured = ((IRenderable)figlet).Measure(options, availableWidth);
        var leftPad = Math.Max(0, (availableWidth - measured.Max) / 2);
        return new Padder(figlet).PadLeft(leftPad).PadRight(0).PadTop(0).PadBottom(0);
    }

    private static string StatusLabel(TmuxSession session)
    {
        if (session.IsWaitingForInput)
            return "[yellow bold]waiting for input[/]";

        return session.IsAttached
            ? "[green]attached[/]"
            : "[grey]detached[/]";
    }
}
