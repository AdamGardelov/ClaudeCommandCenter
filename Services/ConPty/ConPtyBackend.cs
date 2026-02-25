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

    // Double-tap Escape detection for detach
    private long _lastEscapeTicks;

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
        _lastEscapeTicks = 0;

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

                // Detach: double-tap Escape (press Escape twice within 500ms)
                if (key.Key == ConsoleKey.Escape)
                {
                    var now = Environment.TickCount64;
                    if (now - _lastEscapeTicks <= 500)
                    {
                        _detachRequested = true;
                        break;
                    }

                    _lastEscapeTicks = now;
                    // Forward the single Escape to the session
                    ForwardKeyToSession(session, key);
                    continue;
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
                    nint.Zero,
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
        // Map special keys to VT escape sequences
        var sequence = key.Key switch
        {
            ConsoleKey.Enter => "\r",
            ConsoleKey.Backspace => "\x7f",
            ConsoleKey.Tab => "\t",
            ConsoleKey.Escape => "\e",
            ConsoleKey.UpArrow => "\e[A",
            ConsoleKey.DownArrow => "\e[B",
            ConsoleKey.RightArrow => "\e[C",
            ConsoleKey.LeftArrow => "\e[D",
            ConsoleKey.Home => "\e[H",
            ConsoleKey.End => "\e[F",
            ConsoleKey.PageUp => "\e[5~",
            ConsoleKey.PageDown => "\e[6~",
            ConsoleKey.Delete => "\e[3~",
            ConsoleKey.Insert => "\e[2~",
            ConsoleKey.F1 => "\e" + "OP",
            ConsoleKey.F2 => "\e" + "OQ",
            ConsoleKey.F3 => "\e" + "OR",
            ConsoleKey.F4 => "\e" + "OS",
            ConsoleKey.F5 => "\e[15~",
            ConsoleKey.F6 => "\e[17~",
            ConsoleKey.F7 => "\e[18~",
            ConsoleKey.F8 => "\e[19~",
            ConsoleKey.F9 => "\e[20~",
            ConsoleKey.F10 => "\e[21~",
            ConsoleKey.F11 => "\e[23~",
            ConsoleKey.F12 => "\e[24~",
            _ => null
        };

        if (sequence != null)
        {
            session.Input.Write(sequence);
            session.Input.Flush();
            return;
        }

        // Ctrl+key combinations
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key >= ConsoleKey.A && key.Key <= ConsoleKey.Z)
        {
            var ctrlChar = (char)(key.Key - ConsoleKey.A + 1);
            session.Input.Write(ctrlChar);
            session.Input.Flush();
            return;
        }

        // Regular character
        if (key.KeyChar != '\0')
        {
            session.Input.Write(key.KeyChar);
            session.Input.Flush();
        }
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
}
