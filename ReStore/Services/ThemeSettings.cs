using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Wpf.Ui.Appearance;

namespace ReStore.Services
{
    public enum ThemePreference
    {
        System,
        Light,
        Dark
    }

    public class ThemeSettings
    {
        private const string SETTINGS_FILE = "theme.json";
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReStore",
            SETTINGS_FILE);

        public ThemePreference Preference { get; set; } = ThemePreference.System;

        public static ThemeSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var s = JsonSerializer.Deserialize<ThemeSettings>(json);
                    if (s != null) return s;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to load theme settings from {SettingsPath}: {ex.Message}");
            }
            return new ThemeSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to save theme settings to {SettingsPath}: {ex.Message}");
            }
        }

        public void Apply()
        {
            switch (Preference)
            {
                case ThemePreference.Light:
                    ApplicationThemeManager.Apply(ApplicationTheme.Light);
                    break;
                case ThemePreference.Dark:
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                    break;
                default:
                    ApplicationThemeManager.ApplySystemTheme(true);
                    break;
            }
        }
    }
}
