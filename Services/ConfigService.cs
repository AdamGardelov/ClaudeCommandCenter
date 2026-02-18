using System.Text.Json;
using ClaudeCommandCenter.Models;

namespace ClaudeCommandCenter.Services;

public static class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccc");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static CccConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var config = new CccConfig();
            Save(config);
            return config;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<CccConfig>(json, JsonOptions) ?? new CccConfig();
        }
        catch
        {
            return new CccConfig();
        }
    }

    private static void Save(CccConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
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
