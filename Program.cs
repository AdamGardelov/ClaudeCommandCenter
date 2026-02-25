using System.Text;
using ClaudeCommandCenter;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.Services.ConPty;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

try
{
    var mobile = args.Contains("-m") || args.Contains("--mobile");
    ISessionBackend backend = OperatingSystem.IsWindows()
        ? new ConPtyBackend()
        : new TmuxBackend();
    var app = new App(backend, mobile);
    app.Run();
}
catch (Exception ex)
{
    CrashLog.Write(ex);
    Console.CursorVisible = true;
    Console.Write("\e[?1049l"); // Leave alternate screen if we're in it
    Console.Error.WriteLine("Fatal error — logged to ~/.ccc/crash.log");
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
}
