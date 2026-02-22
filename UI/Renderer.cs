using ClaudeCommandCenter.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ClaudeCommandCenter.UI;

public static class Renderer
{
    private static readonly IReadOnlyList<string> _spinnerFrames = Spinner.Known.Dots.Frames;
    private static readonly TimeSpan _spinnerInterval = Spinner.Known.Dots.Interval;

    public static string GetSpinnerFrame()
    {
        var index = (int)(DateTime.Now.Ticks / _spinnerInterval.Ticks % _spinnerFrames.Count);
        return _spinnerFrames[index];
    }

    public static IRenderable BuildLayout(AppState state, string? capturedPane,
        Dictionary<string, string>? allCapturedPanes = null, string? diffOutput = null)
    {
        if (state.MobileMode)
            return BuildMobileLayout(state);

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
                layout["StatusBar"].Update(
                    state.ActiveGroup != null
                        ? BuildGroupGridStatusBar(state)
                        : BuildGridStatusBar(state));
                return layout;
            }
        }

        layout["Main"].SplitColumns(
            new Layout("Sessions").Size(35),
            new Layout("Preview"));

        layout["Sessions"].Update(BuildSessionPanel(state));
        layout["Preview"].Update(BuildPreviewPanel(state, capturedPane, diffOutput: diffOutput));
        layout["StatusBar"].Update(BuildStatusBar(state));

        return layout;
    }

    private static readonly string _version =
        typeof(Renderer).Assembly.GetName().Version?.ToString(3) ?? "?";

    private static Columns BuildHeader(AppState state)
    {
        var versionText = $"[grey50]v{_version}[/]";
        if (state.LatestVersion != null)
            versionText += $" [yellow bold]v{state.LatestVersion} available · u to update[/]";

        var left = new Markup($"[mediumpurple3 bold] Claude Command Center[/] {versionText}");

        var groupInfo = state.ActiveGroup != null
            ? $" [grey]│[/] [mediumpurple3]{Markup.Escape(state.ActiveGroup)}[/]"
            : "";
        var excludedCount = state.Sessions.Count(s => s.IsExcluded);
        var excludedInfo = excludedCount > 0 ? $" [grey50]· {excludedCount} excluded[/]" : "";
        var right = new Markup($"[grey]{state.Sessions.Count} session(s)[/]{excludedInfo}{groupInfo} ");

        return new Columns(left, right) { Expand = true };
    }

    private static IRenderable BuildSessionPanel(AppState state)
    {
        var standalone = state.GetStandaloneSessions();
        var groups = state.Groups;
        var sessionsFocused = state.ActiveSection == ActiveSection.Sessions;
        var groupsFocused = state.ActiveSection == ActiveSection.Groups;

        var rows = new List<IRenderable>();

        // Sessions section header
        var sessionsHeaderColor = sessionsFocused ? "white bold" : "grey50";
        rows.Add(new Markup($" [{sessionsHeaderColor}]Sessions[/]"));

        if (standalone.Count == 0)
            rows.Add(new Markup("  [grey]No standalone sessions[/]"));
        else
            for (var i = 0; i < standalone.Count; i++)
            {
                var session = standalone[i];
                var isSelected = sessionsFocused && i == state.CursorIndex;
                rows.Add(BuildSessionRow(session, isSelected));
            }

        // Separator
        rows.Add(new Rule().RuleStyle(Style.Parse("grey27")));

        // Groups section header
        var groupsHeaderColor = groupsFocused ? "white bold" : "grey50";
        rows.Add(new Markup($" [{groupsHeaderColor}]Groups[/]"));

        if (groups.Count == 0)
            rows.Add(new Markup("  [grey]No groups · press [/][grey70 bold]g[/][grey] to create[/]"));
        else
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var isSelected = groupsFocused && i == state.GroupCursor;
                rows.Add(BuildGroupRow(group, isSelected, state));
            }

        // Panel border color based on focused section
        Color borderColor;
        if (sessionsFocused)
        {
            var selected = state.GetSelectedSession();
            borderColor = selected?.ColorTag != null
                ? Style.Parse(selected.ColorTag).Foreground
                : Color.Grey42;
        }
        else if (groupsFocused)
        {
            var selected = state.GetSelectedGroup();
            borderColor = !string.IsNullOrEmpty(selected?.Color)
                ? Style.Parse(selected.Color).Foreground
                : Color.Grey42;
        }
        else
        {
            borderColor = Color.Grey42;
        }

        return new Panel(new Rows(rows))
            .Header("[grey70] Workspace [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static Markup BuildSessionRow(TmuxSession session, bool isSelected)
    {
        var name = Markup.Escape(session.Name);
        var spinner = Markup.Escape(GetSpinnerFrame());

        if (session.IsDead)
        {
            if (session.IsExcluded)
            {
                if (isSelected)
                    return new Markup($"[grey50 on grey19] [grey42]†[/] {name,-22} [/]");
                return new Markup($" [grey42]†[/] [grey35]{name,-22}[/]");
            }
            if (isSelected)
            {
                var bg = session.ColorTag ?? "grey37";
                return new Markup($"[white on {bg}] † {name,-22} [/]");
            }
            return new Markup($" [red]†[/] [grey50]{name,-22}[/]");
        }

        var isWorking = !session.IsWaitingForInput;
        var status = isWorking ? spinner : "!";

        if (session.IsExcluded)
        {
            var excludedStatus = session.IsWaitingForInput ? "[grey42]![/]" : $"[grey35]{spinner}[/]";
            if (isSelected)
                return new Markup($"[grey50 on grey19] {excludedStatus} {name,-22} [/]");
            return new Markup($" {excludedStatus} [grey35]{name,-22}[/]");
        }

        if (isSelected)
        {
            var bg = session.ColorTag ?? "grey37";
            return new Markup($"[white on {bg}] {status} {name,-22} [/]");
        }

        if (isWorking)
            return new Markup($" [green]{spinner}[/] [navajowhite1]{name,-22}[/]");
        if (session.IsWaitingForInput)
            return new Markup($" [yellow bold]![/] [navajowhite1]{name,-22}[/]");

        return new Markup($"   [grey70]{name,-22}[/]");
    }

    private static Markup BuildGroupRow(SessionGroup group, bool isSelected, AppState state)
    {
        var name = Markup.Escape(group.Name);
        var totalSessions = group.Sessions.Count;

        // Check session states within the group
        var groupSessionNames = new HashSet<string>(group.Sessions);
        var groupSessions = state.Sessions.Where(s => groupSessionNames.Contains(s.Name)).ToList();
        var anyWaiting = groupSessions.Any(s => s.IsWaitingForInput);
        var allDead = groupSessions.Count > 0 && groupSessions.All(s => s.IsDead);

        var spinner = Markup.Escape(GetSpinnerFrame());
        var status = totalSessions == 0 ? "[grey50]x[/]" : allDead ? "[red]†[/]" : anyWaiting ? "[yellow bold]![/]" : $"[green]{spinner}[/]";
        var countLabel = $"({totalSessions})";
        var colorTag = !string.IsNullOrEmpty(group.Color) ? group.Color : "grey50";

        if (isSelected)
        {
            var bg = !string.IsNullOrEmpty(group.Color) ? group.Color : "grey37";
            return new Markup($"[white on {bg}] {status} {name,-14} {countLabel,-4} [/]");
        }

        if (totalSessions == 0)
            return new Markup($" {status} [grey50 strikethrough]{name,-14}[/] [grey42]{countLabel}[/]");

        return new Markup($" {status} [{colorTag}]{name,-14}[/] [grey50]{countLabel}[/]");
    }

    private static Panel BuildPreviewPanel(AppState state, string? capturedPane,
        TmuxSession? sessionOverride = null, string? diffOutput = null)
    {
        var session = sessionOverride ?? state.GetSelectedSession();

        if (session == null)
        {
            // If groups section is focused, show group info instead of figlet
            if (state.ActiveSection == ActiveSection.Groups)
            {
                var group = state.GetSelectedGroup();
                if (group != null)
                    return BuildGroupPreviewPanel(group, state);
            }

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

        rows.Add(new Markup($" [{labelColor}]Created:[/]  [white]{session.Created?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown"}[/]"));
        rows.Add(new Markup($" [{labelColor}]Status:[/]   {StatusLabel(session)}"));
        rows.Add(new Rule().RuleStyle(Style.Parse(session.ColorTag ?? "grey42")));

        if (state.DiffMode)
        {
            RenderDiffContent(rows, session, diffOutput);
        }
        else if (!string.IsNullOrWhiteSpace(capturedPane))
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
        var headerSuffix = state.DiffMode ? " [grey50]· diff[/]" : "";
        return new Panel(new Rows(rows))
            .Header($"[{headerColor} bold] {headerName} [/]{headerSuffix}")
            .BorderColor(borderColor)
            .Expand();
    }

    private static void RenderDiffContent(List<IRenderable> rows, TmuxSession session, string? diffOutput)
    {
        if (session.CurrentPath == null)
        {
            rows.Add(new Markup(" [grey]Session not running[/]"));
            return;
        }

        if (session.StartCommitSha == null)
        {
            rows.Add(new Markup(" [grey]No baseline commit recorded[/]"));
            return;
        }

        if (string.IsNullOrWhiteSpace(diffOutput))
        {
            rows.Add(new Markup(" [grey]No changes since session start[/]"));
            return;
        }

        var availableLines = Math.Max(1, Console.WindowHeight - 9);
        var lines = diffOutput.Split('\n');
        var limit = Math.Min(lines.Length, availableLines);
        var overflow = lines.Length - limit;

        for (var i = 0; i < limit; i++)
        {
            var line = lines[i];
            rows.Add(FormatDiffStatLine(line));
        }

        if (overflow > 0)
            rows.Add(new Markup($" [grey]... and {overflow} more line(s)[/]"));
    }

    private static Markup FormatDiffStatLine(string line)
    {
        // Summary line: " N files changed, N insertions(+), N deletions(-)"
        if (line.Contains("changed") && (line.Contains("insertion") || line.Contains("deletion")))
        {
            var parts = line.Split(',');
            var result = new List<string>();
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Contains("insertion"))
                    result.Add($"[green]{Markup.Escape(trimmed)}[/]");
                else if (trimmed.Contains("deletion"))
                    result.Add($"[red]{Markup.Escape(trimmed)}[/]");
                else
                    result.Add($"[grey]{Markup.Escape(trimmed)}[/]");
            }
            return new Markup(" " + string.Join("[grey],[/] ", result));
        }

        // File line: " path/to/file | N +++---"
        var pipeIdx = line.IndexOf('|');
        if (pipeIdx >= 0)
        {
            var filePart = Markup.Escape(line[..pipeIdx]);
            var statPart = line[(pipeIdx + 1)..];

            var colored = new System.Text.StringBuilder();
            foreach (var c in statPart)
            {
                if (c == '+')
                    colored.Append("[green]+[/]");
                else if (c == '-')
                    colored.Append("[red]-[/]");
                else if (c == '[')
                    colored.Append("[[");
                else if (c == ']')
                    colored.Append("]]");
                else
                    colored.Append(c);
            }

            return new Markup($"[white]{filePart}[/][grey]|[/]{colored}");
        }

        return new Markup($" [grey]{Markup.Escape(line)}[/]");
    }

    private static Panel BuildGroupPreviewPanel(SessionGroup group, AppState state)
    {
        var colorTag = !string.IsNullOrEmpty(group.Color) ? group.Color : "grey50";
        var rows = new List<IRenderable>
        {
            new Markup($" [{colorTag}]Group:[/]     [white bold]{Markup.Escape(group.Name)}[/]"),
            new Markup($" [{colorTag}]Feature:[/]   [grey70]{Markup.Escape(group.Description)}[/]"),
            new Markup($" [{colorTag}]Sessions:[/]  [white]{group.Sessions.Count}[/]"),
            new Rule().RuleStyle(Style.Parse(colorTag))
        };

        // Show each session in the group with its status
        var groupSessionNames = new HashSet<string>(group.Sessions);
        var groupSessions = state.Sessions.Where(s => groupSessionNames.Contains(s.Name)).ToList();

        foreach (var session in groupSessions)
        {
            var spinner = Markup.Escape(GetSpinnerFrame());
            var status = session.IsDead ? "[red]†[/]" : session.IsWaitingForInput ? "[yellow bold]![/]" : $"[green]{spinner}[/]";
            var name = Markup.Escape(session.Name);
            var branch = session.GitBranch != null ? $" [aqua]{Markup.Escape(session.GitBranch)}[/]" : "";
            var path = session.CurrentPath != null ? $" [grey50]{Markup.Escape(ShortenPath(session.CurrentPath))}[/]" : "";
            rows.Add(new Markup($"  {status} [white]{name}[/]{branch}{path}"));
        }

        if (groupSessions.Count == 0)
            rows.Add(new Markup("  [grey50]All sessions have ended[/]"));

        rows.Add(new Text(""));
        rows.Add(new Markup("  [grey]Press [/][grey70 bold]Enter[/][grey] to open group grid · [/][grey70 bold]e[/][grey] to edit[/]"));

        var borderColor = !string.IsNullOrEmpty(group.Color)
            ? Style.Parse(group.Color).Foreground
            : Color.Grey42;

        return new Panel(new Rows(rows))
            .Header($"[{colorTag} bold] {Markup.Escape(group.Name)} [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static IRenderable BuildGridLayout(AppState state, Dictionary<string, string>? allCapturedPanes)
    {
        var visibleSessions = state.GetGridSessions();
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

                if (idx < visibleSessions.Count)
                {
                    var session = visibleSessions[idx];
                    var isSelected = idx == state.CursorIndex;
                    var pane = allCapturedPanes?.GetValueOrDefault(session.Name);
                    cellLayout.Update(BuildGridCell(session, isSelected, pane, outputLines, cols));
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

    private static Panel BuildGridCell(TmuxSession session, bool isSelected, string? capturedPane, int outputLines, int gridCols)
    {
        var rows = new List<IRenderable>();
        var maxWidth = Math.Max(20, Console.WindowWidth / gridCols - 4);

        // Collect output lines from pane
        var outputRows = new List<IRenderable>();
        if (!string.IsNullOrWhiteSpace(capturedPane) && outputLines > 0)
        {
            var lines = capturedPane.Split('\n');
            var offset = Math.Max(0, lines.Length - outputLines);
            var visible = lines.AsSpan(offset, Math.Min(outputLines, lines.Length - offset));

            foreach (var line in visible)
                outputRows.Add(AnsiParser.ParseLine(line, maxWidth));
        }
        else if (outputLines > 0)
        {
            outputRows.Add(new Markup(" [grey]No output[/]"));
        }

        // Pad with empty lines to push content to the bottom of the cell
        var padding = Math.Max(0, outputLines - outputRows.Count);
        for (var i = 0; i < padding; i++)
            rows.Add(new Text(""));

        // Header: name + branch (truncated to prevent wrapping)
        var spinner = Markup.Escape(GetSpinnerFrame());
        var status = session.IsDead ? "[red]†[/]" : session.IsWaitingForInput ? "[yellow bold]![/]" : $"[green]{spinner}[/]";
        var nameStr = session.Name;
        var branchStr = session.GitBranch;
        var prefixLen = 3; // " X " visible chars before name

        if (branchStr != null)
        {
            var avail = maxWidth - prefixLen - 1; // -1 for space between name and branch
            if (nameStr.Length + branchStr.Length > avail)
            {
                var branchAvail = avail - nameStr.Length;
                if (branchAvail >= 6)
                    branchStr = branchStr[..(branchAvail - 2)] + "..";
                else if (nameStr.Length > avail)
                {
                    nameStr = nameStr[..Math.Max(4, avail - 2)] + "..";
                    branchStr = null;
                }
                else
                    branchStr = null;
            }
        }
        else if (nameStr.Length > maxWidth - prefixLen)
        {
            nameStr = nameStr[..Math.Max(4, maxWidth - prefixLen - 2)] + "..";
        }

        var name = Markup.Escape(nameStr);
        var branch = branchStr != null ? $" [aqua]{Markup.Escape(branchStr)}[/]" : "";
        rows.Add(new Markup($" {status} [white bold]{name}[/]{branch}"));

        if (session.CurrentPath != null)
        {
            var shortPath = ShortenPath(session.CurrentPath);
            if (shortPath.Length > maxWidth - 1)
                shortPath = shortPath[..(maxWidth - 3)] + "..";
            rows.Add(new Markup($" [grey50]{Markup.Escape(shortPath)}[/]"));
        }

        var labelColor = session.ColorTag ?? "grey50";
        rows.Add(new Rule().RuleStyle(Style.Parse(labelColor)));

        // Pane output
        rows.AddRange(outputRows);

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

    private static Markup BuildGroupGridStatusBar(AppState state)
    {
        if (state.IsInputMode)
            return BuildInputStatusBar(state);

        var status = state.GetActiveStatus();
        if (status != null)
            return new Markup($" [yellow]{Markup.Escape(status)}[/]");

        var groupName = state.ActiveGroup != null ? Markup.Escape(state.ActiveGroup) : "group";

        return new Markup(
            $" [mediumpurple3]{groupName}[/] [grey]│[/] " +
            "[grey70 bold]arrows[/][grey] navigate [/]" +
            "[grey]│[/] " +
            "[grey70 bold]Enter[/][grey] attach [/]" +
            "[grey70 bold]Y[/][grey] approve [/]" +
            "[grey70 bold]N[/][grey] reject [/]" +
            "[grey70 bold]S[/][grey] send [/]" +
            "[grey70 bold]d[/][grey] kill [/]" +
            "[grey70 bold]x[/][grey] hide [/]" +
            "[grey]│[/] " +
            "[grey70 bold]Esc[/][grey] back [/]" +
            "[grey70 bold]q[/][grey] quit [/]");
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
            "[grey70 bold]x[/][grey] hide [/]" +
            "[grey70 bold]G[/][grey] list view [/]" +
            "[grey70 bold]q[/][grey] quit [/]");
    }

    private static Markup BuildInputStatusBar(AppState state)
    {
        var target = Markup.Escape(state.InputTarget ?? "");
        var buffer = Markup.Escape(state.InputBuffer);
        var limit = state.InputBuffer.Length >= 450
            ? $" [grey50]({state.InputBuffer.Length}/500)[/]"
            : "";
        return new Markup(
            $" [grey70]Send to[/] [white]{target}[/][grey70]>[/] [white]{buffer}[/][grey]▌[/]{limit}" +
            $"  [grey50]Enter[/][grey] send · [/][grey50]Esc[/][grey] cancel[/]");
    }

    // ── Mobile mode rendering ──────────────────────────────────────────

    private static IRenderable BuildMobileLayout(AppState state)
    {
        var sessions = state.GetMobileVisibleSessions();
        var listHeight = Math.Max(1, Console.WindowHeight - 6);

        state.EnsureCursorVisible(listHeight);

        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("List"),
                new Layout("Detail").Size(4),
                new Layout("StatusBar").Size(1));

        layout["Header"].Update(BuildMobileHeader(state, sessions.Count));
        layout["List"].Update(BuildMobileSessionList(state, sessions, listHeight));
        layout["Detail"].Update(BuildMobileDetailBar(state));
        layout["StatusBar"].Update(BuildMobileStatusBar(state));

        return layout;
    }

    private static IRenderable BuildMobileHeader(AppState state, int sessionCount)
    {
        var filterLabel = state.GetGroupFilterLabel();
        var left = new Markup($"[mediumpurple3 bold] CCC[/] [grey50]v{_version}[/] [grey]- {sessionCount} sessions[/]");
        var right = new Markup($"[grey50][[{Markup.Escape(filterLabel)}]][/] ");
        return new Columns(left, right) { Expand = true };
    }

    private static IRenderable BuildMobileSessionList(AppState state, List<TmuxSession> sessions, int listHeight)
    {
        var rows = new List<IRenderable>();

        if (sessions.Count == 0)
        {
            rows.Add(new Markup("  [grey]No sessions[/]"));
        }
        else
        {
            var end = Math.Min(state.TopIndex + listHeight, sessions.Count);
            for (var i = state.TopIndex; i < end; i++)
            {
                var session = sessions[i];
                var isSelected = i == state.CursorIndex;
                rows.Add(BuildMobileSessionRow(session, isSelected));
            }
        }

        // Pad to fill available height so old content doesn't bleed through
        while (rows.Count < listHeight)
            rows.Add(new Text(""));

        return new Rows(rows);
    }

    private static Markup BuildMobileSessionRow(TmuxSession session, bool isSelected)
    {
        var name = Markup.Escape(session.Name);
        var spinner = Markup.Escape(GetSpinnerFrame());

        if (session.IsDead)
        {
            if (session.IsExcluded)
            {
                if (isSelected)
                    return new Markup($"[grey50 on grey19] [grey42]†[/] {name} [/]");
                return new Markup($" [grey42]†[/] [grey35]{name}[/]");
            }
            if (isSelected)
            {
                var bg = session.ColorTag ?? "grey37";
                return new Markup($"[white on {bg}] † {name} [/]");
            }
            return new Markup($" [red]†[/] [grey50]{name}[/]");
        }

        var isWorking = !session.IsWaitingForInput;
        var status = isWorking ? spinner : "!";

        if (session.IsExcluded)
        {
            var excludedStatus = session.IsWaitingForInput ? "[grey42]![/]" : $"[grey35]{spinner}[/]";
            if (isSelected)
                return new Markup($"[grey50 on grey19] {excludedStatus} {name} [/]");
            return new Markup($" {excludedStatus} [grey35]{name}[/]");
        }

        if (isSelected)
        {
            var bg = session.ColorTag ?? "grey37";
            return new Markup($"[white on {bg}] {status} {name} [/]");
        }

        if (isWorking)
            return new Markup($" [green]{spinner}[/] [navajowhite1]{name}[/]");
        if (session.IsWaitingForInput)
            return new Markup($" [yellow bold]![/] [navajowhite1]{name}[/]");

        return new Markup($"   [grey70]{name}[/]");
    }

    private static IRenderable BuildMobileDetailBar(AppState state)
    {
        var session = state.GetSelectedSession();
        var rows = new List<IRenderable>();

        rows.Add(new Rule().RuleStyle(Style.Parse("grey27")));

        if (session == null)
        {
            rows.Add(new Markup(" [grey]No session selected[/]"));
            rows.Add(new Text(""));
            rows.Add(new Text(""));
            return new Rows(rows);
        }

        var color = session.ColorTag ?? "grey70";
        rows.Add(new Markup($" [{color} bold]{Markup.Escape(session.Name)}[/]"));

        var branch = session.GitBranch != null ? $"[aqua]{Markup.Escape(session.GitBranch)}[/]" : "[grey]no branch[/]";
        var path = session.CurrentPath != null ? $" [grey50]{Markup.Escape(ShortenPath(session.CurrentPath))}[/]" : "";
        rows.Add(new Markup($" {branch}{path}"));

        var statusText = session.IsDead
            ? "[red]session ended[/]"
            : session.IsWaitingForInput
            ? "[yellow bold]waiting for input[/]"
            : session.IsAttached ? "[green]attached[/]" : "[grey]working[/]";
        var desc = !string.IsNullOrWhiteSpace(session.Description)
            ? $" [grey50]- {Markup.Escape(session.Description)}[/]"
            : "";
        rows.Add(new Markup($" {statusText}{desc}"));

        return new Rows(rows);
    }

    private static Markup BuildMobileStatusBar(AppState state)
    {
        if (state.IsInputMode)
            return BuildInputStatusBar(state);

        var status = state.GetActiveStatus();
        if (status != null)
            return new Markup($" [yellow]{Markup.Escape(status)}[/]");

        var session = state.GetSelectedSession();
        var parts = new List<string>();

        if (session?.IsWaitingForInput == true)
        {
            parts.Add("[grey70 bold]Y[/][grey] approve [/]");
            parts.Add("[grey70 bold]N[/][grey] reject [/]");
        }

        parts.Add("[grey70 bold]S[/][grey] send [/]");
        parts.Add("[grey70 bold]Enter[/][grey] attach [/]");

        if (state.Groups.Count > 0)
            parts.Add("[grey70 bold]g[/][grey] filter [/]");

        parts.Add("[grey70 bold]q[/][grey] quit[/]");

        return new Markup(" " + string.Join(" ", parts));
    }

    // ── Shared helpers ──────────────────────────────────────────────────

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

        var sessionOnlyActions = new HashSet<string> { "approve", "reject", "send-text" };
        var hiddenWhenNoGroups = new HashSet<string> { "move-to-group" };
        var onGroup = state.ActiveSection == ActiveSection.Groups;
        var hasGroups = state.Groups.Count > 0;

        var parts = new List<string>();
        var prevGroup = -1;

        foreach (var b in visible)
        {
            if (!hasGroups && hiddenWhenNoGroups.Contains(b.ActionId))
                continue;
            var barGroup = b.StatusBarOrder / 10;
            if (prevGroup >= 0 && barGroup != prevGroup)
                parts.Add("[grey]│[/]");
            prevGroup = barGroup;

            var dimmed = onGroup && sessionOnlyActions.Contains(b.ActionId);
            var active = b.ActionId == "toggle-diff" && state.DiffMode;
            var keyColor = dimmed ? "grey35" : active ? "white bold" : "grey70 bold";
            var labelColor = dimmed ? "grey27" : active ? "white underline" : "grey";
            parts.Add($"[{keyColor}]{Markup.Escape(b.Key)}[/][{labelColor}] {Markup.Escape(b.Label!)} [/]");
        }

        // Diff overlay hint when diff mode is active
        if (state.DiffMode)
        {
            parts.Add("[grey]│[/]");
            parts.Add("[white bold]Enter[/][white] open diff [/]");
        }

        // Tab hint when groups exist
        if (state.Groups.Count > 0)
        {
            parts.Add("[grey]│[/]");
            parts.Add("[grey70 bold]Tab[/][grey] switch section [/]");
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
        if (session.IsDead)
            return "[red]session ended[/]";
        if (session.IsWaitingForInput)
            return "[yellow bold]waiting for input[/]";

        return session.IsAttached
            ? "[green]attached[/]"
            : "[grey]detached[/]";
    }

    // ── Diff Overlay View ────────────────────────────────────────────

    public static IRenderable BuildDiffOverlayLayout(AppState state)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("Main"),
                new Layout("StatusBar").Size(1));

        layout["Header"].Update(BuildDiffOverlayHeader(state));
        layout["Main"].Update(BuildDiffOverlayContent(state));
        layout["StatusBar"].Update(BuildDiffOverlayStatusBar());

        return layout;
    }

    private static Columns BuildDiffOverlayHeader(AppState state)
    {
        var name = state.DiffOverlaySessionName ?? "?";
        var branch = state.DiffOverlayBranch != null
            ? $" [aqua]{Markup.Escape(state.DiffOverlayBranch)}[/]"
            : "";

        var totalLines = state.DiffOverlayLines.Length;
        var viewportHeight = Math.Max(1, Console.WindowHeight - 4);
        var maxScroll = Math.Max(0, totalLines - viewportHeight);
        var pct = maxScroll > 0 ? (int)(100.0 * state.DiffScrollOffset / maxScroll) : 100;
        var scrollInfo = $"[grey50]{state.DiffScrollOffset + 1}-{Math.Min(state.DiffScrollOffset + viewportHeight, totalLines)}/{totalLines} ({pct}%)[/]";

        var left = new Markup($"[mediumpurple3 bold] Diff[/] [white bold]{Markup.Escape(name)}[/]{branch}");
        var right = new Markup($"{scrollInfo} ");

        return new Columns(left, right) { Expand = true };
    }

    private static Panel BuildDiffOverlayContent(AppState state)
    {
        var rows = new List<IRenderable>();
        var maxWidth = Math.Max(20, Console.WindowWidth - 4);

        // Stat summary at top
        if (!string.IsNullOrWhiteSpace(state.DiffOverlayStatSummary))
        {
            var statLines = state.DiffOverlayStatSummary.Split('\n');
            foreach (var line in statLines)
                rows.Add(FormatDiffStatLine(line));
            rows.Add(new Rule().RuleStyle(Style.Parse("grey27")));
        }

        // Calculate viewport
        var statRowCount = rows.Count;
        var availableHeight = Math.Max(1, Console.WindowHeight - 4 - statRowCount);
        var totalLines = state.DiffOverlayLines.Length;
        var offset = Math.Clamp(state.DiffScrollOffset, 0, Math.Max(0, totalLines - 1));
        var visibleCount = Math.Min(availableHeight, totalLines - offset);

        for (var i = 0; i < visibleCount; i++)
            rows.Add(FormatDiffPatchLine(state.DiffOverlayLines[offset + i], maxWidth));

        // Pad remaining lines to fill screen
        var padding = availableHeight - visibleCount;
        for (var i = 0; i < padding; i++)
            rows.Add(new Text(""));

        return new Panel(new Rows(rows))
            .BorderColor(Color.Grey42)
            .Expand();
    }

    private static Markup FormatDiffPatchLine(string line, int maxWidth)
    {
        if (line.Length > maxWidth)
            line = line[..maxWidth];

        var escaped = Markup.Escape(line);

        if (line.StartsWith("+++") || line.StartsWith("---"))
            return new Markup($"[white bold]{escaped}[/]");
        if (line.StartsWith('+'))
            return new Markup($"[green]{escaped}[/]");
        if (line.StartsWith('-'))
            return new Markup($"[red]{escaped}[/]");
        if (line.StartsWith("@@"))
            return new Markup($"[cyan]{escaped}[/]");
        if (line.StartsWith("diff "))
            return new Markup($"[yellow bold]{escaped}[/]");
        if (line.StartsWith("index "))
            return new Markup($"[grey50]{escaped}[/]");

        return new Markup($"[grey70]{escaped}[/]");
    }

    private static Markup BuildDiffOverlayStatusBar()
    {
        return new Markup(
            " [grey70 bold]j/k[/][grey] scroll [/]" +
            "[grey70 bold]Space/PgDn[/][grey] page down [/]" +
            "[grey70 bold]PgUp[/][grey] page up [/]" +
            "[grey70 bold]g[/][grey] top [/]" +
            "[grey70 bold]G[/][grey] bottom [/]" +
            "[grey]│[/] " +
            "[grey70 bold]Esc/q[/][grey] back [/]");
    }

    // ── Settings View ──────────────────────────────────────────────

    public static IRenderable BuildSettingsLayout(AppState state, CccConfig config)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(1),
                new Layout("Main"),
                new Layout("StatusBar").Size(1));

        layout["Header"].Update(BuildSettingsHeader());

        var categories = SettingsDefinition.GetCategories();
        var selectedCategory = categories[Math.Clamp(state.SettingsCategory, 0, categories.Count - 1)];
        var items = selectedCategory.BuildItems(config);

        layout["Main"].SplitColumns(
            new Layout("Categories").Size(22),
            new Layout("Settings"));

        layout["Categories"].Update(BuildCategoryPanel(categories, state));
        layout["Settings"].Update(BuildSettingsPanel(selectedCategory, items, state, config));
        layout["StatusBar"].Update(BuildSettingsStatusBar(state, items));

        return layout;
    }

    private static Columns BuildSettingsHeader()
    {
        var left = new Markup("[mediumpurple3 bold] Claude Command Center[/] [grey50]Settings[/]");
        var right = new Markup("[grey50]Esc to return [/]");
        return new Columns(left, right) { Expand = true };
    }

    private static Panel BuildCategoryPanel(List<SettingsCategory> categories, AppState state)
    {
        var rows = new List<IRenderable>();

        for (var i = 0; i < categories.Count; i++)
        {
            var cat = categories[i];
            var isSelected = i == state.SettingsCategory;
            var isFocused = !state.SettingsFocusRight;

            if (isSelected && isFocused)
                rows.Add(new Markup($"[white on grey37] {cat.Icon} {Markup.Escape(cat.Name),-16} [/]"));
            else if (isSelected)
                rows.Add(new Markup($"[white] {cat.Icon} {Markup.Escape(cat.Name),-16} [/]"));
            else
                rows.Add(new Markup($"[grey70]   {Markup.Escape(cat.Name),-16} [/]"));
        }

        var borderColor = !state.SettingsFocusRight ? Color.MediumPurple3 : Color.Grey42;

        return new Panel(new Rows(rows))
            .Header("[grey70] Categories [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static Panel BuildSettingsPanel(SettingsCategory category, List<SettingsItem> items,
        AppState state, CccConfig config)
    {
        var rows = new List<IRenderable>();
        var isFocused = state.SettingsFocusRight;

        if (items.Count == 0)
        {
            rows.Add(new Markup("[grey]No settings in this category[/]"));
        }
        else
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var isSelected = isFocused && i == state.SettingsItemCursor;
                rows.Add(BuildSettingsRow(item, isSelected, state, config));
            }
        }

        var borderColor = isFocused ? Color.MediumPurple3 : Color.Grey42;

        return new Panel(new Rows(rows))
            .Header($"[grey70] {Markup.Escape(category.Name)} [/]")
            .BorderColor(borderColor)
            .Expand();
    }

    private static IRenderable BuildSettingsRow(SettingsItem item, bool isSelected,
        AppState state, CccConfig config)
    {
        var label = Markup.Escape(item.Label);
        var maxValueWidth = Math.Max(10, Console.WindowWidth - 22 - 6 - item.Label.Length - 8);

        switch (item.Type)
        {
            case SettingsItemType.Toggle:
            {
                var value = item.GetValue?.Invoke(config) ?? "OFF";
                var isOn = value == "ON";
                var toggleColor = isOn ? "green" : "grey50";
                var toggleText = isOn ? " ON " : " OFF";
                var keyInfo = item.ActionId != null && config.Keybindings.TryGetValue(item.ActionId, out var kb)
                    ? $"[grey50]({Markup.Escape(kb.Key ?? "?")})[/] " : "";

                if (isSelected)
                    return new Markup($"[white on grey37] {keyInfo}{label,-20} [{toggleColor}]{toggleText}[/] [/]");
                return new Markup($" {keyInfo}{label,-20} [{toggleColor}]{toggleText}[/]");
            }

            case SettingsItemType.Text:
            case SettingsItemType.Number:
            {
                var value = item.GetValue?.Invoke(config) ?? "";

                if (isSelected && state.IsSettingsEditing)
                {
                    var buf = Markup.Escape(state.SettingsEditBuffer);
                    return new Markup($"[white on grey37] {label,-20} [/][white on grey27] {buf}[grey]▌[/] [/]");
                }

                var displayValue = value.Length > maxValueWidth
                    ? value[..(maxValueWidth - 2)] + ".." : value;
                var valueColor = string.IsNullOrEmpty(value) ? "grey42 italic" : "white";
                var displayText = string.IsNullOrEmpty(value) ? "(not set)" : Markup.Escape(displayValue);

                if (isSelected)
                    return new Markup($"[white on grey37] {label,-20} [{valueColor}]{displayText}[/] [/]");
                return new Markup($" [grey70]{label,-20}[/] [{valueColor}]{displayText}[/]");
            }

            case SettingsItemType.Action:
            {
                if (isSelected)
                    return new Markup($"[white on grey37] {label,-20} [grey50]→[/] [/]");
                return new Markup($" [mediumpurple3]{label}[/]");
            }

            default:
                return new Markup($" {label}");
        }
    }

    private static Markup BuildSettingsStatusBar(AppState state, List<SettingsItem> items)
    {
        if (state.IsSettingsEditing)
        {
            var limit = state.SettingsEditBuffer.Length >= 200
                ? $" [grey50]({state.SettingsEditBuffer.Length}/250)[/]" : "";
            return new Markup(
                $" [grey70]Editing>[/] [white]{Markup.Escape(state.SettingsEditBuffer)}[/][grey]▌[/]{limit}" +
                "  [grey50]Enter[/][grey] save · [/][grey50]Esc[/][grey] cancel[/]");
        }

        var itemHint = "";
        if (state.SettingsFocusRight && state.SettingsItemCursor < items.Count)
        {
            var item = items[state.SettingsItemCursor];
            itemHint = item.Type switch
            {
                SettingsItemType.Toggle => "[grey70 bold]Enter[/][grey] toggle [/]",
                SettingsItemType.Text or SettingsItemType.Number =>
                    "[grey70 bold]Enter[/][grey] edit [/]",
                SettingsItemType.Action => "[grey70 bold]Enter[/][grey] execute [/]",
                _ => "",
            };
        }

        var favoritesHint = "";
        if (state.SettingsFocusRight)
        {
            var categories = SettingsDefinition.GetCategories();
            if (state.SettingsCategory < categories.Count && categories[state.SettingsCategory].Name == "Favorites")
                favoritesHint = "[grey70 bold]n[/][grey] add [/][grey70 bold]d[/][grey] delete [/]";
        }

        return new Markup(
            " [grey70 bold]j/k[/][grey] navigate [/]" +
            "[grey70 bold]Tab[/][grey] switch panel [/]" +
            "[grey]│[/] " +
            itemHint +
            favoritesHint +
            "[grey]│[/] " +
            "[grey70 bold]o[/][grey] open config [/]" +
            "[grey70 bold]Esc[/][grey] back [/]");
    }
}
