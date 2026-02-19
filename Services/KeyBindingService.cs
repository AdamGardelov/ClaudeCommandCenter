using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.Services;

public static class KeyBindingService
{
    private static readonly List<KeyBinding> Defaults =
    [
        new() { ActionId = "navigate-up", Key = "k", Label = null, CanDisable = false, StatusBarOrder = -1 },
        new() { ActionId = "navigate-down", Key = "j", Label = null, CanDisable = false, StatusBarOrder = -1 },
        new() { ActionId = "approve", Key = "Y", Label = "approve", CanDisable = true, StatusBarOrder = 1 },
        new() { ActionId = "reject", Key = "N", Label = "reject", CanDisable = true, StatusBarOrder = 2 },
        new() { ActionId = "send-text", Key = "S", Label = "send", CanDisable = true, StatusBarOrder = 3 },
        new() { ActionId = "attach", Key = "Enter", Label = "attach", CanDisable = true, StatusBarOrder = 5 },
        new() { ActionId = "new-session", Key = "n", Label = "new", CanDisable = true, StatusBarOrder = 6 },
        new() { ActionId = "open-folder", Key = "f", Label = "folder", CanDisable = true, StatusBarOrder = 7 },
        new() { ActionId = "open-ide", Key = "i", Label = "ide", CanDisable = true, StatusBarOrder = 8 },
        new() { ActionId = "open-config", Key = "c", Label = "config", CanDisable = true, StatusBarOrder = 9 },
        new() { ActionId = "delete-session", Key = "d", Label = "del", CanDisable = true, StatusBarOrder = 10 },
        new() { ActionId = "edit-session", Key = "e", Label = "edit", CanDisable = true, StatusBarOrder = 11 },
        new() { ActionId = "toggle-grid", Key = "G", Label = "grid", CanDisable = true, StatusBarOrder = 12 },
        new() { ActionId = "refresh", Key = "r", Label = null, CanDisable = true, StatusBarOrder = -1 },
        new() { ActionId = "quit", Key = "q", Label = "quit", CanDisable = false, StatusBarOrder = 99 },
    ];

    public static List<KeyBinding> Resolve(CccConfig config)
    {
        var overrides = config.Keybindings;
        var result = new List<KeyBinding>(Defaults.Count);

        foreach (var def in Defaults)
        {
            if (!overrides.TryGetValue(def.ActionId, out var ovr))
            {
                result.Add(def);
                continue;
            }

            var enabled = !def.CanDisable || (ovr.Enabled ?? def.Enabled);

            // If label is explicitly set in override, use it (even if null/empty to hide).
            // Otherwise keep the default.
            var label = ovr.Label ?? def.Label;
            var order = string.IsNullOrEmpty(label) ? -1 : def.StatusBarOrder;

            result.Add(new KeyBinding
            {
                ActionId = def.ActionId,
                Key = ovr.Key ?? def.Key,
                Enabled = enabled,
                Label = label,
                CanDisable = def.CanDisable,
                StatusBarOrder = order,
            });
        }

        return result;
    }

    public static Dictionary<string, KeyBindingConfig> GetDefaultConfigs()
    {
        var result = new Dictionary<string, KeyBindingConfig>();
        foreach (var def in Defaults)
        {
            result[def.ActionId] = new KeyBindingConfig
            {
                Key = def.Key,
                Enabled = def.CanDisable ? def.Enabled : null,
                Label = def.Label,
            };
        }

        return result;
    }

    public static HashSet<string> GetValidActionIds()
    {
        return Defaults.Select(d => d.ActionId).ToHashSet();
    }

    public static Dictionary<string, string> BuildKeyMap(List<KeyBinding> bindings)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in bindings)
            if (b.Enabled)
                map[b.Key] = b.ActionId;

        return map;
    }
}