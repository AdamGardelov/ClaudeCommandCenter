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
    private string? _capturedPane;
    private DateTime _lastCapture = DateTime.MinValue;
    private string? _lastSelectedSession;

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
        _state.Sessions = TmuxService.ListSessions();
        _state.ClampCursor();
    }

    private bool UpdateCapturedPane()
    {
        // Refresh waiting-for-input status on all sessions
        foreach (var s in _state.Sessions)
            TmuxService.DetectWaitingForInput(s);

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
        switch (key.Key)
        {
            case ConsoleKey.UpArrow or ConsoleKey.K:
                MoveCursor(-1);
                break;

            case ConsoleKey.DownArrow or ConsoleKey.J:
                MoveCursor(1);
                break;


            case ConsoleKey.Enter:
                AttachToSession();
                break;

            case ConsoleKey.Q:
                _state.Running = false;
                break;

            default:
                HandleCharKey(key.KeyChar);
                break;
        }
    }

    private void HandleCharKey(char c)
    {
        switch (c)
        {
            case 'n':
                CreateNewSession();
                break;
            case 'd':
                DeleteSession();
                break;
            case 'R':
                RenameSession();
                break;
            case 'r':
                LoadSessions();
                _state.SetStatus("Refreshed");
                break;
            case 'i':
                OpenInIde();
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
        Console.CursorVisible = true;
        Console.Clear();

        Console.Write("Session name (Enter to cancel): ");
        var name = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.CursorVisible = false;
            _state.SetStatus("Cancelled");
            return;
        }

        string? dir = PickDirectory();

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

        if (TmuxService.CreateSession(name, dir))
            TmuxService.AttachSession(name);
        else
            _state.SetStatus("Failed to create session");

        Console.CursorVisible = false;
        LoadSessions();
        _lastSelectedSession = null;
    }

    private string? PickDirectory()
    {
        var favorites = _config.FavoriteFolders;

        if (favorites.Count == 0)
        {
            Console.Write("Working directory (Enter to cancel): ");
            return Console.ReadLine()?.Trim();
        }

        Console.WriteLine("Pick a directory (Enter to cancel):");
        for (var i = 0; i < favorites.Count; i++)
            Console.WriteLine($"  {i + 1}) {favorites[i].Name,-12} {favorites[i].Path}");
        Console.WriteLine($"  {favorites.Count + 1}) Custom path...");
        Console.Write("> ");

        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(input))
            return null;

        if (int.TryParse(input, out var choice) && choice >= 1 && choice <= favorites.Count + 1)
        {
            if (choice <= favorites.Count)
                return favorites[choice - 1].Path;

            // Custom path
            Console.Write("Working directory (Enter to cancel): ");
            return Console.ReadLine()?.Trim();
        }

        return null;
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

    private void DeleteSession()
    {
        var session = _state.GetSelectedSession();
        if (session == null) return;

        _state.SetStatus($"Kill '{session.Name}'? (y/n)");
        Render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            TmuxService.KillSession(session.Name);
            _state.SetStatus("Session killed");
            LoadSessions();
        }
        else
        {
            _state.SetStatus("Cancelled");
        }
    }

    private void RenameSession()
    {
        var session = _state.GetSelectedSession();
        if (session == null) return;

        Console.CursorVisible = true;
        Console.Clear();
        Console.Write($"Rename '{session.Name}' to: ");

        var newName = Console.ReadLine()?.Trim();
        Console.CursorVisible = false;

        if (!string.IsNullOrWhiteSpace(newName))
        {
            if (TmuxService.RenameSession(session.Name, newName))
            {
                _state.SetStatus($"Renamed to '{newName}'");
                LoadSessions();
            }
            else
            {
                _state.SetStatus("Rename failed");
            }
        }
        else
        {
            _state.SetStatus("Cancelled");
        }
    }
}
