using ClaudeCommandCenter;
using ClaudeCommandCenter.Services;

try
{
    var app = new App();
    app.Run();
}
catch (Exception ex)
{
    CrashLog.Write(ex);
    Console.CursorVisible = true;
    Console.Write("\e[?1049l"); // Leave alternate screen if we're in it
    Console.Error.WriteLine($"Fatal error — logged to ~/.ccc/crash.log");
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
}
