using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace Camera_FOV.Models
{
    /// <summary>A value the user adds to a camera type; written to the family parameter of the same name.</summary>
    public class CustomParameter
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;

        public CustomParameter Clone() => new CustomParameter { Name = Name, Value = Value };
    }

    /// <summary>A sensor size listed in EVS-EN IEC 62676-4:2026, 6.7.2, by its optical format.</summary>
    public sealed class SensorFormat
    {
        public string Name { get; }
        public double WidthMm { get; }

        public SensorFormat(string name, double widthMm)
        {
            Name = name;
            WidthMm = widthMm;
        }

        public static readonly IReadOnlyList<SensorFormat> Standard = new List<SensorFormat>
        {
            new SensorFormat("1/3\"", 4.8),
            new SensorFormat("1/2.8\"", 5.4),
            new SensorFormat("1/2.5\"", 5.76),
            new SensorFormat("1/2\"", 6.4),
            new SensorFormat("1/1.9\"", 7.2),
            new SensorFormat("2/3\"", 8.8)
        };
    }

    /// <summary>
    /// A camera model in the shared library: its sensor, lens and the field of view it can be set to.
    /// Ranges hold a fixed lens as min = max. Optional values are null when unknown.
    /// </summary>
    public class CameraType
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;

        public int HorizontalResolution { get; set; }
        public int? VerticalResolution { get; set; }

        public string SensorFormat { get; set; } // A SensorFormat name, or null for a custom size
        public double? SensorWidthMm { get; set; }
        public double? SensorHeightMm { get; set; }
        public double? FocalLengthMinMm { get; set; }
        public double? FocalLengthMaxMm { get; set; }

        public double HorizontalFovMin { get; set; }
        public double HorizontalFovMax { get; set; }
        public double? VerticalFovMin { get; set; }
        public double? VerticalFovMax { get; set; }

        public List<CustomParameter> CustomParameters { get; set; } = new List<CustomParameter>();

        // Written by the library on every save, so two people editing the same type are told
        // rather than one silently overwriting the other.
        public int Version { get; set; }
        public string ModifiedBy { get; set; }
        public DateTime? ModifiedUtc { get; set; }

        public CameraType Clone()
        {
            var copy = (CameraType)MemberwiseClone();
            copy.CustomParameters = CustomParameters.Select(p => p.Clone()).ToList();
            return copy;
        }

        /// <summary>"Axis · 3840 px · 30–93°" style summary for lists.</summary>
        [JsonIgnore]
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                string maker = string.Join(" ", new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (maker.Length > 0) parts.Add(maker);
                if (HorizontalResolution > 0) parts.Add($"{HorizontalResolution} px");
                if (HorizontalFovMax > 0) parts.Add(FormatRange(HorizontalFovMin, HorizontalFovMax, "°"));
                return string.Join(" · ", parts);
            }
        }

        [JsonIgnore]
        public bool IsVarifocal => HorizontalFovMax - HorizontalFovMin > 0.05;

        /// <summary>Is a horizontal field of view within what this camera can be set to (to 0.05°)?</summary>
        public bool AllowsHorizontalFov(double degrees)
        {
            return degrees >= HorizontalFovMin - 0.05 && degrees <= HorizontalFovMax + 0.05;
        }

        public double ClampHorizontalFov(double degrees)
        {
            return Math.Max(HorizontalFovMin, Math.Min(HorizontalFovMax, degrees));
        }

        public static string FormatRange(double min, double max, string unit)
        {
            return Math.Abs(max - min) < 0.05 ? $"{max:0.#}{unit}" : $"{min:0.#}–{max:0.#}{unit}";
        }

        /// <summary>
        /// The field of view a rectilinear lens gives across a sensor dimension: 2 × atan(size / (2 × focal length)).
        /// </summary>
        public static double FovFromLens(double sensorMm, double focalLengthMm)
        {
            return 2 * Math.Atan(sensorMm / (2 * focalLengthMm)) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Fills the field of view ranges from the sensor and focal lengths: the shortest focal length
        /// gives the widest view. Returns what couldn't be worked out, or null when both were.
        /// </summary>
        public string DeriveFovFromLens()
        {
            if (!(SensorWidthMm > 0)) return "Enter the sensor width, or pick a sensor format.";
            if (!(FocalLengthMinMm > 0)) return "Enter the focal length (the shortest, for a varifocal lens).";

            double shortest = FocalLengthMinMm.Value;
            double longest = FocalLengthMaxMm > 0 ? Math.Max(FocalLengthMaxMm.Value, shortest) : shortest;

            HorizontalFovMax = Math.Round(FovFromLens(SensorWidthMm.Value, shortest), 1);
            HorizontalFovMin = Math.Round(FovFromLens(SensorWidthMm.Value, longest), 1);

            if (SensorHeightMm > 0)
            {
                VerticalFovMax = Math.Round(FovFromLens(SensorHeightMm.Value, shortest), 1);
                VerticalFovMin = Math.Round(FovFromLens(SensorHeightMm.Value, longest), 1);
                return null;
            }

            return "The vertical field of view needs the sensor height.";
        }

        /// <summary>What must be fixed before the type can be saved; empty when it is valid.</summary>
        public List<(string Field, string Problem)> Validate(IEnumerable<CameraType> library)
        {
            var problems = new List<(string, string)>();

            if (string.IsNullOrWhiteSpace(Name))
                problems.Add(("Name", "Give the camera type a name. It becomes the Revit type name."));
            else if (Name.IndexOfAny(new[] { ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\\' }) >= 0)
                problems.Add(("Name", "Revit type names can't contain : { } [ ] | ; < > ? ` ~ or \\."));
            else if (library != null && library.Any(t => t.Id != Id && string.Equals(t.Name.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                problems.Add(("Name", $"Another camera type is already called “{Name.Trim()}”."));

            if (HorizontalResolution <= 0)
                problems.Add(("Horizontal resolution", "Enter the sensor’s horizontal resolution in pixels, for example 3840."));
            if (VerticalResolution.HasValue && VerticalResolution.Value <= 0)
                problems.Add(("Vertical resolution", "Enter a number of pixels above 0, or leave it empty."));

            CheckRange(problems, "Horizontal field of view", HorizontalFovMin, HorizontalFovMax, 360, required: true);
            CheckRange(problems, "Vertical field of view", VerticalFovMin, VerticalFovMax, 180, required: false);
            CheckRange(problems, "Focal length", FocalLengthMinMm, FocalLengthMaxMm, 1000, required: false);

            if (SensorWidthMm.HasValue && SensorWidthMm.Value <= 0)
                problems.Add(("Sensor width", "Enter the width in millimetres above 0, or leave it empty."));
            if (SensorHeightMm.HasValue && SensorHeightMm.Value <= 0)
                problems.Add(("Sensor height", "Enter the height in millimetres above 0, or leave it empty."));

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CustomParameter parameter in CustomParameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Name))
                    problems.Add(("Custom parameters", "Every custom parameter needs the name of the family parameter it fills."));
                else if (!names.Add(parameter.Name.Trim()))
                    problems.Add(("Custom parameters", $"“{parameter.Name.Trim()}” is listed twice."));
            }

            return problems;
        }

        private static void CheckRange(List<(string, string)> problems, string field, double? min, double? max, double limit, bool required)
        {
            if ((!min.HasValue && !max.HasValue) || (min == 0 && max == 0))
            {
                if (required) problems.Add((field, "Enter the narrowest and widest values; for a fixed lens both are the same."));
                return;
            }
            if (!min.HasValue || !max.HasValue)
            {
                problems.Add((field, "Enter both ends of the range; for a fixed lens both are the same."));
                return;
            }
            if (min.Value <= 0 || max.Value > limit)
                problems.Add((field, $"The range must lie above 0 and up to {limit:0}."));
            else if (min.Value > max.Value)
                problems.Add((field, "The first value is the smallest: it can’t be larger than the second."));
        }
    }
}
