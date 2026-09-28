using System.Text.Json;

namespace Hollowbound.Simulation;

public sealed class UISettingsData
{
    public float UiScale { get; set; } = 1f;
    public bool OnboardingCompleted { get; set; }
}

public static class UISettingsStore
{
    public static UISettingsData Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new UISettingsData();

            var settings = JsonSerializer.Deserialize<UISettingsData>(File.ReadAllText(path)) ?? new UISettingsData();
            settings.UiScale = float.IsFinite(settings.UiScale) && settings.UiScale > 0f
                ? UITheme.ClampScale(settings.UiScale)
                : 1f;
            return settings;
        }
        catch
        {
            return new UISettingsData();
        }
    }

    public static void Save(string path, UISettingsData settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
