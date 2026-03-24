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

        public ElementId DrawFilledRegion(double resolution)
        {
            if (_currentPosition == null || _filledRegionTypeId == null)
            {
                TaskDialog.Show("Error", "Camera position or filled region type is not set.");
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
                            CurveLoop boundary = SimplifyBoundary(fovPoints);
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
                                CurveLoop fallback = CreateFallbackBoundary(fovPoints);
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
            TaskDialog.Show("Warning", $"Could not generate a valid filled region boundary even after relaxing resolution.\nLast Error: {lastError}\n\nThe FOV geometry might be too complex or self-intersecting.");
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

        public void CreateAngularDimension(ElementId filledRegionId)
        {
            // FEATURE DISABLED: Angular dimensions created via API are not visible
            // Despite extensive debugging, dimensions are created successfully but remain invisible
            // This appears to be an API limitation or very specific requirement we haven't identified
            // The dimension IS created (valid Element ID, correct value, persists after commit)
            // but Revit does not display it visually
            return;
            
            /* COMMENTED OUT - ORIGINAL IMPLEMENTATION
            // Debug: CreateAngularDimension called
            
            if (filledRegionId == null || filledRegionId == ElementId.InvalidElementId) return;

            using (Transaction trans = new Transaction(_doc, "Create Angular Dimension"))
            {
                trans.Start();
                try
                {
                    // Ensure geometry is up to date
                    _doc.Regenerate();

                    FilledRegion region = _doc.GetElement(filledRegionId) as FilledRegion;
                    if (region == null) 
                    {
                        // Region is NULL
                        trans.RollBack();
                        return;
                    }

                    // Get dependent CurveElements (the boundary lines)
                    var dependentIds = region.GetDependentElements(null);
                    List<CurveElement> convergingCurves = new List<CurveElement>();

                    foreach (var id in dependentIds)
                    {
                        if (_doc.GetElement(id) is CurveElement ce && ce.GeometryCurve is Line line)
                        {
                            // Check if line connects to current camera position
                            if (line.GetEndPoint(0).DistanceTo(_currentPosition) < 0.01 ||
                                line.GetEndPoint(1).DistanceTo(_currentPosition) < 0.01)
                            {
                                convergingCurves.Add(ce);
                            }
                        }
                    }

                    if (convergingCurves.Count >= 2)
                    {
                        // Found converging curves
                        CurveElement curve1 = convergingCurves[0];
                        CurveElement curve2 = convergingCurves[1];
                        Line line1 = curve1.GeometryCurve as Line;
                        Line line2 = curve2.GeometryCurve as Line;

                        // Create references from the lines
                        Reference ref1 = line1.Reference;
                        Reference ref2 = line2.Reference;
                        
                        // References created

                        // Define Dimension Arc
                        // Use a much larger radius so the arc extends well beyond the FOV region
                        // Manual dimensions typically use larger arcs for better visibility
                        double radius = UnitUtils.ConvertToInternalUnits(10000, UnitTypeId.Millimeters); // 10 meters instead of 2

                        // Vectors from center
                         XYZ vector1 = (line1.GetEndPoint(0).DistanceTo(_currentPosition) < 0.01 ? line1.Direction : -line1.Direction);
                         XYZ vector2 = (line2.GetEndPoint(0).DistanceTo(_currentPosition) < 0.01 ? line2.Direction : -line2.Direction);

                        // Create arc for dimension
                        // We need an arc passing through the dimension line location.
                        // Center = _currentPosition
                        // Radius = radius
                        
                        // Plane for dimension
                        // Assuming horizontal plane at Z
                        XYZ normal = XYZ.BasisZ;
                        XYZ xVec = vector1.Normalize();
                        XYZ yVec = normal.CrossProduct(xVec).Normalize();
                        
                        // Angle to vector2
                        double angle = xVec.AngleOnPlaneTo(vector2.Normalize(), normal); // 0 to 2PI
                        
                        // Create BOUNDED Arc using start and end points
                        // Start point: along vector1 at radius distance
                        XYZ startPoint = _currentPosition + (vector1.Normalize() * radius);
                        // End point: along vector2 at radius distance  
                        XYZ endPoint = _currentPosition + (vector2.Normalize() * radius);
                        // Middle point: halfway between start and end on the arc
                        double midAngle = angle / 2.0;
                        XYZ midDirection = (Math.Cos(midAngle) * xVec + Math.Sin(midAngle) * yVec).Normalize();
                        XYZ midPoint = _currentPosition + (midDirection * radius);
                        
                        Arc dimArc = Arc.Create(startPoint, endPoint, midPoint);

                        // View
                        View view = _doc.GetElement(_currentView.Id) as View;

                        // 1. References
                        var refs = new List<Reference> { ref1, ref2 };
                        
                        // 2. Dimension Type (Angular) - Exclude transparent types
                        var allAngularTypes = new FilteredElementCollector(_doc)
                            .OfClass(typeof(DimensionType))
                            .Cast<DimensionType>()
                            .Where(dt => dt.StyleType == DimensionStyleType.Angular)
                            .ToList();
                        
                        // Found angular dimension types
                        
                        // Prefer non-transparent type
                        DimensionType dimType = allAngularTypes
                            .FirstOrDefault(dt => !dt.Name.ToLower().Contains("transparent"))
                            ?? allAngularTypes.FirstOrDefault();

                        if (dimType != null)
                        {
                            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "AngularDimension_Debug.txt");
                            StringBuilder log = new StringBuilder();
                            log.AppendLine($"=== Angular Dimension Debug Log ===");
                            log.AppendLine($"Dimension Type: {dimType.Name}");
                            log.AppendLine($"View: {view.Name} (Type: {view.ViewType}, ID: {view.Id})");
                            log.AppendLine($"Arc Center: {dimArc.Center}");
                            log.AppendLine($"Arc Radius: {dimArc.Radius}");
                            log.AppendLine($"Angle Span: {angle * 180 / Math.PI} degrees");
                            log.AppendLine($"Reference 1 ElementId: {ref1.ElementId}");
                            log.AppendLine($"Reference 2 ElementId: {ref2.ElementId}");
                            
                            // Try the older API: FamilyItemFactory.NewAngularDimension
                            // This takes two separate references instead of a list
                            Dimension dim = _doc.FamilyCreate.NewAngularDimension(view, dimArc, ref1, ref2, dimType);
                            if (dim != null)
                            {
                                ElementId dimId = dim.Id;
                                log.AppendLine($"\n=== Dimension Created ===");
                                log.AppendLine($"Element ID: {dimId}");
                                log.AppendLine($"Value: {dim.Value * 180 / Math.PI} degrees");
                                log.AppendLine($"Curve: {dim.Curve?.GetType().Name ?? "null"}");
                                log.AppendLine($"Number of References: {dim.References?.Size ?? 0}");
                                
                                // Workaround: Move dimension slightly to force visibility
                                // This is a known workaround for invisible dimensions in Revit API
                                try
                                {
                                    XYZ moveVector = new XYZ(0.01, 0.01, 0); // Small movement
                                    ElementTransformUtils.MoveElement(_doc, dimId, moveVector);
                                    log.AppendLine($"Applied move workaround");
                                }
                                catch (Exception moveEx)
                                {
                                    log.AppendLine($"Move workaround failed: {moveEx.Message}");
                                }
                                
                                // Commit transaction
                                trans.Commit();
                                
                                // Verify after commit
                                Element verifyElement = _doc.GetElement(dimId);
                                if (verifyElement != null && verifyElement is AngularDimension verifyDim)
                                {
                                    log.AppendLine($"\n=== Post-Commit Verification ===");
                                    log.AppendLine($"Dimension PERSISTED: True");
                                    log.AppendLine($"Still Valid: {verifyDim.IsValidObject}");
                                    log.AppendLine($"Owner View ID: {verifyDim.OwnerViewId}");
                                    log.AppendLine($"Curve after commit: {verifyDim.Curve?.GetType().Name ?? "null"}");
                                    
                                    // Check if curve is visible
                                    if (verifyDim.Curve != null)
                                    {
                                        log.AppendLine($"Curve IsBound: {verifyDim.Curve.IsBound}");
                                        if (verifyDim.Curve is Arc verifyArc)
                                        {
                                            log.AppendLine($"Arc Center: {verifyArc.Center}");
                                            log.AppendLine($"Arc Radius: {verifyArc.Radius}");
                                            log.AppendLine($"Arc Normal: {verifyArc.Normal}");
                                        }
                                    }
                                    
                                    // Check references
                                    if (verifyDim.References != null)
                                    {
                                        log.AppendLine($"References count: {verifyDim.References.Size}");
                                        foreach (Reference r in verifyDim.References)
                                        {
                                            log.AppendLine($"  - Ref ElementId: {r.ElementId}, LinkedElementId: {r.LinkedElementId}");
                                        }
                                    }
                                    
                                    File.WriteAllText(logPath, log.ToString());
                                    // Debug log written successfully
                                }
                                else
                                {
                                    log.AppendLine($"\n=== Post-Commit Verification ===");
                                    log.AppendLine($"Dimension PERSISTED: False - Element was deleted!");
                                    File.WriteAllText(logPath, log.ToString());
                                    // Dimension was deleted
                                }
                                return; // Exit early since we already committed
                            }
                            else
                            {
                                log.AppendLine($"\n=== Creation Failed ===");
                                log.AppendLine($"AngularDimension.Create returned NULL!");
                                File.WriteAllText(logPath, log.ToString());
                                // Create returned null
                            }
                        }
                        else
                        {
                            // No dimension type found
                        }
                    }
                    else
                    {
                         // Debug: No boundaries found
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                   TaskDialog.Show("Error", "Angular Dimension Failed: " + ex.Message + "\nStack: " + ex.StackTrace);
                }
                }
            }
            */
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
