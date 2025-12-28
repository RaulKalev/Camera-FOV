using Autodesk.Revit.UI;
using ricaun.Revit.UI;
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
            ribbonPanel.CreatePushButton<ShowCameraFOVCommand>()
                .SetLargeImage("Assets/Camera.tiff")
                .SetText("Camera\nFOV")
                .SetToolTip("Draw FOV for cameras.")
                .SetContextualHelp("https://raulkalev.github.io/rktools/");

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            // Trigger the update check
            ribbonPanel?.Remove();
            return Result.Succeeded;
        }

    }
}
