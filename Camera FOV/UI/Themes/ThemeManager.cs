using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using Camera_FOV.Services;

namespace Camera_FOV
{
    /// <summary>
    /// Applies the dark or light palette to the plugin's windows. The appearance is stored in
    /// <see cref="SettingsManager"/> (settings.json) and every registered window follows a change live,
    /// because all styles reference the palette through DynamicResource.
    /// </summary>
    public static class ThemeManager
    {
        private static readonly List<FrameworkElement> Targets = new List<FrameworkElement>();
        private static readonly Dictionary<bool, ResourceDictionary> Palettes = new Dictionary<bool, ResourceDictionary>();

        public static bool IsDarkMode => SettingsManager.Settings?.IsDarkMode ?? true;

        /// <summary>"Show animations in Windows" is off: motion falls back to short cross-fades.</summary>
        public static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

        /// <summary>Applies the current palette to the element and keeps it in sync until it closes.</summary>
        public static void Register(FrameworkElement root)
        {
            if (root == null) return;

            Apply(root);
            if (!Targets.Contains(root))
                Targets.Add(root);

            if (root is Window window)
                window.Closed += (s, e) => Targets.Remove(root);
        }

        public static void SetDarkMode(bool isDarkMode)
        {
            if (SettingsManager.Settings == null || SettingsManager.Settings.IsDarkMode == isDarkMode) return;

            SettingsManager.Settings.IsDarkMode = isDarkMode;
            SettingsManager.SaveSettings();

            foreach (FrameworkElement target in Targets.ToArray())
                Apply(target);
        }

        // Replaces the palette dictionary in place, so the styles merged after it keep their order.
        private static void Apply(FrameworkElement root)
        {
            var dictionaries = root.Resources.MergedDictionaries;
            ResourceDictionary palette = GetPalette(IsDarkMode);

            for (int i = 0; i < dictionaries.Count; i++)
            {
                if (IsPalette(dictionaries[i]))
                {
                    if (!ReferenceEquals(dictionaries[i], palette))
                        dictionaries[i] = palette;
                    return;
                }
            }

            dictionaries.Insert(0, palette);
        }

        private static bool IsPalette(ResourceDictionary dictionary)
        {
            string source = dictionary.Source?.OriginalString;
            return source != null
                && (source.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase));
        }

        private static ResourceDictionary GetPalette(bool isDarkMode)
        {
            if (!Palettes.TryGetValue(isDarkMode, out ResourceDictionary palette))
            {
                string assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
                string file = isDarkMode ? "DarkTheme.xaml" : "LightTheme.xaml";
                palette = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/{assemblyName};component/UI/Themes/{file}", UriKind.Absolute)
                };
                Palettes[isDarkMode] = palette;
            }
            return palette;
        }
    }
}
