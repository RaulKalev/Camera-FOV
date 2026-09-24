using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;


namespace Camera_FOV.Utils
{
    public class DrawingTools
    {
        private readonly Document _doc;
        private readonly View _currentView;
        private DetailCurve _currentDetailLine;
        private FilledRegion _currentFilledRegion;

        private XYZ _currentPosition;
        private double _maxDistance;
        private double _rotationAngle;
        private double _fovAngle = 90; // Default FOV angle
        private ElementId _filledRegionTypeId;

        private class FOVPoint
        {
            public XYZ Point { get; set; }
            public Curve HitGeometry { get; set; } // The boundary curve that was hit
            public bool IsMaxDistance { get; set; }

            public FOVPoint(XYZ point, bool isMaxDistance = false, Curve hitGeometry = null)
            {
                Point = point;
                IsMaxDistance = isMaxDistance;
                HitGeometry = hitGeometry;
            }
        }

        public DrawingTools(Document doc, View currentView)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _currentView = currentView ?? throw new ArgumentNullException(nameof(currentView));
        }

        public Document Document => _doc;
        public View View => _currentView;

        public void SetParameters(XYZ position, double distance, double angle, double fovAngle = 90, ElementId filledRegionTypeId = null)
        {
            _currentPosition = position;
            _maxDistance = distance > 0 ? distance : 10; // Default to 10m if invalid
            _rotationAngle = angle;
            _fovAngle = fovAngle > 0 ? fovAngle : 90;    // Default FOV angle
            _filledRegionTypeId = filledRegionTypeId;   // Nullable, used only for filled regions
        }

        public void DrawDetailLine()
        {
            if (_currentPosition == null) return;

            // Ensure the previous detail line is deleted
            DeleteDetailLine();

            using (Transaction trans = new Transaction(_doc, "Draw Camera Rotation Detail Line"))
            {
                try
                {
                    if (trans.Start() != TransactionStatus.Started)
                        throw new InvalidOperationException("Failed to start transaction.");

                    // Calculate rotation in radians
                    double rotationInRadians = _rotationAngle * Math.PI / 180.0;

                    // Calculate direction vector based on rotation
                    XYZ direction = new XYZ(
                        Math.Cos(rotationInRadians),
                        Math.Sin(rotationInRadians),
                        0
                    );

                    // Calculate endpoint of the detail line
                    XYZ endPoint = _currentPosition + (direction * (_maxDistance / 0.3048)); // Convert meters to feet

                    // Create the detail line and track it
                    Line line = Line.CreateBound(_currentPosition, endPoint);
                    _currentDetailLine = _doc.Create.NewDetailCurve(_currentView, line);

                    if (trans.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Failed to commit transaction.");
                }
                catch (Exception ex)
                {
                    if (trans.HasStarted())
                        trans.RollBack();
                    throw new InvalidOperationException($"Error in DrawDetailLine: {ex.Message}");
                }
            }
        }

        public void DeleteDetailLine()
        {
            if (_currentDetailLine != null)
            {
                using (Transaction trans = new Transaction(_doc, "Delete Detail Line"))
                {
                    try
                    {
                        if (trans.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("Failed to start transaction.");

                        // Delete the current detail line
                        _doc.Delete(_currentDetailLine.Id);
                        _currentDetailLine = null;

                        if (trans.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Failed to commit transaction.");
                    }
                    catch (Exception ex)
                    {
                        if (trans.HasStarted())
                            trans.RollBack();
                        throw new InvalidOperationException($"Error in DeleteDetailLine: {ex.Message}");
                    }
                }
            }
        }

        public void UpdateDetailLine()
        {
            // First, delete the existing detail line
            DeleteDetailLine();

            // Then, draw the new detail line
            DrawDetailLine();
        }

        // Why the last DrawFilledRegion call returned InvalidElementId, for the caller to report.
        public string LastFailure { get; private set; }

        // onCreated runs inside the creating transaction, so anything it writes commits with the region.
        // apexLegLength > 0 splits that length off each straight side leaving the camera, giving a
        // dimension short edges to attach to at the camera.
        // On failure nothing is committed, InvalidElementId is returned and LastFailure says why.
        public ElementId DrawFilledRegion(double resolution, Action<FilledRegion> onCreated = null, double apexLegLength = 0)
        {
            LastFailure = null;

            if (_currentPosition == null || _filledRegionTypeId == null)
            {
                LastFailure = _currentPosition == null
                    ? "No camera position is set."
                    : "The filled region type is missing from the project.";
                return ElementId.InvalidElementId;
            }

            // Retry logic: If fine resolution fails, try coarser resolutions
            var attempts = new List<Tuple<double, bool>>();
            attempts.Add(Tuple.Create(resolution, false));
            attempts.Add(Tuple.Create(resolution, true));
            double[] fallbacks = { 0.5, 1.0, 2.0, 5.0 };
            foreach (var fb in fallbacks)
            {
                if (fb > resolution)
                {
                    attempts.Add(Tuple.Create(fb, false));
                    attempts.Add(Tuple.Create(fb, true));
                }
            }

            string lastError = "";
            XYZ basePosition = _currentPosition;

            try
            {
                foreach (var attempt in attempts)
                {
                    double res = attempt.Item1;
                    bool useOffset = attempt.Item2;

                    _currentPosition = basePosition;
                    if (useOffset)
                    {
                        double rad = _rotationAngle * Math.PI / 180.0;
                        XYZ dir = new XYZ(Math.Cos(rad), Math.Sin(rad), 0);
                        _currentPosition = basePosition + dir * (10.0 / 304.8);
                    }
                using (Transaction trans = new Transaction(_doc, "Draw Filled Region"))
                {
                    try
                    {
                        if (trans.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("Failed to start transaction.");

                        // 1. Get Style
                        GraphicsStyle invisibleLineStyle = new FilteredElementCollector(_doc)
                            .OfClass(typeof(GraphicsStyle))
                            .Cast<GraphicsStyle>()
                            .FirstOrDefault(gs => gs.Name.Equals("<Invisible lines>", StringComparison.OrdinalIgnoreCase));

                        if (invisibleLineStyle == null)
                            throw new InvalidOperationException("<Invisible lines> style not found.");

                        // 2. Calculate Points
                        List<FOVPoint> fovPoints;
                        if (_fovAngle == 360.0)
                            fovPoints = CalculateFOVPointsForCircle(res);
                        else
                            fovPoints = CalculateFOVPointsWithIntersection(res);

                        // 3. Project to Plane (Z)
                        double planeZ = _currentPosition.Z;
                        if (_currentView.SketchPlane != null)
                            planeZ = _currentView.SketchPlane.GetPlane().Origin.Z;
                        else if (_currentView.GenLevel != null)
                            planeZ = _currentView.GenLevel.Elevation;

                        foreach (var fp in fovPoints)
                            fp.Point = new XYZ(fp.Point.X, fp.Point.Y, planeZ);

                        // 4. Generate Boundary
                        bool success = false;
                        FilledRegion region = null;

                        // Try A: Smart Simplify (Arc/Line reconstruction)
                        try
                        {
                            CurveLoop boundary = SplitApexEdges(SimplifyBoundary(fovPoints), apexLegLength);
                            if (boundary.IsValidObject && !boundary.IsOpen() && boundary.Count() >= 3)
                            {
                                region = FilledRegion.Create(_doc, _filledRegionTypeId, _currentView.Id, new List<CurveLoop> { boundary });
                                success = true;
                            }
                        }
                        catch { /* Ignore, proceed to fallback */ }

                        // Try B: Fallback (Simple Polygon)
                        if (!success)
                        {
                            try
                            {
                                CurveLoop fallback = SplitApexEdges(CreateFallbackBoundary(fovPoints), apexLegLength);
                                if (fallback.IsValidObject && !fallback.IsOpen() && fallback.Count() >= 3)
                                {
                                    region = FilledRegion.Create(_doc, _filledRegionTypeId, _currentView.Id, new List<CurveLoop> { fallback });
                                    success = true;
                                }
                            }
                            catch { /* Both failed */ }
                        }

                        if (success && region != null)
                        {
                            _currentFilledRegion = region;

                            // Apply invisible lines
                            var dependentIds = region.GetDependentElements(null);
                            foreach (var id in dependentIds)
                            {
                                if (_doc.GetElement(id) is CurveElement ce)
                                    ce.LineStyle = invisibleLineStyle;
                            }

                            onCreated?.Invoke(region);

                            if (trans.Commit() == TransactionStatus.Committed)
                            {
                                return region.Id; // SUCCESS
                            }
                        }
                        
                        // If we are here, transaction failed or wasn't committed.
                        // Rollback is automatic with 'using' if not committed, or we can explicit.
                        // But if trans.Start() succeeded we should check status? 
                        // If we didn't commit, we just continue loop.
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                    }
                }
                // Loop continues to next coarser resolution
                }
            }
            finally
            {
                _currentPosition = basePosition;
            }

            // If all attempts fail
            LastFailure = string.IsNullOrWhiteSpace(lastError)
                ? "Revit rejected the coverage outline at every resolution tried."
                : $"Revit rejected the coverage outline at every resolution tried. Last error: {lastError}";
            return ElementId.InvalidElementId;
        }

        public void DeleteElement(ElementId elementId)
        {
            if (elementId == null || elementId == ElementId.InvalidElementId) return;

            using (Transaction trans = new Transaction(_doc, "Delete Element"))
            {
                trans.Start();
                try
                {
                    _doc.Delete(elementId);
                    trans.Commit();
                }
                catch
                {
                    // Ignore errors if element doesn't exist or can't be deleted
                }
            }
        }

        private CurveLoop CreateFallbackBoundary(List<FOVPoint> rawPoints)
        {
             CurveLoop loop = new CurveLoop();
             double tolerance = _doc.Application.ShortCurveTolerance;
             
             List<XYZ> points = new List<XYZ>();
             points.Add(rawPoints[0].Point);
             
             for(int k=1; k < rawPoints.Count; k++)
             {
                 if (rawPoints[k].Point.DistanceTo(points.Last()) > tolerance)
                 {
                     points.Add(rawPoints[k].Point);
                 }
             }
             
             // Ensure closure
             if (points[0].DistanceTo(points.Last()) > tolerance)
                points.Add(points[0]);
             else 
                points[points.Count - 1] = points[0]; // Snap last to first

             for(int i=0; i < points.Count - 1; i++)
             {
                 loop.Append(Line.CreateBound(points[i], points[i+1]));
             }
             
             return loop;
        }

        // Places an angular dimension between the two straight edges of the region that meet at the
        // camera (the field of view), with its arc at arcRadius from the camera. The references come
        // from the region's own visible edges, the same ones a user picks when dimensioning by hand;
        // the region's sketch lines are hidden once it is finished, and a dimension on them is too.
        // Must run inside an open transaction. Returns null and sets LastFailure when not possible.
        public Dimension CreateFovDimension(FilledRegion region, DimensionType dimensionType, double arcRadius)
        {
            LastFailure = null;

            if (region == null || dimensionType == null || _currentPosition == null)
            {
                LastFailure = "The region, dimension type or camera position is missing.";
                return null;
            }

            if (_fovAngle >= 180.0)
            {
                LastFailure = $"A {_fovAngle:0}° field of view has no wedge edges to dimension.";
                return null;
            }

            var options = new Options { ComputeReferences = true, View = _currentView, IncludeNonVisibleObjects = true };
            GeometryElement geometry = region.get_Geometry(options);
            if (geometry == null)
            {
                LastFailure = "The region has no geometry in this view.";
                return null;
            }

            // The apex is the region corner nearest the camera (a retry may shift it a few millimetres).
            // Only edges ending exactly there count, so the short split legs are used, not the long
            // sides that start just beyond them.
            List<Tuple<Line, Reference>> lines = GetReferencedLines(geometry).ToList();
            XYZ apexPoint = lines
                .SelectMany(l => new[] { l.Item1.GetEndPoint(0), l.Item1.GetEndPoint(1) })
                .OrderBy(p => DistanceXY(p, _currentPosition))
                .FirstOrDefault();

            if (apexPoint == null || DistanceXY(apexPoint, _currentPosition) > 50.0 / 304.8)
            {
                LastFailure = $"Found {lines.Count} referenceable region edges, none at the camera.";
                return null;
            }

            double apexTolerance = 0.5 / 304.8;
            var edges = new List<Tuple<XYZ, XYZ, Reference>>(); // apex, direction away from it, reference
            foreach (Tuple<Line, Reference> edge in lines)
            {
                Line line = edge.Item1;
                XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);

                if (DistanceXY(p0, apexPoint) < apexTolerance)
                    edges.Add(Tuple.Create(p0, (p1 - p0).Normalize(), edge.Item2));
                else if (DistanceXY(p1, apexPoint) < apexTolerance)
                    edges.Add(Tuple.Create(p1, (p0 - p1).Normalize(), edge.Item2));
            }

            // The wedge sides are the pair of camera edges that open widest
            Tuple<XYZ, XYZ, Reference> first = null, second = null;
            double widest = 0;
            for (int i = 0; i < edges.Count; i++)
            for (int j = i + 1; j < edges.Count; j++)
            {
                double angle = edges[i].Item2.AngleTo(edges[j].Item2);
                if (angle > widest && angle < Math.PI - 1e-6)
                {
                    widest = angle;
                    first = edges[i];
                    second = edges[j];
                }
            }

            if (first == null)
            {
                LastFailure = $"Found {edges.Count} referenceable region edges at the camera; two are needed.";
                return null;
            }

            XYZ apex = first.Item1;
            XYZ bisector = (first.Item2 + second.Item2).Normalize();

            Arc arc = Arc.Create(
                apex + first.Item2 * arcRadius,
                apex + second.Item2 * arcRadius,
                apex + bisector * arcRadius);

            try
            {
                return AngularDimension.Create(_doc, _currentView, arc, new List<Reference> { first.Item3, second.Item3 }, dimensionType);
            }
            catch (Exception ex)
            {
                LastFailure = $"Revit rejected the angular dimension: {ex.Message}";
                return null;
            }
        }

        // Splits legLength off the start of each straight side that leaves the camera (the loop point
        // nearest _currentPosition). The extra vertex is collinear, so the outline is unchanged.
        private CurveLoop SplitApexEdges(CurveLoop loop, double legLength)
        {
            if (legLength <= 0 || loop == null || _currentPosition == null) return loop;

            List<Curve> curves = loop.ToList();
            XYZ apex = curves
                .SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1) })
                .OrderBy(p => DistanceXY(p, _currentPosition))
                .FirstOrDefault();
            if (apex == null) return loop;

            double tolerance = _doc.Application.ShortCurveTolerance;
            legLength = Math.Max(legLength, tolerance * 1.02); // Revit rejects lines shorter than this
            var result = new CurveLoop();
            foreach (Curve curve in curves)
            {
                XYZ start = curve.GetEndPoint(0), end = curve.GetEndPoint(1);
                bool fromApex = start.DistanceTo(apex) < tolerance;
                bool toApex = end.DistanceTo(apex) < tolerance;

                if (curve is Line && (fromApex || toApex) && curve.Length > legLength * 2)
                {
                    // Keep the loop's direction: the leg sits on the apex end
                    if (fromApex)
                    {
                        XYZ split = start + (end - start).Normalize() * legLength;
                        result.Append(Line.CreateBound(start, split));
                        result.Append(Line.CreateBound(split, end));
                    }
                    else
                    {
                        XYZ split = end + (start - end).Normalize() * legLength;
                        result.Append(Line.CreateBound(start, split));
                        result.Append(Line.CreateBound(split, end));
                    }
                }
                else
                {
                    result.Append(curve);
                }
            }

            return result;
        }

        private static IEnumerable<Tuple<Line, Reference>> GetReferencedLines(GeometryElement geometry)
        {
            foreach (GeometryObject geomObj in geometry)
            {
                if (geomObj is Line line && line.Reference != null)
                {
                    yield return Tuple.Create(line, line.Reference);
                }
                else if (geomObj is Solid solid)
                {
                    foreach (Edge edge in solid.Edges)
                    {
                        if (edge.Reference != null && edge.AsCurve() is Line edgeLine)
                            yield return Tuple.Create(edgeLine, edge.Reference);
                    }
                }
                else if (geomObj is GeometryInstance instance)
                {
                    foreach (var nested in GetReferencedLines(instance.GetSymbolGeometry()))
                        yield return nested;
                }
            }
        }

        private static double DistanceXY(XYZ a, XYZ b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private CurveLoop SimplifyBoundary(List<FOVPoint> rawPoints)
        {
            CurveLoop loop = new CurveLoop();
            if (rawPoints.Count < 3) return loop;

            // 1. Filter unique points to avoid short segment issues
            // Use Revit's tolerance
            double tolerance = _doc.Application.ShortCurveTolerance; // approx 0.00256 ft

            List<FOVPoint> points = new List<FOVPoint>();
            points.Add(rawPoints[0]);
            for (int k = 1; k < rawPoints.Count; k++)
            {
                if (rawPoints[k].Point.DistanceTo(points.Last().Point) > tolerance)
                {
                    points.Add(rawPoints[k]);
                }
            }

            // Ensure valid count after filtering
            if (points.Count < 3) return loop;

            // Ensure closure
            if (points[0].Point.DistanceTo(points.Last().Point) > tolerance)
            {
                points.Add(points[0]);
            }
            else
            {
                // Snap last to first for perfect closure loop logic
                points[points.Count - 1] = new FOVPoint(points[0].Point, points.Last().IsMaxDistance, points.Last().HitGeometry);
            }

            int i = 0;
            while (i < points.Count - 1)
            {
                int j = i + 1;
                bool merged = false;
                FOVPoint startNode = points[i];

                // Check for MaxDistance sequence (Arc)
                if (startNode.IsMaxDistance)
                {
                    while (j < points.Count && points[j].IsMaxDistance)
                    {
                        j++;
                    }

                    if (j > i + 1)
                    {
                        FOVPoint endNode = points[j - 1];
                        if (startNode.Point.DistanceTo(endNode.Point) > tolerance)
                        {
                            try
                            {
                                Curve arc = null;

                                // Prefer 3-point arc: uses actual XYZ points (correct Z after projection)
                                // and avoids relying on the plane's implicit X-axis direction.
                                int midIdx = i + (j - 1 - i) / 2;
                                if (midIdx > i && midIdx < j - 1
                                    && points[midIdx].Point.DistanceTo(startNode.Point) > tolerance
                                    && points[midIdx].Point.DistanceTo(endNode.Point) > tolerance)
                                {
                                    try
                                    {
                                        arc = Arc.Create(startNode.Point, endNode.Point, points[midIdx].Point);
                                    }
                                    catch { arc = null; }
                                }

                                // Fallback: plane-based arc with Z aligned to the projected points
                                if (arc == null)
                                {
                                    double arcZ = startNode.Point.Z;
                                    XYZ arcOrigin = new XYZ(_currentPosition.X, _currentPosition.Y, arcZ);
                                    Plane arcPlane = Plane.CreateByOriginAndBasis(arcOrigin, XYZ.BasisX, XYZ.BasisY);
                                    double radius = Math.Sqrt(
                                        Math.Pow(startNode.Point.X - _currentPosition.X, 2) +
                                        Math.Pow(startNode.Point.Y - _currentPosition.Y, 2));
                                    XYZ startVec = new XYZ(startNode.Point.X - _currentPosition.X, startNode.Point.Y - _currentPosition.Y, 0).Normalize();
                                    XYZ endVec   = new XYZ(endNode.Point.X   - _currentPosition.X, endNode.Point.Y   - _currentPosition.Y, 0).Normalize();
                                    double startAngle = Math.Atan2(startVec.Y, startVec.X);
                                    double endAngle   = Math.Atan2(endVec.Y,   endVec.X);
                                    if (endAngle < startAngle) endAngle += 2 * Math.PI;
                                    arc = Arc.Create(arcPlane, radius, startAngle, endAngle);
                                }

                                loop.Append(arc);
                                i = j - 1;
                                merged = true;
                            }
                            catch
                            {
                                // Arc creation failed, fall back to line
                                try
                                {
                                    loop.Append(Line.CreateBound(startNode.Point, endNode.Point));
                                    i = j - 1;
                                    merged = true;
                                }
                                catch { }
                            }
                        }
                        else
                        {
                            // Start and end too close, treat as single point
                            i = j - 1;
                            merged = true;
                        }
                    }
                }
                // Check for HitGeometry sequence (Wall)
                else if (startNode.HitGeometry != null)
                {
                    Curve targetCurve = startNode.HitGeometry;
                    while (j < points.Count && points[j].HitGeometry == targetCurve)
                    {
                        j++;
                    }

                    if (j > i + 1)
                    {
                        FOVPoint endNode = points[j - 1];
                         if (startNode.Point.DistanceTo(endNode.Point) > tolerance)
                         {
                            // Try to reconstruct arc or line
                            try
                            {
                                Curve segment = null;
                                if (targetCurve is Arc)
                                {
                                     int midIndex = (i + j - 1) / 2;
                                     if (midIndex > i && midIndex < j-1)
                                     {
                                         // Create arc on XY plane using three points
                                         double arcZ = startNode.Point.Z;
                                         XYZ midPoint = points[midIndex].Point;
                                         
                                         // Use the three-point arc creation but ensure planarity
                                         try
                                         {
                                             segment = Arc.Create(startNode.Point, endNode.Point, midPoint);
                                         }
                                         catch
                                         {
                                             // Three-point arc failed, use line
                                             segment = null;
                                         }
                                     }
                                }
                                
                                if (segment == null)
                                {
                                    segment = Line.CreateBound(startNode.Point, endNode.Point);
                                }

                                loop.Append(segment);
                                i = j - 1;
                                merged = true;
                            }
                            catch
                            {
                                // Failed, create simple line
                                try
                                {
                                    loop.Append(Line.CreateBound(startNode.Point, endNode.Point));
                                    i = j - 1;
                                    merged = true;
                                }
                                catch { }
                            }
                         }
                         else
                         {
                             // Start and end too close
                             i = j - 1;
                             merged = true;
                         }
                    }
                }

                if (!merged)
                {
                    // No merge happened - create single segment to next point
                    if (points[i].Point.DistanceTo(points[i+1].Point) > tolerance)
                    {
                        try
                        {
                            loop.Append(Line.CreateBound(points[i].Point, points[i + 1].Point));
                        }
                        catch { }
                    }
                    i++;
                }
            }
            
            return loop;
        }

        // Optimized lightweight structure for 2D lines
        private struct SimpleLine
        {
            public double X1, Y1, X2, Y2;
            public Curve OriginalCurve;

            public SimpleLine(double x1, double y1, double x2, double y2, Curve original)
            {
                X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
                OriginalCurve = original;
            }
        }

        private List<SimpleLine> GetOptimizedBoundaryLines(double maxRadius)
        {
            var rawCurves = GetBoundaryDetailLines();
            var optimizedLines = new List<SimpleLine>();
            
            // Plane for projection (Z = current position Z)
            Plane plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, _currentPosition);
            
            // Bounding box for distance filtering (simple square check is faster than radius)
            double minX = _currentPosition.X - maxRadius;
            double maxX = _currentPosition.X + maxRadius;
            double minY = _currentPosition.Y - maxRadius;
            double maxY = _currentPosition.Y + maxRadius;

            foreach (var curve in rawCurves)
            {
                // Unbound curves (full-circle arcs, ellipses) have no endpoints — tessellate directly.
                if (!curve.IsBound)
                {
                    try
                    {
                        IList<XYZ> pts = curve.Tessellate();
                        for (int i = 0; i < pts.Count - 1; i++)
                        {
                            double tx1 = pts[i].X, ty1 = pts[i].Y;
                            double tx2 = pts[i + 1].X, ty2 = pts[i + 1].Y;
                            if ((tx1 < minX && tx2 < minX) || (tx1 > maxX && tx2 > maxX) ||
                                (ty1 < minY && ty2 < minY) || (ty1 > maxY && ty2 > maxY))
                                continue;
                            optimizedLines.Add(new SimpleLine(tx1, ty1, tx2, ty2, curve));
                        }
                    }
                    catch { /* skip unprocessable curves */ }
                    continue;
                }

                // 1. Project to 2D
                // We manually project endpoints for speed, assuming planar movement on Z
                // If curves are not flat, this approximation is still valid for "Plan View" tracing
                
                XYZ start = curve.GetEndPoint(0);
                XYZ end = curve.GetEndPoint(1);

                // Simple Z-drop projection (much faster than API Plane projection)
                double x1 = start.X;
                double y1 = start.Y;
                double x2 = end.X;
                double y2 = end.Y;

                // 2. Distance Filter (AABB check)
                // If both points are outside the box in the same direction, skip
                if ((x1 < minX && x2 < minX) || (x1 > maxX && x2 > maxX) ||
                    (y1 < minY && y2 < minY) || (y1 > maxY && y2 > maxY))
                {
                    continue;
                }
                
                // 3. Tessellate Arcs
                if (curve is Arc arc)
                {
                    // Tessellate arcs into small segments for linear intersection
                    // This is faster than solving Line-Arc intersection mathematically in 2D for multiple rays
                    IList<XYZ> points = arc.Tessellate();
                    for (int i = 0; i < points.Count - 1; i++)
                    {
                        optimizedLines.Add(new SimpleLine(points[i].X, points[i].Y, points[i+1].X, points[i+1].Y, curve));
                    }
                }
                else
                {
                     optimizedLines.Add(new SimpleLine(x1, y1, x2, y2, curve));
                }
            }

            return optimizedLines;
        }

        // Pure Math Intersection (No Revit API)
        // Returns Distance to intersection, or double.MaxValue if none
        private double GetIntersectionDistance(double rX1, double rY1, double rX2, double rY2, SimpleLine wall, out double intX, out double intY)
        {
            intX = 0; intY = 0;
            
            // Ray: P + t * D, but we have segment (rX1,rY1) to (rX2,rY2)
            // Wall: (X1,Y1) to (X2,Y2)
            
            double x1 = wall.X1, y1 = wall.Y1, x2 = wall.X2, y2 = wall.Y2;
            double x3 = rX1, y3 = rY1, x4 = rX2, y4 = rY2;

            double den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
            
            // Parallel?
            if (Math.Abs(den) < 1e-9) return double.MaxValue;

            double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / den;
            double u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / den;

            // t is for wall segment (must be 0..1)
            // u is for ray segment (must be 0..1)
            
            if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
            {
                intX = x1 + t * (x2 - x1);
                intY = y1 + t * (y2 - y1);
                
                // Return distance squared for comparison speed? No, need actual distance for sorting if multiple hits?
                // Actually u is the fraction of ray length. ray length is MaxDistance.
                // So distance = u * MaxDistance
                return u; 
            }

            return double.MaxValue;
        }

        private List<FOVPoint> CalculateFOVPointsForCircle(double resolution)
        {
            List<FOVPoint> circlePoints = new List<FOVPoint>();
            double maxDistFeet = _maxDistance / 0.3048;

            // PRE-PROCESS: Get optimized lines once
            var boundaryLines = GetOptimizedBoundaryLines(maxDistFeet);

            double cx = _currentPosition.X;
            double cy = _currentPosition.Y;
            double planeZ = _currentPosition.Z; // Or active view plane Z

            for (double angle = 0; angle < 360; angle += resolution)
            {
                double rad = angle * Math.PI / 180.0;
                double dirX = Math.Cos(rad);
                double dirY = Math.Sin(rad);
                
                double endX = cx + dirX * maxDistFeet;
                double endY = cy + dirY * maxDistFeet;

                // Find closest hit
                double minU = double.MaxValue;
                Curve hitCurve = null;
                double hitX = endX, hitY = endY;

                foreach (var line in boundaryLines)
                {
                    double ix, iy;
                    double u = GetIntersectionDistance(cx, cy, endX, endY, line, out ix, out iy);
                    if (u < minU)
                    {
                        minU = u;
                        hitX = ix;
                        hitY = iy;
                        hitCurve = line.OriginalCurve;
                    }
                }

                if (minU < 1.0) // Hit something
                {
                     circlePoints.Add(new FOVPoint(new XYZ(hitX, hitY, planeZ), false, hitCurve));
                }
                else // Max distance
                {
                     circlePoints.Add(new FOVPoint(new XYZ(endX, endY, planeZ), true));
                }
            }
            
            return circlePoints;
        }

        private List<FOVPoint> CalculateFOVPointsWithIntersection(double resolution)
        {
             List<FOVPoint> fovPoints = new List<FOVPoint>();
             fovPoints.Add(new FOVPoint(_currentPosition, false, null)); 

            if (_fovAngle == 360.0)
            {
                 return CalculateFOVPointsForCircle(resolution);
            }
            else
            {
                double maxDistFeet = _maxDistance / 0.3048;
                
                // PRE-PROCESS: Get optimized lines once
                var boundaryLines = GetOptimizedBoundaryLines(maxDistFeet);

                double cx = _currentPosition.X;
                double cy = _currentPosition.Y;
                double planeZ = _currentPosition.Z;

                double halfFOV = _fovAngle / 2.0;
                double startAngle = _rotationAngle - halfFOV;
                double endAngle = _rotationAngle + halfFOV;

                for (double angle = startAngle; angle <= endAngle; angle += resolution)
                {
                    double rad = angle * Math.PI / 180.0;
                    double dirX = Math.Cos(rad);
                    double dirY = Math.Sin(rad);
                    
                    double endX = cx + dirX * maxDistFeet;
                    double endY = cy + dirY * maxDistFeet;
                    
                    // Find closest hit optimized
                    double minU = double.MaxValue;
                    Curve hitCurve = null;
                    double hitX = endX, hitY = endY;

                    foreach (var line in boundaryLines)
                    {
                        double ix, iy;
                        double u = GetIntersectionDistance(cx, cy, endX, endY, line, out ix, out iy);
                        if (u < minU)
                        {
                            minU = u;
                            hitX = ix;
                            hitY = iy;
                            hitCurve = line.OriginalCurve;
                        }
                    }

                     if (minU < 1.0)
                        fovPoints.Add(new FOVPoint(new XYZ(hitX, hitY, planeZ), false, hitCurve));
                    else
                        fovPoints.Add(new FOVPoint(new XYZ(endX, endY, planeZ), true));
                }
            }
            
            return fovPoints;
        }
    
        public void DeleteFilledRegion()
        {
            if (_currentFilledRegion != null)
            {
                using (Transaction trans = new Transaction(_doc, "Delete Filled Region"))
                {
                    try
                    {
                        if (trans.Start() != TransactionStatus.Started)
                            throw new InvalidOperationException("Failed to start transaction.");

                        _doc.Delete(_currentFilledRegion.Id);
                        _currentFilledRegion = null;

                        if (trans.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Failed to commit transaction.");
                    }
                    catch (Exception ex)
                    {
                        if (trans.HasStarted())
                            trans.RollBack();
                        throw new InvalidOperationException($"Error in DeleteFilledRegion: {ex.Message}");
                    }
                }
            }
        }

        private List<Curve> GetBoundaryDetailLines()
        {
            var elements = new FilteredElementCollector(_doc, _currentView.Id)
                .OfClass(typeof(CurveElement))
                .WhereElementIsNotElementType()
                .Cast<CurveElement>()
                .Where(el => el.LineStyle.Name == "Boundary");

            ElementId activeLevelId = _currentView.GenLevel?.Id;
            List<Curve> boundaryLines = new List<Curve>();

            foreach (var el in elements)
            {
                if (el is ModelCurve)
                {
                    // For Model Lines: specific logic to restrict to active floor
                    if (activeLevelId != null)
                    {
                        if (el.LevelId == activeLevelId)
                        {
                            boundaryLines.Add(el.GeometryCurve);
                        }
                    }
                    else
                    {
                        // Not a plan view (no GenLevel), so we can't filter by floor. Accept all visible.
                        boundaryLines.Add(el.GeometryCurve);
                    }
                }
                else
                {
                    // Detail Lines are view-specific, so strictly belong to this view.
                    boundaryLines.Add(el.GeometryCurve);
                }
            }

            return boundaryLines;
        }

        // Old methods removed/replaced for optimization
        private Tuple<XYZ, Curve> FindClosestIntersection(Line ray, List<Curve> boundaryLines)
        {
             // Deprecated by optimized inline logic
             return null;
        }

        private Curve ProjectCurveToPlane(Curve curve, Plane plane)
        {
             // ... (Keep existing implementation)
            try
            {
                XYZ start = ProjectPointToPlane(curve.GetEndPoint(0), plane);
                XYZ end = ProjectPointToPlane(curve.GetEndPoint(1), plane);

                if (curve is Line)
                {
                    return Line.CreateBound(start, end);
                }
                else if (curve is Arc arc)
                {
                    XYZ mid = ProjectPointToPlane(arc.Evaluate(0.5, true), plane);
                    return Arc.Create(start, end, mid);
                }
                else
                {
                    return null; 
                }
            }
            catch
            {
                return null;
            }
        }
        private XYZ ProjectPointToPlane(XYZ point, Plane plane)
        {
            // ... (Keep existing implementation)
            XYZ planeOrigin = plane.Origin;
            XYZ planeNormal = plane.Normal;
            XYZ vectorToPoint = point - planeOrigin;
            double distance = vectorToPoint.DotProduct(planeNormal);
            return point - distance * planeNormal;
        }

        private static bool IsAlmostEqualTo(XYZ point1, XYZ point2, double tolerance = 1e-6)
        {
            return point1.DistanceTo(point2) <= tolerance;
        }
    }
}
