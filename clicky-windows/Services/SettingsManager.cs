using System.IO;
using System.Text.Json;

namespace ClickyWindows.Services;

public sealed class AppSettings
{
    public string AnthropicApiKey { get; set; } = "";
    public string AssemblyAiApiKey { get; set; } = "";
    public string ElevenLabsApiKey { get; set; } = "";
    public string ElevenLabsVoiceId { get; set; } = "";
}

public static class SettingsManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClickyWindows",
        "settings.json"
    );

    public static bool SettingsExist()
    {
        if (!File.Exists(SettingsPath)) return false;
        var s = Load();
        return !string.IsNullOrWhiteSpace(s.AnthropicApiKey)
            && !string.IsNullOrWhiteSpace(s.AssemblyAiApiKey)
            && !string.IsNullOrWhiteSpace(s.ElevenLabsApiKey)
            && !string.IsNullOrWhiteSpace(s.ElevenLabsVoiceId);
    }

    public static AppSettings Load()
    {
        try
        {
            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
