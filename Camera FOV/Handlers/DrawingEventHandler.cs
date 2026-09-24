using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Camera_FOV.Utils;
using Camera_FOV;
using Camera_FOV.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Camera_FOV.UI; // Add UI namespace for LinkedModelsSelectionWindow
using System.Windows.Threading;

namespace Camera_FOV.Handlers
{
    public class DoriLayerConfig
    {
        public double Distance { get; set; }
        public ElementId TypeId { get; set; }
        public bool DrawDimension { get; set; }
    }

    public class DrawingEventHandler : IExternalEventHandler
{
    public enum DrawingAction
    {
        None,
        Draw,
        Update,
        Delete,
        DrawFilledRegion,
        UpdateFilledRegion,
        DeleteFilledRegion,
        UndoFilledRegion,
        UpdateCameraParameter,
        CreateBoundaryLine,
        CreateFilledRegions,
        TraceWallsAndDrawBoundary
    }

    private DrawingAction _currentAction = DrawingAction.None;
    private DrawingTools _drawingTools;
    private XYZ _position;
    private double _maxDistance;
    private double _rotationAngle;
    private double _fovAngle;
    private ElementId _filledRegionTypeId;
    private double _sliderResolution;
    private UIDocument _uiDoc; // Add a field for the UIDocument
    private MainWindow _mainWindow;
    private string _newLineStyleName; // Add this to store the new line style name
    private Element _cameraElement; // For parameter updates
    private double _parameterValue; // For parameter updates
    private List<ElementId> _lastBatchCreatedIds = new List<ElementId>(); // Undo batch tracker
    public bool DrawAngularDimension { get; set; } = false;
    private List<DoriLayerConfig> _doriLayers;

    public void SetMainWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
    }
    public void SetupBoundaryLineCreation(UIDocument uiDoc)
    {
        _uiDoc = uiDoc;
        _currentAction = DrawingAction.CreateBoundaryLine;
    }
    public void SetupCreateFilledRegions(UIDocument uiDoc)
    {
        _uiDoc = uiDoc;
        _currentAction = DrawingAction.CreateFilledRegions;
    }
    public void SetupTraceWallsAndDrawBoundary(UIDocument uiDoc)
    {
        _uiDoc = uiDoc;
        _currentAction = DrawingAction.TraceWallsAndDrawBoundary;
    }
    public void SetupChangeBoundaryLineType(UIDocument uiDoc, string newLineStyleName)
    {
        _uiDoc = uiDoc;
        _newLineStyleName = newLineStyleName; // Store the new line style name
    }
    public void Setup(
        DrawingTools drawingTools,
        DrawingAction action,
        XYZ position = null,
        double maxDistance = 0,
        double rotationAngle = 0,
        double fovAngle = 90,
        ElementId filledRegionTypeId = null,
        double sliderResolution = 1.0,
        Element cameraElement = null,
        double userRotationForParameter = 0,
        List<DoriLayerConfig> doriLayers = null)
    {
        _drawingTools = drawingTools;
        _currentAction = action;
        _position = position;
        _maxDistance = maxDistance;
        _rotationAngle = rotationAngle;
        _fovAngle = fovAngle;
        _filledRegionTypeId = filledRegionTypeId;
        _sliderResolution = sliderResolution;
        _cameraElement = cameraElement;
        _parameterValue = userRotationForParameter;
        _doriLayers = doriLayers;
    }

    public void SetupCameraParameterUpdate(Element cameraElement, double rotationValue)
    {
        _cameraElement = cameraElement;
        _parameterValue = rotationValue;
        _currentAction = DrawingAction.UpdateCameraParameter;
    }

    public void Execute(UIApplication app)
    {
        try
        {
            switch (_currentAction)
            {
                case DrawingAction.CreateBoundaryLine:
                    CreateBoundaryLine();
                    break;

                case DrawingAction.Draw:
                    _drawingTools.SetParameters(_position, _maxDistance, _rotationAngle);
                    _drawingTools.DrawDetailLine();
                    break;

                case DrawingAction.Update:
                    _drawingTools.SetParameters(_position, _maxDistance, _rotationAngle);
                    _drawingTools.UpdateDetailLine();
                    break;

                case DrawingAction.Delete:
                    _drawingTools.DeleteDetailLine();
                    break;

                case DrawingAction.DrawFilledRegion:
                    // Update camera parameter if element is provided
                    if (_cameraElement != null)
                    {
                        UpdateCameraParameter();
                    }

                    if (_doriLayers != null && _doriLayers.Any())
                    {
                        _lastBatchCreatedIds.Clear();
                        foreach (var layer in _doriLayers)
                        {
                            ElementId id = DrawLayer(layer.Distance, layer.TypeId, layer.DrawDimension);
                            if (id != ElementId.InvalidElementId) _lastBatchCreatedIds.Add(id);
                        }
                    }
                    else
                    {
                        // Fallback single mode
                        ElementId id = DrawLayer(_maxDistance, _filledRegionTypeId, DrawAngularDimension);
                        _lastBatchCreatedIds.Clear();
                        if (id != ElementId.InvalidElementId) _lastBatchCreatedIds.Add(id);
                    }
                    break;


                case DrawingAction.DeleteFilledRegion:
                    _drawingTools.DeleteFilledRegion();
                    break;

                case DrawingAction.UndoFilledRegion:
                     if (_lastBatchCreatedIds != null && _lastBatchCreatedIds.Any())
                    {
                        foreach (var id in _lastBatchCreatedIds)
                        {
                            _drawingTools.DeleteElement(id);
                        }
                        _lastBatchCreatedIds.Clear();
                    }
                    else
                    {
                        // Fallback to old behavior if list is empty (e.g. legacy or restart)
                        _drawingTools.DeleteFilledRegion();
                    }
                    break;

                case DrawingAction.UpdateCameraParameter:
                    UpdateCameraParameter();
                    break;

                case DrawingAction.CreateFilledRegions:
                    CreateFilledRegions();
                    _mainWindow?.NotifyFilledRegionsCreated(); // Safely invoke if _mainWindow is set
                    break;

                case DrawingAction.TraceWallsAndDrawBoundary:
                    TraceWallsAndDrawBoundary();
                    break;

                default:
                    TaskDialog.Show("Info", "No valid action was set up.");
                    break;
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"An error occurred during {_currentAction}: {ex.Message}");
        }
        finally
        {
            _currentAction = DrawingAction.None; // Reset action
        }
    }
    private void CreateBoundaryLine()
    {
        if (_uiDoc == null)
        {
            TaskDialog.Show("Error", "UIDocument is not initialized.");
            return;
        }

        Document doc = _uiDoc.Document;

        try
        {
            using (Transaction transaction = new Transaction(doc, "Create Boundary Line"))
            {
                transaction.Start();

                // Access the Lines category
                Categories categories = doc.Settings.Categories;
                Category linesCategory = categories.get_Item(BuiltInCategory.OST_Lines);

                // Check if the "Boundary" subcategory already exists
                Category boundaryCategory = null;
                foreach (Category subCategory in linesCategory.SubCategories)
                {
                    if (subCategory.Name == "Boundary")
                    {
                        boundaryCategory = subCategory;
                        break;
                    }
                }

                if (boundaryCategory != null)
                {
                    MessageBox.Show("Boundary line style already exists.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                    transaction.RollBack(); // Rollback since nothing is being changed
                    return;
                }

                // Create a new subcategory for "Boundary"
                boundaryCategory = categories.NewSubcategory(linesCategory, "Boundary");

                if (boundaryCategory != null)
                {
                    // Set the properties for the Boundary line style
                    boundaryCategory.LineColor = new Color(0, 255, 0); // Green color
                    boundaryCategory.SetLineWeight(1, GraphicsStyleType.Projection); // Line weight 1

                    // Assign the "Solid" line pattern
                    LinePatternElement solidPattern = new FilteredElementCollector(doc)
                        .OfClass(typeof(LinePatternElement))
                        .Cast<LinePatternElement>()
                        .FirstOrDefault(lp => lp.Name.Equals("Solid"));

                    if (solidPattern != null)
                    {
                        boundaryCategory.SetLinePatternId(solidPattern.Id, GraphicsStyleType.Projection);
                    }
                    else
                    {
                        TaskDialog.Show("Warning", "Solid line pattern not found.");
                    }

                    MessageBox.Show("Boundary line style created successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    TaskDialog.Show("Error", "Failed to create the Boundary line style.");
                }

                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"An error occurred during CreateBoundaryLine:\n{ex.Message}\n{ex.StackTrace}");
        }
    }
    private void CreateFilledRegions()
    {
        if (_uiDoc == null)
        {
            TaskDialog.Show("Error", "UIDocument is not initialized.");
            return;
        }

        Document doc = _uiDoc.Document;

        var filledRegionData = new Dictionary<string, Autodesk.Revit.DB.Color>
    {
        { "dori_25px", new Autodesk.Revit.DB.Color(255, 213, 213) }, // Red
        { "dori_63px", new Autodesk.Revit.DB.Color(255, 252, 232) }, // Yellow
        { "dori_125px", new Autodesk.Revit.DB.Color(223, 239, 255) }, // Blue
        { "dori_250px", new Autodesk.Revit.DB.Color(226, 252, 231) } // Green
    };

        try
        {
            using (Transaction transaction = new Transaction(doc, "Create Filled Region Types"))
            {
                transaction.Start();

                // Get the solid fill pattern
                FillPatternElement solidFillPattern = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(fp => fp.GetFillPattern().IsSolidFill);

                if (solidFillPattern == null)
                {
                    TaskDialog.Show("Error", "Solid fill pattern not found. Cannot create filled region types.");
                    transaction.RollBack();
                    return;
                }

                // Retrieve all existing filled region types
                var existingFilledRegions = new FilteredElementCollector(doc)
                    .OfClass(typeof(FilledRegionType))
                    .Cast<FilledRegionType>()
                    .ToDictionary(r => r.Name, r => r);

                List<string> existingRegions = new List<string>();
                List<string> createdRegions = new List<string>();

                // Create or check each filled region type
                foreach (var entry in filledRegionData)
                {
                    string regionName = entry.Key;
                    Autodesk.Revit.DB.Color regionColor = entry.Value;

                    // Check if the filled region type already exists
                    if (existingFilledRegions.ContainsKey(regionName))
                    {
                        existingRegions.Add(regionName);
                        continue; // Skip if it already exists
                    }

                    // Duplicate an existing filled region type
                    var defaultRegionType = existingFilledRegions.Values.FirstOrDefault();

                    if (defaultRegionType == null)
                    {
                        TaskDialog.Show("Error", "No default filled region type found. Cannot create new types.");
                        transaction.RollBack();
                        return;
                    }

                    FilledRegionType newRegionType = defaultRegionType.Duplicate(regionName) as FilledRegionType;

                    if (newRegionType != null)
                    {
                        newRegionType.ForegroundPatternId = solidFillPattern.Id;
                        newRegionType.ForegroundPatternColor = regionColor;
                        newRegionType.IsMasking = false;
                        createdRegions.Add(regionName);
                    }
                }

                transaction.Commit();

                if (existingRegions.Any())
                {
                    MessageBox.Show("Filled regions already exist!", "Region types exist", MessageBoxButton.OK, MessageBoxImage.Information);

                }

                if (createdRegions.Any())
                {
                    MessageBox.Show("Filled regions created sucessfuly", "Success!", MessageBoxButton.OK, MessageBoxImage.Information);
                }

            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"An error occurred while creating filled region types:\n{ex.Message}\n{ex.StackTrace}");
        }
    }

    // Categories that act as obstructions when traced. Doors are intentionally left out: wall
    // solids already have door openings cut out of them, so skipping the door leaf keeps the
    // opening open instead of closing the wall across it.
    private static readonly List<BuiltInCategory> TracedCategories = new List<BuiltInCategory>
    {
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_Windows,
        BuiltInCategory.OST_CurtainWallPanels,
        BuiltInCategory.OST_CurtainWallMullions
    };

    private const double GeometryTolerance = 1e-5; // feet

    // Vertical limits of the active plan, in host (internal) coordinates.
    private class TraceRange
    {
        public double CutZ;
        public double BottomZ = double.MinValue;
        public double TopZ = double.MaxValue;
        public Plane CutPlane;
        public Plane ViewPlane;
    }

    private class TraceSummary
    {
        public int LinesCreated;
        public int LinesReplaced;
        public int ElementsTraced;
        public int ElementsBelowOrAboveCut;
        public int LinksTraced;
        public int LinksNotLoaded;
        public int SectionFailures;
    }

    // Traces the footprint of walls, columns, windows and curtain wall parts where they cross the
    // active plan's cut plane. Elements that do not reach the cut plane (e.g. low walls) and
    // geometry on other floors are not traced. Lines from a previous trace in this view are
    // replaced; user-drawn Boundary lines are kept.
    private void TraceWallsAndDrawBoundary()
    {
        if (_uiDoc == null)
        {
            TaskDialog.Show("Error", "UIDocument is not initialized.");
            return;
        }

        Document doc = _uiDoc.Document;

        try
        {
            if (!(_uiDoc.ActiveView is ViewPlan view))
            {
                TaskDialog.Show("Error", "Automatic boundary tracing requires an active plan view. The plan's view range decides which geometry is traced.");
                return;
            }

            TraceRange range = GetTraceRange(view);
            if (range == null)
            {
                TaskDialog.Show("Error", "Could not resolve the cut plane of the active plan view.");
                return;
            }

            GraphicsStyle boundaryLineStyle = GetBoundaryLineStyle(doc);
            if (boundaryLineStyle == null)
            {
                TaskDialog.Show("Error", "Line style 'Boundary' not found. Please create it first.");
                return;
            }

            ElementMulticategoryFilter filter = new ElementMulticategoryFilter(TracedCategories);

            // Collect elements visible in the current view
            var elements = new FilteredElementCollector(doc, view.Id)
                .WherePasses(filter)
                .WhereElementIsNotElementType()
                .ToList();

            List<RevitLinkInstance> selectedLinks = SelectLinkedModels(doc, view);

            if (!elements.Any() && !selectedLinks.Any())
            {
                TaskDialog.Show("Info", "No walls, columns, windows, curtain wall parts or selected linked models found.");
                return;
            }

            var summary = new TraceSummary();

            using (Transaction transaction = new Transaction(doc, "Trace Walls and Columns and Draw Boundary"))
            {
                transaction.Start();

                HashSet<string> drawnCurveHashes = ReplacePreviouslyTracedLines(doc, view, range.ViewPlane, summary);

                foreach (var element in elements)
                {
                    TraceElement(element, Transform.Identity, range, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
                }

                foreach (var link in selectedLinks)
                {
                    TraceLinkedModel(link, filter, range, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
                }

                transaction.Commit();
            }

            ShowTraceSummary(summary);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Error", $"An error occurred while tracing walls and columns:\n{ex.Message}");
        }
    }

    private List<RevitLinkInstance> SelectLinkedModels(Document doc, View view)
    {
        var linkInstances = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .ToList();

        List<RevitLinkInstance> selectedLinks = new List<RevitLinkInstance>();

        if (linkInstances.Any())
        {
            LinkedModelsSelectionWindow window = new LinkedModelsSelectionWindow(linkInstances.Select(l => l.Name).ToList());
            window.ShowDialog();

            if (window.Result == LinkedModelsSelectionWindow.SelectionResult.All)
            {
                selectedLinks.AddRange(linkInstances);
            }
            else if (window.Result == LinkedModelsSelectionWindow.SelectionResult.Selected)
            {
                var selectedNames = window.SelectedLinks.Select(l => l.Name).ToHashSet();
                selectedLinks.AddRange(linkInstances.Where(l => selectedNames.Contains(l.Name)));
            }
        }

        return selectedLinks;
    }

    private static GraphicsStyle GetBoundaryLineStyle(Document doc)
    {
        Category linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        Category boundarySubCategory = linesCategory.SubCategories.Contains("Boundary")
            ? linesCategory.SubCategories.get_Item("Boundary")
            : null;
        return boundarySubCategory?.GetGraphicsStyle(GraphicsStyleType.Projection);
    }

    private static TraceRange GetTraceRange(ViewPlan view)
    {
        Document doc = view.Document;
        PlanViewRange viewRange = view.GetViewRange();

        Level cutLevel = doc.GetElement(viewRange.GetLevelId(PlanViewPlane.CutPlane)) as Level ?? view.GenLevel;
        if (cutLevel == null) return null;

        var range = new TraceRange
        {
            CutZ = cutLevel.ProjectElevation + viewRange.GetOffset(PlanViewPlane.CutPlane)
        };

        // Top/bottom may reference "Unlimited" or "Level Above/Below"; those leave the range open.
        if (doc.GetElement(viewRange.GetLevelId(PlanViewPlane.TopClipPlane)) is Level topLevel)
            range.TopZ = topLevel.ProjectElevation + viewRange.GetOffset(PlanViewPlane.TopClipPlane);

        if (doc.GetElement(viewRange.GetLevelId(PlanViewPlane.BottomClipPlane)) is Level bottomLevel)
            range.BottomZ = bottomLevel.ProjectElevation + viewRange.GetOffset(PlanViewPlane.BottomClipPlane);

        range.CutPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, range.CutZ));

        if (view.SketchPlane != null)
            range.ViewPlane = view.SketchPlane.GetPlane();
        else
            range.ViewPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, view.GenLevel?.ProjectElevation ?? 0));

        return range;
    }

    // Deletes lines generated by a previous trace in this view and returns the hashes of the
    // remaining Boundary lines, so the new trace neither duplicates them nor removes them.
    private HashSet<string> ReplacePreviouslyTracedLines(Document doc, View view, Plane viewPlane, TraceSummary summary)
    {
        var existingHashes = new HashSet<string>();
        var toDelete = new List<ElementId>();

        var curveElements = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(CurveElement))
            .WhereElementIsNotElementType()
            .Cast<CurveElement>();

        foreach (CurveElement curveElement in curveElements)
        {
            if (ElementTagStorage.IsTracedBoundaryLine(curveElement, view))
            {
                toDelete.Add(curveElement.Id);
            }
            else if (curveElement.LineStyle?.Name == "Boundary")
            {
                foreach (Curve projected in ProjectCurveToPlane(curveElement.GeometryCurve, viewPlane, doc))
                {
                    existingHashes.Add(GenerateCurveHash(projected));
                }
            }
        }

        if (toDelete.Any())
        {
            doc.Delete(toDelete);
            summary.LinesReplaced = toDelete.Count;
        }

        return existingHashes;
    }

    private void TraceLinkedModel(RevitLinkInstance link, ElementFilter filter, TraceRange range, View hostView, GraphicsStyle boundaryLineStyle, Document hostDoc, HashSet<string> drawnCurveHashes, TraceSummary summary)
    {
        Document linkedDoc = link.GetLinkDocument();
        if (linkedDoc == null)
        {
            summary.LinksNotLoaded++;
            return;
        }

        summary.LinksTraced++;

        // Converts linked document coordinates to host coordinates (offset, rotation, elevation)
        Transform linkTransform = link.GetTotalTransform();

        var linkedElements = new FilteredElementCollector(linkedDoc)
            .WherePasses(filter)
            .WhereElementIsNotElementType();

        foreach (var element in linkedElements)
        {
            TraceElement(element, linkTransform, range, hostView, boundaryLineStyle, hostDoc, drawnCurveHashes, summary);
        }
    }

    // toHost maps the element's own document coordinates to host coordinates
    // (identity for host elements, the link transform for linked ones).
    private void TraceElement(Element element, Transform toHost, TraceRange range, View view, GraphicsStyle boundaryLineStyle, Document doc, HashSet<string> drawnCurveHashes, TraceSummary summary)
    {
        // Coarse filter on the bounding box, evaluated in host coordinates
        BoundingBoxXYZ box = element.get_BoundingBox(null);
        if (box == null) return;

        GetHostZRange(box, toHost, out double minZ, out double maxZ);

        if (maxZ < range.BottomZ || minZ > range.TopZ)
            return; // Outside the view range entirely, e.g. another floor

        if (minZ > range.CutZ + GeometryTolerance || maxZ < range.CutZ - GeometryTolerance)
        {
            summary.ElementsBelowOrAboveCut++;
            return;
        }

        Options geomOptions = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
        GeometryElement geometry = element.get_Geometry(geomOptions);
        if (geometry == null) return;

        // Section in the element's own coordinates, then bring the outline into the host.
        Transform toLocal = toHost.Inverse;
        Plane localCutPlane = Plane.CreateByNormalAndOrigin(
            toLocal.OfVector(range.CutPlane.Normal),
            toLocal.OfPoint(range.CutPlane.Origin));

        bool hasSolids = false;
        bool traced = false;
        foreach (Solid solid in GetSolids(geometry))
        {
            hasSolids = true;
            foreach (Curve sectionCurve in SectionSolid(solid, localCutPlane, summary))
            {
                traced = true;
                DrawTracedCurve(sectionCurve.CreateTransformed(toHost), range.ViewPlane, view, boundaryLineStyle, doc, drawnCurveHashes, summary);
            }
        }

        if (traced)
            summary.ElementsTraced++;
        else if (hasSolids)
            summary.ElementsBelowOrAboveCut++;
    }

    private static void GetHostZRange(BoundingBoxXYZ box, Transform toHost, out double minZ, out double maxZ)
    {
        Transform boxToHost = toHost.Multiply(box.Transform);
        minZ = double.MaxValue;
        maxZ = double.MinValue;

        foreach (double x in new[] { box.Min.X, box.Max.X })
        foreach (double y in new[] { box.Min.Y, box.Max.Y })
        foreach (double z in new[] { box.Min.Z, box.Max.Z })
        {
            double hostZ = boxToHost.OfPoint(new XYZ(x, y, z)).Z;
            minZ = Math.Min(minZ, hostZ);
            maxZ = Math.Max(maxZ, hostZ);
        }
    }

    private static IEnumerable<Solid> GetSolids(GeometryElement geometry)
    {
        foreach (GeometryObject geomObj in geometry)
        {
            if (geomObj is Solid solid)
            {
                if (solid.Faces.Size > 0 && solid.Volume > 0)
                    yield return solid;
            }
            else if (geomObj is GeometryInstance instance)
            {
                foreach (Solid nested in GetSolids(instance.GetInstanceGeometry()))
                    yield return nested;
            }
        }
    }

    // Returns the outline of the solid on the cut plane. The part above the plane is kept and its
    // faces lying on the plane are the section; solids not crossing the plane yield nothing.
    private static List<Curve> SectionSolid(Solid solid, Plane cutPlane, TraceSummary summary)
    {
        var curves = new List<Curve>();

        Solid abovePart;
        try
        {
            abovePart = BooleanOperationsUtils.CutWithHalfSpace(solid, cutPlane);
        }
        catch (Exception ex)
        {
            summary.SectionFailures++;
            System.Diagnostics.Debug.WriteLine($"Failed to section solid: {ex.Message}");
            return curves;
        }

        if (abovePart == null || abovePart.Faces.Size == 0) return curves;

        foreach (Face face in abovePart.Faces)
        {
            if (!(face is PlanarFace planarFace)) continue;
            if (Math.Abs(planarFace.FaceNormal.DotProduct(cutPlane.Normal)) < 1 - GeometryTolerance) continue;
            if (Math.Abs(cutPlane.Normal.DotProduct(planarFace.Origin - cutPlane.Origin)) > GeometryTolerance) continue;

            foreach (EdgeArray loop in planarFace.EdgeLoops)
            {
                foreach (Edge edge in loop)
                {
                    curves.Add(edge.AsCurve());
                }
            }
        }

        return curves;
    }

    private void DrawTracedCurve(Curve curve, Plane viewPlane, View view, GraphicsStyle boundaryLineStyle, Document doc, HashSet<string> drawnCurveHashes, TraceSummary summary)
    {
        foreach (Curve projectedCurve in ProjectCurveToPlane(curve, viewPlane, doc))
        {
            // Hash the PROJECTED curve to deduplicate 2D lines
            if (!drawnCurveHashes.Add(GenerateCurveHash(projectedCurve)))
                continue;

            try
            {
                DetailCurve detailCurve = doc.Create.NewDetailCurve(view, projectedCurve);
                detailCurve.LineStyle = boundaryLineStyle;
                ElementTagStorage.TagTracedBoundaryLine(detailCurve, view);
                summary.LinesCreated++;
            }
            catch (Exception ex)
            {
                // Ignore failures for individual curves (e.g. degenerate curves)
                System.Diagnostics.Debug.WriteLine($"Failed to create boundary line: {ex.Message}");
            }
        }
    }

    private static void ShowTraceSummary(TraceSummary summary)
    {
        var lines = new List<string>
        {
            $"Created {summary.LinesCreated} Boundary lines from {summary.ElementsTraced} elements crossing the view's cut plane."
        };

        if (summary.LinesReplaced > 0)
            lines.Add($"Replaced {summary.LinesReplaced} lines from the previous trace. Manually drawn Boundary lines were kept.");

        if (summary.ElementsBelowOrAboveCut > 0)
            lines.Add($"Skipped {summary.ElementsBelowOrAboveCut} elements inside the view range that do not reach the cut plane (for example low walls). Draw Boundary lines manually for any that should block the view.");

        if (summary.LinksTraced > 0)
            lines.Add($"Traced {summary.LinksTraced} linked models.");

        if (summary.LinksNotLoaded > 0)
            lines.Add($"Skipped {summary.LinksNotLoaded} linked models that are not loaded.");

        if (summary.SectionFailures > 0)
            lines.Add($"{summary.SectionFailures} solids could not be sectioned and were skipped.");

        TaskDialog.Show("Boundary Tracing", string.Join("\n\n", lines));
    }

    // Helper method to generate a hash for a curve based on its start and end points
    private string GenerateCurveHash(Curve curve)
    {
        XYZ p1 = curve.GetEndPoint(0);
        XYZ p2 = curve.GetEndPoint(1);

        // Format points with precision to avoid floating point issues
        string s1 = $"{Math.Round(p1.X, 4)},{Math.Round(p1.Y, 4)},{Math.Round(p1.Z, 4)}";
        string s2 = $"{Math.Round(p2.X, 4)},{Math.Round(p2.Y, 4)},{Math.Round(p2.Z, 4)}";

        // Sort to ensure direction invariance
        if (string.Compare(s1, s2) < 0)
            return $"{s1}|{s2}";
        else
            return $"{s2}|{s1}";
    }

    // Projects a curve onto the plane. Lines and arcs keep their type; other curves
    // (splines, ellipses) are tessellated into lines. Segments shorter than Revit allows are dropped.
    private static List<Curve> ProjectCurveToPlane(Curve curve, Plane plane, Document doc)
    {
        var result = new List<Curve>();
        if (curve == null || !curve.IsBound) return result;

        double shortCurveTolerance = doc.Application.ShortCurveTolerance * 2;

        try
        {
            XYZ start = ProjectPointToPlane(curve.GetEndPoint(0), plane);
            XYZ end = ProjectPointToPlane(curve.GetEndPoint(1), plane);

            if (curve is Line)
            {
                if (start.DistanceTo(end) >= shortCurveTolerance)
                    result.Add(Line.CreateBound(start, end));
                return result;
            }

            if (curve is Arc arc && start.DistanceTo(end) >= shortCurveTolerance)
            {
                XYZ mid = ProjectPointToPlane(arc.Evaluate(0.5, true), plane);
                try
                {
                    result.Add(Arc.Create(start, end, mid));
                    return result;
                }
                catch
                {
                    // Arc seen edge-on projects to a straight segment; tessellate below
                }
            }

            XYZ last = null;
            foreach (XYZ point in curve.Tessellate())
            {
                XYZ projected = ProjectPointToPlane(point, plane);
                if (last == null)
                {
                    last = projected;
                }
                else if (projected.DistanceTo(last) >= shortCurveTolerance)
                {
                    result.Add(Line.CreateBound(last, projected));
                    last = projected;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to project curve: {ex.Message}");
        }

        return result;
    }

    private static XYZ ProjectPointToPlane(XYZ point, Plane plane)
    {
        XYZ pointVector = point - plane.Origin;
        double distance = pointVector.DotProduct(plane.Normal);
        return point - distance * plane.Normal;
    }

    private void UpdateCameraParameter()
    {
        if (_cameraElement == null)
        {
            TaskDialog.Show("Debug", "Camera element is null");
            return;
        }

        Document doc = _cameraElement.Document;
        
        using (Transaction trans = new Transaction(doc, "Update Camera Parameters"))
        {
            trans.Start();

            // 1. Update "Pööra Kaamerat" (Rotation)
            Parameter rotationParam = _cameraElement.LookupParameter(SettingsManager.Settings.ParameterName_UserRotation);
            if (rotationParam != null && !rotationParam.IsReadOnly)
            {
                double rotationRadians = _parameterValue * (Math.PI / 180.0);
                rotationParam.Set(rotationRadians);
            }

            // 2. Update "Kaamera nurk" (FOV Override)
            Parameter fovParam = _cameraElement.LookupParameter(SettingsManager.Settings.ParameterName_FOVOverride);
            if (fovParam != null && !fovParam.IsReadOnly)
            {
                double fovRadians = _fovAngle * (Math.PI / 180.0);
                fovParam.Set(fovRadians);
            }

            trans.Commit();
        }
    }

    private ElementId DrawLayer(double distance, ElementId typeId, bool drawDimension)
    {
        Document doc = _drawingTools.Document;
        View view = _drawingTools.View;

        // Replace the coverage previously drawn for this camera + DORI type in this view.
        // The association is stored on the region itself, so it survives reopening the
        // window or the project. Untagged regions from older versions are never deleted.
        foreach (ElementId oldRegionId in ElementTagStorage.FindCoverageRegions(doc, view, _cameraElement, typeId))
        {
            _drawingTools.DeleteElement(oldRegionId);
        }

        _drawingTools.SetParameters(_position, distance, _rotationAngle, _fovAngle, typeId);
        ElementId newRegionId = _drawingTools.DrawFilledRegion( // Use slider resolution
            _sliderResolution,
            region => ElementTagStorage.TagCoverageRegion(region, _cameraElement, typeId, view));

        if (newRegionId != ElementId.InvalidElementId && drawDimension)
        {
            _drawingTools.CreateAngularDimension(newRegionId);
        }
        return newRegionId;
    }

    public string GetName()
    {
        return "Drawing Event Handler";
    }
}
}
