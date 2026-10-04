using System.IO;
using System.Text.Json;

namespace WhisperSelectGenSubs.Configuration;

public static class AppSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WhisperSelectGenSubs",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return AppSettings.Empty;

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? AppSettings.Empty;
        }
        catch
        {
            return AppSettings.Empty;
        }
    }

    public static void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
