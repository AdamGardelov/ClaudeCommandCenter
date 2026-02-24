using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;

namespace ClaudeCommandCenter.UI;

public static class SettingsDefinition
{
    public static List<SettingsCategory> GetCategories() =>
    [
        new()
        {
            Name = "General",
            Icon = "⚙",
            BuildItems = BuildGeneralItems,
        },
        new()
        {
            Name = "Keybindings",
            Icon = "⌨",
            BuildItems = BuildKeybindingItems,
        },
        new()
        {
            Name = "Notifications",
            Icon = "🔔",
            BuildItems = BuildNotificationItems,
        },
        new()
        {
            Name = "Favorites",
            Icon = "★",
            BuildItems = BuildFavoriteItems,
        },
        new()
        {
            Name = "Advanced",
            Icon = "⚡",
            BuildItems = BuildAdvancedItems,
        },
    ];

    private static List<SettingsItem> BuildGeneralItems(CccConfig config) =>
    [
        new()
        {
            Label = "IDE Command",
            Type = SettingsItemType.Text,
            GetValue = c => c.IdeCommand,
            SetValue = (c, v) => c.IdeCommand = v,
        },
        new()
        {
            Label = "Worktree Base Path",
            Type = SettingsItemType.Text,
            GetValue = c => c.WorktreeBasePath,
            SetValue = (c, v) => c.WorktreeBasePath = v,
        },
    ];

    private static List<SettingsItem> BuildKeybindingItems(CccConfig config)
    {
        var defaults = KeyBindingService.GetDefaultConfigs();
        var items = new List<SettingsItem>();

        foreach (var (actionId, kbConfig) in config.Keybindings)
        {
            if (defaults.TryGetValue(actionId, out var def) && def.Enabled == null)
                continue;

            items.Add(new SettingsItem
            {
                Label = kbConfig.Label ?? actionId,
                Type = SettingsItemType.Toggle,
                ActionId = actionId,
                GetValue = c => (c.Keybindings.TryGetValue(actionId, out var kb)
                    ? kb.Enabled ?? true : true)
                    ? "ON" : "OFF",
                SetValue = (c, _) =>
                {
                    if (c.Keybindings.TryGetValue(actionId, out var kb))
                        kb.Enabled = !(kb.Enabled ?? true);
                },
            });
        }

        return items;
    }

    private static List<SettingsItem> BuildNotificationItems(CccConfig config) =>
    [
        new()
        {
            Label = "Notifications",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.Notifications.Enabled ? "ON" : "OFF",
            SetValue = (c, _) => c.Notifications.Enabled = !c.Notifications.Enabled,
        },
        new()
        {
            Label = "Bell",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.Notifications.Bell ? "ON" : "OFF",
            SetValue = (c, _) => c.Notifications.Bell = !c.Notifications.Bell,
        },
        new()
        {
            Label = "OSC Notify",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.Notifications.OscNotify ? "ON" : "OFF",
            SetValue = (c, _) => c.Notifications.OscNotify = !c.Notifications.OscNotify,
        },
        new()
        {
            Label = "Desktop Notify",
            Type = SettingsItemType.Toggle,
            GetValue = c => c.Notifications.DesktopNotify ? "ON" : "OFF",
            SetValue = (c, _) => c.Notifications.DesktopNotify = !c.Notifications.DesktopNotify,
        },
        new()
        {
            Label = "Cooldown (seconds)",
            Type = SettingsItemType.Number,
            GetValue = c => c.Notifications.CooldownSeconds.ToString(),
            SetValue = (c, v) =>
            {
                if (int.TryParse(v, out var seconds) && seconds >= 0)
                    c.Notifications.CooldownSeconds = seconds;
            },
        },
    ];

    private static List<SettingsItem> BuildFavoriteItems(CccConfig config)
    {
        var items = new List<SettingsItem>();

        for (var i = 0; i < config.FavoriteFolders.Count; i++)
        {
            var index = i;
            items.Add(new SettingsItem
            {
                Label = config.FavoriteFolders[index].Name,
                Type = SettingsItemType.Text,
                GetValue = c => index < c.FavoriteFolders.Count
                    ? c.FavoriteFolders[index].Path : "",
                SetValue = (c, v) =>
                {
                    if (index < c.FavoriteFolders.Count)
                        c.FavoriteFolders[index].Path = v;
                },
            });
        }

        items.Add(new SettingsItem
        {
            Label = "+ Add Favorite",
            Type = SettingsItemType.Action,
        });

        return items;
    }

    private static List<SettingsItem> BuildAdvancedItems(CccConfig config) =>
    [
        new()
        {
            Label = "Open Config File",
            Type = SettingsItemType.Action,
        },
        new()
        {
            Label = "Reset Keybindings to Defaults",
            Type = SettingsItemType.Action,
        },
    ];
}
