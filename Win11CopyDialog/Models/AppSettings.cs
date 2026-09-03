using System;
using System.IO;
using System.Text.Json;

namespace Win11CopyDialog.Models;

/// <summary>
/// Пользовательские настройки кинетического скроллинга, визуальных эффектов и аудио.
/// Сохраняются в %LOCALAPPDATA%\MotionCommander\settings.json.
/// </summary>
public class AppSettings
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MotionCommander");
    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

    public static AppSettings Instance { get; } = Load();

    // Параметры супер-плавного скроллинга
    public bool SmoothScrollEnabled { get; set; } = true;
    public double ScrollDampingRate { get; set; } = 22.0; // 10..38
    public double ScrollStepSize { get; set; } = 110.0; // 50..220
    public bool ScrollInertiaEnabled { get; set; } = true;
    public bool ScrollHapticEnabled { get; set; } = false;
    public string ScrollPreset { get; set; } = "Balanced"; // "UltraSilk", "Balanced", "Snappy", "Custom"

    // Визуальные эффекты и аудио
    public bool TabAnimationsEnabled { get; set; } = true;
    public bool NeonGlowEnabled { get; set; } = true;
    public bool HapticSoundsEnabled { get; set; } = true;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch { }
        return new AppSettings();
    }
}
