using System;
using System.IO;
using System.Windows;
using Newtonsoft.Json;
using Camera_FOV.Models;
using Camera_FOV.UI;


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
                MessageDialog.ShowError(
                    "Couldn’t read the Camera FOV settings",
                    $"Default values are used instead. The settings file is {SettingsFilePath}; it is rewritten the next time settings are saved.",
                    ex);
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
                MessageDialog.ShowError(
                    "Couldn’t save the Camera FOV settings",
                    $"Changes won’t be remembered next time. Check that you can write to {Path.GetDirectoryName(SettingsFilePath)}.",
                    ex);
            }
        }
    }
}
