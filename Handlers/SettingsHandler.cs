using System.Diagnostics;
using ClaudeCommandCenter.Enums;
using ClaudeCommandCenter.Models;
using ClaudeCommandCenter.Services;
using ClaudeCommandCenter.UI;

namespace ClaudeCommandCenter.Handlers;

public class SettingsHandler(
    AppState state,
    CccConfig config,
    Action render,
    Action refreshKeybindings)
{
    public void HandleKey(ConsoleKeyInfo key)
    {
        var categories = SettingsDefinition.GetCategories();
        var currentCategory = categories[Math.Clamp(state.SettingsCategory, 0, categories.Count - 1)];
        var items = currentCategory.BuildItems(config);

        if (state.IsSettingsRebinding)
        {
            HandleRebindKey(key, items);
            return;
        }

        if (state.IsSettingsEditing)
        {
            HandleEditKey(key, items);
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                state.LeaveSettings();
                return;

            case ConsoleKey.Tab:
                state.SettingsFocusRight = !state.SettingsFocusRight;
                state.SettingsItemCursor = 0;
                return;

            case ConsoleKey.UpArrow:
                if (state.SettingsFocusRight)
                    state.SettingsItemCursor = Math.Max(0, state.SettingsItemCursor - 1);
                else
                {
                    state.SettingsCategory = Math.Max(0, state.SettingsCategory - 1);
                    state.SettingsItemCursor = 0;
                }

                return;

            case ConsoleKey.DownArrow:
                if (state.SettingsFocusRight)
                    state.SettingsItemCursor = Math.Min(items.Count - 1, state.SettingsItemCursor + 1);
                else
                {
                    state.SettingsCategory = Math.Min(categories.Count - 1, state.SettingsCategory + 1);
                    state.SettingsItemCursor = 0;
                }

                return;

            case ConsoleKey.Enter:
            case ConsoleKey.Spacebar:
                if (state.SettingsFocusRight && state.SettingsItemCursor < items.Count)
                    ActivateItem(items[state.SettingsItemCursor]);
                else if (!state.SettingsFocusRight)
                {
                    state.SettingsFocusRight = true;
                    state.SettingsItemCursor = 0;
                }

                return;
        }

        // j/k navigation (character-based)
        switch (key.KeyChar)
        {
            case 'k':
                if (state.SettingsFocusRight)
                    state.SettingsItemCursor = Math.Max(0, state.SettingsItemCursor - 1);
                else
                {
                    state.SettingsCategory = Math.Max(0, state.SettingsCategory - 1);
                    state.SettingsItemCursor = 0;
                }

                return;
            case 'j':
                if (state.SettingsFocusRight)
                    state.SettingsItemCursor = Math.Min(items.Count - 1, state.SettingsItemCursor + 1);
                else
                {
                    state.SettingsCategory = Math.Min(categories.Count - 1, state.SettingsCategory + 1);
                    state.SettingsItemCursor = 0;
                }

                return;
        }

        // Keybinding rebind shortcut
        if (state.SettingsFocusRight && currentCategory.Name == "Keybindings" && key.KeyChar == 'e')
        {
            if (state.SettingsItemCursor < items.Count && items[state.SettingsItemCursor].ActionId != null)
            {
                state.IsSettingsRebinding = true;
                state.SettingsEditBuffer = "";
            }

            return;
        }

        // Favorites shortcuts
        if (state.SettingsFocusRight && currentCategory.Name == "Favorites")
        {
            switch (key.KeyChar)
            {
                case 'n':
                    AddFavorite();
                    return;
                case 'd':
                    DeleteFavorite();
                    return;
            }
        }

        // 'o' opens config file from anywhere in settings
        if (key.KeyChar == 'o')
            OpenConfig();
    }

    private void HandleEditKey(ConsoleKeyInfo key, List<SettingsItem> items)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                state.IsSettingsEditing = false;
                state.SettingsEditBuffer = "";
                state.SetStatus("Cancelled");
                return;

            case ConsoleKey.Enter:
                var item = items[state.SettingsItemCursor];
                item.SetValue?.Invoke(config, state.SettingsEditBuffer);
                ConfigService.SaveConfig(config);
                state.IsSettingsEditing = false;
                state.SettingsEditBuffer = "";
                refreshKeybindings();
                state.SetStatus("Saved");
                return;

            case ConsoleKey.Backspace:
                if (state.SettingsEditBuffer.Length > 0)
                    state.SettingsEditBuffer = state.SettingsEditBuffer[..^1];
                return;

            default:
                if (key.KeyChar >= ' ' && state.SettingsEditBuffer.Length < 250)
                    state.SettingsEditBuffer += key.KeyChar;
                return;
        }
    }

    private void HandleRebindKey(ConsoleKeyInfo key, List<SettingsItem> items)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            state.IsSettingsRebinding = false;
            state.SettingsEditBuffer = "";
            state.SetStatus("Cancelled");
            return;
        }

        // Determine the key string from the press
        var newKey = key.Key switch
        {
            ConsoleKey.Enter => "Enter",
            ConsoleKey.Spacebar => "Space",
            ConsoleKey.Tab => "Tab",
            _ when key.KeyChar >= '!' && key.KeyChar <= '~' => key.KeyChar.ToString(),
            _ => null,
        };

        if (newKey == null)
            return;

        var item = items[state.SettingsItemCursor];
        var actionId = item.ActionId!;

        // Check for conflicts with other enabled bindings (scoped: diff vs non-diff)
        var bindings = KeyBindingService.Resolve(config);
        var isDiffAction = actionId.StartsWith("diff-");
        var conflict = bindings.FirstOrDefault(b =>
            b.Enabled && b.Key == newKey && b.ActionId != actionId
            && b.ActionId.StartsWith("diff-") == isDiffAction);

        if (conflict != null)
        {
            var conflictLabel = config.Keybindings.TryGetValue(conflict.ActionId, out var ckb)
                ? ckb.Label ?? conflict.ActionId
                : conflict.ActionId;
            state.SettingsEditBuffer = $"'{newKey}' already bound to {conflictLabel}";
            return;
        }

        // Apply the new key
        if (config.Keybindings.TryGetValue(actionId, out var kb))
            kb.Key = newKey;

        ConfigService.SaveConfig(config);
        refreshKeybindings();
        state.IsSettingsRebinding = false;
        state.SettingsEditBuffer = "";
        state.SetStatus($"Bound to '{newKey}'");
    }

    private void ActivateItem(SettingsItem item)
    {
        switch (item.Type)
        {
            case SettingsItemType.Toggle:
                item.SetValue?.Invoke(config, "");
                ConfigService.SaveConfig(config);
                refreshKeybindings();
                break;

            case SettingsItemType.Text:
            case SettingsItemType.Number:
                state.IsSettingsEditing = true;
                state.SettingsEditBuffer = item.GetValue?.Invoke(config) ?? "";
                break;

            case SettingsItemType.Action:
                HandleAction(item.Label);
                break;
        }
    }

    private void HandleAction(string label)
    {
        switch (label)
        {
            case "Open Config File":
                OpenConfig();
                break;
            case "Reset Keybindings to Defaults":
                state.SetStatus("Reset all keybindings? (y/n)");
                render();
                var confirm = Console.ReadKey(true);
                if (confirm.Key == ConsoleKey.Y)
                {
                    config.Keybindings = KeyBindingService.GetDefaultConfigs();
                    ConfigService.SaveConfig(config);
                    refreshKeybindings();
                    state.SetStatus("Keybindings reset to defaults");
                }
                else
                {
                    state.SetStatus("Cancelled");
                }

                break;
            case "+ Add Favorite":
                AddFavorite();
                break;
        }
    }

    private void AddFavorite() => FlowHelper.RunFlow("Add Favorite", () =>
    {
        FlowHelper.PrintStep(1, 2, "Name");
        var name = FlowHelper.RequireText("[grey70]Name:[/]");

        FlowHelper.PrintStep(2, 2, "Path");
        var path = FlowHelper.RequireText("[grey70]Path:[/]");

        config.FavoriteFolders.Add(new FavoriteFolder
        {
            Name = name,
            Path = path
        });
        ConfigService.SaveConfig(config);
        state.SetStatus($"Added '{name}'");
    }, state);

    private void DeleteFavorite()
    {
        if (state.SettingsItemCursor >= config.FavoriteFolders.Count)
            return;

        var fav = config.FavoriteFolders[state.SettingsItemCursor];
        state.SetStatus($"Delete '{fav.Name}'? (y/n)");
        render();

        var confirm = Console.ReadKey(true);
        if (confirm.Key == ConsoleKey.Y)
        {
            config.FavoriteFolders.RemoveAt(state.SettingsItemCursor);
            ConfigService.SaveConfig(config);
            state.SettingsItemCursor = Math.Min(state.SettingsItemCursor,
                Math.Max(0, config.FavoriteFolders.Count - 1));
            state.SetStatus("Deleted");
        }
        else
        {
            state.SetStatus("Cancelled");
        }
    }

    private void OpenConfig()
    {
        var configPath = ConfigService.GetConfigPath();

        if (string.IsNullOrWhiteSpace(config.IdeCommand))
        {
            // No IDE configured — fall back to platform default
            try
            {
                var opener = OperatingSystem.IsMacOS() ? "open" :
                    OperatingSystem.IsWindows() ? "explorer" : "xdg-open";
                Process.Start(new ProcessStartInfo
                {
                    FileName = opener,
                    ArgumentList =
                    {
                        configPath
                    },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                state.SetStatus($"Opened config with {opener}");
            }
            catch
            {
                state.SetStatus($"Config at: {configPath}");
            }

            return;
        }

        state.SetStatus(FlowHelper.LaunchWithIde(config.IdeCommand, configPath)
            ? $"Opened config in {config.IdeCommand}"
            : $"Failed to run '{config.IdeCommand}'");
    }
}
