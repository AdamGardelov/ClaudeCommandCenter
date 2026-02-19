using System.Diagnostics;
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;
using Spectre.Console;

namespace ClaudeCommandCenter;

public class App
{
    private readonly AppState _state = new();
    private readonly CccConfig _config = ConfigService.Load();
    private Dictionary<string, string> _keyMap = new();
    private string? _capturedPane;
    private DateTime _lastCapture = DateTime.MinValue;
    private string? _lastSelectedSession;
    private string? _lastSpinnerFrame;
    private bool _hasSpinningSessions;
    private bool _claudeAvailable;

    public void Run()
    {
        if (!TmuxService.HasTmux())
        {
            AnsiConsole.MarkupLine("[red]tmux is not installed or not in PATH.[/]");
            return;
        }

        if (TmuxService.IsInsideTmux())
        {
            AnsiConsole.MarkupLine("[red]ClaudeCommandCenter should run outside tmux.[/]");
            AnsiConsole.MarkupLine("[grey]It manages tmux sessions from the outside. Exit tmux first.[/]");
            return;
        }

        _claudeAvailable = TmuxService.HasClaude();
        if (!_claudeAvailable)
        {
            AnsiConsole.MarkupLine("[yellow]Warning: 'claude' was not found in PATH.[/]");
            AnsiConsole.MarkupLine("[grey]New sessions will fail to start. Install Claude Code: https://docs.anthropic.com/en/docs/claude-code[/]");
        }

        var bindings = KeyBindingService.Resolve(_config);
        _keyMap = KeyBindingService.BuildKeyMap(bindings);
        _state.Keybindings = bindings;

        LoadSessions();

        try
        {
            // Try alternate screen buffer for clean TUI
            Console.Write("\e[?1049h"); // Enter alternate screen
            Console.CursorVisible = false;

            MainLoop();
        }
        finally
        {
            Console.CursorVisible = true;
            Console.Write("\e[?1049l"); // Leave alternate screen
        }
    }

    private void MainLoop()
    {
        Render();

        while (_state.Running)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                HandleKey(key);
                Render();
            }

            // Re-render when a status message expires
            if (_state.HasPendingStatus)
            {
                if (_state.GetActiveStatus() == null)
                    Render();
            }

            // Re-render when spinner frame advances (only if sessions were spinning at last poll)
            var spinnerFrame = Renderer.GetSpinnerFrame();
            if (spinnerFrame != _lastSpinnerFrame)
            {
                _lastSpinnerFrame = spinnerFrame;
                if (_hasSpinningSessions)
                    Render();
            }

            // Periodically capture pane content for preview
            if ((DateTime.Now - _lastCapture).TotalMilliseconds > 500)
            {
                if (UpdateCapturedPane())
                    Render();
                _lastCapture = DateTime.Now;
            }

            Thread.Sleep(30);
        }
    }

    private void LoadSessions()
    {
        var oldSessions = _state.Sessions.ToDictionary(s => s.Name);
        _state.Sessions = TmuxService.ListSessions();
        foreach (var s in _state.Sessions)
        {
            if (_config.SessionDescriptions.TryGetValue(s.Name, out var desc))
                s.Description = desc;
            if (_config.SessionColors.TryGetValue(s.Name, out var color))
                s.ColorTag = color;
            TmuxService.ApplyStatusColor(s.Name, color ?? "grey42");

            // Preserve content tracking state so sessions don't briefly flash as "working"
            if (oldSessions.TryGetValue(s.Name, out var old))
            {
                s.PreviousContentHash = old.PreviousContentHash;
                s.StableContentCount = old.StableContentCount;
                s.IsWaitingForInput = old.IsWaitingForInput;
            }
        }
        _state.ClampCursor();
    }

    private bool UpdateCapturedPane()
    {
        // Refresh waiting-for-input status on all sessions (single tmux call)
        TmuxService.DetectWaitingForInputBatch(_state.Sessions);
        _hasSpinningSessions = _state.Sessions.Any(s => !s.IsWaitingForInput);

        var session = _state.GetSelectedSession();
        var sessionName = session?.Name;

        if (sessionName != _lastSelectedSession)
        {
            _lastSelectedSession = sessionName;
            _capturedPane = session != null ? TmuxService.CapturePaneContent(session.Name) : null;
            return true;
        }

        if (session == null) return false;

        var newContent = TmuxService.CapturePaneContent(session.Name);
        if (newContent != _capturedPane)
        {
            _capturedPane = newContent;
            return true;
        }

        return false;
    }

    private void Render()
    {
        Console.SetCursorPosition(0, 0);
        AnsiConsole.Write(Renderer.BuildLayout(_state, _capturedPane));
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (_state.IsInputMode)
        {
            HandleInputKey(key);
            return;
        }

        if (_state.HasPendingStatus)
        {
            _state.ClearStatus();
            return;
        }

        // Arrow keys are hardcoded fallbacks — always work
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveCursor(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveCursor(1);
                return;
        }

        var keyId = ResolveKeyId(key);
        if (_keyMap.TryGetValue(keyId, out var actionId))
            DispatchAction(actionId);
    }

    private static string ResolveKeyId(ConsoleKeyInfo key)
    {
        return key.Key switch
        {
            ConsoleKey.Enter => "Enter",
            ConsoleKey.UpArrow => "UpArrow",
            ConsoleKey.DownArrow => "DownArrow",
            _ => key.KeyChar.ToString(),
        };
    }

    private void DispatchAction(string actionId)
    {
        switch (actionId)
        {
            case "navigate-up":     MoveCursor(-1); break;
            case "navigate-down":   MoveCursor(1); break;
            case "approve":         SendQuickKey("y"); break;
            case "reject":          SendQuickKey("n"); break;
            case "send-text":       SendText(); break;
            case "attach":          AttachToSession(); break;
            case "new-session":     CreateNewSession(); break;
            case "open-folder":     OpenFolder(); break;
            case "open-ide":        OpenInIde(); break;
            case "open-config":     OpenConfig(); break;
            case "delete-session":  DeleteSession(); break;
            case "edit-session":    EditSession(); break;
            case "refresh":
                LoadSessions();
                _state.SetStatus("Refreshed");
                break;
            case "quit":
                _state.Running = false;
                break;
        }
    }

    private void HandleInputKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _state.IsInputMode = false;
                _state.InputBuffer = "";
                _state.SetStatus("Cancelled");
                break;

            case ConsoleKey.Enter:
                var text = _state.InputBuffer;
                var target = _state.InputTarget;
                _state.IsInputMode = false;
                _state.InputBuffer = "";

                if (text.Length > 0 && target != null)
                {
                    var sendError = TmuxService.SendKeys(target, text);
                    if (sendError == null)
                    {
                        _state.SetStatus($"Sent to {target}");
                        _lastSelectedSession = null;
                    }
                    else
                    {
                        _state.SetStatus(sendError);
                    }
                }
                else
                {
                    _state.SetStatus("Cancelled");
                }
                break;

            case ConsoleKey.Backspace:
                if (_state.InputBuffer.Length > 0)
                    _state.InputBuffer = _state.InputBuffer[..^1];
                break;

            default:
                if (key.KeyChar >= ' ')
                    _state.InputBuffer += key.KeyChar;
                break;
        }
    }

    private void MoveCursor(int delta)
    {
        if (_state.Sessions.Count == 0) return;
        _state.CursorIndex = Math.Clamp(_state.CursorIndex + delta, 0, _state.Sessions.Count - 1);
        _lastSelectedSession = null; // Force pane recapture
    }


    private void AttachToSession()
    {
        var session = _state.GetSelectedSession();
        if (session == null) return;

        Console.CursorVisible = true;
        Console.Clear();

        TmuxService.AttachSession(session.Name);

        // User detached - back to ClaudeCommandCenter
        Console.CursorVisible = false;
        LoadSessions();
        _lastSelectedSession = null;
    }

    private void CreateNewSession()
    {
        if (!_claudeAvailable)
        {
            _state.SetStatus("'claude' not found in PATH — install Claude Code first");
            return;
        }

        Console.CursorVisible = true;
        Console.Clear();

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey70]Session name[/] [grey](empty to go back)[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.CursorVisible = false;
            _state.SetStatus("Cancelled");
            return;
        }

        var description = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey70]Description[/] [grey](optional)[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        var color = PickColor();

        var dir = PickDirectory();

        if (dir == null)
        {
            Console.CursorVisible = false;
            _state.SetStatus("Cancelled");
            return;
        }

        dir = ConfigService.ExpandPath(dir);

        if (!Directory.Exists(dir))
        {
            Console.CursorVisible = false;
            _state.SetStatus("Invalid directory");
            return;
        }

        var error = TmuxService.CreateSession(name, dir);
        if (error == null)
        {
            if (!string.IsNullOrWhiteSpace(description))
                ConfigService.SaveDescription(_config, name, description);
            if (color != null)
                ConfigService.SaveColor(_config, name, color);
            TmuxService.ApplyStatusColor(name, color ?? "grey42");
            TmuxService.AttachSession(name);
        }
        else
        {
            _state.SetStatus(error);
        }

        Console.CursorVisible = false;
        LoadSessions();
        _lastSelectedSession = null;
    }

    private static readonly (string Label, string SpectreColor)[] ColorPalette =
    [
        ("Steel Blue", "SteelBlue"),
        ("Indian Red", "IndianRed"),
        ("Medium Purple", "MediumPurple"),
        ("Cadet Blue", "CadetBlue"),
        ("Light Salmon", "LightSalmon3"),
        ("Dark Sea Green", "DarkSeaGreen"),
        ("Dark Khaki", "DarkKhaki"),
        ("Plum", "Plum4"),
    ];

    private static string? PickColor()
    {
        var prompt = new SelectionPrompt<string>()
            .Title("[grey70]Color[/] [grey](optional)[/]")
            .HighlightStyle(new Style(Color.White, Color.Grey70));

        prompt.AddChoice("None");
        foreach (var (label, spectreColor) in ColorPalette)
            prompt.AddChoice($"[{spectreColor}]████[/]  {label}");

        var selected = AnsiConsole.Prompt(prompt);

        if (selected == "None")
            return null;

        // Match back to palette by label suffix
        foreach (var (label, spectreColor) in ColorPalette)
        {
            if (selected.EndsWith(label))
                return spectreColor;
        }

        return null;
    }

    private const string CustomPathChoice = "Custom path...";
    private const string CancelChoice = "Cancel";

    private string? PickDirectory()
    {
        var favorites = _config.FavoriteFolders;

        if (favorites.Count == 0)
            return PromptCustomPath();

        while (true)
        {
            var prompt = new SelectionPrompt<string>()
                .Title("[grey70]Pick a directory[/]")
                .PageSize(15)
                .HighlightStyle(new Style(Color.White, Color.Grey70))
                .MoreChoicesText("[grey](Move up and down to reveal more)[/]");

            foreach (var fav in favorites)
                prompt.AddChoice($"{fav.Name}  [grey50]{fav.Path}[/]");

            prompt.AddChoice(CustomPathChoice);
            prompt.AddChoice(CancelChoice);

            var selected = AnsiConsole.Prompt(prompt);

            if (selected == CancelChoice)
                return null;

            if (selected == CustomPathChoice)
            {
                var custom = PromptCustomPath();
                if (custom != null)
                    return custom;
                continue; // empty input → back to picker
            }

            // Match back to the favorite by prefix (name before the spacing)
            var selectedName = selected.Split("  ")[0];
            var match = favorites.FirstOrDefault(f => f.Name == selectedName);
            return match?.Path;
        }
    }

    private static string? PromptCustomPath()
    {
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey70]Working directory:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private void OpenFolder()
    {
        var session = _state.GetSelectedSession();
        if (session?.CurrentPath == null) 
            return;

        try
        {
            var command = GetFileManagerCommand();
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                ArgumentList = { session.CurrentPath },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process.Start(startInfo);
            _state.SetStatus($"Opened folder: {session.CurrentPath}");
        }
        catch
        {
            _state.SetStatus("Failed to open folder");
        }
    }

    private static string GetFileManagerCommand()
    {
        if (OperatingSystem.IsMacOS()) 
            return "open";
        
        if (OperatingSystem.IsWindows()) 
            return "explorer";

        // Linux — check for WSL where explorer.exe is available
        try
        {
            var version = File.ReadAllText("/proc/version");
            if (version.Contains("microsoft", StringComparison.OrdinalIgnoreCase))
                return "explorer.exe";
        }
        catch { /* not WSL */ }

        return "xdg-open";
    }

    private void OpenInIde()
    {
        var session = _state.GetSelectedSession();
        if (session?.CurrentPath == null) return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _config.IdeCommand,
                ArgumentList = { session.CurrentPath },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process.Start(startInfo);
            _state.SetStatus($"Opened in {_config.IdeCommand}");
        }
        catch
        {
            _state.SetStatus($"Failed to run '{_config.IdeCommand}'");
        }
    }

    private void OpenConfig()
    {
        var configPath = ConfigService.GetConfigPath();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _config.IdeCommand,
                ArgumentList = { configPath },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process.Start(startInfo);
            _state.SetStatus($"Opened config in {_config.IdeCommand}");
        }
        catch
        {
            _state.SetStatus($"Failed to run '{_config.IdeCommand}'");
        }
    }

    private void DeleteSession()
    {
        var session = _state.GetSelectedSession();
        if (session == null) return;

        _state.SetStatus($"Kill '{session.Name}'? (y/n)");
        Render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            var killError = TmuxService.KillSession(session.Name);
            if (killError == null)
            {
                ConfigService.RemoveDescription(_config, session.Name);
                ConfigService.RemoveColor(_config, session.Name);
                _state.SetStatus("Session killed");
            }
            else
            {
                _state.SetStatus(killError);
            }
            LoadSessions();
        }
        else
        {
            _state.SetStatus("Cancelled");
        }
    }

    private void SendQuickKey(string key)
    {
        var session = _state.GetSelectedSession();
        if (session == null) return;

        var error = TmuxService.SendKeys(session.Name, key);
        if (error == null)
        {
            _state.SetStatus($"Sent '{key}' to {session.Name}");
            _lastSelectedSession = null; // Force pane refresh
        }
        else
        {
            _state.SetStatus(error);
        }
    }

    private void SendText()
    {
        var session = _state.GetSelectedSession();
        if (session == null) return;

        _state.IsInputMode = true;
        _state.InputBuffer = "";
        _state.InputTarget = session.Name;
    }

    private void EditSession()
    {
        var session = _state.GetSelectedSession();
        if (session == null) return;

        Console.CursorVisible = true;
        Console.Clear();

        var escapedName = Markup.Escape(session.Name);
        AnsiConsole.MarkupLine($"[grey70 bold]Edit session[/] [white]'{escapedName}'[/] [grey](empty = keep current)[/]\n");

        var newName = AnsiConsole.Prompt(
            new TextPrompt<string>($"[grey70]Name[/] [grey50]({escapedName})[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        var currentDesc = session.Description ?? "";
        var descHint = string.IsNullOrWhiteSpace(currentDesc) ? "none" : Markup.Escape(currentDesc);
        var newDesc = AnsiConsole.Prompt(
            new TextPrompt<string>($"[grey70]Description[/] [grey50]({descHint})[/][grey70]:[/]")
                .AllowEmpty()
                .PromptStyle(new Style(Color.White)));

        var newColor = PickColor();

        Console.CursorVisible = false;

        var currentName = session.Name;
        var changed = false;

        // Apply name change
        if (!string.IsNullOrWhiteSpace(newName) && newName != currentName)
        {
            var renameError = TmuxService.RenameSession(currentName, newName);
            if (renameError == null)
            {
                ConfigService.RenameDescription(_config, currentName, newName);
                ConfigService.RenameColor(_config, currentName, newName);
                currentName = newName;
                changed = true;
            }
            else
            {
                _state.SetStatus(renameError);
                return;
            }
        }

        // Apply description change
        if (!string.IsNullOrWhiteSpace(newDesc))
        {
            ConfigService.SaveDescription(_config, currentName, newDesc);
            changed = true;
        }

        // Apply color change
        if (newColor != null)
        {
            ConfigService.SaveColor(_config, currentName, newColor);
            TmuxService.ApplyStatusColor(currentName, newColor);
            changed = true;
        }

        if (changed)
        {
            LoadSessions();
            _state.SetStatus("Session updated");
        }
        else
        {
            _state.SetStatus("No changes");
        }
    }
}
