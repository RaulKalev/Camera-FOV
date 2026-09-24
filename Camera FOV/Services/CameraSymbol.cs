using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Camera_FOV.Services
{
    /// <summary>
    /// Reads which way a camera's 2D symbol actually points in a plan view. The family turns its symbol
    /// by the rotation parameter, and flips mirror it, so the symbol's drawn geometry is the only
    /// reliable source for the direction the user sees.
    /// </summary>
    public static class CameraSymbol
    {
        /// <summary>
        /// Unit direction (in plan) of the symbol's far end from the camera's location: the average
        /// direction of the geometry lying at least half as far out as its farthest point.
        /// Null when the camera has no location or its symbol has no clear direction.
        /// </summary>
        public static XYZ GetDirection(Element camera, View view)
        {
            if (!(camera?.Location is LocationPoint location) || view == null) return null;

            GeometryElement geometry = camera.get_Geometry(new Options { View = view });
            if (geometry == null) return null;

            XYZ origin = location.Point;
            var offsets = new List<XYZ>();
            foreach (XYZ point in GetPlanPoints(geometry))
            {
                XYZ offset = new XYZ(point.X - origin.X, point.Y - origin.Y, 0);
                if (offset.GetLength() > 1e-6) offsets.Add(offset);
            }
            if (!offsets.Any()) return null;

            double reach = offsets.Max(o => o.GetLength());
            List<XYZ> farEnd = offsets.Where(o => o.GetLength() >= reach * 0.5).ToList();

            XYZ sum = XYZ.Zero;
            foreach (XYZ offset in farEnd)
                sum += offset.Normalize();

            // A symmetric symbol (e.g. a circle) cancels out and has no direction to follow
            return sum.GetLength() / farEnd.Count < 0.2 ? null : sum.Normalize();
        }

        /// <summary>The symbol direction as an angle in degrees, 0–360, measured like the window's rotation.</summary>
        public static double? GetAngleDegrees(Element camera, View view)
        {
            XYZ direction = GetDirection(camera, view);
            if (direction == null) return null;

            double degrees = Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI;
            return degrees < 0 ? degrees + 360 : degrees;
        }

        private static IEnumerable<XYZ> GetPlanPoints(GeometryElement geometry)
        {
            foreach (GeometryObject geomObj in geometry)
            {
                if (geomObj is Curve curve && curve.IsBound)
                {
                    foreach (XYZ point in curve.Tessellate()) yield return point;
                }
                else if (geomObj is PolyLine polyLine)
                {
                    foreach (XYZ point in polyLine.GetCoordinates()) yield return point;
                }
                else if (geomObj is Solid solid)
                {
                    foreach (Edge edge in solid.Edges)
                    foreach (XYZ point in edge.Tessellate()) yield return point;
                }
                else if (geomObj is GeometryInstance instance)
                {
                    foreach (XYZ point in GetPlanPoints(instance.GetInstanceGeometry())) yield return point;
                }
            }
        }
    }
}
