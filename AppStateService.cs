using System.IO;
using System.Text.Json;

namespace MonitorApp;

public sealed class AppStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public AppStateService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "WindowHome");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "rules.json");
        var oldSettingsPath = Path.Combine(localAppData, "MonitorApp", "rules.json");
        if (!File.Exists(_settingsPath) && File.Exists(oldSettingsPath))
        {
            File.Copy(oldSettingsPath, _settingsPath);
        }
    }

    public string SettingsPath => _settingsPath;

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}
