using System.IO;
using System.Text.Json;
using ScreenAnnotator.Models;

namespace ScreenAnnotator.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenAnnotator");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Load();
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Settings = new AppSettings();
                Save();
                return;
            }

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            Settings = loaded ?? new AppSettings();
            // 补全缺省项
            foreach (var kv in AppSettings.DefaultHotkeys)
            {
                if (!Settings.Hotkeys.ContainsKey(kv.Key))
                    Settings.Hotkeys[kv.Key] = kv.Value;
            }
            // CR-013：清理已废弃项（如 toggle_move_mode）及未知键
            var obsolete = Settings.Hotkeys.Keys
                .Where(k => !AppSettings.DefaultHotkeys.ContainsKey(k))
                .ToList();
            foreach (var k in obsolete)
                Settings.Hotkeys.Remove(k);
            if (obsolete.Count > 0)
                Save();
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        File.WriteAllText(_path, json);
    }
}
