using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Camera_FOV.Services
{
    /// <summary>
    /// Captures what a coverage drawing was made from, so a later check can tell whether it still
    /// matches its camera. Two snapshots are stored on every generated region:
    ///  - the camera state: position, orientation, rotation/FOV/resolution parameters and type;
    ///  - a fingerprint of the Boundary lines within reach of the camera in the view.
    /// A changed camera state makes the coverage stale. Changed Boundary lines only mark it for review,
    /// because they may or may not affect it. Linked models count once they are traced again, since
    /// tracing turns them into Boundary lines in the host view.
    /// </summary>
    public static class CoverageSource
    {
        private const string Position = "pos";
        private const string Orientation = "orient";
        private const string UserRotation = "rot";
        private const string FieldOfView = "fov";
        private const string Resolution = "res";
        private const string CameraType = "type";
        private const string FlipState = "flip";

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { Position, "Position" },
            { Orientation, "Orientation" },
            { UserRotation, "Rotation" },
            { FieldOfView, "Field of view" },
            { Resolution, "Resolution" },
            { CameraType, "Camera type" }
        };

        // "key=value;key=value", with values rounded so re-reading an unchanged camera matches exactly.
        public static string CaptureCameraState(Element camera)
        {
            var values = new SortedDictionary<string, string>();
            if (camera == null) return string.Empty;

            if (camera.Location is LocationPoint location)
            {
                XYZ p = location.Point;
                values[Position] = $"{Round(p.X, 3)},{Round(p.Y, 3)},{Round(p.Z, 3)}";

                values[Orientation] = Round(location.Rotation, 4);

                // Recorded so a flip can be told apart from a real rotation; never reported itself
                if (camera is FamilyInstance instance)
                    values[FlipState] = $"{instance.FacingFlipped},{instance.HandFlipped}";
            }

            values[UserRotation] = ReadAngle(camera.LookupParameter(SettingsManager.Settings.ParameterName_UserRotation));
            values[FieldOfView] = ReadFieldOfView(camera);
            values[Resolution] = ReadResolution(camera);
            values[CameraType] = camera.GetTypeId()?.ToString() ?? "none";

            return string.Join(";", values.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        // The state a drawing actually reflects. Position, orientation, resolution and type come from
        // when the camera was selected, because the drawing used those values even if the camera moved
        // since. Rotation and FOV come from after the draw, which writes the window's values back to
        // the camera. So a camera changed between selecting and drawing is still reported as stale.
        public static string MergeDrawnState(string atSelection, string afterDraw)
        {
            if (string.IsNullOrEmpty(atSelection)) return afterDraw;

            Dictionary<string, string> merged = Parse(atSelection), written = Parse(afterDraw);
            foreach (string key in new[] { UserRotation, FieldOfView })
            {
                if (written.TryGetValue(key, out string value))
                    merged[key] = value;
            }

            return string.Join(";", merged.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        }

        // Names of what differs between two camera states, e.g. "Position", "Rotation".
        public static List<string> CompareCameraStates(string stored, string current)
        {
            Dictionary<string, string> before = Parse(stored), now = Parse(current);

            // Early test builds stored "rotation,facingFlipped,handFlipped,mirrored" in one value
            foreach (var state in new[] { before, now })
            {
                if (state.TryGetValue(Orientation, out string orientation) && orientation.Contains(","))
                {
                    string[] parts = orientation.Split(',');
                    state[Orientation] = parts[0];
                    if (parts.Length >= 3 && !state.ContainsKey(FlipState))
                        state[FlipState] = $"{parts[1]},{parts[2]}";
                }
            }

            // The camera's flip arrows mirror the family, which Revit records as a 180° turn of its
            // location together with a changed flip state. That doesn't change the camera's view, so
            // it is not reported; any other change of rotation is.
            if (IsFlipOnly(before, now))
                before[Orientation] = now[Orientation];

            return Labels.Keys
                .Where(key => (before.TryGetValue(key, out string a) ? a : null) != (now.TryGetValue(key, out string b) ? b : null))
                .Select(key => Labels[key])
                .ToList();
        }

        private static bool IsFlipOnly(Dictionary<string, string> before, Dictionary<string, string> now)
        {
            if (!before.TryGetValue(FlipState, out string flipBefore) || !now.TryGetValue(FlipState, out string flipNow)) return false;
            if (flipBefore == flipNow) return false;

            if (!before.TryGetValue(Orientation, out string a) || !now.TryGetValue(Orientation, out string b)) return false;
            if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double rotationBefore) ||
                !double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double rotationNow)) return false;

            // Difference of the two rotations, folded into [0, π]
            double difference = Math.Abs(rotationNow - rotationBefore) % (2 * Math.PI);
            if (difference > Math.PI) difference = 2 * Math.PI - difference;

            const double tolerance = 1e-3; // radians
            return difference < tolerance || Math.Abs(difference - Math.PI) < tolerance;
        }

        // Fingerprint of the Boundary lines in the view that come within reach (feet) of the camera.
        public static string CaptureBoundaryState(Document doc, View view, XYZ cameraPosition, double reach)
        {
            if (doc == null || view == null || cameraPosition == null) return string.Empty;

            var keys = new List<string>();
            var curves = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(CurveElement))
                .WhereElementIsNotElementType()
                .Cast<CurveElement>()
                .Where(c => c.LineStyle?.Name == "Boundary");

            foreach (CurveElement element in curves)
            {
                Curve curve = element.GeometryCurve;
                if (curve == null || !curve.IsBound) continue;

                XYZ start = curve.GetEndPoint(0);
                XYZ atCurveLevel = new XYZ(cameraPosition.X, cameraPosition.Y, start.Z);
                if (curve.Distance(atCurveLevel) > reach) continue;

                XYZ end = curve.GetEndPoint(1), mid = curve.Evaluate(0.5, true);
                string a = PointKey(start), b = PointKey(end);
                keys.Add(string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}|{PointKey(mid)}" : $"{b}|{a}|{PointKey(mid)}");
            }

            keys.Sort(StringComparer.Ordinal);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", keys)));
                return $"{keys.Count}:{BitConverter.ToString(hash, 0, 12).Replace("-", string.Empty)}";
            }
        }

        public static XYZ GetCameraPosition(Element camera)
        {
            return (camera?.Location as LocationPoint)?.Point;
        }

        // Same priority as the window uses: FOV override (when set), instance standard FOV, type standard FOV.
        private static string ReadFieldOfView(Element camera)
        {
            Parameter overrideParam = camera.LookupParameter(SettingsManager.Settings.ParameterName_FOVOverride);
            if (overrideParam != null && overrideParam.StorageType == StorageType.Double && Math.Abs(overrideParam.AsDouble()) > 0.001)
                return ReadAngle(overrideParam);

            Parameter standard = camera.LookupParameter(SettingsManager.Settings.ParameterName_StandardFOV)
                ?? (camera.Document.GetElement(camera.GetTypeId()) as ElementType)?.LookupParameter(SettingsManager.Settings.ParameterName_StandardFOV);
            return ReadAngle(standard);
        }

        private static string ReadResolution(Element camera)
        {
            Parameter parameter = camera.LookupParameter(SettingsManager.Settings.ParameterName_Resolution)
                ?? (camera.Document.GetElement(camera.GetTypeId()) as ElementType)?.LookupParameter(SettingsManager.Settings.ParameterName_Resolution);
            if (parameter == null) return "none";

            switch (parameter.StorageType)
            {
                case StorageType.String: return parameter.AsString()?.Trim() ?? string.Empty;
                case StorageType.Integer: return parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double: return Round(parameter.AsDouble(), 3);
                default: return parameter.AsValueString() ?? string.Empty;
            }
        }

        private static string ReadAngle(Parameter parameter)
        {
            return parameter != null && parameter.StorageType == StorageType.Double
                ? Round(parameter.AsDouble(), 4)
                : "none";
        }

        private static Dictionary<string, string> Parse(string state)
        {
            var result = new Dictionary<string, string>();
            foreach (string pair in (state ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int split = pair.IndexOf('=');
                if (split > 0) result[pair.Substring(0, split)] = pair.Substring(split + 1);
            }
            return result;
        }

        private static string PointKey(XYZ p) => $"{Round(p.X, 3)},{Round(p.Y, 3)}";

        private static string Round(double value, int digits)
        {
            double rounded = Math.Round(value, digits);
            if (rounded == 0) rounded = 0; // No "-0"
            return rounded.ToString("F" + digits, CultureInfo.InvariantCulture);
        }
    }
}
