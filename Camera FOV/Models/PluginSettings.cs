using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Camera_FOV.Models
{
    /// <summary>How pixel density at a distance is calculated (issue #11).</summary>
    public enum PixelDensityFormula
    {
        // EVS-EN IEC 62676-4:2026, Figure 4: horizontal pixels over the flat scene width 2 × d × tan(FOV / 2)
        Standard,
        // Horizontal pixels over the arc length of the view 2π × d × FOV / 360, matching Axis Site Designer (issue #2)
        Legacy
    }

    public class PluginSettings
    {
        public double FOVAngle { get; set; } = 93.0; // Default FOV angle
        public int HorizontalResolution { get; set; } = 1920; // Default resolution
        public double Resolution { get; set; } = 0.5; // Default draw resolution
        public bool IsDarkMode { get; set; } = true; // Default to dark mode
        public List<int> Resolutions { get; set; } = new List<int> { 1920, 1280, 800 }; // Default resolution list
        public int LastSelectedResolution { get; set; } = 1920; // Default selected resolution

        // Configurable Parameter Names
        public string ParameterName_UserRotation { get; set; } = "Pööra Kaamerat";
        public string ParameterName_FOVOverride { get; set; } = "Kaamera nurk";
        public string ParameterName_StandardFOV { get; set; } = "Vaatenurk";
        public string ParameterName_Resolution { get; set; } = "Horisontaalne Resolutsioon";

        // Field-of-view dimension placed on the innermost coverage region. An empty type name
        // turns the dimension off; a name missing from a project falls back to another angular type.
        public string FovDimensionTypeName { get; set; } = "Kaamera nurk";
        public double FovDimensionDistanceMeters { get; set; } = 2.0;

        // Used by drawing, the point check and the audit. Coverage records the formula it was drawn
        // with, so switching marks coverage drawn with the other one as out of date.
        [JsonConverter(typeof(StringEnumConverter))]
        public PixelDensityFormula PixelDensityFormula { get; set; } = PixelDensityFormula.Standard;

        // Flip the camera family when drawing if its 2D symbol ends up pointing away from the coverage
        public bool AutoFlipCameraSymbol { get; set; } = true;
    }
}
