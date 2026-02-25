using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeCommandCenter.Models;
using Microsoft.Win32.SafeHandles;
using static ClaudeCommandCenter.Services.ConPty.NativeMethods;

namespace ClaudeCommandCenter.Services.ConPty;

public partial class ConPtyBackend : ISessionBackend
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
                _sessions[name] = session;
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
        var savedMode = Console.OutputEncoding;
        Console.OutputEncoding = Encoding.UTF8;
        Console.Clear();

        // Tap the ring buffer's output by temporarily redirecting reader output to stdout.
        // We do this by reading from the ring buffer and also forwarding new output.
        // For attach, we set up direct I/O forwarding.

        // Create a pipe reader for direct output from the pseudoconsole
        using var outputCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
        var token = outputCts.Token;

        // Output forwarding: dump current buffer then forward new content
        var currentContent = session.OutputBuffer.GetContent();
        if (!string.IsNullOrEmpty(currentContent))
            Console.Write(currentContent);

        // Start forwarding loop on a background thread
        var forwardThread = new Thread(() => ForwardOutput(session, token))
        {
            IsBackground = true,
            Name = $"ConPTY-Attach-{name}"
        };
        forwardThread.Start();

        // Input loop: read console keys and forward to session
        try
        {
            while (!_detachRequested && !token.IsCancellationRequested && session.IsAlive)
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }

                var key = Console.ReadKey(true);

                // Detach combo: Ctrl+]
                if (key is { Key: ConsoleKey.Oem6, Modifiers: ConsoleModifiers.Control })
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
            outputCts.Cancel();
            if (forwardThread.IsAlive)
                forwardThread.Join(1000);
            Console.OutputEncoding = savedMode;
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

        return session.OutputBuffer.GetContent(lines);
    }

    public void ResizeWindow(string sessionName, int width, int height)
    {
        ConPtySession? session;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(sessionName, out session))
                return;
        }

        var coord = new Coord((short)width, (short)height);
        ResizePseudoConsole(session.PseudoConsole, coord);
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
    private const int _stableThreshold = 4;

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

    public bool HasClaude()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
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

        // Create pseudoconsole
        var size = new Coord(120, 40);
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
            var inputStream = new FileStream(inputWrite, FileAccess.Write);
            var inputWriter = new StreamWriter(inputStream, Encoding.UTF8)
            {
                AutoFlush = true
            };

            var readerThread = new Thread(() => ReaderLoop(outputRead, buffer, cts.Token))
            {
                IsBackground = true,
                Name = $"ConPTY-Reader-{name}"
            };
            readerThread.Start();

            return new ConPtySession
            {
                Name = name,
                WorkingDirectory = workingDirectory,
                PseudoConsole = hPC,
                ProcessHandle = procInfo.hProcess,
                ProcessId = procInfo.dwProcessId,
                InputWriteHandle = inputWrite,
                Input = inputWriter,
                ReaderThread = readerThread,
                OutputBuffer = buffer,
                Cts = cts,
            };
        }
        catch
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            ClosePseudoConsole(hPC);
            outputRead.Dispose();
            inputWrite.Dispose();
            throw;
        }
    }

    private static void ReaderLoop(SafeFileHandle outputRead, RingBuffer buffer, CancellationToken ct)
    {
        using var stream = new FileStream(outputRead, FileAccess.Read);
        var buf = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = stream.Read(buf, 0, buf.Length);
                if (bytesRead == 0)
                    break; // Pipe closed

                var text = Encoding.UTF8.GetString(buf, 0, bytesRead);
                buffer.AppendChunk(text);
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

    private static void ForwardOutput(ConPtySession session, CancellationToken ct)
    {
        // Poll the ring buffer for new content and write to stdout.
        // This is simpler than tapping the pipe directly during attach,
        // since the reader thread is already writing to the buffer.
        var lastContent = "";
        try
        {
            while (!ct.IsCancellationRequested && session.IsAlive)
            {
                var content = session.OutputBuffer.GetContent();
                if (content != lastContent && content.Length > lastContent.Length)
                {
                    // Write only the new portion
                    var newContent = content[lastContent.Length..];
                    Console.Write(newContent);
                    lastContent = content;
                }

                Thread.Sleep(16); // ~60fps
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
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

        content = GetContentAboveStatusBar(content);

        if (content == session.PreviousContent)
            session.StableContentCount++;
        else
        {
            session.StableContentCount = 0;
            session.PreviousContent = content;
        }

        var isStable = session.StableContentCount >= _stableThreshold;
        session.IsIdle = isStable && IsIdlePrompt(content);
        session.IsWaitingForInput = isStable && !session.IsIdle;
    }

    // The idle prompt and status bar detection logic is identical to TmuxBackend.
    // Duplicated here to keep backends self-contained.

    private static bool IsIdlePrompt(string content)
    {
        var lines = content.Split('\n');

        int bottomSep = -1, prompt = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            if (bottomSep < 0)
                bottomSep = i;
            else
            {
                prompt = i;
                break;
            }
        }

        if (prompt < 0)
            return false;

        var rule = lines[bottomSep].Trim();
        if (rule.Length < 3 || rule.Any(c => c != '─'))
            return false;

        if (!lines[prompt].TrimStart().StartsWith('❯'))
            return false;

        for (var i = prompt - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var trimmed = lines[i].Trim();
            if (trimmed.Length >= 3 && trimmed.All(c => c == '─'))
            {
                for (var j = i - 1; j >= 0; j--)
                {
                    if (string.IsNullOrWhiteSpace(lines[j]))
                        continue;
                    var line = lines[j].TrimStart();
                    if (line.StartsWith('⎿') || line.StartsWith('…') || line.StartsWith('❯')
                        || line.StartsWith('●') || line.StartsWith('✻'))
                        continue;
                    return !line.TrimEnd().EndsWith('?');
                }
            }

            return true;
        }

        return true;
    }

    private static readonly Regex _statusBarTimerPattern = StatusBarTimerRegex();

    private static string GetContentAboveStatusBar(string paneOutput)
    {
        var lines = paneOutput.Split('\n');

        var statusBarIndex = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            if (!_statusBarTimerPattern.IsMatch(lines[i]))
                continue;

            statusBarIndex = i;
            break;
        }

        if (statusBarIndex >= 0)
            return string.Join('\n', lines.AsSpan(0, statusBarIndex));

        var lastNonEmpty = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            lastNonEmpty = i;
            break;
        }

        var end = lastNonEmpty >= 0 ? lastNonEmpty : lines.Length;
        return string.Join('\n', lines.AsSpan(0, end));
    }

    [GeneratedRegex(@"\d+[hms]\d*[ms]?\s*$", RegexOptions.Compiled)]
    private static partial Regex StatusBarTimerRegex();
}
