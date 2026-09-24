using Autodesk.Revit.DB;
using Camera_FOV.Models;
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
    /// Camera values and pixel density shared by the drawing, the point check (issue #7) and the
    /// coverage audit (issue #6). Pixel density and DORI distance are the same relation solved either
    /// way, so a point at a region's edge gets that region's threshold density. The formula is chosen
    /// in Settings (issue #11).
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

        public static PixelDensityFormula CurrentFormula => SettingsManager.Settings.PixelDensityFormula;

        public static string FormulaName(PixelDensityFormula formula)
        {
            return formula == PixelDensityFormula.Legacy ? "Legacy (arc length)" : "IEC 62676-4:2026";
        }

        /// <summary>
        /// Width of the scene per metre of distance.
        ///  - Standard (EVS-EN IEC 62676-4:2026, Figure 4): the flat width w = 2 × d × tan(FOV / 2),
        ///    the same as d × sensor width / focal length.
        ///  - Legacy: the arc length w = 2π × d × FOV / 360, which matches Axis Site Designer (issue #2).
        /// The flat width has no finite value from 180°, where the lens can't be rectilinear, so wider
        /// views use the arc length with either formula.
        /// </summary>
        private static double WidthPerMeter(double fovDegrees, PixelDensityFormula formula)
        {
            if (formula == PixelDensityFormula.Standard && fovDegrees < 180)
                return 2 * Math.Tan(fovDegrees * Math.PI / 360.0);
            return 2 * Math.PI * fovDegrees / 360.0;
        }

        /// <summary>Horizontal pixels per metre at a distance, with the formula chosen in Settings.</summary>
        public static double PixelsPerMeter(int resolution, double fovDegrees, double distanceMeters)
        {
            return PixelsPerMeter(resolution, fovDegrees, distanceMeters, CurrentFormula);
        }

        /// <summary>Horizontal pixels per metre at a distance: resolution / scene width.</summary>
        public static double PixelsPerMeter(int resolution, double fovDegrees, double distanceMeters, PixelDensityFormula formula)
        {
            if (resolution <= 0 || fovDegrees <= 0) return 0;
            if (distanceMeters <= 1e-6) return double.PositiveInfinity;
            return resolution / (WidthPerMeter(fovDegrees, formula) * distanceMeters);
        }

        /// <summary>The distance in metres at which the density falls to pixelsPerMeter: the window's DORI distance.</summary>
        public static double DistanceMeters(int resolution, double fovDegrees, double pixelsPerMeter, PixelDensityFormula formula)
        {
            if (resolution <= 0 || fovDegrees <= 0 || pixelsPerMeter <= 0) return 0;
            return resolution / (WidthPerMeter(fovDegrees, formula) * pixelsPerMeter);
        }

        /// <summary>The horizontal resolution that gives pixelsPerMeter at a distance: the density formula solved for resolution.</summary>
        public static double ResolutionFor(double pixelsPerMeter, double fovDegrees, double distanceMeters, PixelDensityFormula formula)
        {
            return pixelsPerMeter * WidthPerMeter(fovDegrees, formula) * distanceMeters;
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
