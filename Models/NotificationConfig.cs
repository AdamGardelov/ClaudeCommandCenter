namespace ClaudeCommandCenter.Models;

public class NotificationConfig
{
    public bool Enabled { get; set; } = true;
    public bool Bell { get; set; } = true;
    public bool OscNotify { get; set; } = false;
    public bool DesktopNotify { get; set; } = true;
    public int CooldownSeconds { get; set; } = 10;
}
