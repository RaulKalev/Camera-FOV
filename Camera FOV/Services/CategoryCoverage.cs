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
        public double OverviewFeet { get; }  // Plan distance out to which anything is seen at Overview
        public double NearFeet { get; }      // Plan distance of the dead zone's edge (issue #16); 0 without one
        public double FarFeet { get; }       // Plan distance beyond which the target is above the view

        private readonly List<double> _angles = new List<double>(); // Degrees, plan
        private readonly List<double> _reach = new List<double>();  // Feet: to the first Boundary line, at most OverviewFeet
        private readonly bool _fullCircle;
        private readonly PixelDensityFormula _formula;
        private readonly CameraMount _mount;

        internal CameraSight(AuditCamera camera, PixelDensityFormula formula, double stepDegrees, IReadOnlyList<Segment> boundaries)
        {
            Camera = camera;
            _formula = formula;
            _mount = camera.Mount ?? new CameraMount();

            var (nearMeters, farMeters) = _mount.VisibleRange();
            NearFeet = double.IsPositiveInfinity(nearMeters) ? double.PositiveInfinity : nearMeters / 0.3048;
            FarFeet = double.IsPositiveInfinity(farMeters) ? double.PositiveInfinity : farMeters / 0.3048;
            OverviewFeet = Math.Min(DistanceFeet(CameraData.Categories[0].PixelsPerMeter), FarFeet);

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

        /// <summary>
        /// Horizontal pixels per metre at a plan distance in metres from the camera, measured at the
        /// slant distance to the target; 0 in the dead zone and above the view.
        /// </summary>
        public double PixelsPerMeterAt(double planMeters)
        {
            double feet = planMeters / 0.3048;
            if (feet < NearFeet || feet > FarFeet) return 0;
            return CameraData.PixelsPerMeter(Camera.Resolution.Value, Camera.FovDegrees.Value, _mount.SlantMeters(planMeters), _formula);
        }

        /// <summary>The slant distance from the lens, in metres, at a plan distance.</summary>
        public double SlantMeters(double planMeters) => _mount.SlantMeters(planMeters);

        /// <summary>The plan distance in feet at which the density falls to pixelsPerMeter; 0 when the target is never that close.</summary>
        public double DistanceFeet(double pixelsPerMeter)
        {
            double slant = CameraData.DistanceMeters(Camera.Resolution.Value, Camera.FovDegrees.Value, pixelsPerMeter, _formula);
            return (_mount.PlanForSlant(slant) ?? 0) / 0.3048;
        }

        /// <summary>
        /// The area seen at pixelsPerMeter or better, as closed loops (even-odd): every ray's end, each
        /// stopped by a Boundary line or at that density's distance, back along the edge of the dead zone
        /// (or through the camera). A 360° view is a ring: an outer loop and the dead zone as a hole.
        /// Empty when that density isn't reached outside the dead zone.
        /// </summary>
        public List<List<PlanPoint>> Outline(double pixelsPerMeter)
        {
            double limit = Math.Min(OverviewFeet, DistanceFeet(pixelsPerMeter));
            var loops = new List<List<PlanPoint>>();
            if (limit <= NearFeet || limit <= 0) return loops;

            PlanPoint c = Camera.Position.Value;
            PlanPoint At(int i, double r)
            {
                double rad = _angles[i] * Math.PI / 180.0;
                return new PlanPoint(c.X + Math.Cos(rad) * r, c.Y + Math.Sin(rad) * r);
            }

            var outer = new List<PlanPoint>(_angles.Count * 2 + 1);
            for (int i = 0; i < _angles.Count; i++)
                outer.Add(At(i, Math.Max(Math.Min(_reach[i], limit), Math.Min(NearFeet, _reach[i]))));

            if (_fullCircle)
            {
                loops.Add(outer);
                if (NearFeet > 0)
                    loops.Add(Enumerable.Range(0, _angles.Count).Select(i => At(i, Math.Min(NearFeet, _reach[i]))).ToList());
            }
            else
            {
                if (NearFeet > 0)
                    for (int i = _angles.Count - 1; i >= 0; i--) outer.Add(At(i, Math.Min(NearFeet, _reach[i])));
                else
                    outer.Insert(0, c);
                loops.Add(outer);
            }
            return loops;
        }

        /// <summary>Whether the category is reached anywhere outside the dead zone, ignoring Boundary lines.</summary>
        public bool Reaches(ObservationCategory category)
        {
            double limit = Math.Min(Math.Min(OverviewFeet, FarFeet), DistanceFeet(category.PixelsPerMeter));
            return limit > 0 && limit > NearFeet;
        }

        /// <summary>The dead zone below the camera (issue #16), as loops; empty when it has none or it isn't known.</summary>
        public List<List<PlanPoint>> DeadZone()
        {
            var loops = new List<List<PlanPoint>>();
            if (NearFeet <= 0 || double.IsPositiveInfinity(NearFeet)) return loops;

            PlanPoint c = Camera.Position.Value;
            var points = new List<PlanPoint>(_angles.Count + 1);
            if (!_fullCircle) points.Add(c);
            for (int i = 0; i < _angles.Count; i++)
            {
                double rad = _angles[i] * Math.PI / 180.0, r = Math.Min(NearFeet, _reach[i]);
                points.Add(new PlanPoint(c.X + Math.Cos(rad) * r, c.Y + Math.Sin(rad) * r));
            }
            loops.Add(points);
            return loops;
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
