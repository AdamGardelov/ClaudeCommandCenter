using System.Text.Json;
using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.Services;

public static class ConfigService
{
    private static readonly string _configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccc");

    private static readonly string _configPath = Path.Combine(_configDir, "config.json");

    public static string GetConfigPath() => _configPath;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static CccConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            var config = new CccConfig
            {
                Keybindings = KeyBindingService.GetDefaultConfigs(),
            };
            Save(config);
            return config;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<CccConfig>(json, _jsonOptions) ?? new CccConfig();

            if (BackfillKeybindings(config))
                Save(config);

            return config;
        }
        catch
        {
            return new CccConfig();
        }
    }

    public static void SaveDescription(CccConfig config, string sessionName, string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            config.SessionDescriptions.Remove(sessionName);
        else
            config.SessionDescriptions[sessionName] = description;
        Save(config);
    }

    public static void RenameDescription(CccConfig config, string oldName, string newName)
    {
        if (config.SessionDescriptions.Remove(oldName, out var desc))
        {
            config.SessionDescriptions[newName] = desc;
            Save(config);
        }
    }

    public static void RemoveDescription(CccConfig config, string sessionName)
    {
        if (config.SessionDescriptions.Remove(sessionName))
            Save(config);
    }

    public static void SaveColor(CccConfig config, string sessionName, string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            config.SessionColors.Remove(sessionName);
        else
            config.SessionColors[sessionName] = color;
        Save(config);
    }

    public static void RenameColor(CccConfig config, string oldName, string newName)
    {
        if (config.SessionColors.Remove(oldName, out var color))
        {
            config.SessionColors[newName] = color;
            Save(config);
        }
    }

    public static void RemoveColor(CccConfig config, string sessionName)
    {
        if (config.SessionColors.Remove(sessionName))
            Save(config);
    }

    private static bool BackfillKeybindings(CccConfig config)
    {
        var defaults = KeyBindingService.GetDefaultConfigs();
        var validIds = KeyBindingService.GetValidActionIds();
        var changed = false;

        // Add missing actions
        foreach (var (id, def) in defaults)
            if (config.Keybindings.TryAdd(id, def))
                changed = true;

        // Remove stale actions
        var staleKeys = config.Keybindings.Keys
            .Where(id => !validIds.Contains(id))
            .ToList();

        foreach (var key in staleKeys)
        {
            config.Keybindings.Remove(key);
            changed = true;
        }

        return changed;
    }

    private static void Save(CccConfig config)
    {
        Directory.CreateDirectory(_configDir);
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        File.WriteAllText(_configPath, json);
    }

    public static void SaveGroup(CccConfig config, SessionGroup group)
    {
        config.Groups[group.Name] = group;
        Save(config);
    }

    public static void RemoveGroup(CccConfig config, string groupName)
    {
        if (config.Groups.Remove(groupName))
            Save(config);
    }

    public static void RemoveSessionFromGroup(CccConfig config, string groupName, string sessionName)
    {
        if (!config.Groups.TryGetValue(groupName, out var group))
            return;

        group.Sessions.Remove(sessionName);

        if (group.Sessions.Count == 0)
            config.Groups.Remove(groupName);

        Save(config);
    }

    public static string ExpandPath(string path)
    {
        if (path.StartsWith('~'))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[1..].TrimStart('/'));

        return path;
    }
}
