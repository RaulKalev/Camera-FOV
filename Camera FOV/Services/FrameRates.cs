using Autodesk.Revit.DB;
using Camera_FOV.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>
    /// How many frames a person or vehicle gets while crossing a camera's view (issue #18,
    /// EVS-EN IEC 62676-4:2026, 9.2 and Annex D): the scene width at that distance, divided by the
    /// speed, times the frame rate.
    /// </summary>
    public static class FrameRates
    {
        public static readonly double[] Common = { 1, 6, 12.5, 25 };

        public static IReadOnlyList<(string Name, double Kmh)> Speeds => new[]
        {
            ("walking", SettingsManager.Settings.WalkingSpeedKmh),
            ("running", SettingsManager.Settings.RunningSpeedKmh),
            ("vehicle", SettingsManager.Settings.VehicleSpeedKmh)
        };

        /// <summary>Frames while crossing a view widthMeters wide at speedKmh, rounded down: whole frames only.</summary>
        public static int Frames(double widthMeters, double speedKmh, double fps)
        {
            if (speedKmh <= 0 || fps <= 0) return 0;
            return (int)Math.Floor(widthMeters / (speedKmh / 3.6) * fps + 1e-9);
        }

        /// <summary>The frame rate that gives the needed frames per crossing at a speed.</summary>
        public static double RequiredFps(double widthMeters, double speedKmh, int frames)
        {
            return widthMeters > 0 ? frames * (speedKmh / 3.6) / widthMeters : double.PositiveInfinity;
        }

        /// <summary>The camera's frame rate: its parameter (instance, then type), else its camera type; null when unknown.</summary>
        public static double? Read(Element camera)
        {
            if (camera == null) return null;
            string name = SettingsManager.Settings.ParameterName_FrameRate;
            if (!string.IsNullOrWhiteSpace(name))
            {
                Parameter parameter = camera.LookupParameter(name)
                    ?? (camera.Document.GetElement(camera.GetTypeId()) as ElementType)?.LookupParameter(name);
                double? value = null;
                if (parameter != null && parameter.HasValue)
                {
                    if (parameter.StorageType == StorageType.Double) value = parameter.AsDouble();
                    else if (parameter.StorageType == StorageType.Integer) value = parameter.AsInteger();
                    else if (parameter.StorageType == StorageType.String) value = ParseFps(parameter.AsString());
                }
                if (value > 0) return value;
            }

            try { return CameraTypeLink.GetLinked(camera)?.FrameRate; }
            catch (Exception) { return null; }
        }

        private static double? ParseFps(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string number = new string(text.Trim().TakeWhile(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
            return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) && fps > 0 ? fps : (double?)null;
        }

        /// <summary>
        /// "View 4.2 m wide. At 25 fps: walking 75, running 25, vehicle 7 frames." With the camera's
        /// frame rate when known, else a table at the common rates; a warning when any object gets
        /// fewer frames than set in Settings.
        /// </summary>
        public static string Describe(double widthMeters, double? cameraFps)
        {
            int needed = Math.Max(1, SettingsManager.Settings.MinFramesPerCrossing);
            string text = $" View {widthMeters:0.0} m wide.";

            if (cameraFps > 0)
            {
                double fps = cameraFps.Value;
                text += $" At its {fps:0.#} fps: " + string.Join(", ", Speeds.Select(s => $"{s.Name} {Frames(widthMeters, s.Kmh, fps)}")) + " frames.";
                var tooFew = Speeds.Where(s => Frames(widthMeters, s.Kmh, fps) < needed).ToList();
                if (tooFew.Any())
                    text += $" Too few for {string.Join(" and ", tooFew.Select(s => s.Name))}: needs {RequiredFps(widthMeters, tooFew.Max(s => s.Kmh), needed):0.#} fps for {needed} frame{(needed == 1 ? "" : "s")}.";
                return text;
            }

            return text + " Frames while crossing at " + string.Join("/", Common.Select(f => f.ToString("0.#", CultureInfo.InvariantCulture))) + " fps: " +
                   string.Join(", ", Speeds.Select(s => $"{s.Name} " + string.Join("/", Common.Select(f => Frames(widthMeters, s.Kmh, f))))) + ".";
        }
    }
}
