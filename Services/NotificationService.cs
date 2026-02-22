using System.Diagnostics;
using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.Services;

public static class NotificationService
{
    private static readonly Dictionary<string, DateTime> _cooldowns = new();

    public static void NotifyWaiting(List<TmuxSession> transitioned, NotificationConfig config)
    {
        if (!config.Enabled || transitioned.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var eligible = new List<TmuxSession>();

        foreach (var session in transitioned)
        {
            if (_cooldowns.TryGetValue(session.Name, out var lastNotified)
                && (now - lastNotified).TotalSeconds < config.CooldownSeconds)
                continue;

            eligible.Add(session);
            _cooldowns[session.Name] = now;
        }

        if (eligible.Count == 0)
            return;

        var message = eligible.Count == 1
            ? FormatSession(eligible[0])
            : $"{eligible.Count} sessions waiting: {string.Join(", ", eligible.Select(FormatSession))}";

        if (config.Bell)
            Console.Write('\a');

        if (config.OscNotify)
            SendOscNotification("CCC", message);

        if (config.DesktopNotify)
            SendDesktopNotification(message);
    }

    public static void Cleanup(IEnumerable<string> liveSessionNames)
    {
        var live = new HashSet<string>(liveSessionNames);
        var stale = _cooldowns.Keys.Where(k => !live.Contains(k)).ToList();
        foreach (var key in stale)
            _cooldowns.Remove(key);
    }

    private static string FormatSession(TmuxSession session) =>
        string.IsNullOrEmpty(session.Description) ? session.Name : $"{session.Name} ({session.Description})";

    private static void SendOscNotification(string title, string body)
    {
        // OSC 777 (iTerm2/rxvt-unicode style)
        Console.Write($"\x1b]777;notify;{title};{body}\x1b\\");
        // OSC 9 (Windows Terminal / ConEmu style)
        Console.Write($"\x1b]9;{body}\x1b\\");
    }

    private static void SendDesktopNotification(string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notify-send",
                ArgumentList = { "CCC", message },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // notify-send not available — silently ignore
        }
    }
}
