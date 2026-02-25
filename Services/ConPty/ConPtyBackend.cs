using System.Runtime.InteropServices;
using System.Text;
using ClaudeCommandCenter.Models;
using Microsoft.Win32.SafeHandles;
using static ClaudeCommandCenter.Services.ConPty.NativeMethods;

namespace ClaudeCommandCenter.Services.ConPty;

public class ConPtyBackend : ISessionBackend
{
    private readonly Dictionary<string, ConPtySession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _sessionsLock = new();

    // Detach signal for inline attach — set by the detach key combo handler
    private volatile bool _detachRequested;

    public List<Session> ListSessions()
    {
        lock (_sessionsLock)
        {
            var sessions = new List<Session>();
            var dead = new List<string>();

            foreach (var (name, conPty) in _sessions)
            {
                var session = new Session
                {
                    Name = name,
                    Created = conPty.Created,
                    IsAttached = false,
                    WindowCount = 1,
                    CurrentPath = conPty.WorkingDirectory,
                    IsDead = !conPty.IsAlive,
                };

                GitService.DetectGitInfo(session);
                sessions.Add(session);

                if (session.IsDead)
                    dead.Add(name);
            }

            // Clean up dead sessions
            foreach (var name in dead)
            {
                _sessions[name].Dispose();
                _sessions.Remove(name);
            }

            return sessions.OrderBy(s => s.Created).ThenBy(s => s.Name).ToList();
        }
    }

    public string? CreateSession(string name, string workingDirectory)
    {
        lock (_sessionsLock)
        {
            if (_sessions.ContainsKey(name))
                return $"Session '{name}' already exists";
        }

        try
        {
            var session = StartProcess(name, workingDirectory);
            lock (_sessionsLock)
            {
                if (!_sessions.TryAdd(name, session))
                {
                    // Another thread created it between our check and insertion
                    session.Dispose();
                    return $"Session '{name}' already exists";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Failed to create session: {ex.Message}";
        }
    }

    public string? KillSession(string name)
    {
        lock (_sessionsLock)
        {
            if (!_sessions.Remove(name, out var session))
                return $"Session '{name}' not found";
            session.Dispose();
            return null;
        }
    }

    public string? RenameSession(string oldName, string newName)
    {
        lock (_sessionsLock)
        {
            if (!_sessions.Remove(oldName, out var session))
                return $"Session '{oldName}' not found";
            if (_sessions.ContainsKey(newName))
            {
                _sessions[oldName] = session;
                return $"Session '{newName}' already exists";
            }

            session.Name = newName;
            _sessions[newName] = session;
            return null;
        }
    }

    public void AttachSession(string name)
    {
        ConPtySession? session;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(name, out session))
                return;
        }

        _detachRequested = false;

        // Save console state
        var savedEncoding = Console.OutputEncoding;
        Console.OutputEncoding = Encoding.UTF8;

        // Resize the ConPTY to the full terminal size so the session renders correctly
        var fullWidth = (short)Console.WindowWidth;
        var fullHeight = (short)Console.WindowHeight;
        if (session.Width != fullWidth || session.Height != fullHeight)
        {
            var coord = new Coord(fullWidth, fullHeight);
            ResizePseudoConsole(session.PseudoConsole, coord);
            session.Screen.Resize(fullWidth, fullHeight);
            session.Width = fullWidth;
            session.Height = fullHeight;
        }

        using var outputCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
        var token = outputCts.Token;

        // Tell the session to forward raw ConPTY output to CCC's console.
        // We set a volatile flag that the ReaderLoop checks — when set, it writes
        // incoming ConPTY data directly to Console.Out in addition to the buffers.
        session.ForwardToConsole = true;

        // Track terminal size so we can detect resizes during attach
        var lastWidth = Console.WindowWidth;
        var lastHeight = Console.WindowHeight;

        // Input loop: read console keys and forward to session
        try
        {
            while (!_detachRequested && !token.IsCancellationRequested && session.IsAlive)
            {
                // Check for terminal resize
                var curWidth = Console.WindowWidth;
                var curHeight = Console.WindowHeight;
                if (curWidth != lastWidth || curHeight != lastHeight)
                {
                    lastWidth = curWidth;
                    lastHeight = curHeight;
                    var coord = new Coord((short)curWidth, (short)curHeight);
                    ResizePseudoConsole(session.PseudoConsole, coord);
                    session.Screen.Resize(curWidth, curHeight);
                    session.Width = (short)curWidth;
                    session.Height = (short)curHeight;
                }

                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }

                var key = Console.ReadKey(true);

                // Detach: Alt+Q
                if (key.Key == ConsoleKey.Q && key.Modifiers.HasFlag(ConsoleModifiers.Alt))
                {
                    _detachRequested = true;
                    break;
                }

                // Forward the key to the session
                ForwardKeyToSession(session, key);
            }
        }
        finally
        {
            session.ForwardToConsole = false;
            outputCts.Cancel();
            Console.OutputEncoding = savedEncoding;
        }
    }

    public void DetachSession() => _detachRequested = true;

    public string? SendKeys(string sessionName, string text)
    {
        ConPtySession? session;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(sessionName, out session))
                return $"Session '{sessionName}' not found";
        }

        try
        {
            session.Input.Write(text);
            session.Input.Write('\r'); // Enter
            session.Input.Flush();
            return null;
        }
        catch (Exception ex)
        {
            return $"Failed to send keys: {ex.Message}";
        }
    }

    public string? CapturePaneContent(string sessionName, int lines = 500)
    {
        ConPtySession? session;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(sessionName, out session))
                return null;
        }

        return session.Screen.GetContent(lines);
    }

    public void ResizeWindow(string sessionName, int width, int height)
    {
        ConPtySession? session;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(sessionName, out session))
                return;
        }

        // Skip if dimensions haven't changed
        if (session.Width == (short)width && session.Height == (short)height)
            return;

        var coord = new Coord((short)width, (short)height);
        ResizePseudoConsole(session.PseudoConsole, coord);
        session.Screen.Resize(width, height);
        session.Width = (short)width;
        session.Height = (short)height;
    }

    public void ResetWindowSize(string sessionName)
    {
        // Reset to default terminal size
        ResizeWindow(sessionName, Console.WindowWidth, Console.WindowHeight);
    }

    public void ApplyStatusColor(string sessionName, string? spectreColor)
    {
        // No-op — ConPTY has no tmux-style status bar. Color is shown by CCC's own UI.
    }

    // Number of consecutive stable polls before marking as "waiting for input"
    private const int StableThreshold = 4;

    public void DetectWaitingForInputBatch(List<Session> sessions)
    {
        if (sessions.Count == 0)
            return;

        foreach (var session in sessions)
        {
            if (session.IsDead)
            {
                session.IsWaitingForInput = false;
                session.IsIdle = false;
                continue;
            }

            // Prefer hook-based state (same as tmux backend)
            var hookState = HookStateService.ReadState(session.Name);
            if (hookState != null)
            {
                session.IsWaitingForInput = hookState == "waiting";
                session.IsIdle = hookState == "idle";
                continue;
            }

            // Fallback: content hash stability detection
            DetectWaitingByContent(session);
        }
    }

    public bool IsAvailable()
    {
        // ConPTY requires Windows 10 1809+ (build 17763)
        return OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 17763;
    }

    public bool IsInsideHost()
    {
        // ConPTY sessions are children of CCC — you can't be "inside" one
        return false;
    }

    public bool HasClaude() => SessionContentAnalyzer.CheckClaudeAvailable();

    public void Dispose()
    {
        lock (_sessionsLock)
        {
            foreach (var session in _sessions.Values)
                session.Dispose();
            _sessions.Clear();
        }
    }

    private static ConPtySession StartProcess(string name, string workingDirectory)
    {
        // Create pipes: CCC writes to inputWrite → process reads from inputRead
        //               Process writes to outputWrite → CCC reads from outputRead
        if (!CreatePipe(out var inputRead, out var inputWrite, nint.Zero, 0))
            throw new InvalidOperationException($"CreatePipe (input) failed: {Marshal.GetLastWin32Error()}");
        if (!CreatePipe(out var outputRead, out var outputWrite, nint.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new InvalidOperationException($"CreatePipe (output) failed: {Marshal.GetLastWin32Error()}");
        }

        // Create pseudoconsole sized to match the preview pane so content renders correctly from the start.
        // Preview width = terminal width - session panel (35) - borders (6) - padding (2).
        var initialWidth = (short)Math.Max(20, Console.WindowWidth - 35 - 8);
        var initialHeight = (short)Math.Max(10, Console.WindowHeight);

        var size = new Coord(initialWidth, initialHeight);
        var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out var hPC);
        if (hr != 0)
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            throw new InvalidOperationException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");
        }

        // Close the handles that the pseudoconsole now owns
        inputRead.Dispose();
        outputWrite.Dispose();

        // Set up process creation with pseudoconsole attribute
        var attrSize = nint.Zero;
        InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref attrSize);
        var attrList = Marshal.AllocHGlobal(attrSize);

        // Build environment block with CCC_SESSION_NAME so hooks can identify the session
        var envBlock = BuildEnvironmentBlock(name);

        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

            if (!UpdateProcThreadAttribute(
                    attrList, 0,
                    (nuint)ProcThreadAttributePseudoConsole,
                    hPC, (nuint)nint.Size,
                    nint.Zero, nint.Zero))
                throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>()
                },
                lpAttributeList = attrList
            };

            var commandLine = "claude";
            if (!CreateProcessW(
                    null, commandLine,
                    nint.Zero, nint.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    envBlock,
                    workingDirectory,
                    in startupInfo,
                    out var procInfo))
                throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");

            // Close the thread handle — we only need the process handle
            CloseHandle(procInfo.hThread);

            var cts = new CancellationTokenSource();
            var buffer = new RingBuffer();
            var screen = new VtScreenBuffer(size.X, size.Y);
            var inputStream = new FileStream(inputWrite, FileAccess.Write);
            var inputWriter = new StreamWriter(inputStream, Encoding.UTF8)
            {
                AutoFlush = true
            };

            // Create session first so ReaderLoop can check ForwardToConsole
            var session = new ConPtySession
            {
                Name = name,
                WorkingDirectory = workingDirectory,
                PseudoConsole = hPC,
                ProcessHandle = procInfo.hProcess,
                ProcessId = procInfo.dwProcessId,
                InputWriteHandle = inputWrite,
                OutputReadHandle = outputRead,
                Input = inputWriter,
                ReaderThread = null!, // Set below
                Screen = screen,
                OutputBuffer = buffer,
                Cts = cts,
                Width = size.X,
                Height = size.Y,
            };

            var readerThread = new Thread(() => ReaderLoop(outputRead, buffer, screen, session, cts.Token))
            {
                IsBackground = true,
                Name = $"ConPTY-Reader-{name}"
            };
            session.ReaderThread = readerThread;
            readerThread.Start();

            return session;
        }
        catch
        {
            ClosePseudoConsole(hPC);
            outputRead.Dispose();
            inputWrite.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(envBlock);
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    private static void ReaderLoop(SafeFileHandle outputRead, RingBuffer buffer, VtScreenBuffer screen, ConPtySession session, CancellationToken ct)
    {
        using var stream = new FileStream(outputRead, FileAccess.Read);
        // StreamReader handles UTF-8 decoding across read boundaries, preventing
        // garbled characters when multi-byte sequences are split across reads.
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var charBuf = new char[4096];
        var stdout = Console.Out;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var charsRead = reader.Read(charBuf, 0, charBuf.Length);
                if (charsRead == 0)
                    break; // Pipe closed

                var text = new string(charBuf, 0, charsRead);
                buffer.AppendChunk(text);
                screen.Feed(text);

                // In attach mode, forward raw ConPTY output directly to the terminal
                if (session.ForwardToConsole)
                {
                    stdout.Write(text);
                    stdout.Flush();
                }
            }
        }
        catch (IOException)
        {
            // Pipe closed — process exited
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private static void ForwardKeyToSession(ConPtySession session, ConsoleKeyInfo key)
    {
        var sequence = MapKeyToSequence(session, key);
        if (sequence != null)
        {
            session.Input.Write(sequence);
            session.Input.Flush();
        }
    }

    private static string? MapKeyToSequence(ConPtySession session, ConsoleKeyInfo key)
    {
        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);
        var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        var appCursor = session.Screen.ApplicationCursorKeys;

        var hasModifier = ctrl || alt || shift;

        // Arrow keys and navigation keys
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                if (hasModifier) return $"\e[1;{ModifierCode(shift, alt, ctrl)}A";
                return appCursor ? "\eOA" : "\e[A";
            case ConsoleKey.DownArrow:
                if (hasModifier) return $"\e[1;{ModifierCode(shift, alt, ctrl)}B";
                return appCursor ? "\eOB" : "\e[B";
            case ConsoleKey.RightArrow:
                if (hasModifier) return $"\e[1;{ModifierCode(shift, alt, ctrl)}C";
                return appCursor ? "\eOC" : "\e[C";
            case ConsoleKey.LeftArrow:
                if (hasModifier) return $"\e[1;{ModifierCode(shift, alt, ctrl)}D";
                return appCursor ? "\eOD" : "\e[D";
            case ConsoleKey.Home:
                if (hasModifier) return $"\e[1;{ModifierCode(shift, alt, ctrl)}H";
                return appCursor ? "\eOH" : "\e[H";
            case ConsoleKey.End:
                if (hasModifier) return $"\e[1;{ModifierCode(shift, alt, ctrl)}F";
                return appCursor ? "\eOF" : "\e[F";
            case ConsoleKey.Enter:     return "\r";
            case ConsoleKey.Backspace: return "\x7f";
            case ConsoleKey.Tab:
                return shift ? "\e[Z" : "\t";
            case ConsoleKey.Escape:    return "\e";
            case ConsoleKey.PageUp:    return "\e[5~";
            case ConsoleKey.PageDown:  return "\e[6~";
            case ConsoleKey.Delete:    return "\e[3~";
            case ConsoleKey.Insert:    return "\e[2~";
            case ConsoleKey.F1:        return "\eOP";
            case ConsoleKey.F2:        return "\eOQ";
            case ConsoleKey.F3:        return "\eOR";
            case ConsoleKey.F4:        return "\eOS";
            case ConsoleKey.F5:        return "\e[15~";
            case ConsoleKey.F6:        return "\e[17~";
            case ConsoleKey.F7:        return "\e[18~";
            case ConsoleKey.F8:        return "\e[19~";
            case ConsoleKey.F9:        return "\e[20~";
            case ConsoleKey.F10:       return "\e[21~";
            case ConsoleKey.F11:       return "\e[23~";
            case ConsoleKey.F12:       return "\e[24~";
        }

        // Ctrl+key combinations (A-Z -> 0x01-0x1A)
        if (ctrl && key.Key >= ConsoleKey.A && key.Key <= ConsoleKey.Z)
        {
            var ctrlChar = (char)(key.Key - ConsoleKey.A + 1);
            return alt ? $"\e{ctrlChar}" : $"{ctrlChar}";
        }

        // Regular character with Alt -> ESC prefix
        if (key.KeyChar != '\0')
            return alt ? $"\e{key.KeyChar}" : $"{key.KeyChar}";

        return null;
    }

    private static int ModifierCode(bool shift, bool alt, bool ctrl)
    {
        // xterm modifier encoding: value = 1 + (shift?1:0) + (alt?2:0) + (ctrl?4:0)
        return 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
    }

    private void DetectWaitingByContent(Session session)
    {
        var content = CapturePaneContent(session.Name, 20);
        if (content == null)
        {
            session.IsWaitingForInput = true;
            return;
        }

        content = SessionContentAnalyzer.GetContentAboveStatusBar(content);

        if (content == session.PreviousContent)
            session.StableContentCount++;
        else
        {
            session.StableContentCount = 0;
            session.PreviousContent = content;
        }

        var isStable = session.StableContentCount >= StableThreshold;
        session.IsIdle = isStable && SessionContentAnalyzer.IsIdlePrompt(content);
        session.IsWaitingForInput = isStable && !session.IsIdle;
    }

    /// <summary>
    /// Builds a Unicode environment block for CreateProcessW with CCC_SESSION_NAME injected
    /// and CLAUDE_CODE_GIT_BASH_PATH set so Claude Code can find bash for hook execution.
    /// Format: sorted KEY=VALUE\0 pairs, terminated by an extra \0.
    /// </summary>
    private static nint BuildEnvironmentBlock(string sessionName)
    {
        // Start with current environment
        var env = Environment.GetEnvironmentVariables();
        var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in env)
            entries[(string)entry.Key] = (string)entry.Value!;

        // Inject CCC_SESSION_NAME so hooks can identify the session
        entries["CCC_SESSION_NAME"] = sessionName;

        // Ensure Claude Code can find bash for hook execution.
        // When launched via CreateProcessW there is no parent Git Bash shell,
        // so Claude Code can't locate bash unless we tell it explicitly.
        if (!entries.ContainsKey("CLAUDE_CODE_GIT_BASH_PATH"))
        {
            var gitBash = FindGitBash();
            if (gitBash != null)
                entries["CLAUDE_CODE_GIT_BASH_PATH"] = gitBash;
        }

        // Build the block: KEY=VALUE\0KEY=VALUE\0...\0
        var sb = new StringBuilder();
        foreach (var (key, value) in entries)
        {
            sb.Append(key);
            sb.Append('=');
            sb.Append(value);
            sb.Append('\0');
        }
        sb.Append('\0'); // Double null terminator

        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    /// <summary>
    /// Finds Git Bash executable on the system.
    /// </summary>
    private static string? FindGitBash()
    {
        string[] candidates =
        [
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
