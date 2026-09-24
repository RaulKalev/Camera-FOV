using Autodesk.Revit.DB;
using Camera_FOV.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Camera_FOV.Services
{
    public enum PointVerdict
    {
        Covered,     // Inside the field of view, unobstructed, at Detection density or better
        TooFar,      // Visible, but below Detection density
        OutsideFov,  // The point is outside the camera's field of view
        Blocked,     // A Boundary line lies between the camera and the point
        Unknown      // Missing camera data: never reported as covered
    }

    public sealed class PointResult
    {
        public Element Camera { get; set; }
        public PointVerdict Verdict { get; set; }
        public string Reason { get; set; }
        public double DistanceMeters { get; set; }
        public double PixelsPerMeter { get; set; }
        public DoriLevel Level { get; set; }
        public CoverageState CoverageState { get; set; }
    }

    /// <summary>
    /// Which cameras see a point in plan, and how well (issue #7). Read-only. Uses the same 2D model as
    /// the drawn coverage: the field of view as a wedge, the plugin's pixel-density formula, and
    /// Boundary lines as the only obstructions. Camera height, tilt and lens distortion are not modelled.
    /// </summary>
    public static class PointCoverage
    {
        public static List<PointResult> Evaluate(Document doc, View view, XYZ point)
        {
            List<Segment> boundaries = GetBoundarySegments(doc, view);
            Dictionary<string, List<ElementId>> coverage = ElementTagStorage.FindCoverageByCamera(doc, view);

            var cameras = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_SecurityDevices)
                .WhereElementIsNotElementType()
                .Where(e => CameraData.IsCamera(e) || coverage.ContainsKey(e.UniqueId))
                .ToList();

            return cameras
                .Select(camera => EvaluateCamera(doc, view, camera, point, boundaries,
                    coverage.TryGetValue(camera.UniqueId, out List<ElementId> regions) ? regions : new List<ElementId>()))
                .ToList();
        }

        private static PointResult EvaluateCamera(Document doc, View view, Element camera, XYZ point, List<Segment> boundaries, List<ElementId> regions)
        {
            var result = new PointResult { Camera = camera };

            CoverageStatus status = CoverageStatus.Evaluate(doc, view, camera, regions);
            result.CoverageState = status.State;

            XYZ position = CoverageSource.GetCameraPosition(camera);
            if (position == null)
                return Unknown(result, "The camera has no location point.");

            double dx = point.X - position.X, dy = point.Y - position.Y;
            result.DistanceMeters = Math.Sqrt(dx * dx + dy * dy) * 0.3048;

            CameraValues values = GetCameraValues(camera, view, status);
            const string redraw = "Its coverage was drawn before the plugin recorded its field of view and resolution: press Update in the Camera FOV window to redraw it.";
            if ((values.FovDegrees == null || values.Resolution == null) && status.State != CoverageState.None && status.State != CoverageState.CameraMissing)
                return Unknown(result, redraw);
            if (values.FovDegrees == null)
                return Unknown(result, "No field of view: draw the camera’s coverage once, or give the family an FOV parameter.");
            if (values.Resolution == null)
                return Unknown(result, "No horizontal resolution: draw the camera’s coverage once, or give the family a resolution parameter.");
            if (values.AimDegrees == null)
                return Unknown(result, "Can’t tell which way the camera faces: draw its coverage once to record its aim.");

            double fov = values.FovDegrees.Value;
            int resolution = values.Resolution.Value;
            double? aim = values.AimDegrees;

            // Field of view
            if (fov < 360 && result.DistanceMeters > 1e-6)
            {
                double toPoint = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                double offAxis = Math.Abs(NormalizeDegrees(toPoint - aim.Value));
                if (offAxis > fov / 2)
                {
                    result.Verdict = PointVerdict.OutsideFov;
                    result.Reason = $"Outside the field of view: {offAxis:0}° off its axis, the {fov:0}° view reaches {fov / 2:0}° either side.";
                    return result;
                }
            }

            // Obstruction, with the same rule as the drawn coverage's ray casting
            if (boundaries.Any(s => s.Crosses(position.X, position.Y, point.X, point.Y)))
            {
                result.Verdict = PointVerdict.Blocked;
                result.Reason = "A Boundary line lies between the camera and the point.";
                return result;
            }

            result.PixelsPerMeter = CameraData.PixelsPerMeter(resolution, fov, result.DistanceMeters);
            result.Level = CameraData.LevelFor(result.PixelsPerMeter);

            if (result.Level == null)
            {
                result.Verdict = PointVerdict.TooFar;
                result.Reason = $"Too far: {result.PixelsPerMeter:0} px/m at {result.DistanceMeters:0.0} m; Detection needs {CameraData.Levels[0].PixelsPerMeter:0} px/m.";
                return result;
            }

            result.Verdict = PointVerdict.Covered;
            result.Reason = $"{result.Level.Name}: {result.PixelsPerMeter:0} px/m at {result.DistanceMeters:0.0} m ({resolution} px, {fov:0}°).";
            return result;
        }

        public sealed class CameraValues
        {
            public double? AimDegrees;
            public double? FovDegrees;
            public int? Resolution;
        }

        /// <summary>
        /// Direction, FOV and resolution for a camera. The values its coverage was drawn with come first,
        /// while that coverage still matches the camera: the window supplies FOV and resolution when the
        /// family has no such parameters. Otherwise the family's parameters, and the direction of its 2D
        /// symbol (drawing keeps the two aligned).
        /// </summary>
        public static CameraValues GetCameraValues(Element camera, View view, CoverageStatus status)
        {
            var values = new CameraValues();

            if (status != null && status.DrawnValuesApply)
            {
                foreach (ElementId id in status.Regions)
                {
                    if (!ElementTagStorage.TryGetCoverageSource(camera.Document.GetElement(id), out string state, out _, out _))
                        continue;

                    if (values.AimDegrees == null && CoverageSource.TryGetDrawnAim(state, out double aim)) values.AimDegrees = aim;
                    if (values.FovDegrees == null && CoverageSource.TryGetDrawnFov(state, out double fov)) values.FovDegrees = fov;
                    if (values.Resolution == null && CoverageSource.TryGetDrawnResolution(state, out int resolution)) values.Resolution = resolution;
                }
            }

            if (values.FovDegrees == null && CameraData.TryGetFovDegrees(camera, out double parameterFov)) values.FovDegrees = parameterFov;
            if (values.Resolution == null && CameraData.TryGetResolution(camera, out int parameterResolution)) values.Resolution = parameterResolution;

            // Coverage drawn before FOV and resolution were recorded: read what's still missing back from its regions
            if ((values.FovDegrees == null || values.Resolution == null) && values.AimDegrees != null && status != null && status.DrawnValuesApply)
            {
                DeriveFromRegions(camera, status, values);
            }

            if (values.AimDegrees == null) values.AimDegrees = CameraSymbol.GetAngleDegrees(camera, view);

            return values;
        }

        // The drawn wedge opens FOV/2 either side of the aim, so the widest angle of its outline from
        // the aim gives the FOV. The outermost DORI level reaches the recorded Reach, and the density
        // formula solved for resolution at that distance gives the resolution the draw used.
        private static void DeriveFromRegions(Element camera, CoverageStatus status, CameraValues values)
        {
            Document doc = camera.Document;
            XYZ apex = CoverageSource.GetCameraPosition(camera);
            if (apex == null) return;

            double maxOffAxis = 0;
            DoriLevel outermost = null;
            double reach = 0;
            PixelDensityFormula formula = PixelDensityFormula.Legacy;

            foreach (ElementId id in status.Regions)
            {
                if (!(doc.GetElement(id) is FilledRegion region)) continue;

                DoriLevel level = CameraData.LevelForRegionType(doc.GetElement(region.GetTypeId())?.Name);
                if (level != null && (outermost == null || level.PixelsPerMeter < outermost.PixelsPerMeter))
                {
                    outermost = level;
                    if (ElementTagStorage.TryGetCoverageSource(region, out string state, out _, out double storedReach))
                    {
                        reach = storedReach;
                        formula = CoverageSource.GetDrawnFormula(state);
                    }
                }

                foreach (CurveLoop loop in region.GetBoundaries())
                foreach (Curve curve in loop)
                foreach (XYZ point in curve.Tessellate())
                {
                    double dx = point.X - apex.X, dy = point.Y - apex.Y;
                    if (dx * dx + dy * dy < 0.01) continue; // The apex itself has no direction
                    double offAxis = Math.Abs(NormalizeDegrees(Math.Atan2(dy, dx) * 180.0 / Math.PI - values.AimDegrees.Value));
                    maxOffAxis = Math.Max(maxOffAxis, offAxis);
                }
            }

            if (values.FovDegrees == null && maxOffAxis > 0.5)
                values.FovDegrees = Math.Min(360, Math.Round(2 * maxOffAxis, 1));

            if (values.Resolution == null && values.FovDegrees != null && outermost != null && reach > 0)
            {
                double reachMeters = reach * 0.3048;
                double resolution = CameraData.ResolutionFor(outermost.PixelsPerMeter, values.FovDegrees.Value, reachMeters, formula);
                if (resolution > 0) values.Resolution = SnapResolution(resolution);
            }
        }

        // The window's resolution list; a derived value this close to one of them is that sensor width
        private static readonly int[] KnownResolutions = { 7424, 3840, 3712, 2880, 2688, 2592, 2560, 2304, 1920, 1280, 800, 640 };

        private static int SnapResolution(double resolution)
        {
            int nearest = KnownResolutions.OrderBy(r => Math.Abs(r - resolution)).First();
            return Math.Abs(nearest - resolution) <= nearest * 0.03 ? nearest : (int)Math.Round(resolution);
        }

        private static PointResult Unknown(PointResult result, string reason)
        {
            result.Verdict = PointVerdict.Unknown;
            result.Reason = reason;
            return result;
        }

        private static double NormalizeDegrees(double degrees)
        {
            degrees %= 360;
            if (degrees > 180) degrees -= 360;
            if (degrees < -180) degrees += 360;
            return degrees;
        }

        private static List<Segment> GetBoundarySegments(Document doc, View view)
        {
            var segments = new List<Segment>();
            var curves = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(CurveElement))
                .WhereElementIsNotElementType()
                .Cast<CurveElement>()
                .Where(c => c.LineStyle?.Name == "Boundary");

            foreach (CurveElement element in curves)
            {
                Curve curve = element.GeometryCurve;
                if (curve == null) continue;

                IList<XYZ> points;
                try { points = curve is Line ? new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) } : curve.Tessellate(); }
                catch { continue; }

                for (int i = 0; i < points.Count - 1; i++)
                    segments.Add(new Segment(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y));
            }

            return segments;
        }

    }

    /// <summary>A straight piece of a Boundary line in plan, in feet.</summary>
    internal readonly struct Segment
    {
        private readonly double _x1, _y1, _x2, _y2;

        public Segment(double x1, double y1, double x2, double y2)
        {
            _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
        }

        public double MinX => Math.Min(_x1, _x2);
        public double MaxX => Math.Max(_x1, _x2);
        public double MinY => Math.Min(_y1, _y2);
        public double MaxY => Math.Max(_y1, _y2);

        public bool Crosses(double x3, double y3, double x4, double y4) => Fraction(x3, y3, x4, y4) <= 1;

        // Same test as DrawingTools.GetIntersectionDistance: parameters on both segments within [0, 1].
        // Returns how far along (x3,y3)→(x4,y4) this segment is hit, or double.MaxValue for no hit.
        public double Fraction(double x3, double y3, double x4, double y4)
        {
            double den = (_x1 - _x2) * (y3 - y4) - (_y1 - _y2) * (x3 - x4);
            if (Math.Abs(den) < 1e-9) return double.MaxValue;

            double t = ((_x1 - x3) * (y3 - y4) - (_y1 - y3) * (x3 - x4)) / den;
            double u = -((_x1 - _x2) * (_y1 - y3) - (_y1 - _y2) * (_x1 - x3)) / den;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? u : double.MaxValue;
        }
    }
}
