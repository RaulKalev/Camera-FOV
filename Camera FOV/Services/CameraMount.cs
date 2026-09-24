using Autodesk.Revit.DB;
using Camera_FOV.Models;
using System;
using System.Globalization;

namespace Camera_FOV.Services
{
    /// <summary>
    /// How high a camera hangs and how far it tilts down, against the height of what it looks at
    /// (issue #16). Distances here are in metres, in plan unless named slant. Values that aren't known
    /// stay null and are reported as unknown rather than assumed.
    /// </summary>
    public sealed class CameraMount
    {
        public double? HeightMeters;        // Camera above its floor
        public double? TiltDegrees;         // Below horizontal; negative looks up
        public double? VerticalFovDegrees;
        public double TargetHeightMeters;

        public bool Enabled => SettingsManager.Settings.UseMountingGeometry;

        /// <summary>Camera height above the target, when the height is known.</summary>
        public double? HeightAboveTarget => Enabled && HeightMeters.HasValue ? HeightMeters - TargetHeightMeters : null;

        /// <summary>Distance from the lens to the target at a plan distance: the one pixel density depends on.</summary>
        public double SlantMeters(double planMeters)
        {
            double? dh = HeightAboveTarget;
            return dh.HasValue ? Math.Sqrt(planMeters * planMeters + dh.Value * dh.Value) : planMeters;
        }

        /// <summary>The plan distance at which the slant distance is slantMeters, or null when the target is never that close.</summary>
        public double? PlanForSlant(double slantMeters)
        {
            double dh = HeightAboveTarget ?? 0;
            double squared = slantMeters * slantMeters - dh * dh;
            return squared < 0 ? (double?)null : Math.Sqrt(squared);
        }

        /// <summary>How steeply the camera looks down at the target, in degrees; null when the height is unknown.</summary>
        public double? DepressionDegrees(double planMeters)
        {
            double? dh = HeightAboveTarget;
            return dh.HasValue ? Math.Atan2(dh.Value, Math.Max(planMeters, 1e-6)) * 180.0 / Math.PI : (double?)null;
        }

        /// <summary>Whether the tilt and vertical view are known, so the visible band can be worked out.</summary>
        public bool KnowsVerticalView => Enabled && HeightMeters.HasValue && TiltDegrees.HasValue && VerticalFovDegrees.HasValue;

        /// <summary>
        /// The plan distances between which the target is inside the vertical view: nearer is the dead
        /// zone below and in front of the camera, farther is above the top edge of the view. Far is
        /// infinity when the top edge is at or above the target's horizon. (0, ∞) when unknown.
        /// </summary>
        public (double Near, double Far) VisibleRange()
        {
            if (!KnowsVerticalView) return (0, double.PositiveInfinity);

            double dh = HeightAboveTarget.Value;
            double lower = TiltDegrees.Value + VerticalFovDegrees.Value / 2; // Bottom edge, below horizontal
            double upper = TiltDegrees.Value - VerticalFovDegrees.Value / 2; // Top edge, below horizontal
            double Tan(double degrees) => Math.Tan(degrees * Math.PI / 180.0);

            // The angle down to the target, atan(dh / d), runs from ±90° next to the camera to 0° far away
            if (dh > 1e-6)
            {
                if (lower <= 0) return (double.PositiveInfinity, double.PositiveInfinity); // Looks entirely above the target
                double near = lower >= 90 ? 0 : dh / Tan(lower);
                double far = upper > 0 ? dh / Tan(upper) : double.PositiveInfinity;
                return (near, far);
            }
            if (dh < -1e-6)
            {
                double rise = -dh;
                if (upper >= 0) return (double.PositiveInfinity, double.PositiveInfinity); // Looks entirely below the target
                double near = upper <= -90 ? 0 : rise / Tan(-upper);
                double far = lower < 0 ? rise / Tan(-lower) : double.PositiveInfinity;
                return (near, far);
            }
            return upper <= 0 && lower >= 0 ? (0, double.PositiveInfinity) : (double.PositiveInfinity, double.PositiveInfinity);
        }

        /// <summary>
        /// Reads a camera's height and tilt. The vertical view comes from its camera type, or else
        /// from the horizontal view at the camera type's (or a 16:9) aspect ratio.
        /// </summary>
        public static CameraMount Read(Element camera, double? horizontalFovDegrees)
        {
            var mount = new CameraMount { TargetHeightMeters = SettingsManager.Settings.TargetHeightMeters };
            if (camera == null) return mount;

            mount.HeightMeters = ReadHeight(camera);
            mount.TiltDegrees = ReadAngle(camera, SettingsManager.Settings.ParameterName_Tilt);

            CameraType type = null;
            try { type = CameraTypeLink.GetLinked(camera); } catch (Exception) { }

            if (type?.VerticalFovMax > 0 && horizontalFovDegrees.HasValue && type.HorizontalFovMax > 0)
            {
                // Zoomed like the horizontal view: the same focal length gives both
                mount.VerticalFovDegrees = VerticalFromHorizontal(horizontalFovDegrees.Value,
                    Math.Tan(type.VerticalFovMax.Value * Math.PI / 360) / Math.Tan(type.HorizontalFovMax * Math.PI / 360));
            }
            else if (horizontalFovDegrees.HasValue && horizontalFovDegrees.Value < 180)
            {
                double aspect = type != null && type.VerticalResolution > 0 && type.HorizontalResolution > 0
                    ? (double)type.VerticalResolution.Value / type.HorizontalResolution
                    : 9.0 / 16.0;
                mount.VerticalFovDegrees = VerticalFromHorizontal(horizontalFovDegrees.Value, aspect);
            }

            return mount;
        }

        private static double VerticalFromHorizontal(double horizontalDegrees, double ratio)
        {
            return 2 * Math.Atan(Math.Tan(horizontalDegrees * Math.PI / 360) * ratio) * 180 / Math.PI;
        }

        private static double? ReadHeight(Element camera)
        {
            string name = SettingsManager.Settings.ParameterName_MountingHeight?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                Parameter parameter = camera.LookupParameter(name);
                if (parameter != null && parameter.StorageType == StorageType.Double && parameter.HasValue)
                    return parameter.AsDouble() * 0.3048;
                return null; // Named but missing: unknown, not guessed from elsewhere
            }

            // Height above the camera's level, from its location
            if (camera.Location is LocationPoint location && camera.LevelId != null && camera.LevelId != ElementId.InvalidElementId &&
                camera.Document.GetElement(camera.LevelId) is Level level)
                return (location.Point.Z - level.ProjectElevation) * 0.3048;

            Parameter elevation = camera.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            return elevation != null && elevation.HasValue ? elevation.AsDouble() * 0.3048 : (double?)null;
        }

        private static double? ReadAngle(Element camera, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            Parameter parameter = camera.LookupParameter(name)
                ?? (camera.Document.GetElement(camera.GetTypeId()) as ElementType)?.LookupParameter(name);
            if (parameter == null || !parameter.HasValue) return null;

            if (parameter.StorageType == StorageType.Double) return parameter.AsDouble() * 180.0 / Math.PI;
            if (parameter.StorageType == StorageType.Integer) return parameter.AsInteger();
            if (parameter.StorageType == StorageType.String &&
                double.TryParse(parameter.AsString()?.Trim().TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees))
                return degrees;
            return null;
        }

        /// <summary>"3.5 m high, tilted 30°" style text, naming what is unknown.</summary>
        public string Describe()
        {
            if (!Enabled) return "Camera height and tilt are switched off in Settings.";
            string height = HeightMeters.HasValue ? $"{HeightMeters:0.0#} m high" : "height unknown";
            string tilt = TiltDegrees.HasValue ? $"tilted {TiltDegrees:0.#}° down" : "tilt unknown";
            return $"{height}, {tilt}, target at {TargetHeightMeters:0.0#} m";
        }
    }
}
