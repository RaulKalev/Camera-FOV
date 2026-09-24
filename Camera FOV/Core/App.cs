using Autodesk.Revit.UI;
using ricaun.Revit.UI;
using ricaun.Revit.UI.Utils;
using System;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Newtonsoft.Json;
using Camera_FOV.Commands;

namespace Camera_FOV.Core
{
    [AppLoader]
    public class App : IExternalApplication
    {
        private RibbonPanel ribbonPanel;
        private PushButton cameraFovButton;

        // Ribbon icons for Revit's light and dark UI themes
        private const string LightIcon = "Assets/CameraFOV-Light.tiff";
        private const string DarkIcon = "Assets/CameraFOV-Dark.tiff";

        public Result OnStartup(UIControlledApplication application)
        {
            // Define the custom tab name
            string tabName = "RK Tools";

            // Try to create the custom tab (avoid exception if it already exists)
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch
            {
                // Tab already exists; continue without throwing an error
            }

            // Create Ribbon Panel on the custom tab
            ribbonPanel = application.CreateOrSelectPanel(tabName, "EN");

            // Create PushButton with embedded resource
            cameraFovButton = ribbonPanel.CreatePushButton<ShowCameraFOVCommand>()
                .SetLargeImage(RibbonThemeUtils.IsDark ? DarkIcon : LightIcon)
                .SetText("Camera\nFOV")
                .SetToolTip("Draw FOV for cameras.")
                .SetContextualHelp("https://raulkalev.github.io/rktools/");

            // Follow Revit's UI theme when the user switches it (Options > User Interface)
            RibbonThemeUtils.ThemeChanged += OnRevitThemeChanged;

            return Result.Succeeded;
        }

        private void OnRevitThemeChanged(object sender, ThemeChangedEventArgs e)
        {
            cameraFovButton?.SetLargeImage(e.IsDark ? DarkIcon : LightIcon);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            RibbonThemeUtils.ThemeChanged -= OnRevitThemeChanged;
            ribbonPanel?.Remove();
            return Result.Succeeded;
        }

    }
}
