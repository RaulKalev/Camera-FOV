using System;
using System.Collections.Generic;

namespace Camera_FOV.Models
{
    public class PluginSettings
    {
        public double FOVAngle { get; set; } = 93.0; // Default FOV angle
        public int HorizontalResolution { get; set; } = 1920; // Default resolution
        public double Resolution { get; set; } = 0.5; // Default draw resolution
        public bool IsDarkMode { get; set; } = true; // Default to dark mode
        public List<int> Resolutions { get; set; } = new List<int> { 1920, 1280, 800 }; // Default resolution list
        public int LastSelectedResolution { get; set; } = 1920; // Default selected resolution
    }
}
