using Camera_FOV.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>
    /// What one camera sees in plan: rays cast from the camera across its field of view, each stopped
    /// by the first Boundary line or at the Overview distance. The same rule as the drawn coverage, so
    /// Boundary lines cut the category bands where they cut the drawn regions.
    /// </summary>
    public sealed class CameraSight
    {
        public AuditCamera Camera { get; }
        public double OverviewFeet { get; }

        private readonly List<double> _angles = new List<double>(); // Degrees, plan
        private readonly List<double> _reach = new List<double>();  // Feet: to the first Boundary line, at most OverviewFeet
        private readonly bool _fullCircle;
        private readonly PixelDensityFormula _formula;

        internal CameraSight(AuditCamera camera, PixelDensityFormula formula, double stepDegrees, IReadOnlyList<Segment> boundaries)
        {
            Camera = camera;
            _formula = formula;
            OverviewFeet = DistanceFeet(CameraData.Categories[0].PixelsPerMeter);

            double cx = camera.Position.Value.X, cy = camera.Position.Value.Y;
            double fov = camera.FovDegrees.Value, aim = camera.AimDegrees.Value;

            // Only the Boundary lines within reach can stop a ray
            var near = boundaries.Where(s =>
                s.MaxX >= cx - OverviewFeet && s.MinX <= cx + OverviewFeet &&
                s.MaxY >= cy - OverviewFeet && s.MinY <= cy + OverviewFeet).ToList();

            // The same ray angles as DrawingTools.CalculateFOVPointsWithIntersection
            _fullCircle = fov >= 360;
            double start = _fullCircle ? 0 : aim - fov / 2;
            for (double angle = start; _fullCircle ? angle < 360 : angle <= aim + fov / 2; angle += stepDegrees)
            {
                double rad = angle * Math.PI / 180.0;
                double endX = cx + Math.Cos(rad) * OverviewFeet, endY = cy + Math.Sin(rad) * OverviewFeet;

                double nearest = 1;
                foreach (Segment segment in near)
                    nearest = Math.Min(nearest, segment.Fraction(cx, cy, endX, endY));

                _angles.Add(angle);
                _reach.Add(nearest * OverviewFeet);
            }
        }

        /// <summary>Horizontal pixels per metre at a distance in metres from the camera.</summary>
        public double PixelsPerMeterAt(double meters)
        {
            return CameraData.PixelsPerMeter(Camera.Resolution.Value, Camera.FovDegrees.Value, meters, _formula);
        }

        /// <summary>The distance in feet at which the density falls to pixelsPerMeter.</summary>
        public double DistanceFeet(double pixelsPerMeter)
        {
            return CameraData.DistanceMeters(Camera.Resolution.Value, Camera.FovDegrees.Value, pixelsPerMeter, _formula) / 0.3048;
        }

        /// <summary>
        /// The area seen at pixelsPerMeter or better, as a closed outline: the camera, then every ray's
        /// end, each stopped by a Boundary line or at that density's distance. A 360° view has no apex.
        /// </summary>
        public List<PlanPoint> Outline(double pixelsPerMeter)
        {
            double limit = Math.Min(OverviewFeet, DistanceFeet(pixelsPerMeter));
            PlanPoint c = Camera.Position.Value;

            var points = new List<PlanPoint>(_angles.Count + 1);
            if (!_fullCircle) points.Add(c);
            for (int i = 0; i < _angles.Count; i++)
            {
                double rad = _angles[i] * Math.PI / 180.0, r = Math.Min(_reach[i], limit);
                points.Add(new PlanPoint(c.X + Math.Cos(rad) * r, c.Y + Math.Sin(rad) * r));
            }
            return points;
        }
    }

    /// <summary>
    /// Pixel density in the observation categories of EVS-EN IEC 62676-4:2026 (issue #12), worked out
    /// from each camera and the view's Boundary lines rather than from the drawn DORI regions, which
    /// stop at Detection (25 px/m) and have no bands above 250 px/m. In memory only.
    /// </summary>
    public static class CategoryCoverage
    {
        /// <summary>
        /// A sight for every camera with a position, direction, FOV and resolution. The rest are
        /// returned as skipped; deleted cameras are neither.
        /// </summary>
        public static List<CameraSight> Cast(AuditData data, PixelDensityFormula formula, double stepDegrees, out List<AuditCamera> skipped)
        {
            var boundaries = new List<Segment>();
            foreach (List<PlanPoint> line in data.BoundaryLines)
            {
                for (int i = 0; i < line.Count - 1; i++)
                    boundaries.Add(new Segment(line[i].X, line[i].Y, line[i + 1].X, line[i + 1].Y));
            }

            stepDegrees = Math.Max(0.1, Math.Min(1.0, stepDegrees));
            var sights = new List<CameraSight>();
            skipped = new List<AuditCamera>();

            foreach (AuditCamera camera in data.Cameras.Where(c => c.State != CoverageState.CameraMissing))
            {
                if (CanSee(camera)) sights.Add(new CameraSight(camera, formula, stepDegrees, boundaries));
                else skipped.Add(camera);
            }

            return sights;
        }

        public static bool CanSee(AuditCamera camera)
        {
            return camera.Position.HasValue && camera.AimDegrees.HasValue &&
                   camera.FovDegrees.HasValue && camera.FovDegrees.Value > 0 &&
                   camera.Resolution.HasValue && camera.Resolution.Value > 0;
        }
    }
}
