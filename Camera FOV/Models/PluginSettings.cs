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

        // Camera height and tilt (issue #16). The mounting height comes from the named instance
        // parameter, or, when empty, from the camera's height above its level. Tilt is degrees
        // below horizontal. The target is what is looked at, e.g. a face at 1.6 m.
        public bool UseMountingGeometry { get; set; } = true;
        public string ParameterName_MountingHeight { get; set; } = string.Empty;
        public string ParameterName_Tilt { get; set; } = "Tilt Angle";
        public double TargetHeightMeters { get; set; } = 1.6;
        public double MaxFaceViewAngleDegrees { get; set; } = 30.0;

        // What each camera is for (issue #14) and what each room needs (issue #13): text parameters
        public string ParameterName_IntendedCategory { get; set; } = "Observation category";
        public string ParameterName_RiskGrade { get; set; } = "Risk grade";
        public string ParameterName_RequiredCategory { get; set; } = "Required category";

        // Moving objects crossing a view (issue #18)
        public string ParameterName_FrameRate { get; set; } = "Frame rate";
        public double WalkingSpeedKmh { get; set; } = 5.0;
        public double RunningSpeedKmh { get; set; } = 15.0;
        public double VehicleSpeedKmh { get; set; } = 50.0;
        public int MinFramesPerCrossing { get; set; } = 1;

        // Folder of the shared camera type library (a network drive or synced folder). Empty keeps the
        // library on this PC only.
        public string CameraTypesFolder { get; set; } = string.Empty;

        // Flip the camera family when drawing if its 2D symbol ends up pointing away from the coverage
        public bool AutoFlipCameraSymbol { get; set; } = true;
    }
}
