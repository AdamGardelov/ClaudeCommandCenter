using ClaudeCommandCenter.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ClaudeCommandCenter.UI;

public static class Renderer
{
    private static readonly IReadOnlyList<string> SpinnerFrames = Spinner.Known.Dots.Frames;
    private static readonly TimeSpan SpinnerInterval = Spinner.Known.Dots.Interval;

    public static string GetSpinnerFrame()
    {
        var index = (int)(DateTime.Now.Ticks / SpinnerInterval.Ticks % SpinnerFrames.Count);
        return SpinnerFrames[index];
    }

    public static IRenderable BuildLayout(AppState state, string? capturedPane,
        Dictionary<string, string>? allCapturedPanes = null)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("Main"),
                new Layout("StatusBar").Size(1));

        layout["Header"].Update(BuildHeader(state));

        if (state.ViewMode == ViewMode.Grid)
        {
            var (cols, _) = state.GetGridDimensions();
            if (cols == 0) // Too many sessions, fall back to list
            {
                state.ViewMode = ViewMode.List;
            }
            else
            {
                layout["Main"].Update(BuildGridLayout(state, allCapturedPanes));
                layout["StatusBar"].Update(BuildGridStatusBar(state));
                return layout;
            }
        }

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
        var versionText = $"[grey50]v{Version}[/]";
        if (state.LatestVersion != null)
            versionText += $" [yellow bold]v{state.LatestVersion} available[/]";

        var left = new Markup($"[mediumpurple3 bold] Claude Command Center[/] {versionText}");
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
            var isWorking = !session.IsWaitingForInput;
            var status = isWorking ? spinner : "!";

            if (isSelected)
            {
                var bg = session.ColorTag ?? "grey37";
                rows.Add(new Markup($"[white on {bg}] {status} {name,-18} {time} [/]"));
            }
            else if (isWorking)
                rows.Add(new Markup($" [green]{spinner}[/] [navajowhite1]{name,-18}[/] [grey50]{time}[/]"));
            else if (session.IsWaitingForInput)
                rows.Add(new Markup($" [yellow bold]![/] [navajowhite1]{name,-18}[/] [grey50]{time}[/]"));
            else
                rows.Add(new Markup($"   [grey70]{name,-18}[/] [grey42]{time}[/]"));
        }

        if (rows.Count != 0)
        {
            var selected = state.GetSelectedSession();
            var borderColor = selected?.ColorTag != null
                ? Style.Parse(selected.ColorTag).Foreground
                : Color.Grey42;
            var headerColor = selected?.ColorTag ?? "grey70";

            return new Panel(new Rows(rows))
                .Header($"[{headerColor}] Sessions [/]")
                .BorderColor(borderColor)
                .Expand();
        }

        rows.Add(new Text(""));
        rows.Add(new Markup("[grey]  No tmux sessions found[/]"));
        rows.Add(new Markup("[grey]  Press [/][grey70 bold]n[/][grey] to create one[/]"));

        return new Panel(new Rows(rows))
            .Header("[grey70] Sessions [/]")
            .BorderColor(Color.Grey42)
            .Expand();
    }

    private static Panel BuildPreviewPanel(AppState state, string? capturedPane,
        TmuxSession? sessionOverride = null)
    {
        var session = sessionOverride ?? state.GetSelectedSession();

        if (session == null)
        {
            // Panel width = terminal - session panel (35) - panel borders (4)
            var panelWidth = Math.Max(20, Console.WindowWidth - 35 - 4);

            return new Panel(
                new Rows(
                    new Text(""),
                    CenterFiglet("Claude", panelWidth, Color.MediumPurple3),
                    CenterFiglet("Command center", panelWidth, Color.MediumPurple3),
                    new Text(""),
                    Align.Center(new Markup("[grey50]Select a session to see preview[/]"))))
                .Header("[grey70] Live Preview [/]")
                .BorderColor(Color.Grey42)
                .Expand();
        }

        var labelColor = session.ColorTag ?? "grey50";
        var rows = new List<IRenderable>();

        if (!string.IsNullOrWhiteSpace(session.Description))
            rows.Add(new Markup($" [{labelColor}]Desc:[/]     [italic grey70]{Markup.Escape(session.Description)}[/]"));

        rows.Add(new Markup($" [{labelColor}]Path:[/]     [white]{Markup.Escape(session.CurrentPath ?? "unknown")}[/]"));

        if (session.GitBranch != null)
            rows.Add(new Markup($" [{labelColor}]Branch:[/]   [aqua]{Markup.Escape(session.GitBranch)}[/]"));

        if (session.IsWorktree)
            rows.Add(new Markup($" [{labelColor}]Worktree:[/] [mediumpurple2]yes[/]"));

        rows.Add(new Markup($" [{labelColor}]Created:[/]  [white]{session.Created?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown"}[/]"));
        rows.Add(new Markup($" [{labelColor}]Status:[/]   {StatusLabel(session)}"));
        rows.Add(new Rule().RuleStyle(Style.Parse(session.ColorTag ?? "grey42")));

        if (!string.IsNullOrWhiteSpace(capturedPane))
        {
            // Preview width = terminal width - session panel (35) - borders (6) - padding (2)
            var maxWidth = Math.Max(20, Console.WindowWidth - 35 - 8);
            var lines = capturedPane.Split('\n');

            // Available height = terminal - header (1) - status bar (1) - panel border (2) - info rows (5)
            var availableLines = Math.Max(1, Console.WindowHeight - 9);

            // Always show the bottom of the pane output
            var offset = Math.Max(0, lines.Length - availableLines);
            var visibleLines = lines.AsSpan(offset,
                Math.Min(availableLines, lines.Length - offset));

            foreach (var line in visibleLines)
                rows.Add(AnsiParser.ParseLine(line, maxWidth));
        }
        else
        {
            rows.Add(new Markup("[grey] No pane content available[/]"));
        }

        var borderColor = session.ColorTag != null
            ? Style.Parse(session.ColorTag).Foreground
            : Color.Grey42;

        var headerColor = session.ColorTag ?? "grey70";
        var headerName = Markup.Escape(session.Name);
        return new Panel(new Rows(rows))
            .Header($"[{headerColor} bold] {headerName} [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static IRenderable BuildGridLayout(AppState state, Dictionary<string, string>? allCapturedPanes)
    {
        var (cols, gridRows) = state.GetGridDimensions();
        var outputLines = state.GetGridCellOutputLines();

        var layoutRows = new List<Layout>();

        for (var row = 0; row < gridRows; row++)
        {
            var layoutCols = new List<Layout>();

            for (var col = 0; col < cols; col++)
            {
                var idx = row * cols + col;
                var cellName = $"Cell_{row}_{col}";
                var cellLayout = new Layout(cellName);

                if (idx < state.Sessions.Count)
                {
                    var session = state.Sessions[idx];
                    var isSelected = idx == state.CursorIndex;
                    var pane = allCapturedPanes?.GetValueOrDefault(session.Name);
                    cellLayout.Update(BuildGridCell(session, isSelected, pane, outputLines));
                }
                else
                {
                    cellLayout.Update(new Panel(new Text("")).BorderColor(Color.Grey19).Expand());
                }

                layoutCols.Add(cellLayout);
            }

            var rowLayout = new Layout($"Row_{row}");
            rowLayout.SplitColumns(layoutCols.ToArray());
            layoutRows.Add(rowLayout);
        }

        var grid = new Layout("Grid");
        grid.SplitRows(layoutRows.ToArray());
        return grid;
    }

    private static Panel BuildGridCell(TmuxSession session, bool isSelected, string? capturedPane, int outputLines)
    {
        var rows = new List<IRenderable>();

        var spinner = Markup.Escape(GetSpinnerFrame());
        var status = session.IsWaitingForInput ? "[yellow bold]![/]" : $"[green]{spinner}[/]";
        var name = Markup.Escape(session.Name);
        var branch = session.GitBranch != null ? $" [aqua]{Markup.Escape(session.GitBranch)}[/]" : "";
        rows.Add(new Markup($" {status} [white bold]{name}[/]{branch}"));

        if (session.CurrentPath != null)
        {
            var shortPath = ShortenPath(session.CurrentPath);
            rows.Add(new Markup($" [grey50]{Markup.Escape(shortPath)}[/]"));
        }

        var labelColor = session.ColorTag ?? "grey50";
        rows.Add(new Rule().RuleStyle(Style.Parse(labelColor)));

        if (!string.IsNullOrWhiteSpace(capturedPane) && outputLines > 0)
        {
            var maxWidth = Math.Max(20, Console.WindowWidth / 2 - 6);
            var lines = capturedPane.Split('\n');
            var offset = Math.Max(0, lines.Length - outputLines);
            var visible = lines.AsSpan(offset, Math.Min(outputLines, lines.Length - offset));

            foreach (var line in visible)
                rows.Add(AnsiParser.ParseLine(line, maxWidth));
        }
        else if (outputLines > 0)
        {
            rows.Add(new Markup(" [grey]No output[/]"));
        }

        var sessionColor = session.ColorTag != null
            ? Style.Parse(session.ColorTag).Foreground
            : Color.Grey42;

        var borderColor = isSelected
            ? Color.White
            : new Color(
                (byte)(sessionColor.R / 2),
                (byte)(sessionColor.G / 2),
                (byte)(sessionColor.B / 2));

        var headerColor = session.ColorTag ?? "grey50";
        var headerName = Markup.Escape(session.Name);

        return new Panel(new Rows(rows))
            .Header($"[{headerColor} bold] {headerName} [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static Markup BuildGridStatusBar(AppState state)
    {
        if (state.IsInputMode)
            return BuildInputStatusBar(state);

        var status = state.GetActiveStatus();
        if (status != null)
            return new Markup($" [yellow]{Markup.Escape(status)}[/]");

        return new Markup(
            " [grey70 bold]arrows[/][grey] navigate [/]" +
            "[grey]│[/] " +
            "[grey70 bold]Enter[/][grey] attach [/]" +
            "[grey70 bold]Y[/][grey] approve [/]" +
            "[grey70 bold]N[/][grey] reject [/]" +
            "[grey70 bold]S[/][grey] send [/]" +
            "[grey]│[/] " +
            "[grey70 bold]G[/][grey] list view [/]" +
            "[grey70 bold]q[/][grey] quit [/]");
    }

    private static Markup BuildInputStatusBar(AppState state)
    {
        var target = Markup.Escape(state.InputTarget ?? "");
        var buffer = Markup.Escape(state.InputBuffer);
        return new Markup(
            $" [grey70]Send to[/] [white]{target}[/][grey70]>[/] [white]{buffer}[/][grey]▌[/]" +
            $"  [grey50]Enter[/][grey] send · [/][grey50]Esc[/][grey] cancel[/]");
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home))
            return "~" + path[home.Length..];
        return path;
    }

    private static Markup BuildStatusBar(AppState state)
    {
        if (state.IsInputMode)
            return BuildInputStatusBar(state);

        var status = state.GetActiveStatus();

        if (status != null)
            return new Markup($" [yellow]{Markup.Escape(status)}[/]");

        var visible = state.Keybindings
            .Where(b => b.Enabled && b.Label != null && b.StatusBarOrder >= 0)
            .OrderBy(b => b.StatusBarOrder)
            .ToList();

        if (visible.Count == 0)
            return new Markup(" ");

        var parts = new List<string>();
        var prevGroup = -1;

        foreach (var b in visible)
        {
            var group = b.StatusBarOrder < 5 ? 0 : 1;
            if (prevGroup >= 0 && group != prevGroup)
                parts.Add("[grey]│[/]");
            prevGroup = group;

            parts.Add($"[grey70 bold]{Markup.Escape(b.Key)}[/][grey] {Markup.Escape(b.Label!)} [/]");
        }

        return new Markup(" " + string.Join(" ", parts));
    }

    private static Padder CenterFiglet(string text, int availableWidth, Color color)
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
