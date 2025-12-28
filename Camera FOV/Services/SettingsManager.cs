using System;
using System.IO;
using System.Windows;
using Newtonsoft.Json;
using Camera_FOV.Models;


namespace Camera_FOV.Services
{
    public static class SettingsManager
    {
        private const string SettingsFilePath = @"C:\ProgramData\RK Tools\Camera FOV\settings.json";

        public static PluginSettings Settings { get; private set; }

        static SettingsManager()
        {
            LoadSettings();
        }

        public static void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                    Settings = JsonConvert.DeserializeObject<PluginSettings>(json, settings) ?? new PluginSettings();
                }
                else
                {
                    // Initialize with default settings if file does not exist
                    Settings = new PluginSettings();
                    SaveSettings();
                }
            }
            catch (Exception ex)
            {
                // Fallback to default settings in case of an error
                Settings = new PluginSettings();
                MessageBox.Show($"Failed to load settings. Default values will be used.\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static void SaveSettings()
        {
            try
            {
                string json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath));
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings.\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
