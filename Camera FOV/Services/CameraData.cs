using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>A DORI level: its name, the pixel density it needs and its filled region type.</summary>
    public sealed class DoriLevel
    {
        public int Index { get; }
        public string Name { get; }
        public double PixelsPerMeter { get; }
        public string RegionTypeName { get; }

        public DoriLevel(int index, string name, double pixelsPerMeter, string regionTypeName)
        {
            Index = index;
            Name = name;
            PixelsPerMeter = pixelsPerMeter;
            RegionTypeName = regionTypeName;
        }
    }

    /// <summary>
    /// Camera values and pixel density shared by the point check (issue #7) and the coverage audit
    /// (issue #6). The density formula is the inverse of the window's DORI distance formula, which is
    /// validated against Axis Site Designer (issue #2), so a point at a region's edge gets that
    /// region's threshold density.
    /// </summary>
    public static class CameraData
    {
        public static readonly IReadOnlyList<DoriLevel> Levels = new List<DoriLevel>
        {
            new DoriLevel(0, "Detection", 25, "dori_25px"),
            new DoriLevel(1, "Observation", 63, "dori_63px"),
            new DoriLevel(2, "Recognition", 125, "dori_125px"),
            new DoriLevel(3, "Identification", 250, "dori_250px")
        };

        /// <summary>
        /// Horizontal pixels per metre at a distance: resolution × 360 / (2π × FOV × distance).
        /// The window's DORI distance is the same relation solved for distance.
        /// </summary>
        public static double PixelsPerMeter(int resolution, double fovDegrees, double distanceMeters)
        {
            if (resolution <= 0 || fovDegrees <= 0) return 0;
            if (distanceMeters <= 1e-6) return double.PositiveInfinity;
            return resolution * 360.0 / (2 * Math.PI * fovDegrees * distanceMeters);
        }

        /// <summary>The highest DORI level reached at this density, or null below Detection.</summary>
        public static DoriLevel LevelFor(double pixelsPerMeter)
        {
            return Levels.LastOrDefault(l => pixelsPerMeter >= l.PixelsPerMeter);
        }

        /// <summary>The DORI level of a filled region type created by the plugin, from its name.</summary>
        public static DoriLevel LevelForRegionType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            // Longest names first, so "dori_250px" isn't taken for "dori_25px"
            return Levels.OrderByDescending(l => l.RegionTypeName.Length).FirstOrDefault(l => typeName.Contains(l.RegionTypeName));
        }

        /// <summary>Is this security device a camera the plugin can reason about (it has the rotation parameter)?</summary>
        public static bool IsCamera(Element element)
        {
            return element is FamilyInstance && element.LookupParameter(SettingsManager.Settings.ParameterName_UserRotation) != null;
        }

        /// <summary>FOV in degrees, same priority as the window: override (when set), instance, then type standard FOV.</summary>
        public static bool TryGetFovDegrees(Element camera, out double degrees)
        {
            degrees = 0;
            if (camera == null) return false;

            Parameter overrideParam = camera.LookupParameter(SettingsManager.Settings.ParameterName_FOVOverride);
            if (overrideParam != null && overrideParam.StorageType == StorageType.Double && Math.Abs(overrideParam.AsDouble()) > 0.001)
            {
                degrees = overrideParam.AsDouble() * 180.0 / Math.PI;
                return degrees > 0;
            }

            Parameter standard = camera.LookupParameter(SettingsManager.Settings.ParameterName_StandardFOV)
                ?? (camera.Document.GetElement(camera.GetTypeId()) as ElementType)?.LookupParameter(SettingsManager.Settings.ParameterName_StandardFOV);
            if (standard != null && standard.StorageType == StorageType.Double && standard.AsDouble() > 0.001)
            {
                degrees = standard.AsDouble() * 180.0 / Math.PI;
                return true;
            }

            return false;
        }

        /// <summary>Horizontal resolution in pixels from the instance or its type; "3840 px" style text is accepted.</summary>
        public static bool TryGetResolution(Element camera, out int resolution)
        {
            resolution = 0;
            if (camera == null) return false;

            Parameter p = camera.LookupParameter(SettingsManager.Settings.ParameterName_Resolution);
            if (p == null && camera is FamilyInstance instance && instance.Symbol != null)
                p = instance.Symbol.LookupParameter(SettingsManager.Settings.ParameterName_Resolution);
            if (p == null)
                p = (camera.Document.GetElement(camera.GetTypeId()) as ElementType)?.LookupParameter(SettingsManager.Settings.ParameterName_Resolution);
            if (p == null) return false;

            string text = p.AsString()?.Trim();
            if (string.IsNullOrEmpty(text)) text = p.AsValueString()?.Trim();
            if (!string.IsNullOrEmpty(text)) text = new string(text.Where(char.IsDigit).ToArray());

            if (!string.IsNullOrEmpty(text))
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out resolution) && resolution > 0;

            if (p.StorageType == StorageType.Integer) resolution = p.AsInteger();
            else if (p.StorageType == StorageType.Double) resolution = (int)Math.Round(p.AsDouble());
            return resolution > 0;
        }

        /// <summary>"29 · Videokaamera dome, 4MP" style label: Mark and type name.</summary>
        public static string Describe(Element camera)
        {
            if (camera == null) return "Deleted camera";

            string mark = camera.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            string type = camera.Document.GetElement(camera.GetTypeId())?.Name ?? camera.Name;
            return string.IsNullOrWhiteSpace(mark) ? type : $"{mark} · {type}";
        }
    }
}
