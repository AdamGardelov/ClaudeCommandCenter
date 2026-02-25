using Microsoft.Win32.SafeHandles;

namespace ClaudeCommandCenter.Services.ConPty;

/// <summary>
/// Internal state for a single ConPTY-managed session.
/// Not exposed to the UI layer — the backend maps these to Session models.
/// </summary>
internal class ConPtySession : IDisposable
{
    public required string Name { get; set; }
    public required string WorkingDirectory { get; set; }
    public required nint PseudoConsole { get; set; }
    public required nint ProcessHandle { get; set; }
    public required int ProcessId { get; set; }
    public required SafeFileHandle InputWriteHandle { get; set; }
    public required StreamWriter Input { get; set; }
    public required Thread ReaderThread { get; set; }
    public required RingBuffer OutputBuffer { get; set; }
    public required CancellationTokenSource Cts { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public short Width { get; set; } = 120;
    public short Height { get; set; } = 40;

    private bool _disposed;

    public bool IsAlive
    {
        get
        {
            if (ProcessHandle == nint.Zero)
                return false;
            NativeMethods.GetExitCodeProcess(ProcessHandle, out var exitCode);
            return exitCode == NativeMethods.StillActive;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Cts.Cancel();

        // Close pseudoconsole first — this signals the process to terminate
        if (PseudoConsole != nint.Zero)
        {
            NativeMethods.ClosePseudoConsole(PseudoConsole);
            PseudoConsole = nint.Zero;
        }

        // Wait briefly for process to exit, then force terminate
        if (ProcessHandle != nint.Zero)
        {
            if (NativeMethods.WaitForSingleObject(ProcessHandle, 1000) != 0)
                NativeMethods.TerminateProcess(ProcessHandle, 1);
            NativeMethods.CloseHandle(ProcessHandle);
            ProcessHandle = nint.Zero;
        }

        Input.Dispose();
        InputWriteHandle.Dispose();

        // Reader thread will exit on its own when the pipe closes
        if (ReaderThread.IsAlive)
            ReaderThread.Join(2000);

        Cts.Dispose();
    }
}
